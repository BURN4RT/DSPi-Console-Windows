namespace DSPiConsole.Core.Models;

/// <summary>
/// BS2B (Bauer Stereophonic-to-Binaural) crossfeed calculations and presets.
/// </summary>
public static class CrossfeedData
{
    /// <summary>
    /// Crossfeed presets: (cutoff frequency Hz, feed level dB, description).
    /// </summary>
    public static readonly (float freq, float feed, string desc)[] Presets = new[]
    {
        (700f, 4.5f, "Balanced, most popular"),
        (700f, 6.0f, "Stronger spatial effect"),
        (650f, 9.5f, "Natural speaker-like"),
        (700f, 4.5f, "User-defined") // Custom starts with Default values
    };

    /// <summary>
    /// Calculate frequency response curves for BS2B crossfeed filter.
    /// Returns logarithmically spaced frequencies from 20 Hz to 20 kHz and corresponding magnitudes in dB.
    /// </summary>
    /// <param name="cutoffFreq">Lowpass cutoff frequency in Hz (500-2000)</param>
    /// <param name="feedDb">Feed level in dB (0-15)</param>
    /// <returns>Tuple of (frequencies, direct path magnitudes, crossfeed path magnitudes) in dB</returns>
    public static (float[] freqs, float[] directMags, float[] crossfeedMags) GetResponseCurves(float cutoffFreq, float feedDb)
    {
        const int numPoints = 100;
        const float sampleRate = 48000f;
        const float minFreq = 20f;
        const float maxFreq = 20000f;

        // Clamp input parameters to valid ranges to prevent division by zero
        cutoffFreq = Math.Clamp(cutoffFreq, 500f, 2000f);
        feedDb = Math.Clamp(feedDb, 0f, 15f);

        var freqs = new float[numPoints];
        var directMags = new float[numPoints];
        var crossfeedMags = new float[numPoints];

        // Logarithmic frequency spacing
        float logMin = MathF.Log10(minFreq);
        float logMax = MathF.Log10(maxFreq);
        float logStep = (logMax - logMin) / (numPoints - 1);

        for (int i = 0; i < numPoints; i++)
        {
            freqs[i] = MathF.Pow(10f, logMin + i * logStep);
        }

        // BS2B filter parameters
        float omega = 2f * MathF.PI * cutoffFreq / sampleRate; // Normalized angular frequency
        float feedLinear = MathF.Pow(10f, -feedDb / 20f); // Convert dB to linear (attenuation)

        // Calculate magnitude response at each frequency
        for (int i = 0; i < numPoints; i++)
        {
            float freq = freqs[i];
            float w = 2f * MathF.PI * freq / sampleRate; // Normalized angular frequency

            // Crossfeed path: lowpass filter with feed attenuation
            // H_crossfeed(w) = feedLinear / sqrt(1 + (w/omega)^2)
            float ratio = w / omega;
            float crossfeedLinear = feedLinear / MathF.Sqrt(1f + ratio * ratio);

            // Direct path: complementary to maintain constant total energy
            // H_direct(w) = sqrt(1 - H_crossfeed^2)
            float directLinear = MathF.Sqrt(1f - crossfeedLinear * crossfeedLinear);

            // Convert to dB (20 * log10(magnitude))
            // Clamp to prevent log(0)
            directMags[i] = 20f * MathF.Log10(MathF.Max(directLinear, 1e-6f));
            crossfeedMags[i] = 20f * MathF.Log10(MathF.Max(crossfeedLinear, 1e-6f));
        }

        return (freqs, directMags, crossfeedMags);
    }
}
