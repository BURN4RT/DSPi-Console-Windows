using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Advanced › Debug — single toggle for the in-app debug overlay.
/// Persists to <see cref="AppSettings.ShowDebugInfo"/>.
///
/// <para>
/// Apply cadence: <b>Live</b>. Each toggle change writes the JSON file
/// and fires <see cref="AppSettings.NotifyChanged"/> so the rest of the
/// app picks up the new value without needing a Save click.
/// </para>
///
/// <para>
/// One class, two roles: this type is both the UserControl that renders
/// the page <i>and</i> the <see cref="ISettingsPage"/> descriptor the
/// shell queries for metadata. <c>BuildContent</c> creates a fresh
/// instance so the registry's template is never displayed — that keeps
/// the descriptor stateless while letting page state live on the
/// instance that's actually attached to the visual tree.
/// </para>
/// </summary>
public sealed partial class AdvancedDebugPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public AdvancedDebugPage()
    {
        InitializeComponent();
    }

    protected override void Refresh()
    {
        _suppress = true;
        try { DebugToggle.IsOn = AppSettings.Instance.ShowDebugInfo; }
        finally { _suppress = false; }
    }

    private void OnDebugToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var s = AppSettings.Instance;
        s.ShowDebugInfo = DebugToggle.IsOn;
        s.Save();
        s.NotifyChanged();
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "advanced.debug";
    public string Title => "Debug";
    public SettingsCategory Category => SettingsCategory.Advanced;
    public string IconGlyph => ""; // Bug
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var instance = new AdvancedDebugPage();
        instance.Attach(vm, tracker);
        return instance;
    }
}
