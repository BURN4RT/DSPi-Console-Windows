using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// System › Overview — a read-only map of every GPIO the device is holding.
///
/// <para>
/// Only <b>held</b> pins are listed. A feature that is configured but switched
/// off reserves nothing on the device and so appears nowhere here: a disabled
/// optional S/PDIF input, ADAT while stopped, a control interface that is down,
/// a control-surface binding the device refused at boot. That is not a
/// simplification for the sake of the page — it is the firmware's own rule, and
/// the pin pickers on the other pages follow it too.
/// </para>
///
/// <para>
/// Nothing here is editable, so the page stages nothing with the tracker. It
/// rebuilds on the same <see cref="HardwarePins.PinAssignmentsChanged"/>
/// broadcast the pickers use, and on the handful of view-model properties that
/// change which claims are live.
/// </para>
/// </summary>
public sealed partial class HardwareOverviewPage : SettingsModule, ISettingsPage
{
    /// <summary>Columns in the map. Fixed rather than adaptive: the settings
    /// content pane is a known width, and a count that re-decides itself from
    /// the width offered settles a frame late.</summary>
    private const int MapColumns = 9;

    /// <summary>Cell width in the map. Set, not starred: stretched across the
    /// pane a one- or two-digit number sits in a chip four times wider than it
    /// needs, which reads as a table of something rather than a pin header.</summary>
    private const double MapCellWidth = 64;

    /// <summary>Columns in the breakdown.</summary>
    private const int BreakdownColumns = 3;

    /// <summary>Column width in the breakdown. Set, not starred, for the two
    /// reasons starring fails here: a grid left-aligned to its content sizes to
    /// that content, which turns starred columns into Auto ones and clumps the
    /// entries together, while a stretched grid capped by MaxWidth is centred in
    /// what is left over and drifts away from the heading above it. A width says
    /// what it means and stays under the heading. Three of these plus their gaps
    /// come to roughly 750px, which the settings pane has room for; a name too
    /// long for one ellipsizes rather than widening the column.</summary>
    private const double BreakdownColumnWidth = 240;

    /// <summary>One hue per role, from the macOS build so a chip means the same
    /// thing on both, except Other. Chosen by construction rather than by eye:
    /// hues spread around the wheel, each held inside the narrow lightness band
    /// where the fill carries white text at 4.5:1 — the invariant every one of
    /// these holds, running 4.6 to 5.8.
    ///
    /// <para>Other is amber here rather than the olive macOS uses, which reads as
    /// green next to the teal. The cost is separation: it sits 28 degrees from
    /// the clock brick where the olive sat 47. That only matters on the map,
    /// which mixes all five — each role section shows one colour alone. On the
    /// map the colour is the secondary cue anyway: the reliable read is claimed
    /// (saturated) against free (grey).</para></summary>
    private static Color RoleTint(PinRole role) => role switch
    {
        PinRole.Output => Color.FromArgb(255, 0x02, 0x78, 0xC7),   // blue
        PinRole.Clock => Color.FromArgb(255, 0xBA, 0x38, 0x22),    // brick
        PinRole.Input => Color.FromArgb(255, 0x04, 0x85, 0x6F),    // teal
        PinRole.Control => Color.FromArgb(255, 0x95, 0x43, 0xA7),  // orchid
        _ => Color.FromArgb(255, 0x9E, 0x61, 0x04),                // amber
    };

    private static string RoleTitle(PinRole role) => role switch
    {
        PinRole.Output => "Outputs",
        PinRole.Clock => "Clocks",
        PinRole.Input => "Inputs",
        PinRole.Control => "Control",
        _ => "Other",
    };

    public HardwareOverviewPage()
    {
        InitializeComponent();
        // Subscriptions on Loaded/Unloaded rather than in the constructor: the
        // shell caches page instances and detaches them when you navigate away,
        // so a constructor-only subscription would outlive the page's presence.
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnPinsChanged;
        HardwarePins.PinAssignmentsChanged += OnPinsChanged;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
        }
        // Another page may have moved a pin while this one was detached.
        Refresh();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnPinsChanged;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnPinsChanged() => DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>The properties that change which pins are claimed rather than
    /// what they are called. Everything else a pin edit touches arrives through
    /// <see cref="HardwarePins.PinAssignmentsChanged"/>.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected)
            or nameof(MainViewModel.CsStatus)
            or nameof(MainViewModel.CtrlIfaceStatus)
            or nameof(MainViewModel.Platform))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        bool connected = Vm.IsDeviceConnected;
        DisconnectedHint.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        BodyPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (!connected) return;

        var rows = HardwarePins.ActiveAssignments(Vm);
        var claims = rows.ToDictionary(r => r.Pin);
        int total = HardwarePins.ValidPins.Length;
        int free = total - rows.Count;

        UsageText.Text = $"{rows.Count} of {total} GPIOs in use";
        FreeText.Text = free == 0 ? "none free" : $"{free} free";

        BuildMap(claims);
        BuildLegend(rows);
        BuildGroups(rows);
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── The map ──────────────────────────────────────────────────────────────

    /// <summary>Every valid GPIO in pin order, tinted by what holds it and muted
    /// where free, so occupancy reads as colour rather than by parsing numbers.</summary>
    private void BuildMap(IReadOnlyDictionary<byte, PinAssignment> claims)
    {
        MapGrid.Children.Clear();
        MapGrid.ColumnDefinitions.Clear();
        MapGrid.RowDefinitions.Clear();

        var pins = HardwarePins.ValidPins;
        int rowCount = (pins.Length + MapColumns - 1) / MapColumns;
        for (int c = 0; c < MapColumns; c++)
            MapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MapCellWidth) });
        for (int r = 0; r < rowCount; r++)
            MapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < pins.Length; i++)
        {
            byte pin = pins[i];
            claims.TryGetValue(pin, out var claim);
            bool held = claims.ContainsKey(pin);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(0, 8, 0, 9),
                Background = new SolidColorBrush(held
                    ? RoleTint(claim.Role)
                    : Color.FromArgb(0x20, 0x88, 0x88, 0x88)),
                Child = new TextBlock
                {
                    Text = pin.ToString(),
                    FontSize = 13,
                    FontFamily = MonoFont,
                    FontWeight = held ? FontWeights.SemiBold : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = held
                        ? new SolidColorBrush(Colors.White)
                        : SecondaryBrush,
                },
            };
            ToolTipService.SetToolTip(chip, held ? $"GPIO {pin} — {claim.Label}" : $"GPIO {pin} — available");
            Grid.SetColumn(chip, i % MapColumns);
            Grid.SetRow(chip, i / MapColumns);
            MapGrid.Children.Add(chip);
        }
    }

    /// <summary>A dot and a count per role in use — the same key as the sections
    /// below, beside the colours it explains. Reads as a tally ("5 Outputs")
    /// rather than a label with a figure after it.</summary>
    private void BuildLegend(IReadOnlyList<PinAssignment> rows)
    {
        LegendPanel.Children.Clear();
        foreach (var group in GroupByRole(rows))
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            entry.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(RoleTint(group.Role)),
            });
            entry.Children.Add(new TextBlock
            {
                Text = $"{group.Rows.Count} {RoleTitle(group.Role)}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SecondaryBrush,
            });
            LegendPanel.Children.Add(entry);
        }
    }

    // ── The breakdown ────────────────────────────────────────────────────────

    /// <summary>One section per role in use, each a two-column list of pins and
    /// the features holding them.</summary>
    private void BuildGroups(IReadOnlyList<PinAssignment> rows)
    {
        GroupsPanel.Children.Clear();
        foreach (var group in GroupByRole(rows))
        {
            var section = new StackPanel { Spacing = 12 };
            section.Children.Add(new TextBlock
            {
                Text = RoleTitle(group.Role).ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = SecondaryBrush,
            });

            // A fixed column count, like the map's: the pane is a known width,
            // and a count that re-decides itself settles a frame late.
            var grid = new Grid
            {
                ColumnSpacing = 16,
                RowSpacing = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            for (int c = 0; c < BreakdownColumns; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BreakdownColumnWidth) });
            int rowCount = (group.Rows.Count + BreakdownColumns - 1) / BreakdownColumns;
            for (int r = 0; r < rowCount; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < group.Rows.Count; i++)
            {
                var cell = BuildAssignmentCell(group.Rows[i]);
                Grid.SetColumn(cell, i % BreakdownColumns);
                Grid.SetRow(cell, i / BreakdownColumns);
                grid.Children.Add(cell);
            }
            section.Children.Add(grid);
            GroupsPanel.Children.Add(section);
        }
    }

    /// <summary>One pin: a role-tinted GPIO chip and the feature holding it. The
    /// chip is a fixed width so the names line up down every column. No role
    /// glyph — the section names the role and the chip is already coloured by
    /// it, so a third indicator would only cost width.</summary>
    private static FrameworkElement BuildAssignmentCell(PinAssignment row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        panel.Children.Add(new Border
        {
            Width = 43,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(0, 3, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(RoleTint(row.Role)),
            Child = new TextBlock
            {
                Text = $"GP{row.Pin}",
                FontSize = 13,
                FontFamily = MonoFont,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White),
            },
        });
        var label = new TextBlock
        {
            Text = row.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(label, row.Label);
        panel.Children.Add(label);
        return panel;
    }

    /// <summary>Claimed pins bucketed by role, empty roles dropped, each list in
    /// pin order and the roles in the order the enum declares them.</summary>
    private static IEnumerable<(PinRole Role, IReadOnlyList<PinAssignment> Rows)> GroupByRole(
        IReadOnlyList<PinAssignment> rows)
    {
        foreach (PinRole role in Enum.GetValues<PinRole>())
        {
            var members = rows.Where(r => r.Role == role).OrderBy(r => r.Pin).ToList();
            if (members.Count > 0) yield return (role, members);
        }
    }

    // ── Shared brushes ───────────────────────────────────────────────────────

    private static FontFamily MonoFont => new("Consolas");

    private static Brush SecondaryBrush =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    // ── ISettingsPage ────────────────────────────────────────────────────────

    public string Id => "hardware.overview";
    public string Title => "Overview";
    public SettingsCategory Category => SettingsCategory.System;
    public string IconGlyph => ""; // AllApps (a grid)
    public int Order => 5;   // leads the group: it is the summary of the rest
    public bool IsAvailable(MainViewModel vm) => true;

    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var page = new HardwareOverviewPage();
        page.Attach(vm, tracker);
        return page;
    }
}
