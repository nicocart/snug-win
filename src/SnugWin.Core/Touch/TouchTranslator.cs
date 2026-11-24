using System;
using System.Collections.Generic;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Touch;

public sealed record CloseButton(RectD Frame, ulong WindowHandle, RectD WindowFrame)
{
    public bool IsFrontmost(PointD point, IReadOnlyList<(ulong Handle, RectD Frame)> onScreen)
    {
        if (!Frame.Contains(point)) return false;
        foreach (var item in onScreen)
        {
            if (item.Frame.Contains(point))
                return item.Handle == WindowHandle && item.Frame.Matches(WindowFrame);
        }
        return false;
    }

    public bool IsFrontmost(PointD point, IReadOnlyList<(ulong Handle, RectD Frame, bool IsPointer)> onScreen)
    {
        if (!Frame.Contains(point)) return false;
        foreach (var item in onScreen)
        {
            if (!item.IsPointer && item.Frame.Contains(point))
                return item.Handle == WindowHandle && item.Frame.Matches(WindowFrame);
        }
        return false;
    }
}

public sealed record TouchRegion(RectD Frame, double Scale, CloseButton? CloseButton = null)
{
    public static TouchRegion ForPhoneScreen(RectD bounds, CloseButton? closeButton = null) =>
        new(bounds, ScreenFit.Scale(bounds.Width), closeButton);
}

public enum TouchInputKind
{
    Press,
    Drag,
    Release,
    Recognized
}

public enum TouchGesture
{
    None = 0,
    Hold = 1,
    Slide = 2
}

public readonly record struct TouchInput(
    TouchInputKind Kind,
    PointD Location,
    double Time,
    TouchGesture Gesture = TouchGesture.None)
{
    public TouchInput(TouchInputKind kind, PointD location, double time)
        : this(kind, location, time, TouchGesture.None)
    {
    }

    public static TouchInput Recognized(TouchGesture gesture, PointD location, double time) =>
        new(TouchInputKind.Recognized, location, time, gesture);
}

public sealed record TouchOptions(
    bool AllowScroll = true,
    bool AllowRightClick = true,
    bool NotifySelectionEnded = false)
{
    public static readonly TouchOptions Default = new();
}

public abstract record TouchCommand
{
    public sealed record ReplayPress : TouchCommand;
    public sealed record ReplayCurrent : TouchCommand;
    public sealed record RightClick(PointD At) : TouchCommand;
    public sealed record Scroll(ScrollStep Step) : TouchCommand;
    public sealed record CloseWindow(PointD At) : TouchCommand;
    public sealed record SelectionCompleted(PointD At) : TouchCommand;
}

public sealed record TouchResponse(bool PassOriginal, IReadOnlyList<TouchCommand> Commands)
{
    public static readonly TouchResponse Pass = new(true, Array.Empty<TouchCommand>());
    public static readonly TouchResponse SwallowEmpty = new(false, Array.Empty<TouchCommand>());

    public static TouchResponse Swallow(params TouchCommand[] commands) =>
        commands.Length == 0 ? SwallowEmpty : new(false, commands);
}

public struct TouchTranslator
{
    public const double Slop = 10.0;
    public const double HoldDuration = 0.5;
    private const double FlingWindow = 0.1;
    private const double MinFlingSpan = 0.04;
    private const double MaxFlingSpeed = 4000.0;

    private enum PhaseKind
    {
        Idle,
        Pending,
        Scrolling,
        Selecting
    }

    private PhaseKind _phase;
    private double _pendingSince;
    private PointD _origin;
    private PointD _last;
    private List<(double Time, PointD Point)>? _samples;
    private ScrollWheel _wheel;
    private Momentum? _momentum;
    private double _scale;
    private bool _pressStoppedCoast;
    private bool _pressOnClose;
    private bool _held;

    public bool IsCoasting => _momentum.HasValue;
    public PointD Origin => _origin;

    public TouchResponse Handle(TouchInput input, TouchRegion? region) =>
        Handle(input, region, TouchOptions.Default);

    public TouchResponse Handle(TouchInput input, TouchRegion? region, TouchOptions options) => input.Kind switch
    {
        TouchInputKind.Press => Press(input, region),
        TouchInputKind.Drag => Drag(input, options),
        TouchInputKind.Release => Release(input, options),
        TouchInputKind.Recognized => Recognize(input.Gesture),
        _ => TouchResponse.Pass
    };

    public ScrollStep? MomentumFrame()
    {
        if (_momentum is not { } m) return null;
        var frame = m.Next();
        if (frame is null)
        {
            _momentum = null;
            return _wheel.Step(0, 0, ScrollPhase.None, MomentumPhase.End, _origin);
        }
        _momentum = m;
        return _wheel.Step(frame.Value.Dx, frame.Value.Dy, ScrollPhase.None, frame.Value.Phase, _origin);
    }

    private TouchResponse Press(TouchInput input, TouchRegion? region)
    {
        var stop = StopMomentum();
        if (region is null || !region.Frame.Contains(input.Location))
        {
            _phase = PhaseKind.Idle;
            return stop is null ? TouchResponse.Pass : new TouchResponse(true, new[] { stop });
        }

        _scale = region.Scale <= 0 ? 1.0 : region.Scale;
        _pressStoppedCoast = stop is not null;
        _pressOnClose = region.CloseButton?.Frame.Contains(input.Location) ?? false;
        _held = false;
        _phase = PhaseKind.Pending;
        _pendingSince = input.Time;
        _origin = input.Location;
        _last = input.Location;
        _samples ??= new List<(double, PointD)>(32);
        _samples.Clear();
        _samples.Add((input.Time, input.Location));
        _wheel = default;
        return stop is null ? TouchResponse.SwallowEmpty : TouchResponse.Swallow(stop);
    }

    private TouchResponse Drag(TouchInput input, TouchOptions options)
    {
        switch (_phase)
        {
            case PhaseKind.Idle:
            case PhaseKind.Selecting:
                return TouchResponse.Pass;
            case PhaseKind.Pending:
                if (_origin.DistanceTo(input.Location) < Slop * _scale)
                    return TouchResponse.SwallowEmpty;
                if (_held || input.Time - _pendingSince >= HoldDuration || !options.AllowScroll)
                {
                    _phase = PhaseKind.Selecting;
                    return TouchResponse.Swallow(new TouchCommand.ReplayPress(), new TouchCommand.ReplayCurrent());
                }
                _phase = PhaseKind.Scrolling;
                var began = _wheel.Step(0, 0, ScrollPhase.Began, MomentumPhase.None, _origin);
                return TouchResponse.Swallow(
                    new TouchCommand.Scroll(began),
                    new TouchCommand.Scroll(ScrollStepTo(input)));
            case PhaseKind.Scrolling:
                return TouchResponse.Swallow(new TouchCommand.Scroll(ScrollStepTo(input)));
            default:
                return TouchResponse.Pass;
        }
    }

    private TouchResponse Release(TouchInput input, TouchOptions options)
    {
        switch (_phase)
        {
            case PhaseKind.Idle:
                _phase = PhaseKind.Idle;
                return TouchResponse.Pass;
            case PhaseKind.Selecting:
                _phase = PhaseKind.Idle;
                return options.NotifySelectionEnded && _origin.DistanceTo(input.Location) >= Slop * _scale
                    ? new TouchResponse(true, new[] { new TouchCommand.SelectionCompleted(input.Location) })
                    : TouchResponse.Pass;
            case PhaseKind.Pending:
                _phase = PhaseKind.Idle;
                if ((_held || input.Time - _pendingSince >= HoldDuration) && options.AllowRightClick)
                    return TouchResponse.Swallow(new TouchCommand.RightClick(_origin));
                if (_pressStoppedCoast)
                    return TouchResponse.SwallowEmpty;
                return _pressOnClose
                    ? TouchResponse.Swallow(new TouchCommand.CloseWindow(_origin))
                    : TouchResponse.Swallow(new TouchCommand.ReplayPress(), new TouchCommand.ReplayCurrent());
            case PhaseKind.Scrolling:
                _phase = PhaseKind.Idle;
                var ended = _wheel.Step(0, 0, ScrollPhase.Ended, MomentumPhase.None, _origin);
                var (vx, vy) = FlingVelocity(input.Time);
                _momentum = Momentum.TryCreate(vx, vy, _scale);
                return TouchResponse.Swallow(new TouchCommand.Scroll(ended));
            default:
                _phase = PhaseKind.Idle;
                return TouchResponse.Pass;
        }
    }

    private TouchResponse Recognize(TouchGesture gesture)
    {
        if (_phase != PhaseKind.Pending) return TouchResponse.Pass;
        switch (gesture)
        {
            case TouchGesture.Hold:
                _held = true;
                return TouchResponse.SwallowEmpty;
            case TouchGesture.Slide:
                _phase = PhaseKind.Scrolling;
                var began = _wheel.Step(0, 0, ScrollPhase.Began, MomentumPhase.None, _origin);
                return TouchResponse.Swallow(new TouchCommand.Scroll(began));
            default:
                return TouchResponse.Pass;
        }
    }

    private ScrollStep ScrollStepTo(TouchInput input)
    {
        double dx = input.Location.X - _last.X;
        double dy = input.Location.Y - _last.Y;
        _last = input.Location;
        _samples ??= new List<(double, PointD)>(32);
        _samples.Add((input.Time, input.Location));
        if (_samples.Count > 30)
            _samples.RemoveRange(0, _samples.Count - 30);
        return _wheel.Step(dx, dy, ScrollPhase.Changed, MomentumPhase.None, _origin);
    }

    private TouchCommand? StopMomentum()
    {
        if (_momentum is null) return null;
        _momentum = null;
        return new TouchCommand.Scroll(_wheel.Step(0, 0, ScrollPhase.None, MomentumPhase.End, _origin));
    }

    private (double Vx, double Vy) FlingVelocity(double time)
    {
        if (_samples is null || _samples.Count < 2) return (0, 0);

        int firstRecent = -1;
        for (int i = 0; i < _samples.Count; i++)
        {
            if (time - _samples[i].Time <= FlingWindow)
            {
                firstRecent = i;
                break;
            }
        }
        if (firstRecent < 0 || firstRecent >= _samples.Count - 1) return (0, 0);

        var a = _samples[firstRecent];
        var b = _samples[^1];
        if (b.Time <= a.Time) return (0, 0);

        if (b.Time - a.Time < MinFlingSpan)
        {
            a = _samples[0];
            for (int i = _samples.Count - 1; i >= 0; i--)
            {
                if (b.Time - _samples[i].Time >= MinFlingSpan)
                {
                    a = _samples[i];
                    break;
                }
            }
        }

        double dt = b.Time - a.Time;
        if (dt <= 0) return (0, 0);
        double vx = (b.Point.X - a.Point.X) / dt;
        double vy = (b.Point.Y - a.Point.Y) / dt;
        double speed = MathEx.Hypot(vx, vy);
        double cap = MaxFlingSpeed * _scale;
        if (speed > cap)
        {
            vx = vx / speed * cap;
            vy = vy / speed * cap;
        }
        return (vx, vy);
    }
}
