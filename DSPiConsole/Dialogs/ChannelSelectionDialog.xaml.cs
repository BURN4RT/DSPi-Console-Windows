using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Dialogs;

public sealed partial class ChannelSelectionDialog : ContentDialog
{
    private readonly List<CheckBox> _checkboxes = new();

    public List<int> SelectedChannelIds { get; } = new();

    public ChannelSelectionDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Configure for single-channel import (REW format) - applies to any channel.
    /// </summary>
    public void ConfigureForSingleChannel(int filterCount, IReadOnlyList<Channel> activeOutputs,
        Func<int, bool> isOutputEnabled)
    {
        MessageText.Text = $"Found {filterCount} filter(s). Select which channel(s) to apply them to:";

        // Master channels — checked by default
        foreach (var channel in new[] { Channel.MasterLeft, Channel.MasterRight })
            AddCheckbox(channel, isChecked: true);

        // All output channels — unchecked
        for (int o = 0; o < activeOutputs.Count; o++)
            AddCheckbox(activeOutputs[o], isChecked: false);
    }

    /// <summary>
    /// Configure for multi-channel import (DSPi format) - shows all channels,
    /// pre-checks enabled ones that are present in the file.
    /// </summary>
    public void ConfigureForMultiChannel(IEnumerable<int> availableChannelIds,
        IReadOnlyList<Channel> activeOutputs, Func<int, bool> isOutputEnabled)
    {
        MessageText.Text = "This file contains filter settings for multiple channels. Select which channels to import:";
        var inFile = new HashSet<int>(availableChannelIds);

        // Master channels — checked if present in file
        foreach (var channel in new[] { Channel.MasterLeft, Channel.MasterRight })
            AddCheckbox(channel, isChecked: inFile.Contains((int)channel.Id));

        // All output channels — checked if present in file AND enabled
        for (int o = 0; o < activeOutputs.Count; o++)
            AddCheckbox(activeOutputs[o], isChecked: inFile.Contains((int)activeOutputs[o].Id) && isOutputEnabled(o));
    }

    private void AddCheckbox(Channel channel, bool isChecked)
    {
        var checkbox = new CheckBox
        {
            Content = channel.Name,
            Tag = (int)channel.Id,
            IsChecked = isChecked
        };
        _checkboxes.Add(checkbox);
        ChannelCheckboxes.Children.Add(checkbox);
    }

    public void CollectSelectedChannels()
    {
        SelectedChannelIds.Clear();
        foreach (var checkbox in _checkboxes)
        {
            if (checkbox.IsChecked == true && checkbox.Tag is int channelId)
            {
                SelectedChannelIds.Add(channelId);
            }
        }
    }
}
