using System.Globalization;
using DSPiConsole.Models;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using System.Linq;

namespace DSPiConsole;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly MainViewModel _vm;

    // Per-row tracking for live pin assignment
    private readonly List<(PinOutput output, ComboBox typePicker, ComboBox gpioPicker, Border badge)> _outputRows = new();
    private TextBlock? _statusText;
    private bool _suppressSelectionChanged;

    // I2S configuration controls
    private ComboBox? _bckPinCombo;
    private ToggleSwitch? _mckToggle;
    private ComboBox? _mckPinCombo;
    private ComboBox? _mckMultiplierCombo;
    private TextBlock? _lrckSubtitle;
    private TextBlock? _mckMultiplierCaption;

    public SettingsDialog(MainViewModel vm)
    {
        _vm = vm;

        InitializeComponent();

        var settings = AppSettings.Instance;

        GlowToggle.IsOn = settings.ShowGraphGlow;
        LineWidthSlider.Value = settings.GraphLineWidth;
        AnimSpeedSlider.Value = settings.GraphAnimationSpeed;
        DebugToggle.IsOn = settings.ShowDebugInfo;

        LineWidthText.Text = settings.GraphLineWidth.ToString("F1", CultureInfo.InvariantCulture);
        AnimSpeedText.Text = settings.GraphAnimationSpeed.ToString("F2", CultureInfo.InvariantCulture);

        LineWidthSlider.ValueChanged += (s, e) => LineWidthText.Text = e.NewValue.ToString("F1", CultureInfo.InvariantCulture);
        AnimSpeedSlider.ValueChanged += (s, e) => AnimSpeedText.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);

        // Graph scale controls
        DbRangeSlider.Value = settings.GraphDbRange;
        DbCenterSlider.Value = settings.GraphDbCenter;
        UpdateDbRangeText(settings.GraphDbRange);
        UpdateDbCenterText(settings.GraphDbCenter, settings.GraphDbRange);

        DbRangeSlider.ValueChanged += (s, e) =>
        {
            UpdateDbRangeText(e.NewValue);
            UpdateDbCenterText(DbCenterSlider.Value, e.NewValue);
        };
        DbCenterSlider.ValueChanged += (s, e) =>
        {
            UpdateDbCenterText(e.NewValue, DbRangeSlider.Value);
        };

        SelectComboByTag(MinFreqCombo, settings.GraphMinFrequency);
        SelectComboByTag(MaxFreqCombo, settings.GraphMaxFrequency);

        FreqGridToggle.IsOn = settings.ShowFrequencyGrid;
        FreqLabelsToggle.IsOn = settings.ShowFrequencyLabels;
        DbGridToggle.IsOn = settings.ShowDbGrid;
        DbLabelsToggle.IsOn = settings.ShowDbLabels;
        DbUnitsToggle.IsOn = settings.ShowDbUnits;
        DottedInactiveToggle.IsOn = settings.DottedInactiveChannels;
        PopoutFollowsChannelToggle.IsOn = settings.PopoutFollowsSelectedChannel;
        ShowSaveButtonToggle.IsOn = settings.ShowPresetSaveButton;

        PrimaryButtonClick += OnSave;

        InitializeGeneralTab();
        InitializePresetsTab();
        BuildPinAssignmentTable();
    }

    private bool _suppressGeneralEvents;

    private void InitializeGeneralTab()
    {
        _suppressGeneralEvents = true;
        MasterVolumeModeCombo.SelectedIndex = _vm.MasterVolumeMode == 1 ? 1 : 0;
        _suppressGeneralEvents = false;
    }

    private async void OnMasterVolumeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGeneralEvents) return;
        if (MasterVolumeModeCombo.SelectedItem is not ComboBoxItem item) return;
        if (!byte.TryParse(item.Tag?.ToString() ?? "0", out var mode)) return;
        await _vm.SetMasterVolumeMode(mode);
    }

    private bool _suppressPresetEvents;

    private void InitializePresetsTab()
    {
        if (!_vm.PresetsSupported)
        {
            PresetsPanel.Children.Clear();
            PresetsPanel.Children.Add(new TextBlock
            {
                Text = "Presets are not supported by this firmware version.",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        _suppressPresetEvents = true;

        // Startup mode
        StartupModeCombo.SelectedIndex = _vm.PresetStartupMode;

        // Default preset combo
        DefaultPresetCombo.Items.Clear();
        for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
        {
            DefaultPresetCombo.Items.Add(new ComboBoxItem
            {
                Content = _vm.GetPresetDisplayName(i),
                Tag = i
            });
        }
        DefaultPresetCombo.SelectedIndex = _vm.PresetDefaultSlot;
        DefaultPresetCombo.IsEnabled = _vm.PresetStartupMode == 0;

        // Include pins
        IncludePinsToggle.IsOn = _vm.PresetIncludePins;

        _suppressPresetEvents = false;
    }

    private async void OnStartupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetEvents) return;
        if (StartupModeCombo.SelectedIndex < 0) return;

        byte mode = (byte)StartupModeCombo.SelectedIndex;
        DefaultPresetCombo.IsEnabled = mode == 0;

        byte defaultSlot = DefaultPresetCombo.SelectedItem is ComboBoxItem di && di.Tag is int ds ? (byte)ds : (byte)0;
        await _vm.SetPresetStartup(mode, defaultSlot);
    }

    private async void OnDefaultPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetEvents) return;
        if (DefaultPresetCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int slot) return;

        byte mode = (byte)StartupModeCombo.SelectedIndex;
        await _vm.SetPresetStartup(mode, (byte)slot);
    }

    private async void OnIncludePinsToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressPresetEvents) return;
        await _vm.SetPresetIncludePins(IncludePinsToggle.IsOn);
    }


    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var settings = AppSettings.Instance;
        settings.ShowGraphGlow = GlowToggle.IsOn;
        settings.GraphLineWidth = LineWidthSlider.Value;
        settings.GraphAnimationSpeed = AnimSpeedSlider.Value;
        settings.ShowDebugInfo = DebugToggle.IsOn;

        settings.GraphDbRange = DbRangeSlider.Value;
        settings.GraphDbCenter = DbCenterSlider.Value;
        settings.GraphMinFrequency = ReadComboTagDouble(MinFreqCombo, 20.0);
        settings.GraphMaxFrequency = ReadComboTagDouble(MaxFreqCombo, 20000.0);

        settings.ShowFrequencyGrid = FreqGridToggle.IsOn;
        settings.ShowFrequencyLabels = FreqLabelsToggle.IsOn;
        settings.ShowDbGrid = DbGridToggle.IsOn;
        settings.ShowDbLabels = DbLabelsToggle.IsOn;
        settings.ShowDbUnits = DbUnitsToggle.IsOn;
        settings.DottedInactiveChannels = DottedInactiveToggle.IsOn;
        settings.PopoutFollowsSelectedChannel = PopoutFollowsChannelToggle.IsOn;
        settings.ShowPresetSaveButton = ShowSaveButtonToggle.IsOn;

        settings.Save();
        settings.NotifyChanged();
    }

    private void UpdateDbRangeText(double range)
    {
        DbRangeText.Text = $"\u00b1{range / 2:0} dB";
    }

    private void UpdateDbCenterText(double center, double range)
    {
        double bottom = center - range / 2;
        double top = center + range / 2;
        DbCenterText.Text = $"{bottom:0} to {top:+0;-0;0} dB";
    }

    private static void SelectComboByTag(ComboBox combo, double value)
    {
        string valStr = value.ToString("0");
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == valStr)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static double ReadComboTagDouble(ComboBox combo, double fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            return val;
        return fallback;
    }

    // --- Pin Assignment table ---

    private record PinOutput(int Id, string Name, string Detail, string Icon, byte DefaultPin, Color Color, int SlotIndex);

    private static readonly PinOutput[] PinOutputsRp2350 =
    [
        new(0, "Output 1", "OUT 1/2", "\uE767", 6,
            Color.FromArgb(255, 69, 194, 163), 0),   // Teal
        new(1, "Output 2", "OUT 3/4", "\uE767", 7,
            Color.FromArgb(255, 240, 196, 89), 1),    // Yellow
        new(2, "Output 3", "OUT 5/6", "\uE767", 8,
            Color.FromArgb(255, 89, 140, 242), 2),    // Blue
        new(3, "Output 4", "OUT 7/8", "\uE767", 9,
            Color.FromArgb(255, 217, 115, 140), 3),   // Pink
        new(4, "PDM",      "SUB OUT", "\uE9B1", 10,
            Color.FromArgb(255, 186, 135, 243), -1),  // Purple
    ];

    private static readonly PinOutput[] PinOutputsRp2040 =
    [
        new(0, "Output 1", "OUT 1/2", "\uE767", 6,
            Color.FromArgb(255, 69, 194, 163), 0),
        new(1, "Output 2", "OUT 3/4", "\uE767", 7,
            Color.FromArgb(255, 240, 196, 89), 1),
        new(2, "PDM",      "SUB OUT", "\uE9B1", 10,
            Color.FromArgb(255, 186, 135, 243), -1),
    ];

    private static readonly byte[] ValidPins =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
        26, 27, 28
    ];

    /// <summary>
    /// Map PinOutput.Id to the matrix mixer output index used by IsOutputEnabled.
    /// S/PDIF outputs use their pair's first (L) channel; PDM is always the last output.
    /// </summary>
    private int PinOutputIdToMatrixIndex(PinOutput output)
    {
        if (output.SlotIndex < 0) // PDM
            return _vm.ActiveOutputs.Count - 1;
        return output.Id * 2;
    }

    private PinOutput[] GetAllPinOutputs() =>
        _vm.Platform == "RP2350" ? PinOutputsRp2350 : PinOutputsRp2040;

    private void BuildPinAssignmentTable()
    {
        var outputs = GetAllPinOutputs().ToList();

        // Fetch current pin and I2S values from device on a background thread, then populate UI
        _ = Task.Run(() =>
        {
            foreach (var output in outputs)
                _vm.FetchOutputPin(output.Id);
            // Fetch I2S state
            int slotCount = _vm.NumOutputSlots;
            for (int s = 0; s < slotCount; s++)
                _vm.FetchOutputSlotType(s);
            _vm.FetchI2SBckPin();
            _vm.FetchMckEnable();
            _vm.FetchMckPin();
            _vm.FetchMckMultiplier();
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PopulatePinValues(outputs);
                PopulateI2SValues();
            });
        });

        // ── Output Assignment section ──
        BuildSectionHeader("Output Assignment", "\uE950", OnResetToDefaults);

        // Separator
        HardwarePanel.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 2, 0, 2)
        });

        // Pin rows (only enabled outputs)
        foreach (var output in outputs)
        {
            HardwarePanel.Children.Add(BuildPinRow(output));
        }

        // Status TextBlock for feedback
        _statusText = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        HardwarePanel.Children.Add(_statusText);

        // ── I2S Configuration section ──
        BuildI2SConfigSection();
    }

    private void BuildSectionHeader(string title, string glyph, RoutedEventHandler? resetHandler = null)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerIcon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal };
        headerLeft.Children.Add(headerIcon);
        headerLeft.Children.Add(headerText);
        Grid.SetColumn(headerLeft, 0);
        header.Children.Add(headerLeft);

        if (resetHandler != null)
        {
            var resetBtn = new HyperlinkButton
            {
                Content = "Reset to Defaults",
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2)
            };
            resetBtn.Click += resetHandler;
            Grid.SetColumn(resetBtn, 1);
            header.Children.Add(resetBtn);
        }

        HardwarePanel.Children.Add(header);
    }

    private void PopulatePinValues(List<PinOutput> outputs)
    {
        _suppressSelectionChanged = true;
        foreach (var (output, _, gpioPicker, badge) in _outputRows)
        {
            byte currentPin = _vm.GetOutputPinValue(output.Id);
            var idx = Array.IndexOf(ValidPins, currentPin);
            if (idx >= 0)
                gpioPicker.SelectedIndex = idx;

            UpdateBadgeVisibility(badge, currentPin, output.DefaultPin);
            UpdateComboConflicts(output);
        }
        _suppressSelectionChanged = false;
    }

    private void PopulateI2SValues()
    {
        _suppressSelectionChanged = true;

        // Output type pickers
        foreach (var (output, typePicker, _, _) in _outputRows)
        {
            if (output.SlotIndex >= 0)
            {
                var type = _vm.GetOutputSlotType(output.SlotIndex);
                typePicker.SelectedIndex = type == OutputSlotType.I2S ? 1 : 0;
            }
        }

        // BCK pin
        if (_bckPinCombo != null)
        {
            var idx = Array.IndexOf(ValidPins, _vm.I2SBckPin);
            if (idx >= 0) _bckPinCombo.SelectedIndex = idx;
        }

        // MCK toggle
        if (_mckToggle != null)
            _mckToggle.IsOn = _vm.MckEnabled;

        // MCK pin
        if (_mckPinCombo != null)
        {
            var idx = Array.IndexOf(ValidPins, _vm.MckPin);
            if (idx >= 0) _mckPinCombo.SelectedIndex = idx;
        }

        // MCK multiplier
        if (_mckMultiplierCombo != null)
            _mckMultiplierCombo.SelectedIndex = _vm.MckMultiplier == 256 ? 1 : 0;

        _suppressSelectionChanged = false;

        UpdateI2SConstraints();
    }

    private UIElement BuildPinRow(PinOutput output)
    {
        // Main row grid: Icon | Name+Detail | Type picker | DEFAULT badge | GPIO picker
        var row = new Grid
        {
            Padding = new Thickness(0, 6, 0, 6),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // icon
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // name+detail
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // type picker
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // default badge (centered)
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // gpio picker

        // Colored dot
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(output.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(dot, 0);
        row.Children.Add(dot);

        // Label
        var label = new TextBlock
        {
            Text = output.Detail,
            FontSize = 12,
            MinWidth = 54,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0)
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        // Type picker (S/PDIF | I2S or PDM)
        var typePicker = new ComboBox
        {
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(15, 0, 0, 0)
        };
        if (output.SlotIndex >= 0)
        {
            typePicker.Items.Add(new ComboBoxItem { Content = "S/PDIF", Tag = OutputSlotType.Spdif });
            typePicker.Items.Add(new ComboBoxItem { Content = "I2S", Tag = OutputSlotType.I2S });
            typePicker.SelectedIndex = 0; // Default, overwritten by PopulateI2SValues
            typePicker.Tag = output;
            typePicker.SelectionChanged += OnOutputTypeChanged;
        }
        else
        {
            typePicker.Items.Add(new ComboBoxItem { Content = "PDM" });
            typePicker.SelectedIndex = 0;
            typePicker.IsEnabled = false;
        }
        Grid.SetColumn(typePicker, 2);
        row.Children.Add(typePicker);

        // DEFAULT badge (centered in Star spacer column, nudged right)
        var badge = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "DEFAULT",
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            }
        };
        Grid.SetColumn(badge, 3);
        row.Children.Add(badge);

        // GPIO picker (ComboBox)
        var gpioPicker = new ComboBox
        {
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Tag = output
        };
        foreach (var pin in ValidPins)
        {
            gpioPicker.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        }
        // Select the default pin initially (will be overwritten by PopulatePinValues)
        var defaultIndex = Array.IndexOf(ValidPins, output.DefaultPin);
        if (defaultIndex >= 0) gpioPicker.SelectedIndex = defaultIndex;

        gpioPicker.SelectionChanged += OnPinSelectionChanged;

        Grid.SetColumn(gpioPicker, 4);
        row.Children.Add(gpioPicker);

        _outputRows.Add((output, typePicker, gpioPicker, badge));

        return row;
    }

    private async void OnOutputTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (sender is not ComboBox combo || combo.Tag is not PinOutput output) return;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not OutputSlotType newType) return;

        ClearStatus();

        var status = await Task.Run(() => _vm.SetOutputSlotType(output.SlotIndex, newType));

        if (status == PinConfigResult.Success)
        {
            UpdateI2SConstraints();
            RefreshAllConflicts();
            string typeName = newType == OutputSlotType.I2S ? "I2S" : "S/PDIF";
            ShowStatus($"{output.Name} → {typeName}", isError: false);
            return;
        }

        // Revert on error
        _suppressSelectionChanged = true;
        combo.SelectedIndex = _vm.GetOutputSlotType(output.SlotIndex) == OutputSlotType.I2S ? 1 : 0;
        _suppressSelectionChanged = false;
        ShowStatus(GetI2SErrorMessage(status, output.Name), isError: true);
    }

    private async void OnPinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (sender is not ComboBox combo || combo.Tag is not PinOutput output) return;
        if (combo.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not byte newPin) return;

        ClearStatus();

        var status = await Task.Run(() => _vm.SetOutputPinValue(output.Id, newPin));

        if (status == PinConfigResult.Success)
        {
            // Update badge and conflict display for all rows
            var row = _outputRows.FirstOrDefault(r => r.output.Id == output.Id);
            if (row != default)
                UpdateBadgeVisibility(row.badge, newPin, output.DefaultPin);
            RefreshAllConflicts();
            ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
            return;
        }

        if (status == PinConfigResult.OutputActive && output.SlotIndex < 0) // PDM
        {
            // Auto-cycle: disable PDM, set pin, re-enable
            var cycleStatus = await Task.Run(() =>
            {
                int pdmMatrixIndex = PinOutputIdToMatrixIndex(output);
                _vm.Device.SetOutputEnable(pdmMatrixIndex, false);
                var result = _vm.SetOutputPinValue(output.Id, newPin);
                _vm.Device.SetOutputEnable(pdmMatrixIndex, true);
                return result;
            });

            if (cycleStatus == PinConfigResult.Success)
            {
                var row = _outputRows.FirstOrDefault(r => r.output.Id == output.Id);
                if (row != default)
                    UpdateBadgeVisibility(row.badge, newPin, output.DefaultPin);
                RefreshAllConflicts();
                ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
                return;
            }

            status = cycleStatus;
        }

        // Error: revert ComboBox to device's actual value
        RevertGpioCombo(output);
        ShowStatus(GetErrorMessage(status, output.Name), isError: true);
    }

    private async void OnResetToDefaults(object sender, RoutedEventArgs e)
    {
        ClearStatus();

        // Reset all output types to S/PDIF first
        int slotCount = _vm.NumOutputSlots;
        for (int s = 0; s < slotCount; s++)
        {
            if (_vm.GetOutputSlotType(s) != OutputSlotType.Spdif)
            {
                var typeStatus = await Task.Run(() => _vm.SetOutputSlotType(s, OutputSlotType.Spdif));
                if (typeStatus != PinConfigResult.Success)
                {
                    ShowStatus($"Failed to reset Output {s + 1} type", isError: true);
                    return;
                }
            }
        }

        // Reset GPIO pins
        foreach (var (output, typePicker, gpioPicker, badge) in _outputRows)
        {
            byte defaultPin = output.DefaultPin;
            byte currentPin = _vm.GetOutputPinValue(output.Id);
            if (currentPin == defaultPin) continue;

            byte status;
            if (output.SlotIndex < 0) // PDM
            {
                status = await Task.Run(() =>
                {
                    int pdmMatrixIndex = PinOutputIdToMatrixIndex(output);
                    _vm.Device.SetOutputEnable(pdmMatrixIndex, false);
                    var result = _vm.SetOutputPinValue(output.Id, defaultPin);
                    _vm.Device.SetOutputEnable(pdmMatrixIndex, true);
                    return result;
                });
            }
            else
            {
                status = await Task.Run(() => _vm.SetOutputPinValue(output.Id, defaultPin));
            }

            if (status == PinConfigResult.Success)
            {
                _suppressSelectionChanged = true;
                var idx = Array.IndexOf(ValidPins, defaultPin);
                if (idx >= 0) gpioPicker.SelectedIndex = idx;
                UpdateBadgeVisibility(badge, defaultPin, output.DefaultPin);
                _suppressSelectionChanged = false;
            }
            else
            {
                ShowStatus($"Failed to reset {output.Name}: {GetErrorMessage(status, output.Name)}", isError: true);
                return;
            }
        }

        // Reset I2S clock config to defaults
        await Task.Run(() =>
        {
            _vm.SetMckEnable(false);
            _vm.SetI2SBckPin(14);
            _vm.SetMckPin(13);
            _vm.SetMckMultiplier(128);
        });

        // Refresh all UI
        _suppressSelectionChanged = true;
        foreach (var (output, typePicker, _, _) in _outputRows)
        {
            if (output.SlotIndex >= 0)
                typePicker.SelectedIndex = 0; // S/PDIF
        }
        _suppressSelectionChanged = false;

        PopulateI2SValues();
        RefreshAllConflicts();
        ShowStatus("All settings reset to defaults", isError: false);
    }

    private void RevertGpioCombo(PinOutput output)
    {
        var row = _outputRows.FirstOrDefault(r => r.output.Id == output.Id);
        if (row == default) return;

        _suppressSelectionChanged = true;
        byte devicePin = _vm.GetOutputPinValue(output.Id);
        var idx = Array.IndexOf(ValidPins, devicePin);
        if (idx >= 0) row.gpioPicker.SelectedIndex = idx;
        _suppressSelectionChanged = false;
    }

    private void UpdateBadgeVisibility(Border badge, byte currentPin, byte defaultPin)
    {
        badge.Visibility = currentPin == defaultPin ? Visibility.Visible : Visibility.Collapsed;
    }

    private Dictionary<byte, string> BuildPinOwnerMap(int excludeOutputId = -1)
    {
        var pinOwners = new Dictionary<byte, string>();
        foreach (var (other, _, _, _) in _outputRows)
        {
            if (other.Id == excludeOutputId) continue;
            byte otherPin = _vm.GetOutputPinValue(other.Id);
            pinOwners[otherPin] = other.Name;
        }

        // I2S clock pins
        pinOwners[_vm.I2SBckPin] = "BCK";
        pinOwners[(byte)(_vm.I2SBckPin + 1)] = "LRCK";
        if (_vm.MckEnabled)
            pinOwners[_vm.MckPin] = "MCK";

        return pinOwners;
    }

    private void UpdateComboConflicts(PinOutput targetOutput)
    {
        var row = _outputRows.FirstOrDefault(r => r.output.Id == targetOutput.Id);
        if (row == default) return;

        var pinOwners = BuildPinOwnerMap(excludeOutputId: targetOutput.Id);

        _suppressSelectionChanged = true;
        for (int i = 0; i < ValidPins.Length; i++)
        {
            if (row.gpioPicker.Items[i] is ComboBoxItem item)
            {
                byte pin = ValidPins[i];
                if (pinOwners.TryGetValue(pin, out var ownerName))
                    item.Content = $"GPIO {pin} ({ownerName})";
                else
                    item.Content = $"GPIO {pin}";
            }
        }
        _suppressSelectionChanged = false;
    }

    private void UpdateI2SPinConflicts()
    {
        // Build map of output data pins (for BCK/MCK combo conflict labels)
        var dataPinOwners = new Dictionary<byte, string>();
        foreach (var (other, _, _, _) in _outputRows)
        {
            byte pin = _vm.GetOutputPinValue(other.Id);
            dataPinOwners[pin] = other.Name;
        }
        // Add other clock pins
        dataPinOwners[(byte)(_vm.I2SBckPin + 1)] = "LRCK";
        if (_vm.MckEnabled)
            dataPinOwners[_vm.MckPin] = "MCK";

        // Update BCK combo labels
        if (_bckPinCombo != null)
        {
            _suppressSelectionChanged = true;
            for (int i = 0; i < ValidPins.Length; i++)
            {
                if (_bckPinCombo.Items[i] is ComboBoxItem item)
                {
                    byte pin = ValidPins[i];
                    // BCK reserves both pin and pin+1
                    if (dataPinOwners.TryGetValue(pin, out var owner) && owner != "LRCK")
                        item.Content = $"GPIO {pin} ({owner})";
                    else
                        item.Content = $"GPIO {pin}";
                }
            }
            _suppressSelectionChanged = false;
        }

        // Update MCK combo labels
        if (_mckPinCombo != null)
        {
            var mckOwners = new Dictionary<byte, string>(dataPinOwners);
            mckOwners[_vm.I2SBckPin] = "BCK";
            mckOwners[(byte)(_vm.I2SBckPin + 1)] = "LRCK";

            _suppressSelectionChanged = true;
            for (int i = 0; i < ValidPins.Length; i++)
            {
                if (_mckPinCombo.Items[i] is ComboBoxItem item)
                {
                    byte pin = ValidPins[i];
                    if (mckOwners.TryGetValue(pin, out var owner) && owner != "MCK")
                        item.Content = $"GPIO {pin} ({owner})";
                    else
                        item.Content = $"GPIO {pin}";
                }
            }
            _suppressSelectionChanged = false;
        }
    }

    private void RefreshAllConflicts()
    {
        foreach (var (output, _, _, _) in _outputRows)
            UpdateComboConflicts(output);
        UpdateI2SPinConflicts();
    }

    private void ShowStatus(string message, bool isError)
    {
        if (_statusText == null) return;
        _statusText.Text = message;
        _statusText.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 240, 100, 100)
            : Color.FromArgb(255, 100, 200, 140));
        _statusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        if (_statusText == null) return;
        _statusText.Visibility = Visibility.Collapsed;
    }

    private static string GetErrorMessage(byte status, string outputName) => status switch
    {
        PinConfigResult.InvalidPin => "Invalid GPIO pin number",
        PinConfigResult.PinInUse => "Pin is already assigned to another output",
        PinConfigResult.InvalidOutput => "Invalid output index",
        PinConfigResult.OutputActive => $"{outputName} must be disabled before changing its pin",
        0xFF => "USB transfer failed — device may be disconnected",
        _ => $"Unknown error (0x{status:X2})"
    };

    private static string GetI2SErrorMessage(byte status, string contextName) => status switch
    {
        PinConfigResult.InvalidPin => "Invalid GPIO pin number",
        PinConfigResult.PinInUse => "Pin is already assigned to another function",
        PinConfigResult.InvalidOutput => "Invalid output slot index",
        PinConfigResult.OutputActive => "Cannot change while I2S outputs are active",
        0xFF => "USB transfer failed — device may be disconnected",
        _ => $"Unknown error (0x{status:X2})"
    };

    // ── I2S Configuration Section ──

    private void BuildI2SConfigSection()
    {
        // Spacing before I2S section
        HardwarePanel.Children.Add(new Border { Height = 8 });

        BuildSectionHeader("I2S Configuration", "\uE9CE"); // waveform icon

        HardwarePanel.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 2, 0, 2)
        });

        // MCK Toggle
        HardwarePanel.Children.Add(BuildMckToggleRow());

        // MCK Pin row
        HardwarePanel.Children.Add(BuildMckPinRow());

        // BCK Pin row
        HardwarePanel.Children.Add(BuildBckPinRow());

        // MCK Multiplier row
        HardwarePanel.Children.Add(BuildMckMultiplierRow());
    }

    private UIElement BuildBckPinRow()
    {
        var row = new Grid { Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // icon
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // label+subtitle
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // combo

        var icon = new FontIcon
        {
            Glyph = "\uEC04", // waveform
            FontSize = 12,
            Width = 16,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var labelStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        labelStack.Children.Add(new TextBlock { Text = "BCK Pin", FontSize = 13 });
        _lrckSubtitle = new TextBlock
        {
            Text = $"LRCK: GPIO {_vm.I2SBckPin + 1} (BCK + 1)",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        labelStack.Children.Add(_lrckSubtitle);
        Grid.SetColumn(labelStack, 1);
        row.Children.Add(labelStack);

        _bckPinCombo = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center };
        foreach (var pin in ValidPins)
            _bckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        var defaultIdx = Array.IndexOf(ValidPins, (byte)14);
        if (defaultIdx >= 0) _bckPinCombo.SelectedIndex = defaultIdx;
        _bckPinCombo.SelectionChanged += OnBckPinChanged;
        Grid.SetColumn(_bckPinCombo, 2);
        row.Children.Add(_bckPinCombo);

        return row;
    }

    private UIElement BuildMckToggleRow()
    {
        _mckToggle = new ToggleSwitch
        {
            Header = null,
            OnContent = "On",
            OffContent = "Off",
            MinWidth = 120,
            Margin = new Thickness(0, 4, 0, 4)
        };

        var row = new Grid { Padding = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uEA3B", // clock
            FontSize = 12,
            Width = 16,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var labelStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        labelStack.Children.Add(new TextBlock { Text = "Master Clock (MCK)", FontSize = 13 });
        labelStack.Children.Add(new TextBlock
        {
            Text = "Clock reference for external DACs",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        Grid.SetColumn(labelStack, 1);
        row.Children.Add(labelStack);

        Grid.SetColumn(_mckToggle, 2);
        row.Children.Add(_mckToggle);

        _mckToggle.Toggled += OnMckToggled;

        return row;
    }

    private UIElement BuildMckPinRow()
    {
        var row = new Grid { Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE823", // clock
            FontSize = 12,
            Width = 16,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var label = new TextBlock
        {
            Text = "MCK Pin",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        _mckPinCombo = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center };
        foreach (var pin in ValidPins)
            _mckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        var defaultIdx = Array.IndexOf(ValidPins, (byte)13);
        if (defaultIdx >= 0) _mckPinCombo.SelectedIndex = defaultIdx;
        _mckPinCombo.SelectionChanged += OnMckPinChanged;
        Grid.SetColumn(_mckPinCombo, 2);
        row.Children.Add(_mckPinCombo);

        return row;
    }

    private UIElement BuildMckMultiplierRow()
    {
        var container = new StackPanel { Spacing = 4 };

        var row = new Grid { Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE947", // multiply
            FontSize = 12,
            Width = 16,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var label = new TextBlock
        {
            Text = "MCK Multiplier",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        _mckMultiplierCombo = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center };
        _mckMultiplierCombo.Items.Add(new ComboBoxItem { Content = "128x", Tag = 128 });
        _mckMultiplierCombo.Items.Add(new ComboBoxItem { Content = "256x", Tag = 256 });
        _mckMultiplierCombo.SelectedIndex = 0;
        _mckMultiplierCombo.SelectionChanged += OnMckMultiplierChanged;
        Grid.SetColumn(_mckMultiplierCombo, 2);
        row.Children.Add(_mckMultiplierCombo);

        container.Children.Add(row);

        // "Locked to 128x" caption
        _mckMultiplierCaption = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed
        };
        container.Children.Add(_mckMultiplierCaption);

        return container;
    }

    private async void OnBckPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (_bckPinCombo?.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();

        var status = await Task.Run(() => _vm.SetI2SBckPin(newPin));

        if (status == PinConfigResult.Success)
        {
            if (_lrckSubtitle != null)
                _lrckSubtitle.Text = $"LRCK: GPIO {newPin + 1} (BCK + 1)";
            RefreshAllConflicts();
            ShowStatus($"BCK pin set to GPIO {newPin}, LRCK = GPIO {newPin + 1}", isError: false);
            return;
        }

        // Revert
        _suppressSelectionChanged = true;
        var idx = Array.IndexOf(ValidPins, _vm.I2SBckPin);
        if (idx >= 0) _bckPinCombo.SelectedIndex = idx;
        _suppressSelectionChanged = false;

        string msg = status switch
        {
            PinConfigResult.OutputActive => "All outputs must be S/PDIF before changing BCK pin",
            PinConfigResult.PinInUse => $"GPIO {newPin} or {newPin + 1} is already in use",
            _ => GetI2SErrorMessage(status, "BCK")
        };
        ShowStatus(msg, isError: true);
    }

    private async void OnMckToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (_mckToggle == null) return;

        ClearStatus();

        bool newVal = _mckToggle.IsOn;
        var status = await Task.Run(() => _vm.SetMckEnable(newVal));

        if (status == PinConfigResult.Success)
        {
            UpdateI2SConstraints();
            RefreshAllConflicts();
            ShowStatus($"Master clock {(newVal ? "enabled" : "disabled")}", isError: false);
            return;
        }

        // Revert
        _suppressSelectionChanged = true;
        _mckToggle.IsOn = _vm.MckEnabled;
        _suppressSelectionChanged = false;
        ShowStatus($"Failed to {(newVal ? "enable" : "disable")} master clock", isError: true);
    }

    private async void OnMckPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (_mckPinCombo?.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();

        var status = await Task.Run(() => _vm.SetMckPin(newPin));

        if (status == PinConfigResult.Success)
        {
            RefreshAllConflicts();
            ShowStatus($"MCK pin set to GPIO {newPin}", isError: false);
            return;
        }

        // Revert
        _suppressSelectionChanged = true;
        var idx = Array.IndexOf(ValidPins, _vm.MckPin);
        if (idx >= 0) _mckPinCombo.SelectedIndex = idx;
        _suppressSelectionChanged = false;

        string msg = status switch
        {
            PinConfigResult.OutputActive => "Disable MCK before changing its pin",
            PinConfigResult.PinInUse => $"GPIO {newPin} is already in use",
            _ => GetI2SErrorMessage(status, "MCK")
        };
        ShowStatus(msg, isError: true);
    }

    private async void OnMckMultiplierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (_mckMultiplierCombo?.SelectedItem is not ComboBoxItem item || item.Tag is not int newMultiplier) return;

        ClearStatus();

        var status = await Task.Run(() => _vm.SetMckMultiplier(newMultiplier));

        if (status == PinConfigResult.Success)
        {
            ShowStatus($"MCK multiplier set to {newMultiplier}x", isError: false);
            return;
        }

        // Revert
        _suppressSelectionChanged = true;
        _mckMultiplierCombo.SelectedIndex = _vm.MckMultiplier == 256 ? 1 : 0;
        _suppressSelectionChanged = false;
        ShowStatus("Failed to set MCK multiplier", isError: true);
    }

    private void UpdateI2SConstraints()
    {
        bool anyI2S = _vm.AnySlotIsI2S;

        // BCK pin: only changeable when no slots are I2S
        if (_bckPinCombo != null)
            _bckPinCombo.IsEnabled = !anyI2S;

        // MCK pin: only changeable when MCK is disabled
        if (_mckPinCombo != null)
            _mckPinCombo.IsEnabled = !_vm.MckEnabled;

        // MCK multiplier: 256x unavailable at >= 96kHz
        bool highSampleRate = _vm.SampleRateHz >= 96000;
        if (_mckMultiplierCombo != null)
            _mckMultiplierCombo.IsEnabled = !highSampleRate;
        if (_mckMultiplierCaption != null)
        {
            _mckMultiplierCaption.Visibility = highSampleRate ? Visibility.Visible : Visibility.Collapsed;
            if (highSampleRate)
                _mckMultiplierCaption.Text = $"Locked to 128x at {_vm.SampleRateHz / 1000.0:F1} kHz";
        }

        // LRCK subtitle update
        if (_lrckSubtitle != null)
            _lrckSubtitle.Text = $"LRCK: GPIO {_vm.I2SBckPin + 1} (BCK + 1)";
    }
}
