using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Presets &#x203A; Inclusion — output-config persistence mode
/// (REQ_SET_OUTPUT_CONFIG_MODE, 0x98). Mirrors the master-volume mode pattern
/// from GeneralVolumePage. See output_config_independent_load_spec.md.
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
            OutputConfigModeCard.Visibility = Visibility.Collapsed;
            return;
        }

        _suppress = true;
        try
        {
            // ComboBoxItem order: 0=With preset (Tag=1), 1=Independent (Tag=0).
            OutputConfigModeCombo.SelectedIndex = Vm.OutputConfigMode == 1 ? 0 : 1;
        }
        finally { _suppress = false; }
    }

    private void OnOutputConfigModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (OutputConfigModeCombo.SelectedItem is not ComboBoxItem item) return;
        if (!byte.TryParse(item.Tag?.ToString() ?? "1", out var newMode)) return;

        var oldMode = Vm.OutputConfigMode;
        var vm = Vm;
        static string Label(byte m) => m == 1 ? "With preset" : "Independent";
        Tracker.Stage(new PendingChange(
            Key: "presets.inclusion.output-config-mode",
            PageId: Id,
            FieldLabel: "Output configuration",
            OldDisplay: Label(oldMode),
            NewDisplay: Label(newMode),
            Apply: async () => await vm.SetOutputConfigMode(newMode) ? (byte)0 : (byte)0xFF));
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "presets.inclusion";
    public string Title => "Inclusion";
    public SettingsCategory Category => SettingsCategory.Presets;
    public string IconGlyph => ""; // Bulleted list
    public int Order => 20;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new PresetsInclusionPage();
        p.Attach(vm, tracker);
        return p;
    }
}
