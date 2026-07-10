using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DSPiConsole.Controls;

/// <summary>
/// A compact horizontal row of numbered toggle "chips" for editing a bitmask.
/// Each chip maps to a specific bit index (not necessarily contiguous — e.g.
/// only the enabled outputs), shows that channel's 1-based number, and surfaces
/// its real name in the tooltip. A checked chip is filled (the default
/// ToggleButton accent state). Used by the multichannel DSP mask selectors
/// (leveller / loudness / crossfeed).
///
/// The grid never owns the mask — it reports single-bit toggles via the
/// <c>onToggle(bit, isOn)</c> callback and re-renders from an authoritative
/// mask via <see cref="SetMask"/>, so the ViewModel stays the single source of
/// truth.
/// </summary>
public sealed class MaskChipGrid
{
    private readonly List<(ToggleButton chip, int bit)> _chips = new();
    private readonly Action<int, bool> _onToggle;
    private bool _suppress;

    /// <summary>The visual root to place in the window.</summary>
    public Panel Root { get; }

    /// <summary>Contiguous bit indices 0..n-1 — the common "one chip per channel" case.</summary>
    public static IReadOnlyList<int> AllBits(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    /// <param name="bitIndices">The bit each chip controls, in display order. Chip
    /// caption is the 1-based bit number, so a filtered set (e.g. enabled outputs)
    /// still shows real channel numbers.</param>
    /// <param name="tooltipForBit">Tooltip text for a given bit index.</param>
    /// <param name="onToggle">Called with the bit index and its new state on user toggle.</param>
    /// <param name="stretch">When true, chips share the container width equally (each
    /// in a star-sized column) so the row fills the space rather than leaving a gap on
    /// the right. When false, chips are a fixed compact width and left-packed.</param>
    /// <param name="captionForBit">Optional override for a chip's caption; return null
    /// to use the default 1-based number (e.g. return "S" for the sub output).</param>
    public MaskChipGrid(IReadOnlyList<int> bitIndices, Func<int, string> tooltipForBit,
                        Action<int, bool> onToggle, bool stretch = false,
                        Func<int, string?>? captionForBit = null)
    {
        _onToggle = onToggle;
        int count = bitIndices.Count;

        Grid? grid = null;
        StackPanel? stack = null;
        if (stretch)
        {
            grid = new Grid { ColumnSpacing = 6 };
            for (int i = 0; i < count; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        }

        for (int j = 0; j < count; j++)
        {
            int bit = bitIndices[j];
            var chip = new ToggleButton
            {
                Content = captionForBit?.Invoke(bit) ?? (bit + 1).ToString(),
                Height = 30,
                Padding = new Thickness(0),
                FontSize = 12
            };
            if (stretch)
            {
                // Fill the column; MinWidth 0 lets many chips shrink to fit the
                // row instead of overflowing (and clipping the last chip).
                chip.HorizontalAlignment = HorizontalAlignment.Stretch;
                chip.MinWidth = 0;
                Grid.SetColumn(chip, j);
                grid!.Children.Add(chip);
            }
            else
            {
                chip.MinWidth = 34;
                chip.Width = 34;
                stack!.Children.Add(chip);
            }
            ToolTipService.SetToolTip(chip, tooltipForBit(bit));
            chip.Checked += (_, _) => { if (!_suppress) _onToggle(bit, true); };
            chip.Unchecked += (_, _) => { if (!_suppress) _onToggle(bit, false); };
            _chips.Add((chip, bit));
        }

        Root = (Panel?)grid ?? stack!;
    }

    /// <summary>Reflect an authoritative mask into the chip states without firing callbacks.</summary>
    public void SetMask(uint mask)
    {
        _suppress = true;
        foreach (var (chip, bit) in _chips)
            chip.IsChecked = (mask & (1u << bit)) != 0;
        _suppress = false;
    }
}
