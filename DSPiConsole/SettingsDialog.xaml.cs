using DSPiConsole.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly string _platform;

    public SettingsDialog(string platform)
    {
        _platform = platform;

        InitializeComponent();

        var settings = AppSettings.Instance;

        GlowToggle.IsOn = settings.ShowGraphGlow;
        LineWidthSlider.Value = settings.GraphLineWidth;
        AnimSpeedSlider.Value = settings.GraphAnimationSpeed;
        DebugToggle.IsOn = settings.ShowDebugInfo;

        LineWidthText.Text = settings.GraphLineWidth.ToString("F1");
        AnimSpeedText.Text = settings.GraphAnimationSpeed.ToString("F2");

        LineWidthSlider.ValueChanged += (s, e) => LineWidthText.Text = e.NewValue.ToString("F1");
        AnimSpeedSlider.ValueChanged += (s, e) => AnimSpeedText.Text = e.NewValue.ToString("F2");

        PrimaryButtonClick += OnSave;

        BuildPinAssignmentTable();
    }


    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var settings = AppSettings.Instance;
        settings.ShowGraphGlow = GlowToggle.IsOn;
        settings.GraphLineWidth = LineWidthSlider.Value;
        settings.GraphAnimationSpeed = AnimSpeedSlider.Value;
        settings.ShowDebugInfo = DebugToggle.IsOn;
        settings.Save();
        settings.NotifyChanged();
    }

    // --- Pin Assignment table (visual only for now) ---

    private record PinOutput(int Id, string Name, string Detail, string Icon, byte DefaultPin, Color Color);

    private static readonly PinOutput[] PinOutputsRp2350 =
    [
        new(0, "S/PDIF 1", "Stereo pair 1 (L/R)", "\uE767", 6,
            Color.FromArgb(255, 69, 194, 163)),   // Teal
        new(1, "S/PDIF 2", "Stereo pair 2 (L/R)", "\uE767", 7,
            Color.FromArgb(255, 240, 196, 89)),    // Yellow
        new(2, "S/PDIF 3", "Stereo pair 3 (L/R)", "\uE767", 8,
            Color.FromArgb(255, 89, 140, 242)),    // Blue
        new(3, "S/PDIF 4", "Stereo pair 4 (L/R)", "\uE767", 9,
            Color.FromArgb(255, 217, 115, 140)),   // Pink
        new(4, "PDM",      "Subwoofer output",     "\uE9B1", 10,
            Color.FromArgb(255, 186, 135, 243)),   // Purple
    ];

    private static readonly PinOutput[] PinOutputsRp2040 =
    [
        new(0, "S/PDIF 1", "Stereo pair 1 (L/R)", "\uE767", 6,
            Color.FromArgb(255, 69, 194, 163)),
        new(1, "S/PDIF 2", "Stereo pair 2 (L/R)", "\uE767", 7,
            Color.FromArgb(255, 240, 196, 89)),
        new(2, "PDM",      "Subwoofer output",     "\uE9B1", 10,
            Color.FromArgb(255, 186, 135, 243)),
    ];

    private static readonly byte[] ValidPins =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
        26, 27, 28
    ];

    private void BuildPinAssignmentTable()
    {
        var outputs = _platform == "RP2350" ? PinOutputsRp2350 : PinOutputsRp2040;

        // Header row: "Pin Assignment" label + "Reset to Defaults" button
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerIcon = new FontIcon
        {
            Glyph = "\uE950", // CPU icon
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerText = new TextBlock
        {
            Text = "Pin Assignment",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal };
        headerLeft.Children.Add(headerIcon);
        headerLeft.Children.Add(headerText);
        Grid.SetColumn(headerLeft, 0);
        header.Children.Add(headerLeft);

        var resetBtn = new HyperlinkButton
        {
            Content = "Reset to Defaults",
            FontSize = 11,
            Padding = new Thickness(4, 2, 4, 2),
            IsEnabled = false // visual only for now
        };
        Grid.SetColumn(resetBtn, 1);
        header.Children.Add(resetBtn);

        HardwarePanel.Children.Add(header);

        // Separator
        HardwarePanel.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 2, 0, 2)
        });

        // Pin rows
        foreach (var output in outputs)
        {
            HardwarePanel.Children.Add(BuildPinRow(output));
        }
    }

    private UIElement BuildPinRow(PinOutput output)
    {
        // Main row grid: Icon | Name+Detail | DEFAULT badge | GPIO picker
        var row = new Grid
        {
            Padding = new Thickness(0, 6, 0, 6),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // icon
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name+detail
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // default badge
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // gpio picker

        // Colored icon
        var icon = new FontIcon
        {
            Glyph = output.Icon,
            FontSize = 14,
            Foreground = new SolidColorBrush(output.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        // Name + detail
        var nameStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameStack.Children.Add(new TextBlock
        {
            Text = output.Name,
            FontSize = 13
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = output.Detail,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        Grid.SetColumn(nameStack, 1);
        row.Children.Add(nameStack);

        // DEFAULT badge (shown since we display default pins)
        var badge = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Child = new TextBlock
            {
                Text = "DEFAULT",
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            }
        };
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);

        // GPIO picker (ComboBox)
        var combo = new ComboBox
        {
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = false // visual only for now
        };
        foreach (var pin in ValidPins)
        {
            combo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}" });
        }
        // Select the default pin
        var defaultIndex = Array.IndexOf(ValidPins, output.DefaultPin);
        if (defaultIndex >= 0) combo.SelectedIndex = defaultIndex;

        Grid.SetColumn(combo, 3);
        row.Children.Add(combo);

        return row;
    }

}
