using System;
using System.Collections.Generic;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Touch;

public enum PointerEventKind
{
    Move,
    LeftDown,
    LeftDrag,
    LeftUp
}

public readonly record struct PointerEvent(PointerEventKind Kind, PointD Location, double Time);

public sealed record Normalized(IReadOnlyList<TouchInput> Before, TouchInput? Current)
{
    public static readonly Normalized Ignored = new(Array.Empty<TouchInput>(), null);

    public Normalized(TouchInput? current)
        : this(Array.Empty<TouchInput>(), current)
    {
    }
}

public interface ITouchNormalizer
{
    IReadOnlySet<PointerEventKind> Consumes { get; }
    Normalized Normalize(PointerEvent ev);
    double? Deadline => null;
    IReadOnlyList<TouchInput> Tick(double time) => Array.Empty<TouchInput>();
}

public sealed class DirectTouch : ITouchNormalizer
{
    private static readonly IReadOnlySet<PointerEventKind> ConsumedKinds = new HashSet<PointerEventKind>
    {
        PointerEventKind.LeftDown,
        PointerEventKind.LeftDrag,
        PointerEventKind.LeftUp
    };

    public IReadOnlySet<PointerEventKind> Consumes => ConsumedKinds;

    public Normalized Normalize(PointerEvent ev) => ev.Kind switch
    {
        PointerEventKind.LeftDown => new Normalized(new TouchInput(TouchInputKind.Press, ev.Location, ev.Time)),
        PointerEventKind.LeftDrag => new Normalized(new TouchInput(TouchInputKind.Drag, ev.Location, ev.Time)),
        PointerEventKind.LeftUp => new Normalized(new TouchInput(TouchInputKind.Release, ev.Location, ev.Time)),
        _ => Normalized.Ignored
    };
}

/// <summary>
/// Validates standard finger input grammar emitted by a <see cref="ITouchNormalizer"/>:
/// Press, zero or more Drags, at most one Recognized gesture before Release, non-overlapping gestures,
/// and non-decreasing timestamps. Synthesized presses must immediately be followed by Recognized(Slide),
/// and synthesized releases may only close a Slide.
/// </summary>
public struct TouchGrammar
{
    private bool _pressed;
    private TouchGesture? _recognized;
    private bool _synthesizedPress;
    private double _lastTime = double.NegativeInfinity;

    public TouchGrammar()
    {
    }

    public string? Check(TouchInput input, bool synthesized)
    {
        double prev = _lastTime;
        _lastTime = Math.Max(_lastTime, input.Time);
        if (input.Time < prev)
            return $"时间倒退：{input.Kind} @{input.Time} 早于 {prev}";

        if (_synthesizedPress)
        {
            _synthesizedPress = false;
            if (input.Kind != TouchInputKind.Recognized || input.Gesture != TouchGesture.Slide)
                return $"补出来的按下后面不是「认出滑动」 @{input.Time}：点击、长按要靠原事件补发";
        }

        switch (input.Kind)
        {
            case TouchInputKind.Press:
                if (_pressed)
                    return $"上一下还没抬起又按下 @{input.Time}";
                _pressed = true;
                _recognized = null;
                _synthesizedPress = synthesized;
                break;

            case TouchInputKind.Drag:
                if (!_pressed)
                    return $"没按下就拖动 @{input.Time}";
                break;

            case TouchInputKind.Recognized:
                if (!_pressed)
                    return $"没按下就认出手势 @{input.Time}";
                if (_recognized is not null)
                    return $"一下里认出了两次手势 @{input.Time}";
                _recognized = input.Gesture;
                break;

            case TouchInputKind.Release:
                if (!_pressed)
                    return $"没按下就抬起 @{input.Time}";
                _pressed = false;
                if (synthesized && _recognized != TouchGesture.Slide)
                    return $"补出来的抬起收尾的不是滑动 @{input.Time}：点击、长按、选择要靠原事件补发";
                break;
        }

        return null;
    }
}
