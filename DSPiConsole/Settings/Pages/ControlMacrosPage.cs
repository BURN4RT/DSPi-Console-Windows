using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Control › Macros. A sequence of up to eight delayed parameter changes fired by
/// a button, a remote key or the page itself (firmware caps v9). Renders the
/// Macros section of <see cref="ControlSurfacesPanel"/>; see
/// <see cref="ControlSurfacesPage"/> for why these pages aren't SettingsModules.
/// </summary>
public sealed class ControlMacrosPage : ISettingsPage
{
    public string Id => "control.macros";
    public string Title => "Macros";
    public SettingsCategory Category => SettingsCategory.Control;
    public string IconGlyph => ""; // Sliders
    public int Order => 30;
    public bool IsAvailable(MainViewModel vm) => vm.ControlSurfacesSupported && vm.CsGroupsSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
        => new ControlSurfacesPanel(vm, CsSection.Macros);
}
