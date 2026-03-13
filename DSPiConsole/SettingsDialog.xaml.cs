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
    private readonly List<(PinOutput output, ComboBox combo, Border badge)> _pinRows = new();
    private TextBlock? _statusText;
    private bool _suppressSelectionChanged;

    public SettingsDialog(MainViewModel vm)
    {
        _vm = vm;

        InitializeComponent();

        var settings = AppSettings.Instance;

        GlowToggle.IsOn = settings.ShowGraphGlow;
        LineWidthSlider.Value = settings.GraphLineWidth;
        AnimSpeedSlider.Value = settings.GraphAnimationSpeed;
        DebugToggle.IsOn = settings.ShowDebugInfo;

        LineWidthText.Text = settings.GraphLineWidth.ToString("F1");
        AnimSpeedText.Text = settings.GraphAnimationSpeed.ToString("F2");

        LineWidthSlider.ValueChanged += (s, e) => LineWidthText.Text = e.NewValue.ToString("F1");
        AnimSpeedSlider.ValueChanged += (s, e) => AnimSpeedText.Text = e.NewValue.ToString("F2");

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

        PrimaryButtonClick += OnSave;

        InitializePresetsTab();
        BuildPinAssignmentTable();
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
        DefaultPresetCombo.IsEnabled = _vm.PresetStartupMode == 1;

        // Include pins
        IncludePinsToggle.IsOn = _vm.PresetIncludePins;

        _suppressPresetEvents = false;
    }

    private async void OnStartupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetEvents) return;
        if (StartupModeCombo.SelectedIndex < 0) return;

        byte mode = (byte)StartupModeCombo.SelectedIndex;
        DefaultPresetCombo.IsEnabled = mode == 1;

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
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && double.TryParse(tag, out var val))
            return val;
        return fallback;
    }

    // --- Pin Assignment table ---

    private record PinOutput(int Id, string Name, string Detail, string Icon, byte DefaultPin, Color Color);

    private static readonly PinOutput[] PinOutputsRp2350 =
    [
        new(0, "S/PDIF 1", "Stereo pair 1 (L/R)", "\uE767", 6,
            Color.FromArgb(255, 69, 194, 163)),   // Teal
        new(1, "S/PDIF 2", "Stereo pair 2 (L/R)", "\uE767", 7,
            Color.FromArgb(255, 240, 196, 89)),    // Yellow
        new(2, "S/PDIF 3", "Stereo pair 3 (L/R)", "\uE767", 8,
            Color.FromArgb(255, 89, 140, 242)),    // Blue
        new(3, "S/PDIF 4", "Stereo pair 4 (L/R)", "\uE767", 9,
            Color.FromArgb(255, 217, 115, 140)),   // Pink
        new(4, "PDM",      "Subwoofer output",     "\uE9B1", 10,
            Color.FromArgb(255, 186, 135, 243)),   // Purple
    ];

    private static readonly PinOutput[] PinOutputsRp2040 =
    [
        new(0, "S/PDIF 1", "Stereo pair 1 (L/R)", "\uE767", 6,
            Color.FromArgb(255, 69, 194, 163)),
        new(1, "S/PDIF 2", "Stereo pair 2 (L/R)", "\uE767", 7,
            Color.FromArgb(255, 240, 196, 89)),
        new(2, "PDM",      "Subwoofer output",     "\uE9B1", 10,
            Color.FromArgb(255, 186, 135, 243)),
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
        if (output.Name == "PDM")
            return _vm.ActiveOutputs.Count - 1;
        return output.Id * 2;
    }

    private PinOutput[] GetAllPinOutputs() =>
        _vm.Platform == "RP2350" ? PinOutputsRp2350 : PinOutputsRp2040;

    private void BuildPinAssignmentTable()
    {
        var allOutputs = GetAllPinOutputs();

        // Filter to only enabled outputs
        var outputs = allOutputs
            .Where(o => _vm.IsOutputEnabled(PinOutputIdToMatrixIndex(o)))
            .ToList();

        if (outputs.Count == 0)
        {
            HardwarePanel.Children.Add(new TextBlock
            {
                Text = "No outputs enabled. Enable outputs in the Matrix Mixer to configure pin assignment.",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }

        // Fetch current pin values from device on a background thread, then populate UI
        var pinValues = new Dictionary<int, byte>();
        _ = Task.Run(() =>
        {
            foreach (var output in outputs)
                _vm.FetchOutputPin(output.Id);
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() => PopulatePinValues(outputs));
        });

        // Header row: "Pin Assignment" label + "Reset to Defaults" button
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerIcon = new FontIcon
        {
            Glyph = "\uE950", // CPU icon
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerText = new TextBlock
        {
            Text = "Pin Assignment",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal };
        headerLeft.Children.Add(headerIcon);
        headerLeft.Children.Add(headerText);
        Grid.SetColumn(headerLeft, 0);
        header.Children.Add(headerLeft);

        var resetBtn = new HyperlinkButton
        {
            Content = "Reset to Defaults",
            FontSize = 11,
            Padding = new Thickness(4, 2, 4, 2)
        };
        resetBtn.Click += OnResetToDefaults;
        Grid.SetColumn(resetBtn, 1);
        header.Children.Add(resetBtn);

        HardwarePanel.Children.Add(header);

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
    }

    private void PopulatePinValues(List<PinOutput> outputs)
    {
        _suppressSelectionChanged = true;
        foreach (var (output, combo, badge) in _pinRows)
        {
            byte currentPin = _vm.GetOutputPinValue(output.Id);
            var idx = Array.IndexOf(ValidPins, currentPin);
            if (idx >= 0)
                combo.SelectedIndex = idx;

            UpdateBadgeVisibility(badge, currentPin, output.DefaultPin);
            UpdateComboConflicts(output);
        }
        _suppressSelectionChanged = false;
    }

    private UIElement BuildPinRow(PinOutput output)
    {
        // Main row grid: Icon | Name+Detail | DEFAULT badge | GPIO picker
        var row = new Grid
        {
            Padding = new Thickness(0, 6, 0, 6),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // icon
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name+detail
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // default badge
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // gpio picker

        // Colored icon
        var icon = new FontIcon
        {
            Glyph = output.Icon,
            FontSize = 14,
            Foreground = new SolidColorBrush(output.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        // Name + detail
        var nameStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameStack.Children.Add(new TextBlock
        {
            Text = output.Name,
            FontSize = 13
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = output.Detail,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        Grid.SetColumn(nameStack, 1);
        row.Children.Add(nameStack);

        // DEFAULT badge
        var badge = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "DEFAULT",
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            }
        };
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);

        // GPIO picker (ComboBox)
        var combo = new ComboBox
        {
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = output
        };
        foreach (var pin in ValidPins)
        {
            combo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        }
        // Select the default pin initially (will be overwritten by PopulatePinValues)
        var defaultIndex = Array.IndexOf(ValidPins, output.DefaultPin);
        if (defaultIndex >= 0) combo.SelectedIndex = defaultIndex;

        combo.SelectionChanged += OnPinSelectionChanged;

        Grid.SetColumn(combo, 3);
        row.Children.Add(combo);

        _pinRows.Add((output, combo, badge));

        return row;
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
            var row = _pinRows.FirstOrDefault(r => r.output.Id == output.Id);
            if (row != default)
                UpdateBadgeVisibility(row.badge, newPin, output.DefaultPin);
            RefreshAllConflicts();
            ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
            return;
        }

        if (status == PinConfigResult.OutputActive && output.Name == "PDM")
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
                var row = _pinRows.FirstOrDefault(r => r.output.Id == output.Id);
                if (row != default)
                    UpdateBadgeVisibility(row.badge, newPin, output.DefaultPin);
                RefreshAllConflicts();
                ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
                return;
            }

            status = cycleStatus;
        }

        // Error: revert ComboBox to device's actual value
        RevertCombo(output);
        ShowStatus(GetErrorMessage(status, output.Name), isError: true);
    }

    private async void OnResetToDefaults(object sender, RoutedEventArgs e)
    {
        ClearStatus();

        foreach (var (output, combo, badge) in _pinRows)
        {
            byte defaultPin = output.DefaultPin;
            byte currentPin = _vm.GetOutputPinValue(output.Id);
            if (currentPin == defaultPin) continue;

            byte status;
            if (output.Name == "PDM")
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
                if (idx >= 0) combo.SelectedIndex = idx;
                UpdateBadgeVisibility(badge, defaultPin, output.DefaultPin);
                _suppressSelectionChanged = false;
            }
            else
            {
                ShowStatus($"Failed to reset {output.Name}: {GetErrorMessage(status, output.Name)}", isError: true);
                return;
            }
        }

        RefreshAllConflicts();
        ShowStatus("All pins reset to defaults", isError: false);
    }

    private void RevertCombo(PinOutput output)
    {
        var row = _pinRows.FirstOrDefault(r => r.output.Id == output.Id);
        if (row == default) return;

        _suppressSelectionChanged = true;
        byte devicePin = _vm.GetOutputPinValue(output.Id);
        var idx = Array.IndexOf(ValidPins, devicePin);
        if (idx >= 0) row.combo.SelectedIndex = idx;
        _suppressSelectionChanged = false;
    }

    private void UpdateBadgeVisibility(Border badge, byte currentPin, byte defaultPin)
    {
        badge.Visibility = currentPin == defaultPin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateComboConflicts(PinOutput targetOutput)
    {
        var row = _pinRows.FirstOrDefault(r => r.output.Id == targetOutput.Id);
        if (row == default) return;

        // Build a map of pin → owner name (excluding this output)
        var pinOwners = new Dictionary<byte, string>();
        foreach (var (other, _, _) in _pinRows)
        {
            if (other.Id == targetOutput.Id) continue;
            byte otherPin = _vm.GetOutputPinValue(other.Id);
            if (otherPin != 0 || _pinRows.Any(r => r.output.Id == other.Id))
                pinOwners[otherPin] = other.Name;
        }

        _suppressSelectionChanged = true;
        for (int i = 0; i < ValidPins.Length; i++)
        {
            if (row.combo.Items[i] is ComboBoxItem item)
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

    private void RefreshAllConflicts()
    {
        foreach (var (output, _, _) in _pinRows)
            UpdateComboConflicts(output);
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
}
