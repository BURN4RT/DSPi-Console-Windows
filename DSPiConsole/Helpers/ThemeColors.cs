using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DSPiConsole.Helpers;

public static class ThemeColors
{
    private static bool IsDark(ElementTheme theme) => theme != ElementTheme.Light;

    // Dashboard cards, filter editor, output controls
    public static Color CardBackground(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(153, 45, 45, 48) : Color.FromArgb(20, 0, 0, 0);

    // Dashboard card borders
    public static Color CardBorder(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(51, 128, 128, 128) : Color.FromArgb(40, 0, 0, 0);

    // BodePlot, Crossfeed, Loudness grid lines
    public static Color GridLine(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(25, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);

    // BodePlot, Crossfeed, Loudness zero line
    public static Color ZeroLine(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(76, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0);

    // Graph axis labels
    public static Color AxisLabel(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(140, 180, 180, 180) : Color.FromArgb(200, 80, 80, 80);

    // BodePlot canvas background
    public static Color PlotBackground(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(128, 32, 32, 36) : Color.FromArgb(128, 240, 240, 244);

    // Vertical dividers in output controls
    public static Color Separator(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(25, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0);

    // Dashboard alternating rows
    public static Color AltRowBackground(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(8, 255, 255, 255) : Color.FromArgb(8, 0, 0, 0);

    // Output GAIN/DELAY labels
    public static Color DimLabel(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(160, 180, 180, 180) : Color.FromArgb(200, 80, 80, 80);

    // dB/ms/Hz unit text
    public static Color UnitLabel(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(140, 180, 180, 180) : Color.FromArgb(180, 80, 80, 80);

    // Dashboard band numbers (theme-independent)
    public static Color BandNumber() =>
        Color.FromArgb(178, 128, 128, 128);

    // Dashboard inactive filter text (theme-independent)
    public static Color InactiveFilter() =>
        Color.FromArgb(102, 128, 128, 128);

    // Dashboard flat filter indicator (theme-independent)
    public static Color FlatDash() =>
        Color.FromArgb(51, 128, 128, 128);

    // Muted text indicator (theme-independent)
    public static Color MutedText() =>
        Color.FromArgb(255, 200, 80, 80);

    // Mute icon when unmuted
    public static Color MuteIconActive(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(200, 200, 200, 200) : Color.FromArgb(200, 60, 60, 60);

    // Mute icon when muted
    public static Color MuteIconMuted(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(255, 80, 80, 80) : Color.FromArgb(255, 200, 200, 200);

    // Secondary text (channel names, filter values)
    public static Color SecondaryText(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(255, 204, 204, 204) : Color.FromArgb(255, 68, 68, 68);

    // Tertiary text (unit labels in filter rows)
    public static Color TertiaryText(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(255, 136, 136, 136) : Color.FromArgb(255, 102, 102, 102);

    // Sidebar acrylic backdrop tint
    public static Color AcrylicTint(ElementTheme theme) =>
        IsDark(theme) ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);

    public static void ApplyTitleBar(AppWindowTitleBar titleBar, ElementTheme theme)
    {
        if (IsDark(theme))
        {
            titleBar.ForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.InactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 50, 50, 50);
        }
        else
        {
            titleBar.ForegroundColor = Color.FromArgb(255, 30, 30, 30);
            titleBar.BackgroundColor = Color.FromArgb(255, 243, 243, 243);
            titleBar.InactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.InactiveBackgroundColor = Color.FromArgb(255, 243, 243, 243);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 30, 30, 30);
            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 243, 243, 243);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 243, 243, 243);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 0, 0, 0);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 229, 229, 229);
        }
    }
}
