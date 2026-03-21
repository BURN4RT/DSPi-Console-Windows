using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        Title = "Import Filters";
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
        Title = "Import Filters";
        MessageText.Text = "This file contains filter settings for multiple channels. Select which channels to import:";
        var inFile = new HashSet<int>(availableChannelIds);

        // Master channels — checked if present in file
        foreach (var channel in new[] { Channel.MasterLeft, Channel.MasterRight })
            AddCheckbox(channel, isChecked: inFile.Contains((int)channel.Id));

        // All output channels — checked if present in file AND enabled
        for (int o = 0; o < activeOutputs.Count; o++)
            AddCheckbox(activeOutputs[o], isChecked: inFile.Contains((int)activeOutputs[o].Id) && isOutputEnabled(o));
    }

    /// <summary>
    /// Configure for AutoEQ profile application - groups channels by stereo pairs with custom names.
    /// </summary>
    public void ConfigureForAutoEQ(int filterCount, IReadOnlyList<Channel> activeOutputs,
        Func<int, bool> isOutputEnabled, Func<Channel, string> getChannelName)
    {
        Title = "Apply AutoEQ Profile";
        PrimaryButtonText = "Apply";
        MessageText.Text = $"Select which channel(s) to apply {filterCount} filter(s) to:";

        // Inputs group
        AddGroupHeader("Inputs");
        AddStereoCheckbox(Channel.MasterLeft, Channel.MasterRight, getChannelName, isChecked: true);

        // Outputs group
        AddGroupHeader("Outputs");
        for (int o = 0; o < activeOutputs.Count; o++)
        {
            var ch = activeOutputs[o];
            if (o + 1 < activeOutputs.Count &&
                (int)activeOutputs[o + 1].Id == (int)ch.Id + 1 &&
                isOutputEnabled(o) && isOutputEnabled(o + 1))
            {
                AddStereoCheckbox(ch, activeOutputs[o + 1], getChannelName, isChecked: false);
                o++; // skip partner
            }
            else if (isOutputEnabled(o))
            {
                AddCheckbox(ch, isChecked: false, getChannelName(ch));
            }
        }
    }

    private void AddGroupHeader(string text)
    {
        ChannelCheckboxes.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
    }

    private void AddStereoCheckbox(Channel left, Channel right,
        Func<Channel, string> getChannelName, bool isChecked)
    {
        var leftName = getChannelName(left);
        var rightName = getChannelName(right);
        var checkbox = new CheckBox
        {
            Content = $"{leftName} / {rightName}",
            Tag = new int[] { (int)left.Id, (int)right.Id },
            IsChecked = isChecked
        };
        _checkboxes.Add(checkbox);
        ChannelCheckboxes.Children.Add(checkbox);
    }

    private void AddCheckbox(Channel channel, bool isChecked, string? displayName = null)
    {
        var checkbox = new CheckBox
        {
            Content = displayName ?? channel.Name,
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
            if (checkbox.IsChecked != true) continue;
            if (checkbox.Tag is int channelId)
                SelectedChannelIds.Add(channelId);
            else if (checkbox.Tag is int[] channelIds)
                SelectedChannelIds.AddRange(channelIds);
        }
    }
}
