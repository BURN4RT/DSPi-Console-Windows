using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › Output Assignment — per-output Type (S/PDIF / I²S / PDM)
/// and GPIO pin, plus a "Reset to defaults" button.
///
/// <para>
/// Pin combos are dynamically built from <see cref="HardwarePins.AllPinOutputs"/>
/// so the page is identical for RP2040 (3 rows) and RP2350 (5 rows).
/// Pin-conflict greying queries <see cref="HardwarePins.BuildOwnerMap"/>
/// and reacts to <see cref="HardwarePins.PinAssignmentsChanged"/> so a
/// pin claim on another Hardware page (I²S, S/PDIF, DAC Mute) repaints
/// this page's pickers without any direct coupling.
/// </para>
///
/// <para>
/// Apply cadence: <b>per-change flash write</b> in Phase 1, matching the
/// legacy dialog. Phase 2 will route through the pending-change tracker.
/// </para>
/// </summary>
public sealed partial class HardwareOutputAssignmentPage : SettingsModule, ISettingsPage
{
    // Per-row tracking. The page rebuilds these from scratch on each
    // Attach so reconnecting to a different platform doesn't leak rows.
    private readonly List<(HardwarePins.PinOutput output, ComboBox typePicker, ComboBox gpioPicker, Border badge)> _rows = new();
    private bool _suppress;

    public HardwareOutputAssignmentPage()
    {
        InitializeComponent();
        // Detach on Unloaded so static / VM events don't keep the page
        // alive after the Settings window closes.
        Unloaded += (_, _) =>
        {
            HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
            if (Vm != null) Vm.BulkRefreshed -= OnBulkRefreshed;
        };
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        // Subscribe to cross-page pin updates. Other Hardware pages
        // raise the event after a successful flash write; we refresh
        // our combo conflict labels in response.
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;

        // Subscribe to BulkRefreshed so a preset load / factory reset /
        // reconnect — all of which silently update _outputPins and
        // _outputSlotTypes via FetchAll — repaints our combos to match.
        // Without this, the page shows stale pin/type values after a
        // preset load.
        if (Vm != null) Vm.BulkRefreshed -= OnBulkRefreshed;
        vm.BulkRefreshed += OnBulkRefreshed;

        base.Attach(vm, tracker);
    }

    private void OnExternalPinChange()
    {
        // Schedule on the dispatcher — event is raised from arbitrary
        // contexts (e.g. inside a USB callback if a page does the work
        // off-thread).
        DispatcherQueue.TryEnqueue(RefreshAllConflicts);
    }

    private void OnBulkRefreshed(object? sender, EventArgs e)
    {
        // The bulk path is already on the UI thread (BulkRefreshed fires
        // from the same dispatcher block), but TryEnqueue is the harmless
        // default for any cross-thread caller. PopulateAfterFetch just
        // mirrors the (now-fresh) VM state into the existing row combos
        // — no row rebuild, no extra device fetches.
        DispatcherQueue.TryEnqueue(PopulateAfterFetch);
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        // Wipe and rebuild rows. Cheaper than reconciling differential
        // updates and the page rarely refreshes (only on Attach or a
        // VM-driven bulk refetch).
        OutputRowsHost.Children.Clear();
        _rows.Clear();

        var outputs = HardwarePins.AllPinOutputs(Vm.Platform);

        // Fetch current device state on a background thread, then
        // populate the UI on the dispatcher. Same pattern as the legacy
        // dialog — keeps the USB IO off the UI thread.
        var fetchVm = Vm; // avoid closure on nullable property
        _ = Task.Run(() =>
        {
            foreach (var o in outputs)
                fetchVm.FetchOutputPin(o.Id);
            int slotCount = fetchVm.NumOutputSlots;
            for (int s = 0; s < slotCount; s++)
                fetchVm.FetchOutputSlotType(s);
        }).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PopulateAfterFetch();
            });
        });

        // Build rows immediately with default values; PopulateAfterFetch
        // overwrites them once the device replies. This means the page
        // is interactive even before the fetch completes — the user
        // just sees default pin values briefly.
        foreach (var o in outputs)
            OutputRowsHost.Children.Add(BuildPinRow(o));
    }

    private void PopulateAfterFetch()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            foreach (var (output, typePicker, gpioPicker, badge) in _rows)
            {
                // Pin
                byte currentPin = Vm.GetOutputPinValue(output.Id);
                var idx = System.Array.IndexOf(HardwarePins.ValidPins, currentPin);
                if (idx >= 0) gpioPicker.SelectedIndex = idx;
                UpdateBadgeVisibility(badge, currentPin, output.DefaultPin);

                // Type (S/PDIF / I²S, or PDM-locked)
                if (output.SlotIndex >= 0)
                {
                    var t = Vm.GetOutputSlotType(output.SlotIndex);
                    typePicker.SelectedIndex = t == OutputSlotType.I2S ? 1 : 0;
                }
            }
        }
        finally { _suppress = false; }
        RefreshAllConflicts();
    }

    /// <summary>
    /// Build one row Grid: colored dot, label, type combo, DEFAULT
    /// badge, GPIO combo. Layout mirrors the legacy dialog so the page
    /// is visually familiar — Phase 1 changes the chrome, not the row.
    /// </summary>
    private UIElement BuildPinRow(HardwarePins.PinOutput output)
    {
        var row = new Grid { Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // dot
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // label
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // type combo
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // badge spacer
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // gpio combo

        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(output.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(dot, 0);
        row.Children.Add(dot);

        var label = new TextBlock
        {
            Text = output.Detail,
            FontSize = 13,
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0)
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        // Type picker — PDM is fixed.
        var typePicker = new ComboBox
        {
            Width = 100,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0)
        };
        if (output.SlotIndex >= 0)
        {
            typePicker.Items.Add(new ComboBoxItem { Content = "S/PDIF", Tag = OutputSlotType.Spdif });
            typePicker.Items.Add(new ComboBoxItem { Content = "I²S",    Tag = OutputSlotType.I2S });
            typePicker.SelectedIndex = 0;
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

        // DEFAULT badge — visible only when the pin equals the factory
        // default. Lives in the star-width spacer column, centred.
        var badge = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
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

        // GPIO picker — every ValidPin enumerated, with conflict labels
        // applied by UpdateComboConflicts.
        var gpioPicker = new ComboBox
        {
            Width = 140,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = output
        };
        foreach (var pin in HardwarePins.ValidPins)
            gpioPicker.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        var defaultIdx = System.Array.IndexOf(HardwarePins.ValidPins, output.DefaultPin);
        if (defaultIdx >= 0) gpioPicker.SelectedIndex = defaultIdx;
        gpioPicker.SelectionChanged += OnGpioChanged;
        Grid.SetColumn(gpioPicker, 4);
        row.Children.Add(gpioPicker);

        _rows.Add((output, typePicker, gpioPicker, badge));
        return row;
    }

    // ── Live-apply handlers ──────────────────────────────────────────
    // Per-preset parameters — each control change writes through
    // immediately, with revert + status text on error.

    private async void OnOutputTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (sender is not ComboBox combo || combo.Tag is not HardwarePins.PinOutput output) return;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not OutputSlotType newType) return;

        ClearStatus();

        var status = await Task.Run(() => Vm.SetOutputSlotType(output.SlotIndex, newType));

        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshAllConflicts();
            ShowStatus($"{output.Name} → {(newType == OutputSlotType.I2S ? "I²S" : "S/PDIF")}", isError: false);
            return;
        }

        // Revert UI to whatever the device actually reports.
        _suppress = true;
        combo.SelectedIndex = Vm.GetOutputSlotType(output.SlotIndex) == OutputSlotType.I2S ? 1 : 0;
        _suppress = false;
        ShowStatus(I2SError(status, output.Name), isError: true);
    }

    private async void OnGpioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (sender is not ComboBox combo || combo.Tag is not HardwarePins.PinOutput output) return;
        if (combo.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not byte newPin) return;

        ClearStatus();

        var status = await Task.Run(() => Vm.SetOutputPinValue(output.Id, newPin));

        if (status == PinConfigResult.Success)
        {
            UpdateRowBadge(output, newPin);
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshAllConflicts();
            ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
            return;
        }

        // PDM has the special auto-cycle (disable, set pin, re-enable).
        // Firmware refuses to move the PDM pin while the PDM output
        // is enabled. Mirrors the legacy dialog's branch for
        // output.SlotIndex < 0.
        if (status == PinConfigResult.OutputActive && output.SlotIndex < 0)
        {
            var cycleStatus = await Task.Run(() =>
            {
                int pdmMatrixIndex = Vm.ActiveOutputs.Count - 1;
                Vm.Device.SetOutputEnable(pdmMatrixIndex, false);
                var r = Vm.SetOutputPinValue(output.Id, newPin);
                Vm.Device.SetOutputEnable(pdmMatrixIndex, true);
                return r;
            });

            if (cycleStatus == PinConfigResult.Success)
            {
                UpdateRowBadge(output, newPin);
                HardwarePins.RaisePinAssignmentsChanged();
                RefreshAllConflicts();
                ShowStatus($"{output.Name} → GPIO {newPin}", isError: false);
                return;
            }
            status = cycleStatus;
        }

        RevertGpioCombo(output);
        ShowStatus(GpioError(status, output.Name), isError: true);
    }

    private void UpdateRowBadge(HardwarePins.PinOutput output, byte newPin)
    {
        var row = _rows.FirstOrDefault(r => r.output.Id == output.Id);
        if (row != default)
            UpdateBadgeVisibility(row.badge, newPin, output.DefaultPin);
    }

    private void RevertGpioCombo(HardwarePins.PinOutput output)
    {
        if (Vm == null) return;
        var row = _rows.FirstOrDefault(r => r.output.Id == output.Id);
        if (row == default) return;

        _suppress = true;
        byte devicePin = Vm.GetOutputPinValue(output.Id);
        var idx = System.Array.IndexOf(HardwarePins.ValidPins, devicePin);
        if (idx >= 0) row.gpioPicker.SelectedIndex = idx;
        _suppress = false;
    }

    private static void UpdateBadgeVisibility(Border badge, byte currentPin, byte defaultPin) =>
        badge.Visibility = currentPin == defaultPin ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Refresh every GPIO picker on this page so claimed pins
    /// (here or elsewhere) appear as in-use. Called after a successful
    /// change here, and in response to <see cref="HardwarePins.PinAssignmentsChanged"/>
    /// for changes elsewhere.</summary>
    private void RefreshAllConflicts()
    {
        if (Vm == null) return;
        foreach (var (output, _, picker, _) in _rows)
            UpdateComboConflicts(output, picker);
    }

    private void UpdateComboConflicts(HardwarePins.PinOutput targetOutput, ComboBox picker)
    {
        if (Vm == null) return;

        var owners = HardwarePins.BuildOwnerMap(Vm, excludeOutputId: targetOutput.Id);

        _suppress = true;
        for (int i = 0; i < HardwarePins.ValidPins.Length; i++)
        {
            if (picker.Items[i] is not ComboBoxItem item) continue;
            byte pin = HardwarePins.ValidPins[i];
            if (owners.TryGetValue(pin, out var owner))
            {
                item.Content = $"GPIO {pin} ({owner})";
                item.IsEnabled = false;
            }
            else
            {
                item.Content = $"GPIO {pin}";
                item.IsEnabled = true;
            }
        }
        _suppress = false;
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        ClearStatus();

        // Per-preset: reset writes through immediately, in the firmware's
        // required order. Slot types must go to S/PDIF before BCK pin can
        // move; MCK must be off before its pin can move.

        // Reset slot types → S/PDIF.
        int slotCount = Vm.NumOutputSlots;
        for (int s = 0; s < slotCount; s++)
        {
            if (Vm.GetOutputSlotType(s) != OutputSlotType.Spdif)
            {
                var typeStatus = await Task.Run(() => Vm.SetOutputSlotType(s, OutputSlotType.Spdif));
                if (typeStatus != PinConfigResult.Success)
                {
                    ShowStatus($"Failed to reset Output {s + 1} type", isError: true);
                    return;
                }
            }
        }

        // Reset GPIO pins.
        foreach (var (output, typePicker, gpioPicker, badge) in _rows)
        {
            byte defaultPin = output.DefaultPin;
            byte currentPin = Vm.GetOutputPinValue(output.Id);
            if (currentPin == defaultPin) continue;

            byte status;
            if (output.SlotIndex < 0) // PDM auto-cycle
            {
                status = await Task.Run(() =>
                {
                    int pdmMatrixIndex = Vm.ActiveOutputs.Count - 1;
                    Vm.Device.SetOutputEnable(pdmMatrixIndex, false);
                    var r = Vm.SetOutputPinValue(output.Id, defaultPin);
                    Vm.Device.SetOutputEnable(pdmMatrixIndex, true);
                    return r;
                });
            }
            else
            {
                status = await Task.Run(() => Vm.SetOutputPinValue(output.Id, defaultPin));
            }

            if (status == PinConfigResult.Success)
            {
                _suppress = true;
                var idx = System.Array.IndexOf(HardwarePins.ValidPins, defaultPin);
                if (idx >= 0) gpioPicker.SelectedIndex = idx;
                UpdateBadgeVisibility(badge, defaultPin, output.DefaultPin);
                _suppress = false;
            }
            else
            {
                ShowStatus($"Failed to reset {output.Name}: {GpioError(status, output.Name)}", isError: true);
                return;
            }
        }

        // Reset I²S clocks to defaults (MCK off, BCK 14, MCK pin 13, mult 128).
        await Task.Run(() =>
        {
            Vm.SetMckEnable(false);
            Vm.SetI2SBckPin(14);
            Vm.SetMckPin(13);
            Vm.SetMckMultiplier(128);
        });

        // Re-sync UI: type combos back to S/PDIF.
        _suppress = true;
        foreach (var (output, typePicker, _, _) in _rows)
        {
            if (output.SlotIndex >= 0)
                typePicker.SelectedIndex = 0;
        }
        _suppress = false;

        HardwarePins.RaisePinAssignmentsChanged();
        RefreshAllConflicts();
        ShowStatus("All hardware settings reset to defaults.", isError: false);
    }

    private void ShowStatus(string msg, bool isError)
    {
        StatusText.Text = msg;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 240, 100, 100)
            : Color.FromArgb(255, 100, 200, 140));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus() => StatusText.Visibility = Visibility.Collapsed;

    private static string GpioError(byte status, string outputName) => status switch
    {
        PinConfigResult.InvalidPin    => "Invalid GPIO pin number",
        PinConfigResult.PinInUse      => "Pin is already assigned to another output",
        PinConfigResult.InvalidOutput => "Invalid output index",
        PinConfigResult.OutputActive  => $"{outputName} must be disabled before changing its pin",
        0xFF                          => "USB transfer failed — device may be disconnected",
        _                             => $"Unknown error (0x{status:X2})"
    };

    private static string I2SError(byte status, string contextName) => status switch
    {
        PinConfigResult.InvalidPin    => "Invalid GPIO pin number",
        PinConfigResult.PinInUse      => "Pin is already assigned to another function",
        PinConfigResult.InvalidOutput => "Invalid output slot index",
        PinConfigResult.OutputActive  => "Cannot change while I²S outputs are active",
        0xFF                          => "USB transfer failed — device may be disconnected",
        _                             => $"Unknown error (0x{status:X2})"
    };

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.output-assignment";
    public string Title => "Output Assignment";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => ""; // Speakers
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareOutputAssignmentPage();
        p.Attach(vm, tracker);
        return p;
    }
}
