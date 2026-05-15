using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Presets › Startup — power-on preset behaviour. Two combo boxes that
/// together produce one <see cref="MainViewModel.SetPresetStartup"/>
/// call (firmware REQ_SET_PRESET_STARTUP packs mode and default slot
/// into a single transfer, so the helper takes both args at once).
///
/// <para>
/// If the connected firmware doesn't support presets, both cards are
/// hidden and a notice replaces them. Capability gating is checked once
/// at <see cref="Attach"/> time — <see cref="IsAvailable"/> further
/// guards at the registry level for sidebar visibility.
/// </para>
/// </summary>
public sealed partial class PresetsStartupPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public PresetsStartupPage() { InitializeComponent(); }

    protected override void Refresh()
    {
        if (Vm == null) return;

        // Firmware doesn't expose presets at all → swap cards for the
        // notice. We still build the page (the shell expects content)
        // but the actionable controls are hidden.
        if (!Vm.PresetsSupported)
        {
            UnsupportedNotice.Visibility = Visibility.Visible;
            StartupModeCard.Visibility = Visibility.Collapsed;
            DefaultPresetCard.Visibility = Visibility.Collapsed;
            return;
        }

        _suppress = true;
        try
        {
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
        }
        finally { _suppress = false; }
    }

    private void OnStartupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (StartupModeCombo.SelectedIndex < 0) return;

        // Default-preset combo is only meaningful when mode == 0
        // ("Default preset"). Updated immediately for UI; the actual
        // value going to the device is whichever combo is selected at
        // Apply time.
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
    /// Stage the combined startup config. Mode + default slot ride
    /// together in a single firmware command (REQ_SET_PRESET_STARTUP)
    /// — so they share one tracker key. Re-edit either combo and
    /// the second Stage de-dupes with the first.
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
            Key: "presets.startup",
            PageId: Id,
            FieldLabel: "Startup behaviour",
            OldDisplay: Describe(oldMode, oldSlot),
            NewDisplay: Describe(newMode, newSlot),
            Apply: async () => await vm.SetPresetStartup(newMode, newSlot) ? (byte)0 : (byte)0xFF));
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "presets.startup";
    public string Title => "Startup";
    public SettingsCategory Category => SettingsCategory.Presets;
    public string IconGlyph => ""; // Play
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true; // shown either way; renders notice if unsupported
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new PresetsStartupPage();
        p.Attach(vm, tracker);
        return p;
    }
}
