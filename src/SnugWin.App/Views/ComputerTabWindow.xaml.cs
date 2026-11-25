using System;
using System.Windows;
using System.Windows.Input;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.App.Views;

/// <summary>
/// Left-edge vertical pill docked on each ready computer virtual screen
/// (1:1 Windows port of upstream Snug v2.9.0 ComputerTabView).
/// </summary>
public partial class ComputerTabWindow : Window
{
    public const double TabWidthDip = 18.0;
    public const double TabHeightDip = 68.0;

    private readonly Action _onToggle;

    public ComputerTabWindow(Action onToggle)
    {
        InitializeComponent();
        _onToggle = onToggle;
    }

    public RectD LastPhysicalBounds { get; private set; }

    public void UpdateVisual(bool isOpen, DeskWork? work, DeskNotice? notice)
    {
        bool showOpen = isOpen;
        bool showWorking = !isOpen && work is not null;
        bool showNotice = !isOpen && work is null && notice is not null;
        bool showIdle = !showOpen && !showWorking && !showNotice;

        ChevronRight.Visibility = showIdle ? Visibility.Visible : Visibility.Collapsed;
        ChevronLeft.Visibility = showOpen ? Visibility.Visible : Visibility.Collapsed;
        WorkingGlyph.Visibility = showWorking ? Visibility.Visible : Visibility.Collapsed;
        NoticeGlyph.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;

        TabPill.SetResourceReference(
            System.Windows.Controls.Border.BackgroundProperty,
            showOpen ? "AccentSoftBg" : "BubbleBg");

        TabPill.ToolTip = showWorking
            ? FormatWorkTooltip(work!)
            : showNotice
                ? FormatNoticeTooltip(notice!)
                : "App 资源库";
    }

    public void ShowAtLeftEdge(RectD workAreaPhysical, double dpiScale)
    {
        double scale = Math.Max(1.0, dpiScale);
        double physW = Math.Round(TabWidthDip * scale);
        double physH = Math.Round(TabHeightDip * scale);
        double physX = Math.Round(workAreaPhysical.X);
        double physY = Math.Round(workAreaPhysical.Y + Math.Max(0, (workAreaPhysical.Height - physH) / 2.0));
        LastPhysicalBounds = new RectD(physX, physY, physW, physH);
        WindowPlacementHelper.PlacePhysical(this, LastPhysicalBounds, topmost: true, noActivate: true);
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _onToggle();
    }

    private static string FormatWorkTooltip(DeskWork work) => work.Kind switch
    {
        DeskWorkKind.Opening => $"正在打开 {work.Label}…",
        DeskWorkKind.Placing => $"正在把 {work.Label} 挪过来…",
        DeskWorkKind.PuttingBack => "正在还原…",
        DeskWorkKind.Reshaping => "正在调整屏幕比例…",
        _ => "请稍候…"
    };

    private static string FormatNoticeTooltip(DeskNotice notice) => notice switch
    {
        DeskNotice.WindowGone g => $"{g.App} 窗口已关闭",
        DeskNotice.WindowUnresponsive u => $"{u.App} 无响应",
        DeskNotice.RecordUnwritable => "无法保存原位置",
        DeskNotice.ScreenNotReady => "屏幕未就绪",
        DeskNotice.OpenFailed o => $"无法打开 {o.App}",
        DeskNotice.NoWindow n => $"未找到 {n.App} 的窗口",
        _ => "App 资源库"
    };
}
