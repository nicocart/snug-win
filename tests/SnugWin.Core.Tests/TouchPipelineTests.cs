using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;
using SnugWin.Windows.Input;
using SnugWin.Windows.Native;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class TouchPipelineTests
{
    private static readonly TouchRegion Phone = new(
        new RectD(0, 0, 540, 950),
        1.0,
        new CloseButton(new RectD(8, 30, 14, 14), 7, new RectD(0, 22, 540, 900)));

    private static readonly TouchRegion Elsewhere = new(new RectD(2000, 0, 540, 950), 1.0);

    private static PointerEvent Ev(PointerEventKind kind, double x, double y, double t) =>
        new(kind, new PointD(x, y), t);

    private static TouchInput In(TouchInputKind kind, double x, double y, double t) =>
        new(kind, new PointD(x, y), t);

    private static IReadOnlyList<ScrollPhase> ScrollPhases(TouchStep step) =>
        step.Actions
            .OfType<TouchAction.Perform>()
            .Select(p => p.Command)
            .OfType<TouchCommand.Scroll>()
            .Select(s => s.Step.Phase)
            .ToList();

    /// <summary>
    /// Fake normalizer that synthesizes a slide from unpressed moves and closes it 0.3s after the last move.
    /// </summary>
    private sealed class SlideFromMoves : ITouchNormalizer
    {
        private static readonly IReadOnlySet<PointerEventKind> Consumed = new HashSet<PointerEventKind> { PointerEventKind.Move };
        private PointerEvent? _last;

        public IReadOnlySet<PointerEventKind> Consumes => Consumed;
        public double? Deadline => _last is { } l ? l.Time + 0.3 : null;

        public Normalized Normalize(PointerEvent ev)
        {
            var prev = _last;
            _last = ev;
            if (prev is null)
            {
                return new Normalized(
                    [
                        In(TouchInputKind.Press, ev.Location.X, ev.Location.Y, ev.Time),
                        TouchInput.Recognized(TouchGesture.Slide, ev.Location, ev.Time)
                    ],
                    null);
            }
            return new Normalized(In(TouchInputKind.Drag, ev.Location.X, ev.Location.Y, ev.Time));
        }

        public IReadOnlyList<TouchInput> Tick(double time)
        {
            if (_last is not { } l) return Array.Empty<TouchInput>();
            _last = null;
            return [In(TouchInputKind.Release, l.Location.X, l.Location.Y, l.Time)];
        }
    }

    private sealed class IgnoresAll : ITouchNormalizer
    {
        private static readonly IReadOnlySet<PointerEventKind> Consumed = new HashSet<PointerEventKind>
        {
            PointerEventKind.LeftDown,
            PointerEventKind.LeftUp
        };

        public IReadOnlySet<PointerEventKind> Consumes => Consumed;
        public Normalized Normalize(PointerEvent ev) => Normalized.Ignored;
    }

    private sealed class TapFromMove : ITouchNormalizer
    {
        private static readonly IReadOnlySet<PointerEventKind> Consumed = new HashSet<PointerEventKind> { PointerEventKind.Move };
        public IReadOnlySet<PointerEventKind> Consumes => Consumed;

        public Normalized Normalize(PointerEvent ev) =>
            new(
                [
                    In(TouchInputKind.Press, ev.Location.X, ev.Location.Y, ev.Time),
                    In(TouchInputKind.Release, ev.Location.X, ev.Location.Y, ev.Time)
                ],
                null);
    }

    [Fact]
    public void KindsOf_UnionsNormalizerConsumedKinds()
    {
        var direct = TouchPipeline.KindsOf([new DirectTouch()]);
        Assert.Equal(3, direct.Count);
        Assert.Contains(PointerEventKind.LeftDown, direct);
        Assert.Contains(PointerEventKind.LeftDrag, direct);
        Assert.Contains(PointerEventKind.LeftUp, direct);

        var both = TouchPipeline.KindsOf([new DirectTouch(), new SlideFromMoves()]);
        Assert.Equal(4, both.Count);
        Assert.Contains(PointerEventKind.Move, both);
        Assert.Contains(PointerEventKind.LeftDown, both);
        Assert.Contains(PointerEventKind.LeftDrag, both);
        Assert.Contains(PointerEventKind.LeftUp, both);
    }

    [Fact]
    public void DirectTouch_TranslatesLeftButtonEventsAndIgnoresMove()
    {
        ITouchNormalizer dt = new DirectTouch();
        Assert.Equal(Normalized.Ignored, dt.Normalize(Ev(PointerEventKind.Move, 10, 20, 0.1)));
        Assert.Equal(In(TouchInputKind.Press, 10, 20, 0.2), dt.Normalize(Ev(PointerEventKind.LeftDown, 10, 20, 0.2)).Current);
        Assert.Equal(In(TouchInputKind.Drag, 15, 25, 0.3), dt.Normalize(Ev(PointerEventKind.LeftDrag, 15, 25, 0.3)).Current);
        Assert.Equal(In(TouchInputKind.Release, 15, 25, 0.4), dt.Normalize(Ev(PointerEventKind.LeftUp, 15, 25, 0.4)).Current);
        Assert.Null(dt.Deadline);
        Assert.Empty(dt.Tick(1.0));
    }

    [Fact]
    public void TouchGrammar_ValidatesValidAndInvalidSequences()
    {
        var g = new TouchGrammar();
        Assert.Null(g.Check(In(TouchInputKind.Press, 10, 10, 0.0), synthesized: false));
        Assert.Null(g.Check(In(TouchInputKind.Drag, 12, 12, 0.1), synthesized: false));
        Assert.Null(g.Check(TouchInput.Recognized(TouchGesture.Hold, new PointD(12, 12), 0.15), synthesized: true));
        Assert.Null(g.Check(In(TouchInputKind.Release, 12, 12, 0.2), synthesized: false));

        // Synthesized slide sequence
        Assert.Null(g.Check(In(TouchInputKind.Press, 10, 10, 0.3), synthesized: true));
        Assert.Null(g.Check(TouchInput.Recognized(TouchGesture.Slide, new PointD(10, 10), 0.3), synthesized: true));
        Assert.Null(g.Check(In(TouchInputKind.Drag, 10, 40, 0.35), synthesized: false));
        Assert.Null(g.Check(In(TouchInputKind.Release, 10, 40, 0.65), synthesized: true));

        // Synthesized press not followed by Slide -> error
        var bad = new TouchGrammar();
        Assert.Null(bad.Check(In(TouchInputKind.Press, 10, 10, 0.0), synthesized: true));
        Assert.NotNull(bad.Check(In(TouchInputKind.Drag, 10, 20, 0.1), synthesized: false));

        // Synthesized release without Slide -> error
        var badRel = new TouchGrammar();
        Assert.Null(badRel.Check(In(TouchInputKind.Press, 10, 10, 0.0), synthesized: false));
        Assert.NotNull(badRel.Check(In(TouchInputKind.Release, 10, 10, 0.1), synthesized: true));
    }

    [Fact]
    public void Pipeline_UnconsumedInvalidSourceOrIgnored_PassesThrough()
    {
        var p = new TouchPipeline([() => new DirectTouch(), () => new IgnoresAll()]);

        Assert.Equal(TouchStep.Pass, p.Take(Ev(PointerEventKind.Move, 100, 300, 0), 0, [Phone]));
        Assert.Equal(TouchStep.Pass, p.Take(Ev(PointerEventKind.LeftDown, 100, 300, 0), 5, [Phone]));
        Assert.Equal(TouchStep.Pass, p.Take(Ev(PointerEventKind.LeftDown, 100, 300, 0), 1, [Phone]));
        Assert.Equal(TouchStep.Pass, p.Take(Ev(PointerEventKind.LeftUp, 100, 300, 0.1), 1, [Phone]));

        var down = p.Take(Ev(PointerEventKind.LeftDown, 100, 300, 1.0), 0, [Phone]);
        Assert.False(down.PassOriginal);
        Assert.IsType<TouchAction.HoldOriginal>(Assert.Single(down.Actions));
    }

    [Fact]
    public void Pipeline_TapOrder_HoldsPressReplaysThenDropsHeld()
    {
        var p = new TouchPipeline([() => new DirectTouch()]);

        var down = p.Take(Ev(PointerEventKind.LeftDown, 100, 300, 0), 0, [Phone]);
        Assert.False(down.PassOriginal);
        Assert.IsType<TouchAction.HoldOriginal>(Assert.Single(down.Actions));

        var up = p.Take(Ev(PointerEventKind.LeftUp, 100, 300, 0.1), 0, [Phone]);
        Assert.False(up.PassOriginal);
        Assert.Equal(3, up.Actions.Count);
        var a0 = Assert.IsType<TouchAction.Perform>(up.Actions[0]);
        Assert.IsType<TouchCommand.ReplayPress>(a0.Command);
        Assert.Equal(Phone, a0.Region);
        var a1 = Assert.IsType<TouchAction.Perform>(up.Actions[1]);
        Assert.IsType<TouchCommand.ReplayCurrent>(a1.Command);
        Assert.IsType<TouchAction.DropHeld>(up.Actions[2]);

        // Outside region passes through and drops held
        Assert.Equal(TouchStep.Pass, p.Take(Ev(PointerEventKind.LeftDown, 900, 300, 1), 0, [Phone]));
        var upOut = p.Take(Ev(PointerEventKind.LeftUp, 900, 300, 1.1), 0, [Phone]);
        Assert.True(upOut.PassOriginal);
        Assert.IsType<TouchAction.DropHeld>(Assert.Single(upOut.Actions));
    }

    [Fact]
    public void Pipeline_TickAndLateEvent_CloseSlideAtDeadline()
    {
        var p = new TouchPipeline([() => new SlideFromMoves()]);

        var s1 = p.Take(Ev(PointerEventKind.Move, 100, 300, 0), 0, [Phone]);
        Assert.Equal([ScrollPhase.Began], ScrollPhases(s1));
        Assert.Equal(0.3, p.Deadline);

        var s2 = p.Take(Ev(PointerEventKind.Move, 100, 340, 0.05), 0, [Phone]);
        Assert.Equal([ScrollPhase.Changed], ScrollPhases(s2));
        Assert.Equal(0.35, p.Deadline!.Value, 6);

        // Before deadline -> nothing
        Assert.Equal(TouchStep.Pass, p.Tick(0.2, [Phone]));

        // At deadline -> closes slide
        var sEnd = p.Tick(0.35, [Phone]);
        Assert.Equal([ScrollPhase.Ended], ScrollPhases(sEnd));
        Assert.Null(p.Deadline);

        // Late event after deadline flushes old slide before starting new one
        var p2 = new TouchPipeline([() => new SlideFromMoves()]);
        p2.Take(Ev(PointerEventKind.Move, 100, 300, 0), 0, [Phone]);
        var late = p2.Take(Ev(PointerEventKind.Move, 100, 350, 0.5), 0, [Phone]);
        Assert.Equal([ScrollPhase.Ended, ScrollPhase.Began], ScrollPhases(late));
    }

    [Fact]
    public void Pipeline_Reset_ClearsNormalizerDeadlinesAndTranslatorMomentum()
    {
        var p = new TouchPipeline([() => new SlideFromMoves()]);
        p.Take(Ev(PointerEventKind.Move, 100, 300, 0), 0, [Phone]);
        p.Take(Ev(PointerEventKind.Move, 100, 380, 0.03), 0, [Phone]);
        p.Tick(0.4, [Phone]);
        Assert.True(p.IsCoasting);

        p.Take(Ev(PointerEventKind.Move, 100, 300, 0.5), 0, [Phone]);
        Assert.NotNull(p.Deadline);

        p.Reset();
        Assert.Null(p.Deadline);
        Assert.False(p.IsCoasting);
        Assert.Null(p.MomentumFrame());
    }

    [Fact]
    public void Pipeline_SkipsIllegalSynthesizedReplayCommands()
    {
        var p = new TouchPipeline([() => new TapFromMove()]);
        var step = p.Take(Ev(PointerEventKind.Move, 100, 300, 0), 0, [Phone]);
        Assert.True(step.PassOriginal);
        Assert.Empty(step.Actions);
    }

    [Fact]
    public void Translator_RecognizedHold_SelectsOnDragAndRightClicksOnRelease()
    {
        var t = new TouchTranslator();
        t.Handle(In(TouchInputKind.Press, 100, 300, 0), Phone);
        Assert.Equal(TouchResponse.SwallowEmpty, t.Handle(TouchInput.Recognized(TouchGesture.Hold, new PointD(100, 300), 0.01), Phone));
        Assert.Equal(TouchResponse.SwallowEmpty, t.Handle(In(TouchInputKind.Drag, 104, 303, 0.03), Phone));

        var drag = t.Handle(In(TouchInputKind.Drag, 100, 330, 0.05), Phone);
        Assert.False(drag.PassOriginal);
        Assert.IsType<TouchCommand.ReplayPress>(drag.Commands[0]);
        Assert.IsType<TouchCommand.ReplayCurrent>(drag.Commands[1]);

        var selEnd = t.Handle(
            In(TouchInputKind.Release, 100, 360, 0.1),
            Phone,
            new TouchOptions(NotifySelectionEnded: true));
        Assert.True(selEnd.PassOriginal);
        Assert.IsType<TouchCommand.SelectionCompleted>(Assert.Single(selEnd.Commands));

        // Hold without drag -> right click even on close button and before 0.5s
        var t2 = new TouchTranslator();
        var closePt = new PointD(15, 37);
        t2.Handle(In(TouchInputKind.Press, closePt.X, closePt.Y, 0), Phone);
        t2.Handle(TouchInput.Recognized(TouchGesture.Hold, closePt, 0.05), Phone);
        var rel = t2.Handle(In(TouchInputKind.Release, closePt.X, closePt.Y, 0.1), Phone);
        var rc = Assert.IsType<TouchCommand.RightClick>(Assert.Single(rel.Commands));
        Assert.Equal(closePt, rc.At);

        // AllowRightClick = false falls back to normal close/replay behavior
        var t3 = new TouchTranslator();
        t3.Handle(In(TouchInputKind.Press, 100, 300, 0), Phone);
        t3.Handle(TouchInput.Recognized(TouchGesture.Hold, new PointD(100, 300), 0.05), Phone);
        var noRc = t3.Handle(In(TouchInputKind.Release, 100, 300, 0.1), Phone, new TouchOptions(AllowRightClick: false));
        Assert.IsType<TouchCommand.ReplayPress>(noRc.Commands[0]);
        Assert.IsType<TouchCommand.ReplayCurrent>(noRc.Commands[1]);
    }

    [Fact]
    public void Translator_RecognizedSlide_ScrollsImmediatelyAndCoasts()
    {
        var t = new TouchTranslator();
        Assert.Equal(TouchResponse.SwallowEmpty, t.Handle(In(TouchInputKind.Press, 100, 300, 0), Phone));
        var beginResp = t.Handle(TouchInput.Recognized(TouchGesture.Slide, new PointD(100, 300), 0), Phone);
        Assert.Equal(ScrollPhase.Began, Assert.IsType<TouchCommand.Scroll>(Assert.Single(beginResp.Commands)).Step.Phase);

        var slow = t.Handle(In(TouchInputKind.Drag, 100, 303, 0.8), Phone);
        Assert.False(slow.PassOriginal);
        Assert.Equal(3, Assert.IsType<TouchCommand.Scroll>(Assert.Single(slow.Commands)).Step.PixelsY);

        t.Handle(In(TouchInputKind.Drag, 100, 340, 0.81), Phone);
        t.Handle(In(TouchInputKind.Drag, 100, 380, 0.826), Phone);
        t.Handle(In(TouchInputKind.Drag, 100, 420, 0.842), Phone);
        var endResp = t.Handle(In(TouchInputKind.Release, 100, 420, 0.85), Phone);
        Assert.Equal(ScrollPhase.Ended, Assert.IsType<TouchCommand.Scroll>(Assert.Single(endResp.Commands)).Step.Phase);
        Assert.True(t.IsCoasting);

        // Recognized outside Pending or outside region is ignored
        var outside = new TouchTranslator();
        outside.Handle(In(TouchInputKind.Press, 100, 300, 0), Elsewhere);
        Assert.Equal(TouchResponse.Pass, outside.Handle(TouchInput.Recognized(TouchGesture.Slide, new PointD(100, 300), 0), Elsewhere));
    }

    [Fact]
    public void CloseButton_IsFrontmost_IgnoresPointerWindows()
    {
        var cb = new CloseButton(new RectD(8, 30, 14, 14), 7, new RectD(0, 22, 540, 900));
        var pt = new PointD(15, 37);

        Assert.True(cb.IsFrontmost(pt, [(99, new RectD(0, 0, 50, 50), true), (7, new RectD(0, 22, 540, 900), false)]));
        Assert.False(cb.IsFrontmost(pt, [(99, new RectD(0, 0, 50, 50), false), (7, new RectD(0, 22, 540, 900), false)]));
    }

    [Fact]
    public void Pipeline_CrossDeskTap_ReplaysPressAndReleaseInOrder()
    {
        var p = new TouchPipeline([() => new SnugWin.Core.Remotes.CrossDesk.CrossDeskTouch()]);

        var move = p.Take(Ev(PointerEventKind.Move, 100, 300, 1.000), 0, [Phone]);
        Assert.True(move.PassOriginal);

        var down = p.Take(Ev(PointerEventKind.LeftDown, 100, 300, 1.001), 0, [Phone]);
        Assert.False(down.PassOriginal);
        Assert.IsType<TouchAction.HoldOriginal>(Assert.Single(down.Actions));

        var up = p.Take(Ev(PointerEventKind.LeftUp, 100, 300, 1.050), 0, [Phone]);
        Assert.False(up.PassOriginal);
        Assert.Equal(3, up.Actions.Count);
        Assert.IsType<TouchCommand.ReplayPress>(Assert.IsType<TouchAction.Perform>(up.Actions[0]).Command);
        Assert.IsType<TouchCommand.ReplayCurrent>(Assert.IsType<TouchAction.Perform>(up.Actions[1]).Command);
        Assert.IsType<TouchAction.DropHeld>(up.Actions[2]);
    }

    [Fact]
    public void ReplayEchoTracker_PreservesCrossDeskExtraInfoAndConsumesEchoesCleanly()
    {
        const nuint crossDeskExtra = 0x4353444D; // 'CSDM'
        var tracker = new ReplayEchoTracker();

        // Fallback marker when no non-zero dwExtraInfo has been observed
        Assert.Equal(ReplayEchoTracker.FallbackReplayMarker, tracker.CurrentReplayExtraInfo);
        Assert.True(tracker.TryConsumeEcho(Win32.WM_LBUTTONDOWN, 100, 100, ReplayEchoTracker.FallbackReplayMarker, 1000));

        // Incoming CrossDesk event arrives -> preserved for subsequent replays (PrivacyInputGuard compatibility)
        Assert.False(tracker.TryConsumeEcho(Win32.WM_MOUSEMOVE, 3100, 950, crossDeskExtra, 1000));
        tracker.ObserveIncomingExtraInfo(crossDeskExtra);
        Assert.Equal(crossDeskExtra, tracker.CurrentReplayExtraInfo);

        // Expect Down & Up at (3100, 950); Windows quantizes virtual desktop coordinates by ±1px
        Assert.Equal(crossDeskExtra, tracker.ExpectClick(3100, 950, down: true, right: false, 1010));
        Assert.Equal(crossDeskExtra, tracker.ExpectClick(3100, 950, down: false, right: false, 1020));

        // Windows emits optional Move before Down, then Down, then Up (without Move)
        Assert.True(tracker.TryConsumeEcho(Win32.WM_MOUSEMOVE, 3101, 950, crossDeskExtra, 1021));
        Assert.True(tracker.TryConsumeEcho(Win32.WM_LBUTTONDOWN, 3101, 950, crossDeskExtra, 1022));
        Assert.True(tracker.TryConsumeEcho(Win32.WM_LBUTTONUP, 3099, 951, crossDeskExtra, 1030));

        // Subsequent tap at the exact same spot 20ms later must NOT be swallowed as a leftover Move/Down echo
        Assert.False(tracker.TryConsumeEcho(Win32.WM_MOUSEMOVE, 3100, 950, crossDeskExtra, 1050));
        Assert.False(tracker.TryConsumeEcho(Win32.WM_LBUTTONDOWN, 3100, 950, crossDeskExtra, 1051));

        // Reset clears preserved dwExtraInfo
        tracker.Reset();
        Assert.Equal(ReplayEchoTracker.FallbackReplayMarker, tracker.CurrentReplayExtraInfo);
    }
}
