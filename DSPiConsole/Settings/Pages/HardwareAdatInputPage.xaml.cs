using System;
using System.ComponentModel;
using System.Threading.Tasks;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › ADAT Input. The RP2350-only 8-channel ADAT optical input source:
/// enable, a conflict-aware RX pin, master/slave clock mode, and a live lock
/// indicator. Only registered when the connected firmware reports ADAT input
/// support (see <see cref="IsAvailable"/>).
/// </summary>
public sealed partial class HardwareAdatInputPage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private DispatcherQueueTimer? _statusTimer;

    public HardwareAdatInputPage()
    {
        InitializeComponent();

        // "(Not set)" sentinel first, then every audio-routing GPIO. RefreshConflicts
        // only toggles enabled state / reselects — it never rebuilds the items.
        PinCombo.Items.Add(new ComboBoxItem { Content = "(Not set)", Tag = MainViewModel.AdatInputPinUnset });
        foreach (var pin in HardwarePins.ValidPins)
            PinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
        var fetchVm = vm;
        _ = Task.Run(() => fetchVm.FetchAdatInputConfig())
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            Refresh();
        }

        // Poll the live lock state while the page is visible.
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(700);
        _statusTimer.Tick += (_, _) =>
        {
            if (Vm is { AdatInputSupported: true, AdatInputEnabled: true })
                _ = Task.Run(() => Vm.RefreshAdatInputStatus());
        };
        _statusTimer.Start();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void OnExternalPinChange() => DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.AdatInputEnabled):
            case nameof(MainViewModel.AdatInputPin):
            case nameof(MainViewModel.AdatInputClockMode):
            case nameof(MainViewModel.AdatInputSupported):
                DispatcherQueue.TryEnqueue(Refresh);
                break;
            case nameof(MainViewModel.AdatInputStatus):
                DispatcherQueue.TryEnqueue(RefreshLock);
                break;
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            EnableToggle.IsOn = Vm.AdatInputEnabled;
            SelectByTag(ClockModeCombo, (int)Vm.AdatInputClockMode);
        }
        finally { _suppress = false; }
        RefreshConflicts();
        RefreshLock();
    }

    private void RefreshLock()
    {
        if (Vm == null) return;
        var st = Vm.AdatInputStatus;
        if (!Vm.AdatInputEnabled || st == null)
        {
            LockCard.Visibility = Visibility.Collapsed;
            return;
        }
        LockCard.Visibility = Visibility.Visible;
        string rate = st.IsLocked ? $" · {st.DetectedRateText}" : "";
        LockText.Text = st.StateText + rate;
        LockText.Foreground = new SolidColorBrush(st.IsLocked
            ? Color.FromArgb(255, 100, 200, 140)
            : Color.FromArgb(255, 240, 180, 90));
    }

    /// <summary>Grey-out pins owned by other features and reselect the current pin.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            var owners = HardwarePins.BuildOwnerMap(Vm, excludeAdatInputSelf: true);
            byte currentPin = Vm.AdatInputPin;
            for (int i = 0; i < PinCombo.Items.Count; i++)
            {
                if (PinCombo.Items[i] is not ComboBoxItem item || item.Tag is not byte pin) continue;
                if (pin == MainViewModel.AdatInputPinUnset) { item.IsEnabled = true; continue; }

                bool isCurrent = pin == currentPin;
                string? owner = null;
                if (!isCurrent && owners.TryGetValue(pin, out var o)) owner = o;
                item.Content = owner != null ? $"GPIO {pin} ({owner})" : $"GPIO {pin}";
                item.IsEnabled = owner == null;
            }
            SelectByTag(PinCombo, currentPin);
        }
        finally { _suppress = false; }
    }

    private async void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        bool enable = EnableToggle.IsOn;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputEnable(enable));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshConflicts();
            RefreshLock();
            return;
        }
        _suppress = true;
        EnableToggle.IsOn = Vm.AdatInputEnabled;
        _suppress = false;
        ShowStatus(status switch
        {
            PinConfigResult.InvalidPin => "Set a valid RX pin before enabling the ADAT input.",
            PinConfigResult.InvalidOutput => "ADAT input isn't supported on this device.",
            PinConfigResult.PinInUse => "The ADAT input pin is already claimed — pick a free GPIO.",
            _ => $"Failed to change ADAT input (0x{status:X2})."
        }, true);
    }

    private async void OnPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (PinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            string label = newPin == MainViewModel.AdatInputPinUnset ? "cleared" : $"set to GPIO {newPin}";
            ShowStatus($"ADAT input pin {label}", false);
            return;
        }
        _suppress = true;
        SelectByTag(PinCombo, Vm.AdatInputPin);
        _suppress = false;
        ShowStatus(status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already assigned to another peripheral.",
            PinConfigResult.InvalidPin => $"GPIO {newPin} can't receive the ADAT input.",
            _ => $"Failed to set ADAT input pin (0x{status:X2})."
        }, true);
    }

    private async void OnClockModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (ClockModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!byte.TryParse(tag, out var mode)) return;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputClockMode(mode));
        if (status != PinConfigResult.Success)
        {
            _suppress = true;
            SelectByTag(ClockModeCombo, (int)Vm.AdatInputClockMode);
            _suppress = false;
            ShowStatus($"Failed to set clock mode (0x{status:X2}).", true);
        }
    }

    private void SelectByTag(ComboBox combo, object value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not ComboBoxItem item) continue;
            // Tags are byte (pins) or string ("0"/"1" for clock mode).
            bool match = item.Tag is byte b && value is byte vb && b == vb
                || item.Tag is string s && value is int vi && s == vi.ToString();
            if (match) { combo.SelectedIndex = i; return; }
        }
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

    // ── ISettingsPage ──────────────────────────────────────────────────────
    public string Id => "hardware.adat-input";
    public string Title => "ADAT Input";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => "";
    public int Order => 30;
    public bool IsAvailable(MainViewModel vm) => vm.AdatInputSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareAdatInputPage();
        p.Attach(vm, tracker);
        return p;
    }
}
