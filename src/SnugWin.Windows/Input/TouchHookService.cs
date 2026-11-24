using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;
using SnugWin.Windows.Native;
using SnugWin.Windows.Windows;

namespace SnugWin.Windows.Input;

public sealed class TouchHookService : IDisposable
{
    private const double WheelScaleFactor = 2.5; // 120 / 48 DIPs -> ~1:1 touch scroll tracking
    private const int MaxClickDiagCount = 5;

    private readonly object _gate = new();
    private readonly long _startTick = Stopwatch.GetTimestamp();
    private readonly ReplayEchoTracker _echoes = new();

    private IReadOnlyList<TouchRegion> _regions = Array.Empty<TouchRegion>();
    private IReadOnlyList<Func<ITouchNormalizer>> _normalizerFactories = [static () => new DirectTouch()];
    private TouchPipeline _pipeline = new();
    private int _activeSource;
    private bool _injectedLeftDown;
    private PointD _injectedLeftDownPoint;
    private long _injectedLeftDownTickMs;
    private TouchOptions _options = TouchOptions.Default;
    private PointD _heldPressPoint;
    private bool _passthroughBrowserChrome;
    private bool _requireInjectedFlag = true;
    private bool _loggedFirstFlag;
    private int _clickDiagCount;
    private int _activeDiagClickNo;
    private int _replayDiagCount;
    private nint _bubbleHwnd;
    private readonly Dictionary<int, string> _pidExeCache = new();

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private Win32.LowLevelMouseProc? _hookProc; // keep delegate alive for GC
    private System.Threading.Timer? _coastTimer;
    private System.Threading.Timer? _deadlineTimer;
    private int _coastingActive;
    private BlockingCollection<Action>? _replayQueue;
    private Thread? _replayThread;

    public event Action<ulong>? CloseWindowRequested;
    public event Action<PointD>? SelectionCompleted;
    public event Action? SelectionDismissed;

    public bool RequireInjectedFlag
    {
        get { lock (_gate) return _requireInjectedFlag; }
        set { lock (_gate) _requireInjectedFlag = value; }
    }

    public TouchOptions Options
    {
        get { lock (_gate) return _options; }
        set { lock (_gate) _options = value; }
    }

    public bool IsRunning => _hookHandle != IntPtr.Zero;

    public void SetSelectionBubbleHandle(IntPtr hwnd) => Volatile.Write(ref _bubbleHwnd, hwnd);

    public void SetNormalizers(IReadOnlyList<Func<ITouchNormalizer>> factories)
    {
        bool coasting;
        double? deadline;
        lock (_gate)
        {
            _normalizerFactories = factories is { Count: > 0 }
                ? factories.ToArray()
                : [static () => new DirectTouch()];
            _pipeline = new TouchPipeline(_normalizerFactories);
            if (_activeSource < 0 || _activeSource >= _normalizerFactories.Count)
                _activeSource = 0;
            _injectedLeftDown = false;
            _heldPressPoint = default;
            _passthroughBrowserChrome = false;
            coasting = _pipeline.IsCoasting;
            deadline = _pipeline.Deadline;
        }
        UpdateCoastTimer(coasting);
        UpdateDeadlineTimer(deadline);
    }

    public void SetActiveSource(int sourceIndex)
    {
        lock (_gate)
        {
            _activeSource = sourceIndex >= 0 && sourceIndex < _normalizerFactories.Count ? sourceIndex : 0;
        }
    }

    public void RemoteEnded()
    {
        bool coasting;
        double? deadline;
        lock (_gate)
        {
            _pipeline.Reset();
            _echoes.Reset();
            _injectedLeftDown = false;
            _injectedLeftDownPoint = default;
            _injectedLeftDownTickMs = 0;
            _heldPressPoint = default;
            _passthroughBrowserChrome = false;
            _clickDiagCount = 0;
            _activeDiagClickNo = 0;
            Interlocked.Exchange(ref _replayDiagCount, 0);
            coasting = _pipeline.IsCoasting;
            deadline = _pipeline.Deadline;
        }
        UpdateCoastTimer(coasting);
        UpdateDeadlineTimer(deadline);
    }

    public bool SetRegion(TouchRegion? region) =>
        SetRegions(region is null ? Array.Empty<TouchRegion>() : [region]);

    public bool SetRegions(IReadOnlyList<TouchRegion> regions)
    {
        bool empty;
        lock (_gate)
        {
            _regions = regions ?? Array.Empty<TouchRegion>();
            empty = _regions.Count == 0;
            if (empty)
            {
                _pipeline.Reset();
                _echoes.Reset();
                _injectedLeftDown = false;
                _heldPressPoint = default;
                _passthroughBrowserChrome = false;
                _clickDiagCount = 0;
                _activeDiagClickNo = 0;
                Interlocked.Exchange(ref _replayDiagCount, 0);
            }
        }

        if (empty)
        {
            Stop();
            return true;
        }
        return Start();
    }

    private bool Start()
    {
        lock (_gate)
        {
            if (_hookThread is not null) return _hookHandle != IntPtr.Zero;

            using var ready = new ManualResetEventSlim(false);
            bool startedOk = false;

            _hookThread = new Thread(() =>
            {
                TryEnableThreadPerMonitorDpiV2();
                _hookThreadId = Win32.GetCurrentThreadId();
                _hookProc = HookCallback;
                IntPtr mod = Win32.GetModuleHandleW(null);
                _hookHandle = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _hookProc, mod, 0);
                startedOk = _hookHandle != IntPtr.Zero;
                ready.Set();

                if (!startedOk) return;

                while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessageW(ref msg);
                }

                if (_hookHandle != IntPtr.Zero)
                {
                    Win32.UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }
            })
            {
                IsBackground = true,
                Name = "SnugWin.TouchHook",
                Priority = ThreadPriority.Highest
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
            ready.Wait();

            if (!startedOk)
            {
                int err = Marshal.GetLastWin32Error();
                Log.Error($"手势翻译钩子启动失败（Win32 Error={err}）");
                _hookThread = null;
                return false;
            }

            _replayQueue = new BlockingCollection<Action>(256);
            var q = _replayQueue;
            _replayThread = new Thread(() =>
            {
                TryEnableThreadPerMonitorDpiV2();
                foreach (var act in q.GetConsumingEnumerable())
                {
                    try { act(); } catch { }
                }
            })
            {
                IsBackground = true,
                Name = "SnugWin.TouchReplay",
                Priority = ThreadPriority.AboveNormal
            };
            _replayThread.Start();

            _coastTimer = new System.Threading.Timer(OnCoastTick, null, Timeout.Infinite, Timeout.Infinite);
            _deadlineTimer = new System.Threading.Timer(OnDeadlineTick, null, Timeout.Infinite, Timeout.Infinite);
            Log.Info("手势翻译已开启（WH_MOUSE_LL 独立线程）");
            return true;
        }
    }

    private void Stop()
    {
        Thread? threadToJoin;
        Thread? replayToJoin;
        BlockingCollection<Action>? queueToDispose;
        lock (_gate)
        {
            _coastTimer?.Dispose();
            _coastTimer = null;
            _deadlineTimer?.Dispose();
            _deadlineTimer = null;
            Interlocked.Exchange(ref _coastingActive, 0);
            _injectedLeftDown = false;
            _heldPressPoint = default;
            _passthroughBrowserChrome = false;
            threadToJoin = _hookThread;
            _hookThread = null;
            if (_hookThreadId != 0)
            {
                Win32.PostThreadMessageW(_hookThreadId, Win32.WM_QUIT, 0, 0);
                _hookThreadId = 0;
            }
            queueToDispose = _replayQueue;
            _replayQueue = null;
            replayToJoin = _replayThread;
            _replayThread = null;
            queueToDispose?.CompleteAdding();
        }

        if (replayToJoin is not null && Thread.CurrentThread != replayToJoin)
            replayToJoin.Join(500);
        queueToDispose?.Dispose();

        if (threadToJoin is not null && Thread.CurrentThread != threadToJoin)
        {
            threadToJoin.Join(1000);
            Log.Info("手势翻译已关闭");
        }
    }

    private double _scrollRemX;
    private double _scrollRemY;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        if (msg is not (Win32.WM_LBUTTONDOWN or Win32.WM_MOUSEMOVE or Win32.WM_LBUTTONUP))
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
        if (_echoes.TryConsumeEcho(msg, info.pt.X, info.pt.Y, info.dwExtraInfo, Environment.TickCount64))
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        TouchStep step;
        List<(TouchCommand Command, TouchRegion? Region)>? commandsToRun = null;
        bool coasting;
        double? deadline;
        PointD pressOrigin;
        PointD currentPoint = new(info.pt.X, info.pt.Y);
        PointerEventKind evKind;
        bool dismissSelectionBubble = false;
        int diagClickNo = 0;
        long? diagDownDurationMs = null;
        int diagSource = 0;
        string? diagHit = null;
        lock (_gate)
        {
            if (_regions.Count == 0)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            bool injected = (info.flags & Win32.LLMHF_INJECTED) != 0;
            bool inRegion = _regions.Any(r => r.Frame.Contains(currentPoint));
            if (!_loggedFirstFlag && msg == Win32.WM_LBUTTONDOWN && inRegion)
            {
                _loggedFirstFlag = true;
                Log.Info($"触控屏首次按下事件 flags=0x{info.flags:X} (injected={injected}) extra=0x{info.dwExtraInfo:X}");
            }

            if (_requireInjectedFlag && !injected)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            _echoes.ObserveIncomingExtraInfo(info.dwExtraInfo);

            if (msg == Win32.WM_LBUTTONDOWN)
            {
                _injectedLeftDown = true;
                _injectedLeftDownPoint = currentPoint;
                _injectedLeftDownTickMs = Environment.TickCount64;
                evKind = PointerEventKind.LeftDown;
            }
            else if (msg == Win32.WM_LBUTTONUP)
            {
                _injectedLeftDown = false;
                diagDownDurationMs = _injectedLeftDownTickMs > 0
                    ? Environment.TickCount64 - _injectedLeftDownTickMs
                    : null;
                _injectedLeftDownPoint = default;
                _injectedLeftDownTickMs = 0;
                evKind = PointerEventKind.LeftUp;
            }
            else if (_injectedLeftDown)
            {
                // Windows SendInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP) emits a 0-pixel WM_MOUSEMOVE
                // right before WM_LBUTTONUP while _injectedLeftDown is still true. Pass it through
                // without feeding a synthetic LeftDrag that would turn a normal tap into Hold/RightClick.
                if (currentPoint.DistanceTo(_injectedLeftDownPoint) <= 0.5)
                    return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                _injectedLeftDownPoint = currentPoint;
                evKind = PointerEventKind.LeftDrag;
            }
            else
            {
                evKind = PointerEventKind.Move;
            }

            if (evKind == PointerEventKind.LeftDown)
            {
                if (inRegion && _clickDiagCount < MaxClickDiagCount)
                {
                    _activeDiagClickNo = ++_clickDiagCount;
                    diagClickNo = _activeDiagClickNo;
                }
                else
                {
                    _activeDiagClickNo = 0;
                }

                var hitRegion = _regions.FirstOrDefault(r => r.Frame.Contains(currentPoint));
                if (hitRegion is not null)
                {
                    bool isBrowserChrome = false;
                    bool onBubble = IsSelectionBubbleAt(currentPoint, out isBrowserChrome, hitRegion.Scale);
                    dismissSelectionBubble = !onBubble;
                    _passthroughBrowserChrome = onBubble || isBrowserChrome;
                }
                else
                {
                    _passthroughBrowserChrome = false;
                }
            }
            else if (evKind == PointerEventKind.LeftUp && _activeDiagClickNo > 0)
            {
                diagClickNo = _activeDiagClickNo;
                _activeDiagClickNo = 0;
            }

            if (_passthroughBrowserChrome)
            {
                if (evKind == PointerEventKind.LeftUp)
                    _passthroughBrowserChrome = false;
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            double now = Stopwatch.GetElapsedTime(_startTick).TotalSeconds;
            var raw = new PointerEvent(evKind, currentPoint, now);
            diagSource = _activeSource;
            step = _pipeline.Take(raw, _activeSource, _regions, _options);

            pressOrigin = _heldPressPoint;
            foreach (var action in step.Actions)
            {
                switch (action)
                {
                    case TouchAction.HoldOriginal:
                        _heldPressPoint = currentPoint;
                        pressOrigin = currentPoint;
                        break;
                    case TouchAction.Perform p:
                        commandsToRun ??= new List<(TouchCommand, TouchRegion?)>();
                        commandsToRun.Add((p.Command, p.Region));
                        break;
                    case TouchAction.DropHeld:
                        _heldPressPoint = default;
                        break;
                }
            }
            coasting = _pipeline.IsCoasting;
            deadline = _pipeline.Deadline;
            if (diagClickNo > 0)
                diagHit = DescribePointWindow(info.pt);
        }

        if (diagClickNo > 0)
        {
            string cmds = commandsToRun is { Count: > 0 }
                ? string.Join(",", commandsToRun.Select(c => c.Command.GetType().Name))
                : "none";
            string dtSuffix = diagDownDurationMs is { } dt ? $" dt={dt}ms" : string.Empty;
            Log.Info($"[TouchDiag #{diagClickNo}] {evKind} pt=({info.pt.X},{info.pt.Y}) flags=0x{info.flags:X} extra=0x{info.dwExtraInfo:X} src={diagSource} pass={step.PassOriginal} cmds=[{cmds}]{dtSuffix} hit={diagHit}");
        }

        if (dismissSelectionBubble)
            SelectionDismissed?.Invoke();

        EnqueueCommands(commandsToRun, evKind, pressOrigin, currentPoint);
        UpdateCoastTimer(coasting);
        UpdateDeadlineTimer(deadline);

        return step.PassOriginal
            ? Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam)
            : new IntPtr(1);
    }

    private void EnqueueCommands(
        List<(TouchCommand Command, TouchRegion? Region)>? commands,
        PointerEventKind currentKind,
        PointD pressOrigin,
        PointD currentPoint)
    {
        if (commands is not { Count: > 0 }) return;
        var q = _replayQueue;
        if (q is null || q.IsAddingCompleted) return;
        q.TryAdd(() =>
        {
            bool hasPress = false;
            foreach (var (cmd, region) in commands)
            {
                if (hasPress && cmd is TouchCommand.ReplayCurrent)
                    Thread.Sleep(currentKind == PointerEventKind.LeftUp ? 10 : 4);
                ExecuteCommand(cmd, currentKind, pressOrigin, currentPoint, region);
                if (cmd is TouchCommand.ReplayPress) hasPress = true;
            }
        });
    }

    private void ExecuteCommand(TouchCommand cmd, PointerEventKind currentKind, PointD heldPressPoint, PointD currentPoint, TouchRegion? region)
    {
        switch (cmd)
        {
            case TouchCommand.ReplayPress:
                SendMouseClick(heldPressPoint, down: true, right: false);
                break;

            case TouchCommand.ReplayCurrent:
                if (currentKind == PointerEventKind.LeftUp)
                    SendMouseClick(currentPoint, down: false, right: false);
                else if (currentKind == PointerEventKind.LeftDrag)
                    SendMouseMove(currentPoint);
                break;

            case TouchCommand.RightClick rc:
                SendMouseClick(rc.At, down: true, right: true);
                Thread.Sleep(10);
                SendMouseClick(rc.At, down: false, right: true);
                break;

            case TouchCommand.Scroll sc:
                SendScroll(sc.Step);
                break;

            case TouchCommand.CloseWindow cw:
                HandleCloseTap(cw.At, region?.CloseButton);
                break;

            case TouchCommand.SelectionCompleted sel:
                SelectionCompleted?.Invoke(sel.At);
                break;
        }
    }

    private bool IsSelectionBubbleAt(PointD point, out bool isBrowserChrome, double scale)
    {
        isBrowserChrome = false;
        IntPtr hit = Win32.WindowFromPoint(new Win32.POINT { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
        if (hit == IntPtr.Zero) return false;
        IntPtr root = Win32.GetAncestor(hit, Win32.GA_ROOT);
        if (root == IntPtr.Zero) root = hit;
        if (!Win32.GetWindowRect(root, out var rect)) return false;

        Win32.GetWindowThreadProcessId(root, out int pid);
        if (pid <= 0) return false;
        if (pid == Environment.ProcessId)
        {
            nint bubble = Volatile.Read(ref _bubbleHwnd);
            return bubble != 0 && root == (IntPtr)bubble;
        }

        if (!_pidExeCache.TryGetValue(pid, out string? exe))
        {
            string? path = Win32.GetProcessImagePath(pid);
            exe = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
            if (_pidExeCache.Count > 64) _pidExeCache.Clear();
            _pidExeCache[pid] = exe;
        }

        bool isBrowser = exe.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("brave.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("opera.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase);
        if (!isBrowser) return false;

        double chromeHeight = 120.0 * Math.Max(1.0, scale);
        isBrowserChrome = point.Y >= rect.Top && point.Y <= rect.Top + chromeHeight;
        return false;
    }

    private void HandleCloseTap(PointD at, CloseButton? button)
    {
        if (button is not null)
        {
            var zOrder = WindowService.GetOnScreenWindowZOrder();
            if (button.IsFrontmost(at, zOrder))
            {
                CloseWindowRequested?.Invoke(button.WindowHandle);
                return;
            }
        }
        SendMouseClick(at, down: true, right: false);
        SendMouseClick(at, down: false, right: false);
    }

    private void UpdateCoastTimer(bool coasting)
    {
        if (coasting)
        {
            if (Interlocked.Exchange(ref _coastingActive, 1) == 0)
            {
                int intervalMs = (int)Math.Round(1000.0 / Momentum.FrameRate);
                _coastTimer?.Change(intervalMs, intervalMs);
            }
        }
        else if (Interlocked.Exchange(ref _coastingActive, 0) == 1)
        {
            _coastTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void OnCoastTick(object? _)
    {
        ScrollStep? step;
        bool stillCoasting;
        lock (_gate)
        {
            step = _pipeline.MomentumFrame();
            stillCoasting = _pipeline.IsCoasting;
        }

        if (step is { } s)
            SendScroll(s);

        if (!stillCoasting)
            UpdateCoastTimer(false);
    }

    private void UpdateDeadlineTimer(double? deadline)
    {
        var timer = _deadlineTimer;
        if (timer is null) return;
        if (deadline is not { } due)
        {
            try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
            return;
        }
        double now = Stopwatch.GetElapsedTime(_startTick).TotalSeconds;
        int delayMs = Math.Max(1, (int)Math.Ceiling((due - now) * 1000.0));
        try { timer.Change(delayMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void OnDeadlineTick(object? _)
    {
        List<(TouchCommand Command, TouchRegion? Region)>? commandsToRun = null;
        bool coasting;
        double? nextDeadline;
        lock (_gate)
        {
            if (_regions.Count == 0 || _hookHandle == IntPtr.Zero) return;
            double now = Stopwatch.GetElapsedTime(_startTick).TotalSeconds;
            var step = _pipeline.Tick(now, _regions, _options);
            foreach (var action in step.Actions)
            {
                if (action is TouchAction.Perform p)
                {
                    commandsToRun ??= new List<(TouchCommand, TouchRegion?)>();
                    commandsToRun.Add((p.Command, p.Region));
                }
            }
            coasting = _pipeline.IsCoasting;
            nextDeadline = _pipeline.Deadline;
        }

        EnqueueCommands(commandsToRun, PointerEventKind.Move, default, default);
        UpdateCoastTimer(coasting);
        UpdateDeadlineTimer(nextDeadline);
    }

    private static void TryEnableThreadPerMonitorDpiV2()
    {
        try
        {
            Win32.SetThreadDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Fall back to process DPI awareness on older OS builds.
        }
    }

    private string DescribePointWindow(Win32.POINT pt)
    {
        IntPtr hit = Win32.WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return "none";
        var cls = new System.Text.StringBuilder(128);
        _ = Win32.GetClassNameW(hit, cls, cls.Capacity);
        IntPtr root = Win32.GetAncestor(hit, Win32.GA_ROOT);
        if (root == IntPtr.Zero) root = hit;
        Win32.GetWindowThreadProcessId(root, out int pid);
        string exe = string.Empty;
        if (pid > 0 && !_pidExeCache.TryGetValue(pid, out exe!))
        {
            string? path = Win32.GetProcessImagePath(pid);
            exe = string.IsNullOrEmpty(path) ? pid.ToString() : Path.GetFileName(path);
            if (_pidExeCache.Count > 64) _pidExeCache.Clear();
            _pidExeCache[pid] = exe;
        }
        return $"{exe}/{cls}";
    }

    private static (int AbsX, int AbsY, int Vx, int Vy, int Vw, int Vh) ToVirtualDesktopAbsolute(PointD pt)
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
        int absX = (int)Math.Round((pt.X - vx) * 65535.0 / Math.Max(1, vw - 1));
        int absY = (int)Math.Round((pt.Y - vy) * 65535.0 / Math.Max(1, vh - 1));
        return (absX, absY, vx, vy, vw, vh);
    }

    private void SendMouseMove(PointD pt)
    {
        int px = (int)Math.Round(pt.X);
        int py = (int)Math.Round(pt.Y);
        var (ax, ay, _, _, _, _) = ToVirtualDesktopAbsolute(pt);
        nuint extra = _echoes.ExpectMove(px, py, Environment.TickCount64);
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.INPUTUNION
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = extra
                }
            }
        };
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
    }

    private void SendMouseClick(PointD pt, bool down, bool right)
    {
        int px = (int)Math.Round(pt.X);
        int py = (int)Math.Round(pt.Y);
        var (ax, ay, vx, vy, vw, vh) = ToVirtualDesktopAbsolute(pt);
        uint btnFlag = right
            ? (down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP)
            : (down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP);
        nuint extra = _echoes.ExpectClick(px, py, down, right, Environment.TickCount64);

        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.INPUTUNION
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK | btnFlag,
                    dwExtraInfo = extra
                }
            }
        };
        uint sent = Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
        if (sent == 0 || Interlocked.Increment(ref _replayDiagCount) <= MaxClickDiagCount * 2)
        {
            Log.Info($"[TouchReplay] click({(right ? "R" : "L")}:{(down ? "Down" : "Up")}) pt=({px},{py}) abs=({ax},{ay}) vscreen=({vx},{vy},{vw}x{vh}) extra=0x{extra:X} sent={sent}");
        }
    }

    private void SendScroll(ScrollStep step)
    {
        if (step.Phase == ScrollPhase.Began)
        {
            _scrollRemX = 0;
            _scrollRemY = 0;
            Win32.SetCursorPos((int)Math.Round(step.Location.X), (int)Math.Round(step.Location.Y));
        }

        _scrollRemY += step.DeltaY * WheelScaleFactor;
        _scrollRemX += -step.DeltaX * WheelScaleFactor;
        int dy = (int)Math.Truncate(_scrollRemY);
        int dx = (int)Math.Truncate(_scrollRemX);
        _scrollRemY -= dy;
        _scrollRemX -= dx;
        if (dy == 0 && dx == 0) return;

        nuint extra = _echoes.CurrentReplayExtraInfo;
        var list = new List<Win32.INPUT>(2);

        if (dy != 0)
        {
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                U = new Win32.INPUTUNION
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)dy),
                        dwFlags = Win32.MOUSEEVENTF_WHEEL,
                        dwExtraInfo = extra
                    }
                }
            });
        }

        if (dx != 0)
        {
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                U = new Win32.INPUTUNION
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)dx),
                        dwFlags = Win32.MOUSEEVENTF_HWHEEL,
                        dwExtraInfo = extra
                    }
                }
            });
        }

        var arr = list.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
    }

    public void Dispose() => Stop();
}

internal sealed class ReplayEchoTracker
{
    public const nuint FallbackReplayMarker = 0x534E5547; // 'SNUG'
    private const long DefaultTtlMs = 300;
    private const int CoordinateTolerancePx = 4;

    private readonly record struct ExpectedEcho(
        int Msg,
        int X,
        int Y,
        nuint ExtraInfo,
        bool Optional,
        long ExpiresAtMs);

    private readonly object _gate = new();
    private readonly List<ExpectedEcho> _pending = new();
    private nuint _passthroughExtraInfo;

    public nuint CurrentReplayExtraInfo
    {
        get { lock (_gate) return _passthroughExtraInfo != 0 ? _passthroughExtraInfo : FallbackReplayMarker; }
    }

    public void ObserveIncomingExtraInfo(nuint extraInfo)
    {
        if (extraInfo != 0 && extraInfo != FallbackReplayMarker)
        {
            lock (_gate) _passthroughExtraInfo = extraInfo;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _passthroughExtraInfo = 0;
            _pending.Clear();
        }
    }

    public nuint ExpectClick(int x, int y, bool down, bool right, long nowMs)
    {
        lock (_gate)
        {
            PruneLocked(nowMs);
            nuint extra = _passthroughExtraInfo != 0 ? _passthroughExtraInfo : FallbackReplayMarker;
            long exp = nowMs + DefaultTtlMs;
            // SendInput with MOUSEEVENTF_MOVE | button may synthesize a WM_MOUSEMOVE before the button event.
            _pending.Add(new ExpectedEcho(Win32.WM_MOUSEMOVE, x, y, extra, Optional: true, exp));
            if (!right)
            {
                int btnMsg = down ? Win32.WM_LBUTTONDOWN : Win32.WM_LBUTTONUP;
                _pending.Add(new ExpectedEcho(btnMsg, x, y, extra, Optional: false, exp));
            }
            return extra;
        }
    }

    public nuint ExpectMove(int x, int y, long nowMs)
    {
        lock (_gate)
        {
            PruneLocked(nowMs);
            nuint extra = _passthroughExtraInfo != 0 ? _passthroughExtraInfo : FallbackReplayMarker;
            _pending.Add(new ExpectedEcho(Win32.WM_MOUSEMOVE, x, y, extra, Optional: false, nowMs + DefaultTtlMs));
            return extra;
        }
    }

    public bool TryConsumeEcho(int msg, int x, int y, nuint extraInfo, long nowMs)
    {
        if (extraInfo == FallbackReplayMarker)
            return true;

        lock (_gate)
        {
            PruneLocked(nowMs);
            for (int i = 0; i < _pending.Count; i++)
            {
                var item = _pending[i];
                if (item.Msg == msg
                    && item.ExtraInfo == extraInfo
                    && Math.Abs(item.X - x) <= CoordinateTolerancePx
                    && Math.Abs(item.Y - y) <= CoordinateTolerancePx)
                {
                    _pending.RemoveAt(i);
                    if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP)
                    {
                        for (int j = _pending.Count - 1; j >= 0; j--)
                        {
                            var opt = _pending[j];
                            if (opt.Optional
                                && opt.Msg == Win32.WM_MOUSEMOVE
                                && opt.ExtraInfo == extraInfo
                                && Math.Abs(opt.X - x) <= CoordinateTolerancePx
                                && Math.Abs(opt.Y - y) <= CoordinateTolerancePx)
                            {
                                _pending.RemoveAt(j);
                            }
                        }
                    }
                    return true;
                }
            }
            return false;
        }
    }

    private void PruneLocked(long nowMs)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].ExpiresAtMs <= nowMs)
                _pending.RemoveAt(i);
        }
    }
}
