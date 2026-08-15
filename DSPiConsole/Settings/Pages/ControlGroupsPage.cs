using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Control › Channel Groups. A named set of channels one binding drives as a
/// unit (firmware caps v9). Renders the Groups section of
/// <see cref="ControlSurfacesPanel"/>; see <see cref="ControlSurfacesPage"/> for
/// why these pages aren't SettingsModules.
/// </summary>
public sealed class ControlGroupsPage : ISettingsPage
{
    public string Id => "control.groups";
    public string Title => "Channel Groups";
    public SettingsCategory Category => SettingsCategory.Control;
    public string IconGlyph => ""; // Sliders
    public int Order => 20;
    // Groups and macros arrived together in caps v9; a pre-v9 firmware advertises
    // no slots and STALLs the commands, so the pages have nothing to show.
    public bool IsAvailable(MainViewModel vm) => vm.ControlSurfacesSupported && vm.CsGroupsSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
        => new ControlSurfacesPanel(vm, CsSection.Groups);
}
