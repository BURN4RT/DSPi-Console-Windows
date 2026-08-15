using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Control › Control Surfaces. The physical-control bindings half of the
/// control-surface editor: buttons, switches, pots, encoders, LEDs and the IR
/// receiver. The editor body itself is <see cref="ControlSurfacesPanel"/>, shared
/// with the Channel Groups and Macros pages — this class is only the registry
/// entry that picks a section.
///
/// <para>
/// These three are plain ISettingsPage implementations rather than
/// <see cref="SettingsModule"/> subclasses: the panel manages its own device
/// state through the Save/Revert bar, so it has nothing to stage with the
/// pending-change tracker and nothing for the shell's RefreshFromShell to do.
/// </para>
/// </summary>
public sealed class ControlSurfacesPage : ISettingsPage
{
    public string Id => "control.surfaces";
    public string Title => "Control Surfaces";
    public SettingsCategory Category => SettingsCategory.Control;
    public string IconGlyph => ""; // Sliders
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => vm.ControlSurfacesSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
        => new ControlSurfacesPanel(vm, CsSection.Bindings);
}
