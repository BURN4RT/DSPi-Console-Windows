using System.Text.Json;

namespace DSPiConsole.Models;

public class AppSettings
{
    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DSPiConsole", "settings.json");

    public bool ShowGraphGlow { get; set; } = true;
    public double GraphLineWidth { get; set; } = 2.0;
    public double GraphAnimationSpeed { get; set; } = 0.2;
    public bool ShowDebugInfo { get; set; }

    // Graph scale
    public double GraphDbRange { get; set; } = 50.0;
    public double GraphDbCenter { get; set; } = 0.0;
    public double GraphMinFrequency { get; set; } = 20.0;
    public double GraphMaxFrequency { get; set; } = 20000.0;

    // Grid/label visibility
    public bool ShowFrequencyGrid { get; set; } = true;
    public bool ShowFrequencyLabels { get; set; } = true;
    public bool ShowDbGrid { get; set; } = true;
    public bool ShowDbLabels { get; set; } = true;
    public bool ShowDbUnits { get; set; } = true;

    // Dotted lines for non-selected channels
    public bool DottedInactiveChannels { get; set; } = true;

    // Whether the popout graph follows the selected channel editor page
    public bool PopoutFollowsSelectedChannel { get; set; } = true;

    // Master L/R PEQ link
    public bool MasterPeqLinked { get; set; }

    public event EventHandler? SettingsChanged;

    public void NotifyChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Ignore load errors
        }
        return new AppSettings();
    }
}
