using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// General › Globals — device-wide settings that are not part of an individual
/// preset's DSP state: master-volume persistence mode, power-on startup
/// behaviour, and output-config (physical IO) persistence mode. The startup and
/// output-config sections require preset support and are hidden behind a notice
/// otherwise. Consolidates the former Volume / Presets-Startup /
/// Presets-Inclusion pages. All three settings are flash-persistent and routed
/// through the pending-change tracker.
/// </summary>
public sealed partial class GeneralVolumePage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public GeneralVolumePage() { InitializeComponent(); }

    protected override void Refresh()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            // Master volume mode is available regardless of preset support.
            MasterVolumeModeCombo.SelectedIndex = Vm.MasterVolumeMode == 1 ? 1 : 0;

            // Startup + output-config require presets — hide them behind a
            // notice when the firmware doesn't expose presets.
            bool presets = Vm.PresetsSupported;
            UnsupportedNotice.Visibility = presets ? Visibility.Collapsed : Visibility.Visible;
            StartupModeCard.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;
            DefaultPresetCard.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;
            OutputConfigModeCard.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;
            if (!presets) return;

            StartupModeCombo.SelectedIndex = Vm.PresetStartupMode;

            DefaultPresetCombo.Items.Clear();
            for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
            {
                DefaultPresetCombo.Items.Add(new ComboBoxItem
                {
                    Content = Vm.GetPresetDisplayName(i),
                    Tag = i
                });
            }
            DefaultPresetCombo.SelectedIndex = Vm.PresetDefaultSlot;
            DefaultPresetCombo.IsEnabled = Vm.PresetStartupMode == 0;

            // ComboBoxItem order: 0=With preset (Tag=1), 1=Independent (Tag=0).
            OutputConfigModeCombo.SelectedIndex = Vm.OutputConfigMode == 1 ? 0 : 1;
        }
        finally { _suppress = false; }
    }

    private void OnMasterVolumeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (MasterVolumeModeCombo.SelectedItem is not ComboBoxItem item) return;
        if (!byte.TryParse(item.Tag?.ToString() ?? "0", out var newMode)) return;

        // Flash-persistent — stage rather than commit.
        var oldMode = Vm.MasterVolumeMode;
        var vm = Vm;
        static string Label(byte m) => m == 1 ? "Per preset" : "Global";
        Tracker.Stage(new PendingChange(
            Key: "general.globals.master-mode",
            PageId: Id,
            FieldLabel: "Master volume mode",
            OldDisplay: Label(oldMode),
            NewDisplay: Label(newMode),
            Apply: async () => await vm.SetMasterVolumeMode(newMode) ? (byte)0 : (byte)0xFF));
    }

    // ── Startup (moved from the former Presets › Startup page) ──────────
    private void OnStartupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (StartupModeCombo.SelectedIndex < 0) return;

        // Default-preset combo is only meaningful when mode == 0 ("Default
        // preset"); update its enabled state immediately for the UI.
        byte mode = (byte)StartupModeCombo.SelectedIndex;
        DefaultPresetCombo.IsEnabled = mode == 0;
        StageStartupChange();
    }

    private void OnDefaultPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (DefaultPresetCombo.SelectedItem is not ComboBoxItem || DefaultPresetCombo.SelectedIndex < 0) return;
        StageStartupChange();
    }

    /// <summary>
    /// Stage the combined startup config. Mode + default slot ride together in a
    /// single firmware command (REQ_SET_PRESET_STARTUP), so they share one
    /// tracker key — re-editing either combo de-dupes with the first stage.
    /// </summary>
    private void StageStartupChange()
    {
        if (Vm == null || Tracker == null) return;

        byte newMode = (byte)System.Math.Max(0, StartupModeCombo.SelectedIndex);
        byte newSlot = DefaultPresetCombo.SelectedItem is ComboBoxItem di && di.Tag is int ds
            ? (byte)ds : (byte)0;

        byte oldMode = Vm.PresetStartupMode;
        byte oldSlot = Vm.PresetDefaultSlot;

        string Describe(byte mode, byte slot) =>
            mode == 0
                ? $"Default preset → {Vm!.GetPresetDisplayName(slot)}"
                : "Last used";

        var vm = Vm;
        Tracker.Stage(new PendingChange(
            Key: "general.globals.startup",
            PageId: Id,
            FieldLabel: "Startup behaviour",
            OldDisplay: Describe(oldMode, oldSlot),
            NewDisplay: Describe(newMode, newSlot),
            Apply: async () => await vm.SetPresetStartup(newMode, newSlot) ? (byte)0 : (byte)0xFF));
    }

    // ── Output config mode (moved from the former Presets › Inclusion page) ──
    private void OnOutputConfigModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (OutputConfigModeCombo.SelectedItem is not ComboBoxItem item) return;
        if (!byte.TryParse(item.Tag?.ToString() ?? "1", out var newMode)) return;

        var oldMode = Vm.OutputConfigMode;
        var vm = Vm;
        static string Label(byte m) => m == 1 ? "With preset" : "Independent";
        Tracker.Stage(new PendingChange(
            Key: "general.globals.output-config-mode",
            PageId: Id,
            FieldLabel: "Output configuration",
            OldDisplay: Label(oldMode),
            NewDisplay: Label(newMode),
            Apply: async () => await vm.SetOutputConfigMode(newMode) ? (byte)0 : (byte)0xFF));
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "general.globals";
    public string Title => "Globals";
    public SettingsCategory Category => SettingsCategory.General;
    public string IconGlyph => ""; // Volume
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new GeneralVolumePage();
        p.Attach(vm, tracker);
        return p;
    }
}
