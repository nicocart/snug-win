using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.App.Views;

public partial class PhoneModelWindow : Window
{
    public sealed record ModelRow(PhoneModel Model, SnugWin.Core.PhoneScreen.ScreenOrientation Orientation)
    {
        public string DisplayTitle => Model.Id;
        public string KindLabel => Model.Kind.DisplayName();
        public string ResolutionText
        {
            get
            {
                var (w, h) = Model.Screen.Dimensions(Orientation);
                return Model.Screen.Points is { } pts
                    ? $"{w} × {h}（{pts}pt）"
                    : $"{w} × {h}";
            }
        }
    }

    private readonly AppController _controller;
    private readonly Action<bool> _themeHandler;
    private readonly string? _targetScreenId;
    private readonly IReadOnlySet<DeviceKind> _allowedKinds;
    private readonly bool _selectionOnly;
    private readonly SnugWin.Core.PhoneScreen.ScreenOrientation _orientation;
    private DeviceKind? _selectedKindFilter;

    public PhoneChoice? SelectedChoice { get; private set; }

    public PhoneModelWindow(
        AppController controller,
        string? screenId = null,
        IReadOnlySet<DeviceKind>? allowedKinds = null,
        bool selectionOnly = false,
        string? customTitle = null,
        SnugWin.Core.PhoneScreen.ScreenOrientation? orientation = null)
    {
        InitializeComponent();
        _controller = controller;
        _selectionOnly = selectionOnly;
        _targetScreenId = screenId ?? controller.PrimaryTouchScreenId;

        var screenCfg = _targetScreenId is not null ? controller.GetScreenConfig(_targetScreenId) : null;
        _orientation = orientation ?? screenCfg?.Orientation ?? SnugWin.Core.PhoneScreen.ScreenOrientation.Portrait;
        _allowedKinds = allowedKinds
            ?? screenCfg?.Device.Kind.SwappableKinds()
            ?? new HashSet<DeviceKind> { DeviceKind.Phone, DeviceKind.Tablet, DeviceKind.Computer };

        CustomHintText.Text = _orientation == SnugWin.Core.PhoneScreen.ScreenOrientation.Landscape
            ? "自定义分辨率 / 比例（例如 2400 × 1080 或 20 × 9）"
            : "自定义分辨率 / 比例（例如 1080 × 2400 或 9 × 20）";

        if (!string.IsNullOrWhiteSpace(customTitle))
        {
            Title = customTitle;
            WindowTitleText.Text = customTitle;
        }
        else if (screenCfg is not null)
        {
            string heading = $"为「{screenCfg.Name}」选择设备型号";
            Title = heading;
            WindowTitleText.Text = heading;
        }

        ConfigureKindButtons();

        WindowPlacementHelper.ApplyMicaBackdrop(this, _controller.IsEffectiveDarkTheme);
        _themeHandler = isDark => Dispatcher.BeginInvoke(() => WindowPlacementHelper.ApplyMicaBackdrop(this, isDark));
        _controller.ThemeChanged += _themeHandler;
        Closed += (_, _) => _controller.ThemeChanged -= _themeHandler;

        RefreshHeaderLabel();
        RefreshList(string.Empty);
    }

    private void ConfigureKindButtons()
    {
        bool hasPhone = _allowedKinds.Contains(DeviceKind.Phone);
        bool hasTablet = _allowedKinds.Contains(DeviceKind.Tablet);
        bool hasComputer = _allowedKinds.Contains(DeviceKind.Computer);

        PhoneKindBtn.Visibility = hasPhone ? Visibility.Visible : Visibility.Collapsed;
        TabletKindBtn.Visibility = hasTablet ? Visibility.Visible : Visibility.Collapsed;
        ComputerKindBtn.Visibility = hasComputer ? Visibility.Visible : Visibility.Collapsed;

        ColPhone.Width = hasPhone ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColTablet.Width = hasTablet ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColComputer.Width = hasComputer ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        if (_allowedKinds.Count <= 1)
        {
            KindFilterBar.Visibility = Visibility.Collapsed;
            foreach (var only in _allowedKinds)
            {
                _selectedKindFilter = only;
                if (only == DeviceKind.Phone) PhoneKindBtn.IsChecked = true;
                else if (only == DeviceKind.Tablet) TabletKindBtn.IsChecked = true;
                else if (only == DeviceKind.Computer) ComputerKindBtn.IsChecked = true;
            }
        }
        else
        {
            KindFilterBar.Visibility = Visibility.Visible;
            AllKindBtn.Visibility = Visibility.Visible;
            ColAll.Width = new GridLength(1, GridUnitType.Star);
            AllKindBtn.IsChecked = true;
            _selectedKindFilter = null;
        }
    }

    private void RefreshHeaderLabel()
    {
        if (_selectionOnly)
        {
            string kindSummary = _selectedKindFilter?.DisplayName() ?? "全部可选分类";
            CurrentLabel.Text = $"新增虚拟屏（{kindSummary}）";
            return;
        }

        var cfg = _targetScreenId is not null ? _controller.GetScreenConfig(_targetScreenId) : null;
        var cur = cfg?.Device ?? _controller.Config.Phone;
        CurrentLabel.Text = $"{cur.Model ?? "自定义"}（{cur.Screen.SizeText(_orientation)}）";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnKindFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender == AllKindBtn) _selectedKindFilter = null;
        else if (sender == PhoneKindBtn) _selectedKindFilter = DeviceKind.Phone;
        else if (sender == TabletKindBtn) _selectedKindFilter = DeviceKind.Tablet;
        else if (sender == ComputerKindBtn) _selectedKindFilter = DeviceKind.Computer;
        RefreshList(SearchBox.Text);
    }

    public sealed record GenericPresetRow(string Label, PhoneChoice Choice);

    private void RefreshList(string query)
    {
        IReadOnlySet<DeviceKind> filter = _selectedKindFilter is { } single
            ? new HashSet<DeviceKind> { single }
            : _allowedKinds;

        var presets = new List<GenericPresetRow>();
        foreach (var preset in PhoneCatalog.GenericPresets)
        {
            if (filter.Contains(preset.Kind))
                presets.Add(new GenericPresetRow(preset.Model ?? preset.Kind.DisplayName(), preset));
        }
        GenericPresetsList.ItemsSource = presets;
        GenericPresetsCard.Visibility = presets.Count > 0 && string.IsNullOrWhiteSpace(query)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var rows = new List<ModelRow>();
        foreach (var brand in _controller.Catalog.Search(query, filter))
        {
            foreach (var m in brand.Models)
                rows.Add(new ModelRow(m, _orientation));
        }
        ModelList.ItemsSource = rows;
    }

    private void OnSelectGenericPreset(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: PhoneChoice choice })
            ApplyChoice(choice);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        RefreshList(SearchBox.Text);

    private void ApplyChoice(PhoneChoice choice)
    {
        if (_selectionOnly)
        {
            SelectedChoice = choice;
            DialogResult = true;
            Close();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_targetScreenId))
            _controller.SelectScreenDevice(_targetScreenId, choice);
        else
            _controller.SelectPhone(choice);
        Close();
    }

    private void OnSelectRow(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ModelRow row })
            ApplyChoice(PhoneChoice.FromModel(row.Model));
    }

    private void OnModelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModelList.SelectedItem is ModelRow row)
            ApplyChoice(PhoneChoice.FromModel(row.Model));
    }

    private void OnApplyCustom(object sender, RoutedEventArgs e)
    {
        int? w = int.TryParse(CustomWidthBox.Text.Trim(), out int wi) ? wi : null;
        int? h = int.TryParse(CustomHeightBox.Text.Trim(), out int hi) ? hi : null;
        var custom = PhoneScreenSpecData.Custom(w, h);
        if (custom is null)
        {
            System.Windows.MessageBox.Show("请输入有效的正整数分辨率或比例（1 ~ 20000）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var kind = _selectedKindFilter ?? (_allowedKinds.Count == 1 ? GetSingle(_allowedKinds) : DeviceKind.Phone);
        ApplyChoice(new PhoneChoice(null, custom, kind));
    }

    private static DeviceKind GetSingle(IReadOnlySet<DeviceKind> set)
    {
        foreach (var k in set) return k;
        return DeviceKind.Phone;
    }
}
