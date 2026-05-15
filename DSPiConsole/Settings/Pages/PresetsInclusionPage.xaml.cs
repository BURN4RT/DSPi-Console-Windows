using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Presets › Inclusion — single toggle controlling whether pin
/// assignments are bundled with each preset (firmware
/// REQ_SET_PRESET_INCLUDE_PINS).
/// </summary>
public sealed partial class PresetsInclusionPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public PresetsInclusionPage() { InitializeComponent(); }

    protected override void Refresh()
    {
        if (Vm == null) return;
        if (!Vm.PresetsSupported)
        {
            UnsupportedNotice.Visibility = Visibility.Visible;
            IncludePinsCard.Visibility = Visibility.Collapsed;
            return;
        }

        _suppress = true;
        try { IncludePinsToggle.IsOn = Vm.PresetIncludePins; }
        finally { _suppress = false; }
    }

    private void OnIncludePinsToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;

        // Stage rather than commit: the firmware write happens when
        // the user clicks Apply in the InfoBar. Re-toggling produces
        // a re-Stage with the same key, which the tracker dedupes;
        // toggling back to the original value auto-discards.
        var oldValue = Vm.PresetIncludePins;
        var newValue = IncludePinsToggle.IsOn;
        var vm = Vm;
        Tracker.Stage(new PendingChange(
            Key: "presets.inclusion.pins",
            PageId: Id,
            FieldLabel: "Include pin assignments",
            OldDisplay: oldValue ? "On" : "Off",
            NewDisplay: newValue ? "On" : "Off",
            Apply: async () => await vm.SetPresetIncludePins(newValue) ? (byte)0 : (byte)0xFF));
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "presets.inclusion";
    public string Title => "Inclusion";
    public SettingsCategory Category => SettingsCategory.Presets;
    public string IconGlyph => ""; // Bulleted list
    public int Order => 20;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new PresetsInclusionPage();
        p.Attach(vm, tracker);
        return p;
    }
}
