using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings;

/// <summary>
/// One leaf page in the Settings window. Each page is a self-contained
/// UserControl that owns its XAML, VM binding, and apply behaviour. The
/// <see cref="SettingsShell"/> hosts pages selected from the NavigationView
/// sidebar — it never inspects a page's internals.
///
/// <para>
/// Adding a new firmware feature is a four-step exercise:
///   1. Add a new <see cref="SettingsModule"/> UserControl under
///      <c>Settings/Pages/</c>.
///   2. Have the page class also implement <see cref="ISettingsPage"/>;
///      <c>BuildContent</c> returns a fresh attached instance.
///   3. Register the page in <see cref="SettingsRegistry"/>.
///   4. (Optional) Override <see cref="IsAvailable"/> to hide the entry
///      from the sidebar when the connected firmware lacks the feature.
/// </para>
/// </summary>
public interface ISettingsPage
{
    /// <summary>Stable identifier used for nav-state persistence and
    /// pending-change keys. Format: "{category}.{page}", lowercase, kebab.
    /// e.g. "hardware.output-assignment".</summary>
    string Id { get; }

    /// <summary>Human-readable title shown both in the sidebar and at the
    /// top of the content area. Keep to a short noun phrase
    /// (e.g. "Output Assignment", not "Configure Output Assignments").</summary>
    string Title { get; }

    /// <summary>The category this page belongs under. Pages with the same
    /// CategoryId render under one collapsible group in the sidebar.</summary>
    SettingsCategory Category { get; }

    /// <summary>Segoe Fluent / MDL2 glyph string. Sub-page icons are
    /// suppressed in the sidebar (the category icon covers the group);
    /// the glyph is only used inside the content header.</summary>
    string IconGlyph { get; }

    /// <summary>Sort order within the page's category. Lower numbers
    /// appear first. Use multiples of 10 to leave room for inserts.</summary>
    int Order { get; }

    /// <summary>Capability gate. Returns false to hide the page from the
    /// sidebar entirely — used for firmware-version-dependent features
    /// (e.g. S/PDIF Input needs V7+, DAC Mute needs V10+).</summary>
    bool IsAvailable(MainViewModel vm);

    /// <summary>Build (or rebuild) the page's content tree. Called once
    /// per page selection — the shell caches the result so repeated
    /// navigation back to the same page reuses the same instance and
    /// preserves scroll position / focus. <paramref name="tracker"/>
    /// is the Settings window's pending-change tracker; device-flash
    /// pages stage their edits there. Live AppSettings pages can
    /// ignore it (they continue to write through immediately).</summary>
    UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker);
}

/// <summary>
/// Top-level sidebar group. Order here defines sidebar order. About is
/// rendered separately in NavigationView.FooterMenuItems so it always
/// pins to the bottom regardless of category count.
/// </summary>
public enum SettingsCategory
{
    General,
    Graphing,
    Hardware,
    Presets,
    Advanced,
    About,
}

/// <summary>Display metadata for a category — title and the glyph that
/// stands in for it in the collapsed sidebar. Centralised here so the
/// shell doesn't switch on the enum value.
///
/// <para>
/// Glyph codepoints reference Segoe Fluent Icons. Use C# Unicode escape
/// sequences so the source stays ASCII-clean (raw private-use-area
/// characters in source files have a history of being mangled by file
/// rewrites). See
/// https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font
/// for the canonical list of glyphs and their codepoints.
/// </para></summary>
public static class SettingsCategoryInfo
{
    public static (string Title, string Glyph) For(SettingsCategory cat) => cat switch
    {
        SettingsCategory.General  => ("General",  ""),  // Settings (gear)
        SettingsCategory.Graphing => ("Graphing", ""),  // BarChart4
        SettingsCategory.Hardware => ("Hardware", ""),  // USB
        SettingsCategory.Presets  => ("Presets",  ""),  // Save
        SettingsCategory.Advanced => ("Advanced", ""),  // DeveloperTools
        SettingsCategory.About    => ("About",    ""),  // Info
        _ => (cat.ToString(), ""),
    };
}
