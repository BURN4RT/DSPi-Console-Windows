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
    /// <summary>Base input channels the output ids sit above (Master L/R).</summary>
    public const int AppInputCount = 2;

    /// <summary>Total app channel id space: 2 base inputs + 9 outputs (ids 0..10)
    /// plus 6 extra input channels (ids 11..16). Sizes any id-indexed array.</summary>
    public const int AppChannelCount = 17;

    /// <summary>First app id of the extra unified-model input channels (Input3).
    /// Ids <see cref="ExtraInputFirstId"/>..+5 map to wire input indices 2..7.</summary>
    public const int ExtraInputFirstId = 11;

    /// <summary>App channel id → absolute firmware wire channel index.</summary>
    public static int AppToWire(int appChannelId, int numInputChannels)
    {
        if (appChannelId < AppInputCount)
            return appChannelId;                        // inputs 0,1 pass straight through
        if (appChannelId >= ExtraInputFirstId)
            return AppInputCount + (appChannelId - ExtraInputFirstId); // extra inputs → wire 2..7
        int outputPos = appChannelId - AppInputCount;   // 0-based output position (0..8)
        return numInputChannels + outputPos;
    }

    /// <summary>
    /// Absolute firmware wire channel index → app channel id, or -1 if the wire
    /// channel has no representation in the app model (padding beyond the valid
    /// channel count).
    /// </summary>
    public static int WireToApp(int wireIndex, int numInputChannels)
    {
        if (wireIndex < numInputChannels)
        {
            if (wireIndex < AppInputCount) return wireIndex;   // Master L/R
            if (wireIndex < 8) return ExtraInputFirstId + (wireIndex - AppInputCount); // 2..7 → 11..16
            return -1;
        }
        int outputPos = wireIndex - numInputChannels;          // 0-based output position
        int appId = AppInputCount + outputPos;                 // outputs occupy ids 2..10
        return appId <= (int)10 ? appId : -1;
    }

    /// <summary>True for the extra unified-model input channels (ids 11..16), which
    /// only exist on a device with more than 2 wire input channels.</summary>
    public static bool IsExtraInput(int appChannelId) => appChannelId >= ExtraInputFirstId;
}
