using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Presets › UI — non-flash toggles that affect preset UI surfaces in
/// the main window. AppSettings JSON; live-applied.
/// </summary>
public sealed partial class PresetsUIPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public PresetsUIPage() { InitializeComponent(); }

    protected override void Refresh()
    {
        _suppress = true;
        try { ShowSaveButtonToggle.IsOn = AppSettings.Instance.ShowPresetSaveButton; }
        finally { _suppress = false; }
    }

    private void OnShowSaveButtonToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var s = AppSettings.Instance;
        s.ShowPresetSaveButton = ShowSaveButtonToggle.IsOn;
        s.Save();
        s.NotifyChanged();
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "presets.ui";
    public string Title => "UI";
    public SettingsCategory Category => SettingsCategory.Presets;
    public string IconGlyph => ""; // NewWindow
    public int Order => 30;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new PresetsUIPage();
        p.Attach(vm, tracker);
        return p;
    }
}
