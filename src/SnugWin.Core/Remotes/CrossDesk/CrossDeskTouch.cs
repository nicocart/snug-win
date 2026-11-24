using System;
using System.Collections.Generic;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;

namespace SnugWin.Core.Remotes.CrossDesk;

/// <summary>
/// CrossDesk mouse-event normalizer: converts CrossDesk's absolute-mouse touch stream
/// (move-only slides, move+down+up taps, 0.35s client long-press) into standard finger inputs
/// for <see cref="TouchTranslator"/>, while passing relative-mouse cursor feeds and virtual mouse bar
/// clicks through untouched. Faithful port of upstream <c>CrossDeskTouch.swift</c>.
/// </summary>
public sealed class CrossDeskTouch : ITouchNormalizer
{
    public enum FeedKind
    {
        Unknown,
        Finger,
        Cursor
    }

    public const double ClientHoldDelay = 0.35;
    public const double HoldAfterDown = TouchTranslator.HoldDuration - ClientHoldDelay; // 0.15s
    public const double TouchMoveLead = 0.02;
    public const double SlideEnd = 0.3;
    public const double LandingGap = 0.15;
    public const double LandingJump = 80.0;
    public const double PauseGap = 0.06;
    public const double GestureGap = 0.25;
    public const double VoteWindow = 10.0;
    public const double SamePlace = 0.5;
    public const int CursorVotes = 3;

    private static readonly IReadOnlySet<PointerEventKind> ConsumedKinds = new HashSet<PointerEventKind>
    {
        PointerEventKind.Move,
        PointerEventKind.LeftDown,
        PointerEventKind.LeftDrag,
        PointerEventKind.LeftUp
    };

    private bool _hasPress;
    private bool _pressIsVirtual;
    private PointerEvent _pressDown;
    private bool _pressHeld;

    private PointerEvent? _slide;
    private PointerEvent? _paused;
    private PointerEvent? _candidate;
    private PointerEvent? _last;
    private int _cursorRun;

    public FeedKind Feed { get; private set; } = FeedKind.Unknown;

    public IReadOnlySet<PointerEventKind> Consumes => ConsumedKinds;

    public double? Deadline
    {
        get
        {
            if (_paused is { } p) return p.Time + TouchMoveLead;
            if (_slide is { } s) return s.Time + SlideEnd;
            return null;
        }
    }

    public IReadOnlyList<TouchInput> Tick(double time)
    {
        var inputs = new List<TouchInput>();
        if (_paused is { } p && time >= p.Time + TouchMoveLead)
        {
            _paused = null;
            _slide = p;
            inputs.Add(new TouchInput(TouchInputKind.Drag, p.Location, p.Time));
        }

        if (_slide is { } s && time >= s.Time + SlideEnd)
        {
            _slide = null;
            inputs.Add(new TouchInput(TouchInputKind.Release, s.Location, s.Time));
        }

        return inputs;
    }

    public Normalized Normalize(PointerEvent ev)
    {
        try
        {
            var before = SettlePaused(ev);
            Vote(ev);

            switch (ev.Kind)
            {
                case PointerEventKind.Move:
                    return HandleMove(ev, before);

                case PointerEventKind.LeftDown:
                    if (_slide is { } s)
                    {
                        before.Add(new TouchInput(TouchInputKind.Release, s.Location, ev.Time));
                        _slide = null;
                    }
                    _candidate = null;

                    if (_last is { } l
                        && l.Kind == PointerEventKind.Move
                        && Distance(l.Location, ev.Location) <= SamePlace
                        && ev.Time - l.Time <= TouchMoveLead)
                    {
                        _hasPress = true;
                        _pressIsVirtual = false;
                        _pressDown = ev;
                        _pressHeld = false;
                        return new Normalized(before, new TouchInput(TouchInputKind.Press, ev.Location, ev.Time));
                    }

                    _hasPress = true;
                    _pressIsVirtual = true;
                    return new Normalized(before, null);

                case PointerEventKind.LeftDrag:
                    if (!_hasPress || _pressIsVirtual)
                        return new Normalized(before, null);

                    // Windows SendInput(MOUSEEVENTF_MOVE | LEFTUP) emits a 0-pixel WM_MOUSEMOVE
                    // right before WM_LBUTTONUP; do not treat stationary pointer noise as a drag/Hold.
                    if (!_pressHeld && Distance(ev.Location, _pressDown.Location) <= SamePlace)
                        return new Normalized(before, null);

                    if (!_pressHeld)
                    {
                        _pressHeld = true;
                        before.Add(TouchInput.Recognized(TouchGesture.Hold, ev.Location, ev.Time));
                    }
                    return new Normalized(before, new TouchInput(TouchInputKind.Drag, ev.Location, ev.Time));

                case PointerEventKind.LeftUp:
                    try
                    {
                        if (!_hasPress || _pressIsVirtual)
                            return new Normalized(before, null);

                        if (!_pressHeld && ev.Time - _pressDown.Time >= HoldAfterDown)
                            before.Add(TouchInput.Recognized(TouchGesture.Hold, ev.Location, ev.Time));

                        return new Normalized(before, new TouchInput(TouchInputKind.Release, ev.Location, ev.Time));
                    }
                    finally
                    {
                        _hasPress = false;
                        _pressIsVirtual = false;
                        _pressHeld = false;
                    }

                default:
                    return new Normalized(before, null);
            }
        }
        finally
        {
            _last = ev;
        }
    }

    private Normalized HandleMove(PointerEvent ev, List<TouchInput> before)
    {
        _hasPress = false;
        _pressIsVirtual = false;
        _pressHeld = false;

        if (Feed != FeedKind.Finger)
        {
            _slide = null;
            _candidate = null;
            return new Normalized(before, null);
        }

        if (_slide is { } s)
        {
            if (Landed(ev, s))
            {
                before.Add(new TouchInput(TouchInputKind.Release, s.Location, ev.Time));
                _slide = null;
                _candidate = ev;
                return new Normalized(before, null);
            }

            if (ev.Time - s.Time >= PauseGap)
            {
                _paused = ev;
                return new Normalized(before, new TouchInput(TouchInputKind.Drag, s.Location, ev.Time));
            }

            _slide = ev;
            return new Normalized(before, new TouchInput(TouchInputKind.Drag, ev.Location, ev.Time));
        }

        if (_candidate is { } c && ev.Time - c.Time < SlideEnd && !Landed(ev, c))
        {
            _candidate = null;
            _slide = ev;
            before.Add(new TouchInput(TouchInputKind.Press, c.Location, c.Time));
            before.Add(TouchInput.Recognized(TouchGesture.Slide, c.Location, c.Time));
            return new Normalized(before, new TouchInput(TouchInputKind.Drag, ev.Location, ev.Time));
        }

        _candidate = ev;
        return new Normalized(before, null);
    }

    private List<TouchInput> SettlePaused(PointerEvent ev)
    {
        var list = new List<TouchInput>();
        if (_paused is not { } p)
            return list;

        _paused = null;
        if (ev.Kind == PointerEventKind.LeftDown
            && Distance(p.Location, ev.Location) <= SamePlace
            && ev.Time - p.Time <= TouchMoveLead)
        {
            return list;
        }

        _slide = p;
        list.Add(new TouchInput(TouchInputKind.Drag, p.Location, p.Time));
        return list;
    }

    private void Vote(PointerEvent ev)
    {
        if (ev.Kind != PointerEventKind.Move || _hasPress || _slide is not null || _last is not { } l)
            return;

        double gap = ev.Time - l.Time;
        if (gap < GestureGap || gap > VoteWindow)
            return;

        double d = Distance(l.Location, ev.Location);
        if (d <= SamePlace)
        {
            _cursorRun++;
            if (_cursorRun >= CursorVotes)
                Feed = FeedKind.Cursor;
        }
        else
        {
            _cursorRun = 0;
            if (d >= LandingJump)
                Feed = FeedKind.Finger;
        }
    }

    private static bool Landed(PointerEvent ev, PointerEvent previous) =>
        ev.Time - previous.Time >= LandingGap && Distance(ev.Location, previous.Location) >= LandingJump;

    private static double Distance(PointD a, PointD b) => a.DistanceTo(b);
}
