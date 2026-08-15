using System;
using System.ComponentModel;
using System.Globalization;
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
/// Hardware › Clocking — the device's clock domain in one place: the generated
/// sample rate, and the master/slave choice for each interface that has one (I2S,
/// ADAT input) with its live lock state. Clock GPIO assignment stays on the pages
/// that own the wiring. Registered when the firmware exposes any of the three.
/// </summary>
public sealed partial class HardwareClockingPage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private DispatcherQueueTimer? _statusTimer;

    public HardwareClockingPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
        var fetchVm = vm;
        _ = Task.Run(() =>
            {
                if (fetchVm.InputI2sSupported) fetchVm.FetchI2sInputRate();
                fetchVm.FetchI2sClockConfig();
                if (fetchVm.AdatInputSupported) fetchVm.FetchAdatInputConfig();
            })
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            Refresh();
        }

        // The ADAT receiver's lock state has no push notification, so poll it while
        // the page is visible. The I2S slave status arrives on notification 0x09.
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
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.I2sInputRateHz):
            case nameof(MainViewModel.I2sClockMode):
            case nameof(MainViewModel.I2sClockModeSupported):
            case nameof(MainViewModel.I2sSlaveActive):
            case nameof(MainViewModel.I2sSlaveStatus):
            case nameof(MainViewModel.AdatInputClockMode):
            case nameof(MainViewModel.AdatInputSupported):
            case nameof(MainViewModel.AdatInputEnabled):
            case nameof(MainViewModel.AdatSupported):
            case nameof(MainViewModel.AdatEnabled):
                DispatcherQueue.TryEnqueue(Refresh);
                break;
            case nameof(MainViewModel.AdatInputStatus):
                DispatcherQueue.TryEnqueue(RefreshAdatLock);
                break;
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        RateCard.Visibility = Vis(Vm.InputI2sSupported);
        I2sClockCard.Visibility = Vis(Vm.I2sClockModeSupported);
        AdatClockCard.Visibility = Vis(Vm.AdatInputSupported);

        _suppress = true;
        try
        {
            SelectRate(Vm.I2sInputRateHz);
            SelectByStringTag(I2sClockCombo, Vm.I2sClockMode);
            SelectByStringTag(AdatClockCombo, Vm.AdatInputClockMode);
            RefreshRateAvailability();
        }
        finally { _suppress = false; }

        RefreshI2sLock();
        RefreshAdatLock();
        RefreshFreeRunWarning();
    }

    /// <summary>In I2S slave mode an external master owns the rate — the firmware
    /// detects whatever arrives and this picker does nothing, so grey it out rather
    /// than leave a live-looking control with no effect. The card's description
    /// already states the condition.</summary>
    private void RefreshRateAvailability()
    {
        if (Vm == null) return;
        RateCombo.IsEnabled = !Vm.I2sSlaveActive;
    }

    private void RefreshI2sLock()
    {
        if (Vm == null) return;
        var st = Vm.I2sSlaveStatus;
        if (!Vm.I2sSlaveActive || st == null)
        {
            I2sLockPill.Visibility = Visibility.Collapsed;
            return;
        }
        I2sLockPill.Visibility = Visibility.Visible;
        string rate = st.IsLocked ? $" · {st.DetectedRateText}" : "";
        I2sLockPill.Text = st.StateText + rate;
        I2sLockPill.Foreground = LockBrush(st.IsLocked);
    }

    private void RefreshAdatLock()
    {
        if (Vm == null) return;
        var st = Vm.AdatInputStatus;
        if (!Vm.AdatInputSupported || !Vm.AdatInputEnabled || st == null)
        {
            AdatLockPill.Visibility = Visibility.Collapsed;
            return;
        }
        AdatLockPill.Visibility = Visibility.Visible;
        string rate = st.IsLocked ? $" · {st.DetectedRateText}" : "";
        AdatLockPill.Text = st.StateText + rate;
        AdatLockPill.Foreground = LockBrush(st.IsLocked);
    }

    private static SolidColorBrush LockBrush(bool locked) => new(locked
        ? Color.FromArgb(255, 100, 200, 140)
        : Color.FromArgb(255, 240, 180, 90));

    /// <summary>Master clock mode with the ADAT output off means nothing external
    /// is locked to the DSPi's clock — the source drifts against it and produces
    /// periodic pops/clicks. Surface a warning with a one-click fix.</summary>
    private void RefreshFreeRunWarning()
    {
        FreeRunBar.IsOpen = Vm is
        {
            AdatInputSupported: true,
            AdatInputEnabled: true,
            AdatInputClockMode: 0, // Master
            AdatSupported: true,
            AdatEnabled: false,
        };
    }

    private async void OnEnableAdatOutputClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatEnable(true));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus("ADAT output enabled.", false);
            RefreshFreeRunWarning();
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.PinInUse => "The ADAT transmit pin is already claimed — free it on the ADAT page.",
            PinConfigResult.InvalidPin => "Pick a valid ADAT transmit pin on the ADAT page first.",
            _ => $"Failed to enable the ADAT output (0x{status:X2})."
        }, true);
    }

    // ── Handlers ───────────────────────────────────────────────────────────

    private async void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (RateCombo.SelectedItem is not ComboBoxItem item) return;
        if (!uint.TryParse(item.Tag?.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var hz)) return;
        if (hz == Vm.I2sInputRateHz) return;

        ClearStatus();
        var ok = await Task.Run(() => Vm.SetI2sInputRate(hz));
        if (ok)
        {
            ShowStatus($"Sample rate set to {hz / 1000.0:0.#} kHz", false);
            return;
        }
        _suppress = true;
        SelectRate(Vm.I2sInputRateHz);
        _suppress = false;
        ShowStatus("Failed to set sample rate", true);
    }

    private async void OnI2sClockModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (I2sClockCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!byte.TryParse(tag, out var mode) || mode == Vm.I2sClockMode) return;
        ClearStatus();

        // Switching mode while an I2S output is live can emit sustained loud noise
        // from the DAC if wiring hasn't been adjusted — confirm first.
        if (Vm.AnySlotIsI2S)
        {
            var dialog = new ContentDialog
            {
                Title = "Change I2S clock mode?",
                Content = "One or more I2S outputs are active. Switching between Master and Slave "
                        + "modes may cause sustained loud noise from the connected DAC if the wiring "
                        + "has not been adjusted.",
                PrimaryButtonText = "Change Clock Mode",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _suppress = true;
                SelectByStringTag(I2sClockCombo, Vm.I2sClockMode);
                _suppress = false;
                return;
            }
        }

        await Task.Run(() => Vm.SetI2sClockMode(mode));
        // Slave mode releases the BCK/LRCK GPIOs, which the pin pages show as owners.
        HardwarePins.RaisePinAssignmentsChanged();
        ShowStatus($"I2S clock mode set to {(mode == 1 ? "Slave" : "Master")}", false);
    }

    private async void OnAdatClockModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (AdatClockCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!byte.TryParse(tag, out var mode) || mode == Vm.AdatInputClockMode) return;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputClockMode(mode));
        if (status != PinConfigResult.Success)
        {
            _suppress = true;
            SelectByStringTag(AdatClockCombo, Vm.AdatInputClockMode);
            _suppress = false;
            ShowStatus($"Failed to set the ADAT clock source (0x{status:X2}).", true);
            return;
        }
        ShowStatus($"ADAT clock source set to {(mode == 1 ? "Slave" : "Master")}", false);
        RefreshFreeRunWarning();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private void SelectRate(uint hz)
    {
        for (int i = 0; i < RateCombo.Items.Count; i++)
        {
            if (RateCombo.Items[i] is ComboBoxItem item
                && uint.TryParse(item.Tag?.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var v)
                && v == hz)
            {
                RateCombo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Select the combo item whose string Tag ("0"/"1") equals the byte
    /// <paramref name="value"/>.</summary>
    private static void SelectByStringTag(ComboBox combo, byte value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem item && item.Tag is string s
                && byte.TryParse(s, out var v) && v == value)
            {
                combo.SelectedIndex = i;
                return;
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
    public string Id => "hardware.clocking";
    public string Title => "Clocking";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => "";
    public int Order => 15;
    public bool IsAvailable(MainViewModel vm) =>
        vm.InputI2sSupported || vm.I2sClockModeSupported || vm.AdatInputSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareClockingPage();
        p.Attach(vm, tracker);
        return p;
    }
}
