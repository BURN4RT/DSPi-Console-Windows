using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// General › Volume — two settings that govern how volume is stored
/// and which slider the main window's sidebar drives.
///
/// <para>
/// Master Volume Mode is flash-persistent on the device (Phase 1 still
/// writes on each change). Sidebar Volume Mode is AppSettings JSON and
/// is live-applied.
/// </para>
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
            MasterVolumeModeCombo.SelectedIndex = Vm.MasterVolumeMode == 1 ? 1 : 0;

            // Sidebar mode is "master" or "user" string in AppSettings. Map
            // the Tag back to the index. Default to Master if anything's odd.
            var mode = AppSettings.Instance.SidebarVolumeMode;
            SidebarVolumeModeCombo.SelectedIndex = mode == "user" ? 1 : 0;
        }
        finally { _suppress = false; }
    }

    private void OnMasterVolumeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (MasterVolumeModeCombo.SelectedItem is not ComboBoxItem item) return;
        if (!byte.TryParse(item.Tag?.ToString() ?? "0", out var newMode)) return;

        // Flash-persistent — stage rather than commit. Sidebar Volume
        // Mode below stays live (AppSettings JSON, not flash).
        var oldMode = Vm.MasterVolumeMode;
        var vm = Vm;
        static string Label(byte m) => m == 1 ? "Per preset" : "Global";
        Tracker.Stage(new PendingChange(
            Key: "general.volume.master-mode",
            PageId: Id,
            FieldLabel: "Master volume mode",
            OldDisplay: Label(oldMode),
            NewDisplay: Label(newMode),
            Apply: async () => await vm.SetMasterVolumeMode(newMode) ? (byte)0 : (byte)0xFF));
    }

    private void OnSidebarVolumeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (SidebarVolumeModeCombo.SelectedItem is not ComboBoxItem item) return;
        var mode = (item.Tag as string) ?? "master";
        var s = AppSettings.Instance;
        if (s.SidebarVolumeMode == mode) return;
        s.SidebarVolumeMode = mode;
        s.Save();
        s.NotifyChanged();
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "general.volume";
    public string Title => "Volume";
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
