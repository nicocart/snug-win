using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace SnugWin.App.Views;

/// <summary>
/// Floating App Library panel docked next to the left-edge tab on a computer virtual screen
/// (1:1 Windows port of upstream Snug v2.9.0 ComputerLibrary &amp; ComputerLibraryView).
/// </summary>
public partial class ComputerLibraryWindow : Window
{
    public const double PanelWidthDip = 324.0;
    public const double PanelMaxHeightDip = 520.0;
    private const double EdgeMarginDip = 16.0;
    private const double TabGapDip = 8.0;

    public enum RowKind { Focus, Pull, Open }

    public sealed class LibraryRowVm
    {
        public RowKind Kind { get; init; }
        public WindowInfo? Window { get; init; }
        public AppInfo? App { get; init; }
        public ImageSource Icon { get; init; } = null!;
        public string Title { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public Visibility SubtitleVis => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        public string ActionLabel { get; init; } = string.Empty;
        public Brush ActionBrush { get; init; } = Brushes.Gray;
        public Brush RowBackground { get; init; } = Brushes.Transparent;
        public Visibility RestoreVis { get; init; } = Visibility.Collapsed;
        public bool Enabled { get; init; } = true;
    }

    public sealed record AllAppGridVm(AppInfo App, ImageSource Icon, double RunningDotOpacity)
    {
        public string Name => App.Name;
    }

    private readonly AppController _controller;
    private readonly string _screenId;
    private readonly Action _onOpenChanged;
    private bool _isOpen;
    private bool _showAllTab;
    private bool _closeWhenDone;
    private LibraryRowVm? _firstSearchHit;

    public bool IsLibraryOpen => _isOpen;

    public ComputerLibraryWindow(AppController controller, string screenId, Action onOpenChanged)
    {
        InitializeComponent();
        _controller = controller;
        _screenId = screenId;
        _onOpenChanged = onOpenChanged;
        UpdateTabButtons();
    }

    public void PositionNextToTab(RectD workAreaPhysical, RectD tabPhysical, double dpiScale)
    {
        double scale = Math.Max(1.0, dpiScale);
        double edge = EdgeMarginDip * scale;
        double gap = TabGapDip * scale;
        double physW = Math.Round(Math.Min(PanelWidthDip * scale, Math.Max(220 * scale, workAreaPhysical.Width - tabPhysical.Width - edge * 2)));
        double physH = Math.Round(Math.Min(PanelMaxHeightDip * scale, Math.Max(200 * scale, workAreaPhysical.Height - edge * 2)));
        double physX = Math.Round(tabPhysical.MaxX + gap);
        double centeredY = tabPhysical.MidY - physH / 2.0;
        double physY = Math.Round(Math.Clamp(centeredY, workAreaPhysical.Y + edge, Math.Max(workAreaPhysical.Y + edge, workAreaPhysical.MaxY - edge - physH)));
        WindowPlacementHelper.PlacePhysical(
            this,
            new RectD(physX, physY, physW, physH),
            topmost: true,
            noActivate: false,
            focusIfActivated: _isOpen);
    }

    public void ShowOverlay(RectD workAreaPhysical, RectD tabPhysical, double dpiScale)
    {
        _closeWhenDone = false;
        _isOpen = true;
        SearchBox.Text = string.Empty;
        ContentScroll.ScrollToTop();
        SyncState();
        PositionNextToTab(workAreaPhysical, tabPhysical, dpiScale);
        Activate();
        Dispatcher.BeginInvoke(() => SearchBox.Focus());
        _onOpenChanged();
    }

    public void DismissOverlay(string reason)
    {
        _closeWhenDone = false;
        if (!_isOpen) return;
        _isOpen = false;
        if (_controller.GetScreenState(_screenId).Notice is not null)
            _controller.DismissNotice(_screenId);
        Hide();
        _onOpenChanged();
    }

    public void SyncState()
    {
        var st = _controller.GetScreenState(_screenId);
        bool busy = st.Work is not null;

        if (_isOpen && _closeWhenDone && !busy)
        {
            _closeWhenDone = false;
            if (st.Notice is null)
            {
                DismissOverlay("done");
                return;
            }
            Activate();
        }

        if (!_isOpen) return;

        UpdateBanner(st);
        ContentHost.IsEnabled = !busy;
        ContentHost.Opacity = busy ? 0.55 : 1.0;

        string query = SearchBox.Text.Trim();
        bool searching = !string.IsNullOrEmpty(query);
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        SegmentBar.IsEnabled = !searching;
        SegmentBar.Opacity = searching ? 0.5 : 1.0;

        if (!searching && _showAllTab)
        {
            GroupedPanel.Visibility = Visibility.Collapsed;
            AllAppsGrid.Visibility = Visibility.Visible;
            _firstSearchHit = null;
            PopulateAllApps();
        }
        else
        {
            AllAppsGrid.Visibility = Visibility.Collapsed;
            GroupedPanel.Visibility = Visibility.Visible;
            PopulateGroups(st, query, searching);
        }
    }

    private void PopulateGroups(PhoneDeskState st, string query, bool searching)
    {
        uint displayId = st.DisplayId ?? 0;
        var running = _controller.RunningApps;
        var installed = _controller.InstalledApps;
        var spots = _controller.WindowSpots;
        var groups = ComputerList.Groups(displayId, query, running, installed, spots);

        var accentBg = ResolveBrush("AccentSoftBg", Color.FromArgb(0x20, 0x25, 0x63, 0xEB));
        var accentFg = ResolveBrush("AccentSoftText", Color.FromRgb(0x1D, 0x4E, 0xD8));
        var secondaryFg = ResolveBrush("TextSecondary", Color.FromRgb(0x47, 0x55, 0x69));

        LibraryRowVm? first = null;

        var hereRows = new List<LibraryRowVm>(groups.Here.Count);
        foreach (var w in groups.Here)
        {
            bool enabled = !st.Quitting.Contains(w.Key.Pid) && !st.Closing.Contains(w.Key);
            bool highlighted = searching && first is null && enabled;
            var vm = new LibraryRowVm
            {
                Kind = RowKind.Focus,
                Window = w,
                Icon = _controller.Icons.GetIcon(w),
                Title = string.IsNullOrEmpty(w.Title) ? w.AppName : w.Title,
                Subtitle = string.Equals(w.Title, w.AppName, StringComparison.OrdinalIgnoreCase) ? string.Empty : w.AppName,
                ActionLabel = "切换",
                ActionBrush = highlighted ? accentFg : secondaryFg,
                RowBackground = highlighted ? accentBg : Brushes.Transparent,
                RestoreVis = st.Brought.Contains(w.Key) ? Visibility.Visible : Visibility.Collapsed,
                Enabled = enabled
            };
            if (highlighted) first = vm;
            hereRows.Add(vm);
        }

        var elsewhereRows = new List<LibraryRowVm>(groups.Elsewhere.Count);
        foreach (var w in groups.Elsewhere)
        {
            bool enabled = !st.Quitting.Contains(w.Key.Pid) && !st.Closing.Contains(w.Key);
            bool highlighted = searching && first is null && enabled;
            spots.TryGetValue(w.Key, out var spot);
            var loc = ComputerList.LocationOf(w, spot, displayId);
            string locLabel = loc switch
            {
                ComputerLocation.Minimized => "已最小化",
                ComputerLocation.Shown s => _controller.GetDisplayLabel(s.DisplayId),
                _ => "其他位置"
            };
            string subtitle = string.IsNullOrEmpty(w.Title) || string.Equals(w.Title, w.AppName, StringComparison.OrdinalIgnoreCase)
                ? locLabel
                : $"{w.AppName} · {locLabel}";

            var vm = new LibraryRowVm
            {
                Kind = RowKind.Pull,
                Window = w,
                Icon = _controller.Icons.GetIcon(w),
                Title = string.IsNullOrEmpty(w.Title) ? w.AppName : w.Title,
                Subtitle = subtitle,
                ActionLabel = "移至此屏",
                ActionBrush = highlighted ? accentFg : secondaryFg,
                RowBackground = highlighted ? accentBg : Brushes.Transparent,
                Enabled = enabled
            };
            if (highlighted) first = vm;
            elsewhereRows.Add(vm);
        }

        var windowlessRows = new List<LibraryRowVm>(groups.Windowless.Count);
        foreach (var app in groups.Windowless)
        {
            bool highlighted = searching && first is null;
            var vm = new LibraryRowVm
            {
                Kind = RowKind.Open,
                App = app,
                Icon = _controller.Icons.GetIcon(app),
                Title = app.Name,
                ActionLabel = "打开",
                ActionBrush = highlighted ? accentFg : secondaryFg,
                RowBackground = highlighted ? accentBg : Brushes.Transparent
            };
            if (highlighted) first = vm;
            windowlessRows.Add(vm);
        }

        var unopenedRows = new List<LibraryRowVm>(groups.Unopened.Count);
        foreach (var app in groups.Unopened)
        {
            bool highlighted = searching && first is null;
            var vm = new LibraryRowVm
            {
                Kind = RowKind.Open,
                App = app,
                Icon = _controller.Icons.GetIcon(app),
                Title = app.Name,
                ActionLabel = "打开",
                ActionBrush = highlighted ? accentFg : secondaryFg,
                RowBackground = highlighted ? accentBg : Brushes.Transparent
            };
            if (highlighted) first = vm;
            unopenedRows.Add(vm);
        }

        _firstSearchHit = first;

        HereList.ItemsSource = hereRows;
        HereSection.Visibility = hereRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ElsewhereList.ItemsSource = elsewhereRows;
        ElsewhereSection.Visibility = elsewhereRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        WindowlessList.ItemsSource = windowlessRows;
        WindowlessSection.Visibility = windowlessRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UnopenedList.ItemsSource = unopenedRows;
        UnopenedSection.Visibility = unopenedRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        EmptyStateText.Visibility = groups.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = searching ? "没有匹配的应用或窗口" : (_controller.CatalogLoaded ? "没有正在运行的 App" : "正在读取…");
    }

    private void PopulateAllApps()
    {
        var runningIds = new HashSet<string>(
            _controller.RunningApps.Select(r => r.App.AppId),
            StringComparer.OrdinalIgnoreCase);

        AllAppsGrid.ItemsSource = _controller.InstalledApps
            .Select(a => new AllAppGridVm(a, _controller.Icons.GetIcon(a), runningIds.Contains(a.AppId) ? 1.0 : 0.0))
            .ToList();
    }

    private void UpdateBanner(PhoneDeskState st)
    {
        if (st.Work is { } work)
        {
            BannerCard.Visibility = Visibility.Visible;
            BannerCloseBtn.Visibility = Visibility.Collapsed;
            BannerTitle.Text = work.Kind switch
            {
                DeskWorkKind.Opening => $"正在打开 {work.Label}…",
                DeskWorkKind.Placing => $"正在把 {work.Label} 挪过来…",
                DeskWorkKind.PuttingBack => "正在还原…",
                DeskWorkKind.Reshaping => "正在调整屏幕比例…",
                _ => "请稍候…"
            };
            BannerDetail.Text = string.Empty;
            BannerDetail.Visibility = Visibility.Collapsed;
            return;
        }

        if (st.Notice is { } notice)
        {
            BannerCard.Visibility = Visibility.Visible;
            BannerCloseBtn.Visibility = Visibility.Visible;
            (string head, string detail) = notice switch
            {
                DeskNotice.WindowGone g => ($"{g.App} 窗口已关闭", "请另选一个窗口"),
                DeskNotice.WindowUnresponsive u => ($"{u.App} 无响应", "可能正忙，请稍后再试"),
                DeskNotice.RecordUnwritable => ("无法保存原位置", "为确保能放回原处，窗口未移动"),
                DeskNotice.ScreenNotReady => ("屏幕未就绪", "屏幕正在调整，请稍后再试"),
                DeskNotice.OpenFailed o => ($"无法打开 {o.App}", "请稍后再试"),
                DeskNotice.NoWindow n => ($"未找到 {n.App} 的窗口", "App 已打开，但没有可显示的窗口"),
                _ => ("提示", string.Empty)
            };
            BannerTitle.Text = head;
            BannerDetail.Text = detail;
            BannerDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        BannerCard.Visibility = Visibility.Collapsed;
    }

    private void ExecuteRow(LibraryRowVm vm)
    {
        if (!vm.Enabled) return;
        switch (vm.Kind)
        {
            case RowKind.Focus when vm.Window is { } fw:
                _controller.FocusComputerWindow(_screenId, fw);
                DismissOverlay("focus");
                break;
            case RowKind.Pull when vm.Window is { } pw:
                _controller.PullComputerWindow(_screenId, pw);
                SettleAfterAction();
                break;
            case RowKind.Open when vm.App is { } app:
                _controller.OpenComputerApp(_screenId, app);
                SettleAfterAction();
                break;
        }
    }

    private void SettleAfterAction()
    {
        var st = _controller.GetScreenState(_screenId);
        if (st.Work is not null)
            _closeWhenDone = true;
        else if (st.Notice is null)
            DismissOverlay("settled");
    }

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: LibraryRowVm vm })
            ExecuteRow(vm);
    }

    private void OnRestoreRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: LibraryRowVm { Window: { } w } })
            _controller.PutBackComputerWindow(_screenId, w.Key);
    }

    private void OnAllAppClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AppInfo app }) return;
        var st = _controller.GetScreenState(_screenId);
        uint displayId = st.DisplayId ?? 0;
        switch (ComputerList.ActionFor(app, displayId, _controller.RunningApps, _controller.WindowSpots))
        {
            case ComputerAppAction.Focus f:
                _controller.FocusComputerWindow(_screenId, f.Window);
                DismissOverlay("focus-app");
                break;
            case ComputerAppAction.Pull p:
                _controller.PullComputerWindow(_screenId, p.Window);
                SettleAfterAction();
                break;
            case ComputerAppAction.Open o:
                _controller.OpenComputerApp(_screenId, o.App);
                SettleAfterAction();
                break;
        }
    }

    private void OnSelectRunningTab(object sender, RoutedEventArgs e)
    {
        _showAllTab = false;
        UpdateTabButtons();
        SyncState();
    }

    private void OnSelectAllTab(object sender, RoutedEventArgs e)
    {
        _showAllTab = true;
        UpdateTabButtons();
        SyncState();
    }

    private void UpdateTabButtons()
    {
        RunningTabBtn.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            !_showAllTab ? "PhoneSegmentBg" : "TransparentBrushFallback");
        AllTabBtn.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            _showAllTab ? "PhoneSegmentBg" : "TransparentBrushFallback");
        if (_showAllTab) RunningTabBtn.Background = Brushes.Transparent;
        else AllTabBtn.Background = Brushes.Transparent;

        RunningTabBtn.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            !_showAllTab ? "TextPrimary" : "TextSecondary");
        AllTabBtn.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            _showAllTab ? "TextPrimary" : "TextSecondary");
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => SyncState();
    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchBox.Text = string.Empty;
    private void OnDismissBanner(object sender, RoutedEventArgs e) => _controller.DismissNotice(_screenId);

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
                SearchBox.Text = string.Empty;
            else
                DismissOverlay("Esc");
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SearchBox.Text) && _firstSearchHit is { } hit)
        {
            var st = _controller.GetScreenState(_screenId);
            if (st.Work is null)
                ExecuteRow(hit);
            e.Handled = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_isOpen || _closeWhenDone) return;
        if (_controller.GetScreenState(_screenId).Work is not null)
            _closeWhenDone = true;
        else
            DismissOverlay("deactivated");
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush b)
            return b;
        var created = new SolidColorBrush(fallback);
        created.Freeze();
        return created;
    }
}
