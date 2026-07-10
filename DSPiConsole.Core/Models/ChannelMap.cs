namespace DSPiConsole.Core.Models;

/// <summary>
/// Maps between the app's stable channel identity and the firmware's V16+
/// unified-model wire channel index.
///
/// The app models channels as <see cref="ChannelId"/>: ids 0,1 = inputs
/// (Master L/R), ids 2..10 = the nine output channels. The firmware's unified
/// channel space is [ inputs 0..numInputs-1 ][ outputs numInputs.. ].
///
/// Before V16 the firmware always had exactly 2 inputs, so app id == wire index
/// and this map was the identity. On RP2350 V20 firmware the input region grew
/// to 8 channels, shifting every output index up by 6 (outputs now occupy wire
/// 8..16 instead of 2..10). RP2040 keeps 2 inputs, so it stays the identity.
///
/// All per-channel USB commands, bulk-array reads, live notifications and meter
/// peaks funnel through this map so the rest of the app can keep speaking in
/// stable <see cref="ChannelId"/> values.
/// </summary>
public static class ChannelMap
{
    /// <summary>Number of input channels the app UI currently models (Master L/R).</summary>
    public const int AppInputCount = 2;

    /// <summary>Number of app channels total (2 inputs + 9 outputs).</summary>
    public const int AppChannelCount = 11;

    /// <summary>App channel id → absolute firmware wire channel index.</summary>
    public static int AppToWire(int appChannelId, int numInputChannels)
    {
        if (appChannelId < AppInputCount)
            return appChannelId;                        // inputs 0,1 pass straight through
        int outputPos = appChannelId - AppInputCount;   // 0-based output position (0..8)
        return numInputChannels + outputPos;
    }

    /// <summary>
    /// Absolute firmware wire channel index → app channel id, or -1 if the wire
    /// channel has no representation in the app model (the extra inputs 2..7 on
    /// RP2350, or padding beyond the valid channel count).
    /// </summary>
    public static int WireToApp(int wireIndex, int numInputChannels)
    {
        if (wireIndex < numInputChannels)
            return wireIndex < AppInputCount ? wireIndex : -1; // extra inputs not modeled yet
        int outputPos = wireIndex - numInputChannels;          // 0-based output position
        int appId = AppInputCount + outputPos;
        return appId < AppChannelCount ? appId : -1;
    }
}
