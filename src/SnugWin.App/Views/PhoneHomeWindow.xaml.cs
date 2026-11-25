using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace SnugWin.App.Views;

public partial class PhoneHomeWindow : Window
{
    public enum SubPage { None, Search, PhoneModel }

    public sealed class RunningAppVm
    {
        public RunningApp App { get; }
        public string AppName => App.App.Name;
        public ImageSource Icon { get; }
        public string Subtitle { get; }
        public Brush SubtitleBrush { get; }
        public bool NotQuitting { get; }
        public Visibility NormalActionsVis { get; }
        public Visibility AskQuitVis { get; }
        public Visibility QuittingVis { get; }
        public Brush RailBackground { get; }
        public IReadOnlyList<WindowVm> Windows { get; }

        public RunningAppVm(
            RunningApp app,
            PhoneDeskState state,
            ImageSource icon,
            bool askQuit,
            Func<WindowKey, bool> isAskClose,
            Action onStateReady,
            bool isSelectedInRail = false)
        {
            App = app;
            Icon = icon;
            RailBackground = isSelectedInRail
                ? ResolveThemeBrush("PhoneTagOnBg", Color.FromArgb(0x28, 0x00, 0x7A, 0xFF))
                : Brushes.Transparent;
            bool quitting = state.Quitting.Contains(app.Pid);
            NotQuitting = !quitting;
            QuittingVis = quitting ? Visibility.Visible : Visibility.Collapsed;
            AskQuitVis = !quitting && askQuit ? Visibility.Visible : Visibility.Collapsed;
            NormalActionsVis = !quitting && !askQuit ? Visibility.Visible : Visibility.Collapsed;

            var secondaryBrush = ResolveThemeBrush("PhoneSecondaryText", Color.FromRgb(0x6B, 0x72, 0x80));
            var redBrush = ResolveThemeBrush("PhoneAlertRed", Color.FromRgb(0xDC, 0x26, 0x26));

            if (askQuit)
            {
                Subtitle = $"退出 {AppName}？";
                SubtitleBrush = redBrush;
            }
            else if (quitting)
            {
                Subtitle = "正在退出";
                SubtitleBrush = secondaryBrush;
            }
            else
            {
                Subtitle = app.Windows.Count == 0 ? "没有窗口" : $"{app.Windows.Count} 个窗口";
                SubtitleBrush = secondaryBrush;
            }

            Windows = app.Windows
                .Select(w => new WindowVm(w, state, isAskClose(w.Key)))
                .ToList();
        }
    }

    public sealed class WindowVm
    {
        public WindowInfo Window { get; }
        public bool OnPhone { get; }
        public string DisplayTitle { get; }
        public string RowSymbol => OnPhone ? "📱" : "❐";
        public Brush RowSymbolBrush { get; }
        public Brush TitleBrush { get; }
        public bool NotClosing { get; }
        public Visibility TagsVis { get; }
        public Visibility OnPhoneTagVis { get; }
        public Visibility MinimizedVis { get; }
        public Visibility NormalCloseVis { get; }
        public Visibility AskCloseVis { get; }
        public Visibility ClosingVis { get; }
        public Visibility PlacedControlsVis { get; }
        public Visibility ZoomControlsVis { get; }
        public bool CanZoomOut { get; }
        public bool CanZoomIn { get; }
        public string ZoomText { get; }

        public WindowVm(WindowInfo window, PhoneDeskState state, bool askClose)
        {
            var primaryBrush = ResolveThemeBrush("PhoneTextPrimary", Color.FromRgb(0x11, 0x18, 0x27));
            var tintBrush = ResolveThemeBrush("PhoneTintBlue", Color.FromRgb(0x00, 0x7A, 0xFF));
            var secondaryBrush = ResolveThemeBrush("PhoneSecondaryText", Color.FromRgb(0x6B, 0x72, 0x80));
            var redBrush = ResolveThemeBrush("PhoneAlertRed", Color.FromRgb(0xDC, 0x26, 0x26));

            Window = window;
            OnPhone = state.Placed?.Window.Key == window.Key;
            bool closing = state.Closing.Contains(window.Key);
            NotClosing = !closing;
            RowSymbolBrush = OnPhone ? tintBrush : secondaryBrush;
            DisplayTitle = askClose ? "关闭此窗口？" : (string.IsNullOrEmpty(window.Title) ? "无标题" : window.Title);
            TitleBrush = askClose ? redBrush : primaryBrush;

            TagsVis = askClose ? Visibility.Collapsed : Visibility.Visible;
            OnPhoneTagVis = OnPhone ? Visibility.Visible : Visibility.Collapsed;
            MinimizedVis = window.Minimized ? Visibility.Visible : Visibility.Collapsed;

            ClosingVis = closing ? Visibility.Visible : Visibility.Collapsed;
            AskCloseVis = !closing && askClose ? Visibility.Visible : Visibility.Collapsed;
            NormalCloseVis = !closing && !askClose ? Visibility.Visible : Visibility.Collapsed;

            if (OnPhone && state.Placed is { } placed)
            {
                PlacedControlsVis = Visibility.Visible;
                ZoomControlsVis = placed.Zoomable ? Visibility.Visible : Visibility.Collapsed;
                CanZoomOut = placed.Zoom > ZoomBook.MinSteps;
                CanZoomIn = placed.Zoom < ZoomBook.MaxSteps;
                ZoomText = placed.Zoom == 0 ? "默认字号" : $"字号 {(placed.Zoom > 0 ? "+" : "")}{placed.Zoom}";
            }
            else
            {
                PlacedControlsVis = Visibility.Collapsed;
                ZoomControlsVis = Visibility.Collapsed;
                ZoomText = string.Empty;
            }
        }
    }

    public sealed record AllAppVm(AppInfo App, ImageSource Icon, bool NotQuitting, double RunningDotOpacity)
    {
        public string Name => App.Name;
    }

    public sealed record SearchAppRowVm(AppInfo App, ImageSource Icon, string Subtitle, bool NotQuitting)
    {
        public string Name => App.Name;
    }

    public sealed record SearchLetterGroupVm(string Letter, IReadOnlyList<SearchAppRowVm> Apps);

    public sealed record SearchWindowRowVm(WindowInfo Window, ImageSource Icon)
    {
        public string Title => string.IsNullOrEmpty(Window.Title) ? Window.AppName : Window.Title;
        public string AppName => Window.AppName;
    }

    public sealed record ModelBrandVm(string Name, IReadOnlyList<ModelItemVm> Models);
    public sealed record ModelItemVm(PhoneModel Model, bool Selected)
    {
        public string Name => Model.Name;
        public string SizeText => Model.Screen.Points is { } pts
            ? $"{Model.Screen.Width} × {Model.Screen.Height}（{pts}pt）"
            : $"{Model.Screen.Width} × {Model.Screen.Height}";
        public Visibility CheckVis => Selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly AppController _controller;
    private readonly bool _previewMode;
    private string? _screenId;
    private DeviceKind _deviceKind;
    private readonly Action _uiChangedHandler;
    private readonly Action<bool> _themeHandler;
    private SubPage _subPage = SubPage.None;
    private bool _showAllTab;
    private bool _confirmDismiss;
    private int? _confirmQuitPid;
    private WindowKey? _confirmCloseWindow;
    private int? _selectedLandscapePid;
    private System.Windows.Controls.Primitives.UniformGrid? _allAppsUniformGrid;
    private (string App, int Pid)? _stalledQuit;
    private bool _lastListVisible = true;
    private DeskWorkKind? _lastWorkKind;
    private string? _lastRunningSig;
    private string? _lastAllSig;

    public bool AcceptsKeys => _subPage != SubPage.None;

    public PhoneHomeWindow(AppController controller, bool previewMode = false, string? screenId = null, DeviceKind kind = DeviceKind.Phone)
    {
        InitializeComponent();
        _controller = controller;
        _previewMode = previewMode;
        _screenId = screenId;
        _deviceKind = kind;
        if (_previewMode)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanMinimize;
            ShowInTaskbar = true;
            Topmost = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowPlacementHelper.ApplyWindowChromeTheme(this, _controller.IsEffectiveDarkTheme);
        }
        ApplyTopTrailingClear();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        _uiChangedHandler = () => Dispatcher.BeginInvoke(SyncState);
        _themeHandler = isDark => Dispatcher.BeginInvoke(() =>
        {
            if (_previewMode)
                WindowPlacementHelper.ApplyWindowChromeTheme(this, isDark);
            _lastRunningSig = null;
            _lastAllSig = null;
            UpdateTabStyles();
            SyncState();
        });
        _controller.UiStateChanged += _uiChangedHandler;
        _controller.ThemeChanged += _themeHandler;
        Closed += (_, _) =>
        {
            _controller.UiStateChanged -= _uiChangedHandler;
            _controller.ThemeChanged -= _themeHandler;
        };
        UpdateTabStyles();
    }

    public void ConfigureTouchScreen(string screenId, DeviceKind kind)
    {
        _screenId = screenId;
        _deviceKind = kind;
        ApplyTopTrailingClear();
        UpdateResponsiveLayout();
    }

    private bool IsLandscapeLayout =>
        (!double.IsNaN(PhoneRootGrid.Width) && !double.IsNaN(PhoneRootGrid.Height) && PhoneRootGrid.Width > PhoneRootGrid.Height)
        || (ActualWidth > 0 && ActualHeight > 0 && ActualWidth > ActualHeight);

    private void ApplyTopTrailingClear()
    {
        double trailing = _deviceKind == DeviceKind.Tablet ? 68 : 20;
        HomeTopRow.Margin = new Thickness(20, 12, trailing, 0);
        SearchTopRow.Margin = new Thickness(20, 12, trailing, 12);
        ModelTopRow.Margin = new Thickness(20, 12, trailing, 0);
    }

    private void UpdateResponsiveLayout()
    {
        bool landscape = IsLandscapeLayout;
        if (_allAppsUniformGrid is not null)
            _allAppsUniformGrid.Columns = landscape ? 6 : 4;
        _lastRunningSig = null;
        RefreshRunningAndAll();
    }

    private void OnAllAppsUniformGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.UniformGrid ug)
        {
            _allAppsUniformGrid = ug;
            ug.Columns = IsLandscapeLayout ? 6 : 4;
        }
    }

    private static Brush ResolveThemeBrush(string key, Color fallback)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
            return b;
        var created = new SolidColorBrush(fallback);
        created.Freeze();
        return created;
    }

    public void ApplyPreviewLayout(SizeD workAreaDip, ScreenFit fit, PhoneChoice phone)
    {
        if (!_previewMode) return;
        var (designW, designH, scale) = ScreenFit.ComputePreviewLayout(workAreaDip, fit);
        PhoneRootGrid.Width = designW;
        PhoneRootGrid.Height = designH;
        PhoneRootGrid.LayoutTransform = new ScaleTransform(scale, scale);
        var (pxW, pxH) = fit.PixelSize(fit.BaseWidth);
        Title = $"SnugWin 预览 — {phone.Model ?? "自定义"} ({pxW} × {pxH})";
        UpdateResponsiveLayout();
    }

    public void CloseSubPage()
    {
        if (_subPage == SubPage.None) return;
        _subPage = SubPage.None;
        SearchInputBox.Text = string.Empty;
        ModelSearchBox.Text = string.Empty;
        UpdatePageVisibility();
    }

    public void ShowOnDisplay(RectD bounds, bool focusOnShow)
    {
        WindowPlacementHelper.PlacePhysical(
            this,
            bounds,
            topmost: true,
            noActivate: !AcceptsKeys,
            focusIfActivated: focusOnShow && AcceptsKeys);
    }

    public void SyncState()
    {
        var st = _controller.GetScreenState(_screenId);

        if (st.ListVisible != _lastListVisible)
        {
            _lastListVisible = st.ListVisible;
            ClearConfirmations();
            if (st.ListVisible)
                HomeScroll.ScrollToTop();
        }

        if (st.Work?.Kind != _lastWorkKind)
        {
            _lastWorkKind = st.Work?.Kind;
            ClearConfirmations();
        }

        if (st.Placed is { } placed)
        {
            BackBtnLabel.Text = $"‹ {placed.Window.AppName}";
            BackBtn.Visibility = Visibility.Visible;
        }
        else
        {
            BackBtn.Visibility = Visibility.Collapsed;
        }

        DismissBannerDetail.Text = st.StaysOnDismiss ? "窗口还原，屏幕保留" : "窗口还原，下次远程时恢复";
        DismissBanner.Visibility = _confirmDismiss ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotice(st.Notice);
        UpdateBusyOverlay(st);
        UpdatePageVisibility();
        RefreshRunningAndAll();
        if (_subPage == SubPage.Search)
        {
            RefreshSearchPage();
        }
        else if (_subPage == SubPage.PhoneModel)
        {
            RefreshModelPage();
        }
    }

    private void ShowPreviewReadOnlyNotice()
    {
        NoticeBanner.Visibility = Visibility.Visible;
        NoticeHeadline.Text = "本地预览模式";
        NoticeDetail.Text = "仅演示 App 资源库、拼音搜索与机型比例，不会移动或关闭桌面窗口；连入远程后即可实战操控。";
        NoticeActionsRow.Visibility = Visibility.Collapsed;
        NoticeCloseBtn.Visibility = Visibility.Visible;
    }

    private void ClearConfirmations()
    {
        _confirmDismiss = false;
        _confirmQuitPid = null;
        _confirmCloseWindow = null;
    }

    private void UpdatePageVisibility()
    {
        HomePageView.Visibility = _subPage == SubPage.None ? Visibility.Visible : Visibility.Collapsed;
        SearchPageView.Visibility = _subPage == SubPage.Search ? Visibility.Visible : Visibility.Collapsed;
        ModelPageView.Visibility = _subPage == SubPage.PhoneModel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBusyOverlay(PhoneDeskState st)
    {
        if (st.Work is null || st.Work.Kind is DeskWorkKind.PuttingBack or DeskWorkKind.Zooming)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        switch (st.Work.Kind)
        {
            case DeskWorkKind.Opening:
                var appMatch = _controller.InstalledApps.FirstOrDefault(a => a.Name == st.Work.Label)
                               ?? _controller.RunningApps.Select(r => r.App).FirstOrDefault(a => a.Name == st.Work.Label);
                BusyIcon.Source = appMatch is not null ? _controller.Icons.GetIcon(appMatch) : null;
                BusyTitle.Text = st.Work.Label ?? string.Empty;
                BusySubtitle.Visibility = Visibility.Collapsed;
                BusyStatus.Text = "正在打开";
                break;

            case DeskWorkKind.Placing:
                var winMatch = _controller.RunningApps.SelectMany(r => r.Windows).FirstOrDefault(w => w.Label == st.Work.Label);
                BusyIcon.Source = winMatch is not null ? _controller.Icons.GetIcon(winMatch) : null;
                BusyTitle.Text = winMatch?.AppName ?? st.Work.Label ?? string.Empty;
                BusySubtitle.Text = winMatch is not null && !string.IsNullOrEmpty(winMatch.Title) ? winMatch.Title : string.Empty;
                BusySubtitle.Visibility = string.IsNullOrEmpty(BusySubtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
                BusyStatus.Text = "正在打开";
                break;

            case DeskWorkKind.Reshaping:
                BusyIcon.Source = null;
                var dev = _controller.GetScreenConfig(_screenId)?.Device ?? _controller.Config.Phone;
                BusyTitle.Text = dev.Model ?? "自定义";
                var sc = dev.Screen;
                BusySubtitle.Text = $"{sc.Width} × {sc.Height}";
                BusySubtitle.Visibility = Visibility.Visible;
                BusyStatus.Text = "正在调整屏幕比例";
                break;

            case DeskWorkKind.Dismissing:
                BusyIcon.Source = null;
                BusyTitle.Text = _controller.GetScreenConfig(_screenId)?.Name ?? PhoneScreenSpec.Name;
                BusySubtitle.Visibility = Visibility.Collapsed;
                BusyStatus.Text = "正在退出";
                break;
        }

        BusyOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateNotice(DeskNotice? notice)
    {
        _stalledQuit = null;
        if (notice is null || _confirmDismiss)
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            return;
        }

        NoticeBanner.Visibility = Visibility.Visible;
        (string headline, string detail) = notice switch
        {
            DeskNotice.WindowGone g => ($"{g.App} 窗口已关闭", "请另选一个窗口"),
            DeskNotice.WindowUnresponsive u => ($"{u.App} 无响应", "可能正忙，请稍后再试（或以管理员身份运行）"),
            DeskNotice.RecordUnwritable => ("无法保存原位置", "为确保能放回原处，窗口未移动"),
            DeskNotice.ScreenNotReady => ("手机屏未就绪", "屏幕正在调整，请稍后再试"),
            DeskNotice.OpenFailed o => ($"无法打开 {o.App}", "请稍后再试"),
            DeskNotice.NoNewWindow n => ($"{n.App} 无法新建窗口", "可点 App 打开已有窗口"),
            DeskNotice.NoWindow n => ($"未找到 {n.App} 的窗口", "App 已打开，但没有可显示的窗口"),
            DeskNotice.QuitStalled q => ($"{q.App} 未退出", "强制退出将丢失未存储的内容"),
            DeskNotice.Reshaped => (
                "屏幕比例已调整",
                _controller.IsLaunchOnlyOwner
                    ? "在设置页或托盘点击「一键重启远控」重新识别后，点按位置才准确"
                    : "在远控中切到其他屏幕再切回，点按位置才准确"),
            DeskNotice.ReshapeFailed => ("屏幕比例未能调整", "下次连接时生效"),
            DeskNotice.KeptOnDismiss => ("窗口已还原", "屏幕保留，可在远控中切换屏幕"),
            DeskNotice.CircuitBreakerTripped => ("已触发建屏熔断保护", "防止屏幕闪烁，请确认驱动正常后在界面重新开启"),
            DeskNotice.ZoomLeftBehind z => ($"{z.App} 窗口已关闭", $"再打开时字号若偏大，按 {z.Steps} 次 Ctrl+- 复原"),
            _ => (string.Empty, string.Empty)
        };

        NoticeHeadline.Text = headline;
        NoticeDetail.Text = detail;

        if (notice is DeskNotice.QuitStalled qs)
        {
            _stalledQuit = (qs.App, qs.Pid);
            NoticeActionsRow.Visibility = Visibility.Visible;
            NoticeCloseBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoticeActionsRow.Visibility = Visibility.Collapsed;
            NoticeCloseBtn.Visibility = Visibility.Visible;
        }
    }

    private void RefreshRunningAndAll()
    {
        var st = _controller.GetScreenState(_screenId);
        var running = _controller.RunningApps;
        bool landscape = IsLandscapeLayout;

        string runningSig = BuildRunningSignature(running, st, _controller.CatalogLoaded, _confirmQuitPid, _confirmCloseWindow)
                            + $"|{(landscape ? 'L' : 'P')}|{_selectedLandscapePid}";
        if (!string.Equals(runningSig, _lastRunningSig, StringComparison.Ordinal))
        {
            _lastRunningSig = runningSig;

            if (landscape)
            {
                PortraitRunningView.Visibility = Visibility.Collapsed;
                if (running.Count == 0)
                {
                    LandscapeRunningView.Visibility = Visibility.Collapsed;
                    RunningEmptyText.Text = _controller.CatalogLoaded ? "没有正在运行的 App" : "正在读取";
                    RunningEmptyState.Visibility = Visibility.Visible;
                }
                else
                {
                    RunningEmptyState.Visibility = Visibility.Collapsed;
                    LandscapeRunningView.Visibility = Visibility.Visible;

                    var ordered = new List<RunningApp>();
                    RunningApp? placedApp = st.Placed is { } pl ? BuildPlacedApp(pl.Window, running) : null;
                    if (placedApp is not null) ordered.Add(placedApp);
                    ordered.AddRange(running.Where(r => placedApp is null || r.Pid != placedApp.Pid));

                    int selectedPid = _selectedLandscapePid is { } sp && ordered.Any(r => r.Pid == sp)
                        ? sp
                        : ordered[0].Pid;
                    _selectedLandscapePid = selectedPid;

                    var vms = ordered.Select(r => ToVm(r, st, isSelectedInRail: r.Pid == selectedPid)).ToList();
                    LandscapeAppRail.ItemsSource = vms;
                    LandscapeSelectedCardHost.Content = vms.FirstOrDefault(v => v.App.Pid == selectedPid) ?? vms[0];
                }
            }
            else
            {
                LandscapeRunningView.Visibility = Visibility.Collapsed;
                PortraitRunningView.Visibility = Visibility.Visible;

                if (st.Placed is { } placed)
                {
                    var placedApp = BuildPlacedApp(placed.Window, running);
                    CurrentAppCardHost.Content = ToVm(placedApp, st);
                    CurrentAppSection.Visibility = Visibility.Visible;
                    var others = running.Where(r => r.Pid != placed.Window.Key.Pid).ToList();
                    OtherAppsHeader.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    RunningList.ItemsSource = others.Select(r => ToVm(r, st)).ToList();
                    RunningEmptyState.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CurrentAppSection.Visibility = Visibility.Collapsed;
                    CurrentAppCardHost.Content = null;
                    OtherAppsHeader.Visibility = Visibility.Collapsed;
                    RunningList.ItemsSource = running.Select(r => ToVm(r, st)).ToList();
                    if (!_controller.CatalogLoaded)
                    {
                        RunningEmptyText.Text = "正在读取";
                        RunningEmptyState.Visibility = Visibility.Visible;
                    }
                    else if (running.Count == 0)
                    {
                        RunningEmptyText.Text = "没有正在运行的 App";
                        RunningEmptyState.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        RunningEmptyState.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        // All tab (4-column icon grid)
        var installed = _controller.InstalledApps;
        string allSig = BuildAllSignature(installed, running, st.Quitting);
        if (!string.Equals(allSig, _lastAllSig, StringComparison.Ordinal))
        {
            _lastAllSig = allSig;
            if (installed.Count == 0)
            {
                AllLoadingText.Visibility = Visibility.Visible;
                AllAppsGrid.ItemsSource = null;
            }
            else
            {
                AllLoadingText.Visibility = Visibility.Collapsed;
                var runningById = running
                    .GroupBy(r => r.App.AppId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Pid, StringComparer.OrdinalIgnoreCase);

                AllAppsGrid.ItemsSource = installed.Select(a =>
                {
                    bool hasPid = runningById.TryGetValue(a.AppId, out int pid);
                    bool quitting = hasPid && st.Quitting.Contains(pid);
                    return new AllAppVm(a, _controller.Icons.GetIcon(a, hasPid ? pid : null), !quitting, hasPid ? 1.0 : 0.0);
                }).ToList();
            }
        }
    }

    private static string BuildRunningSignature(
        IReadOnlyList<RunningApp> running,
        PhoneDeskState st,
        bool loaded,
        int? confirmQuitPid,
        WindowKey? confirmClose)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(loaded ? '1' : '0').Append('|');
        sb.Append(st.Placed?.Window.Key.ToString() ?? "-").Append('|').Append(st.Placed?.Zoom ?? 0).Append('|');
        sb.Append(confirmQuitPid?.ToString() ?? "-").Append('|').Append(confirmClose?.ToString() ?? "-").Append('|');
        foreach (int q in st.Quitting.OrderBy(x => x)) sb.Append('Q').Append(q).Append(',');
        sb.Append('|');
        foreach (var c in st.Closing.OrderBy(x => x.Handle)) sb.Append('C').Append(c).Append(',');
        sb.Append('|');
        foreach (var app in running)
        {
            sb.Append(app.Pid).Append(':').Append(app.App.AppId).Append('[');
            foreach (var w in app.Windows)
                sb.Append(w.Key).Append(':').Append(w.Title).Append(':').Append(w.Minimized ? '1' : '0').Append(';');
            sb.Append("];");
        }
        return sb.ToString();
    }

    private static string BuildAllSignature(
        IReadOnlyList<AppInfo> installed,
        IReadOnlyList<RunningApp> running,
        IReadOnlySet<int> quitting)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(installed.Count).Append('|');
        if (installed.Count > 0)
            sb.Append(installed[0].AppId).Append('|').Append(installed[^1].AppId).Append('|');
        foreach (var r in running.OrderBy(x => x.App.AppId, StringComparer.OrdinalIgnoreCase))
            sb.Append(r.App.AppId).Append(':').Append(quitting.Contains(r.Pid) ? 'Q' : 'R').Append(';');
        return sb.ToString();
    }

    private static RunningApp BuildPlacedApp(WindowInfo placedWindow, IReadOnlyList<RunningApp> running)
    {
        var found = running.FirstOrDefault(r => r.Pid == placedWindow.Key.Pid)
                    ?? running.FirstOrDefault(r => !string.IsNullOrEmpty(placedWindow.AppId)
                                                   && string.Equals(r.App.AppId, placedWindow.AppId, StringComparison.OrdinalIgnoreCase));
        var windows = found?.Windows ?? Array.Empty<WindowInfo>();
        var latest = windows.FirstOrDefault(w => w.Key == placedWindow.Key) ?? placedWindow;
        var reordered = new List<WindowInfo> { latest };
        reordered.AddRange(windows.Where(w => w.Key != placedWindow.Key));
        var app = found?.App ?? new AppInfo(placedWindow.AppId ?? placedWindow.AppName, placedWindow.AppName, null);
        return new RunningApp(app, placedWindow.Key.Pid, reordered);
    }

    private RunningAppVm ToVm(RunningApp ra, PhoneDeskState st, bool isSelectedInRail = false) =>
        new(
            ra,
            st,
            _controller.Icons.GetIcon(ra.App, ra.Pid),
            _confirmQuitPid == ra.Pid,
            k => _confirmCloseWindow == k,
            SyncState,
            isSelectedInRail);

    private void OnSelectLandscapeApp(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: RunningAppVm vm })
        {
            ClearConfirmations();
            _selectedLandscapePid = vm.App.Pid;
            _lastRunningSig = null;
            RefreshRunningAndAll();
        }
    }

    private void RefreshSearchPage()
    {
        string q = SearchInputBox.Text.Trim();
        ClearSearchBtn.Visibility = string.IsNullOrEmpty(SearchInputBox.Text) ? Visibility.Collapsed : Visibility.Visible;

        var running = _controller.RunningApps;
        var installed = _controller.InstalledApps;
        var st = _controller.GetScreenState(_screenId);

        SearchAppRowVm MakeAppRow(AppInfo a)
        {
            var r = running.FirstOrDefault(x => string.Equals(x.App.AppId, a.AppId, StringComparison.OrdinalIgnoreCase));
            bool quitting = r is not null && st.Quitting.Contains(r.Pid);
            string sub = quitting
                ? "正在退出"
                : (r is null ? "未打开" : (r.Windows.Count == 0 ? "没有窗口" : $"{r.Windows.Count} 个窗口"));
            return new SearchAppRowVm(a, _controller.Icons.GetIcon(a, r?.Pid), sub, !quitting);
        }

        if (string.IsNullOrEmpty(q))
        {
            LetterGroupsList.Visibility = Visibility.Visible;
            SearchResultsPanel.Visibility = Visibility.Collapsed;
            LetterGroupsList.ItemsSource = AppList.GroupedByLetter(installed)
                .Select(g => new SearchLetterGroupVm(g.Letter, g.Apps.Select(MakeAppRow).ToList()))
                .ToList();
        }
        else
        {
            LetterGroupsList.Visibility = Visibility.Collapsed;
            SearchResultsPanel.Visibility = Visibility.Visible;
            var res = AppList.Search(q, installed, running);
            SearchNoResultText.Visibility = res.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            SearchAppsSection.Visibility = res.Apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchWindowsSection.Visibility = res.Windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchAppsResults.ItemsSource = res.Apps.Select(MakeAppRow).ToList();
            SearchWindowsResults.ItemsSource = res.Windows
                .Select(w => new SearchWindowRowVm(w, _controller.Icons.GetIcon(w)))
                .ToList();
        }
    }

    private void RefreshModelPage()
    {
        string q = ModelSearchBox.Text.Trim();
        var cfgScreen = _controller.GetScreenConfig(_screenId);
        var current = cfgScreen?.Device ?? _controller.Config.Phone;
        CurrentModelTitle.Text = current.Model ?? "自定义";
        CurrentModelSize.Text = $"{current.Screen.Width} × {current.Screen.Height}";

        if (current.Model is null && string.IsNullOrEmpty(CustomWidthBox.Text) && string.IsNullOrEmpty(CustomHeightBox.Text))
        {
            CustomWidthBox.Text = current.Screen.Width.ToString();
            CustomHeightBox.Text = current.Screen.Height.ToString();
        }

        var kinds = current.Kind.SwappableKinds();
        var brands = _controller.Catalog.Search(q, kinds);
        CurrentModelSection.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
        CustomModelSection.Visibility = (string.IsNullOrEmpty(q) || brands.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

        bool multiKind = brands.Select(b => b.Kind).Distinct().Count() > 1;
        ModelBrandsList.ItemsSource = brands
            .Select(b => new ModelBrandVm(
                multiKind ? $"{b.Name} · {b.Kind.DisplayName()}" : b.Name,
                b.Models.Select(m => new ModelItemVm(m, m.Id == current.Model)).ToList()))
            .ToList();
    }

    private void UpdateTabStyles()
    {
        var activeBrush = ResolveThemeBrush("PhoneSegmentBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
        var activeText = ResolveThemeBrush("PhoneTextPrimary", Color.FromRgb(0x11, 0x18, 0x27));
        var inactiveText = ResolveThemeBrush("PhoneSegmentInactiveText", Color.FromRgb(0x4B, 0x55, 0x63));
        RunningTabBd.Background = !_showAllTab ? activeBrush : Brushes.Transparent;
        AllTabBd.Background = _showAllTab ? activeBrush : Brushes.Transparent;
        RunningTabText.Foreground = !_showAllTab ? activeText : inactiveText;
        AllTabText.Foreground = _showAllTab ? activeText : inactiveText;
        RunningPagePanel.Visibility = !_showAllTab ? Visibility.Visible : Visibility.Collapsed;
        AllAppsPanel.Visibility = _showAllTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectRunningTab(object sender, RoutedEventArgs e)
    {
        _showAllTab = false;
        ClearConfirmations();
        UpdateTabStyles();
    }

    private void OnSelectAllTab(object sender, RoutedEventArgs e)
    {
        _showAllTab = true;
        ClearConfirmations();
        UpdateTabStyles();
    }

    private void OnOpenSearchPage(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        _subPage = SubPage.Search;
        SearchInputBox.Text = string.Empty;
        RefreshSearchPage();
        UpdatePageVisibility();
        _controller.SyncSubPageActivation(_screenId, focus: true);
        Dispatcher.BeginInvoke(new Action(() => SearchInputBox.Focus()));
    }

    private void OnOpenModelsPage(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        _subPage = SubPage.PhoneModel;
        ModelSearchBox.Text = string.Empty;
        RefreshModelPage();
        UpdatePageVisibility();
        _controller.SyncSubPageActivation(_screenId, focus: true);
    }

    private void OnCloseSubPage(object sender, RoutedEventArgs e)
    {
        CloseSubPage();
        _controller.SyncSubPageActivation(_screenId, focus: false);
    }

    private void OnSearchInputChanged(object sender, TextChangedEventArgs e) => RefreshSearchPage();
    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchInputBox.Text = string.Empty;
    private void OnModelSearchChanged(object sender, TextChangedEventArgs e) => RefreshModelPage();

    private void OnSelectPhoneModel(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: PhoneModel m })
        {
            CloseSubPage();
            _controller.SyncSubPageActivation(_screenId, focus: false);
            _controller.SelectScreenDevice(_screenId ?? _controller.PrimaryTouchScreenId, PhoneChoice.FromModel(m));
        }
    }

    private void OnApplyCustomModel(object sender, RoutedEventArgs e)
    {
        int? w = int.TryParse(CustomWidthBox.Text.Trim(), out int wi) ? wi : null;
        int? h = int.TryParse(CustomHeightBox.Text.Trim(), out int hi) ? hi : null;
        var custom = PhoneScreenSpecData.Custom(w, h);
        if (custom is null) return;
        CloseSubPage();
        _controller.SyncSubPageActivation(_screenId, focus: false);
        _controller.SelectScreenDevice(_screenId ?? _controller.PrimaryTouchScreenId, new PhoneChoice(null, custom, _deviceKind));
    }

    private void OnBackToPlaced(object sender, RoutedEventArgs e) => _controller.ReturnToPlaced(_screenId);
    private void OnPutBack(object sender, RoutedEventArgs e) => _controller.PutBack(_screenId);
    private void OnZoomIn(object sender, RoutedEventArgs e) => _controller.ZoomBy(1, _screenId);
    private void OnZoomOut(object sender, RoutedEventArgs e) => _controller.ZoomBy(-1, _screenId);
    private void OnDismissNotice(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
            NoticeBanner.Visibility = Visibility.Collapsed;
        else
            _controller.DismissNotice(_screenId);
    }

    private void OnForceQuitFromNotice(object sender, RoutedEventArgs e)
    {
        if (_stalledQuit is { } sq)
            _controller.ForceQuitApp(sq.Pid, sq.App, _screenId);
    }

    private void OnAskDismissScreen(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
        {
            Close();
            return;
        }
        ClearConfirmations();
        _confirmDismiss = true;
        HomeScroll.ScrollToTop();
        SyncState();
    }

    private void OnConfirmDismissScreen(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        _controller.DismissScreen(_screenId);
    }

    private void OnCancelConfirm(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        SyncState();
    }

    private void OnAppHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: RunningApp ra })
        {
            ClearConfirmations();
            _controller.OpenApp(ra.App, _screenId);
        }
    }

    private void OnPullWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: WindowInfo w })
        {
            ClearConfirmations();
            _controller.PullWindow(w, _screenId);
        }
    }

    private void OnNewWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: RunningApp ra })
        {
            ClearConfirmations();
            _controller.NewWindow(ra, _screenId);
        }
    }

    private void OnAskQuitApp(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: RunningAppVm vm })
        {
            ClearConfirmations();
            _confirmQuitPid = vm.App.Pid;
            RefreshRunningAndAll();
        }
    }

    private void OnConfirmQuitApp(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: RunningApp ra })
        {
            ClearConfirmations();
            _controller.QuitApp(ra, _screenId);
        }
    }

    private void OnAskCloseWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: WindowVm vm })
        {
            ClearConfirmations();
            _confirmCloseWindow = vm.Window.Key;
            RefreshRunningAndAll();
        }
    }

    private void OnConfirmCloseWindow(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: WindowInfo w })
        {
            ClearConfirmations();
            _controller.CloseWindow(w, _screenId);
        }
    }

    private void OnOpenApp(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: AppInfo app })
        {
            ClearConfirmations();
            _controller.OpenApp(app, _screenId);
        }
    }

    private void OnSearchOpenApp(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
        {
            CloseSubPage();
            ShowPreviewReadOnlyNotice();
            return;
        }
        if (sender is System.Windows.Controls.Button { Tag: AppInfo app })
        {
            CloseSubPage();
            _controller.SyncSubPageActivation(_screenId, focus: false);
            _controller.OpenApp(app, _screenId);
        }
    }

    private void OnSearchPullWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
        {
            CloseSubPage();
            ShowPreviewReadOnlyNotice();
            return;
        }
        if (sender is System.Windows.Controls.Button { Tag: WindowInfo w })
        {
            CloseSubPage();
            _controller.SyncSubPageActivation(_screenId, focus: false);
            _controller.PullWindow(w, _screenId);
        }
    }
}
