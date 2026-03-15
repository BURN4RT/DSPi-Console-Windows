using DSPiConsole.Core.Models;

namespace DSPiConsole.Core;

/// <summary>
/// DSP mathematics for biquad filter coefficient calculation and frequency response.
/// Direct port of the macOS DSPMath.swift and firmware coefficient calculations.
/// All internal math uses double (64-bit) precision for accuracy at low frequencies.
/// </summary>
public static class DspMath
{
    public const float SampleRate = 48000.0f;

    /// <summary>
    /// Biquad filter coefficients (normalized, a0 = 1)
    /// </summary>
    public readonly struct Coefficients
    {
        public readonly double B0, B1, B2, A1, A2;

        public Coefficients(double b0, double b1, double b2, double a1, double a2)
        {
            B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
        }

        public static Coefficients Unity => new(1, 0, 0, 0, 0);
    }

    /// <summary>
    /// Calculate biquad coefficients for a filter.
    /// Matches the firmware compute_coefficients() function.
    /// </summary>
    public static Coefficients CalculateCoefficients(FilterParams p, float sampleRate = SampleRate)
    {
        if (p.Type == FilterType.Flat)
            return Coefficients.Unity;

        double omega = 2.0 * Math.PI * p.Frequency / sampleRate;
        double sn = Math.Sin(omega);
        double cs = Math.Cos(omega);
        double alpha = sn / (2.0 * p.Q);
        double A = Math.Pow(10.0, p.Gain / 40.0);

        double b0 = 1, b1 = 0, b2 = 0;
        double a0 = 1, a1 = 0, a2 = 0;

        switch (p.Type)
        {
            case FilterType.LowPass:
                b0 = (1 - cs) / 2;
                b1 = 1 - cs;
                b2 = (1 - cs) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;

            case FilterType.HighPass:
                b0 = (1 + cs) / 2;
                b1 = -(1 + cs);
                b2 = (1 + cs) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;

            case FilterType.Peaking:
                b0 = 1 + alpha * A;
                b1 = -2 * cs;
                b2 = 1 - alpha * A;
                a0 = 1 + alpha / A;
                a1 = -2 * cs;
                a2 = 1 - alpha / A;
                break;

            case FilterType.LowShelf:
                {
                    double sqrtA = Math.Sqrt(A);
                    b0 = A * ((A + 1) - (A - 1) * cs + 2 * sqrtA * alpha);
                    b1 = 2 * A * ((A - 1) - (A + 1) * cs);
                    b2 = A * ((A + 1) - (A - 1) * cs - 2 * sqrtA * alpha);
                    a0 = (A + 1) + (A - 1) * cs + 2 * sqrtA * alpha;
                    a1 = -2 * ((A - 1) + (A + 1) * cs);
                    a2 = (A + 1) + (A - 1) * cs - 2 * sqrtA * alpha;
                }
                break;

            case FilterType.HighShelf:
                {
                    double sqrtA = Math.Sqrt(A);
                    b0 = A * ((A + 1) + (A - 1) * cs + 2 * sqrtA * alpha);
                    b1 = -2 * A * ((A - 1) + (A + 1) * cs);
                    b2 = A * ((A + 1) + (A - 1) * cs - 2 * sqrtA * alpha);
                    a0 = (A + 1) - (A - 1) * cs + 2 * sqrtA * alpha;
                    a1 = 2 * ((A - 1) - (A + 1) * cs);
                    a2 = (A + 1) - (A - 1) * cs - 2 * sqrtA * alpha;
                }
                break;
        }

        // Normalize by a0
        return new Coefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>
    /// Calculate the frequency response magnitude in dB for a set of filters at a given frequency.
    /// Evaluates H(e^jω) for each filter and multiplies the magnitudes.
    /// </summary>
    public static float ResponseAt(float freq, IEnumerable<FilterParams> filters, float sampleRate = SampleRate)
    {
        double magSquaredTotal = 1.0;

        foreach (var f in filters)
        {
            if (f.Type == FilterType.Flat || !f.IsActive)
                continue;

            var coeffs = CalculateCoefficients(f, sampleRate);
            double w = 2.0 * Math.PI * freq / sampleRate;

            // Evaluate Transfer Function |H(e^jw)|²
            // H(z) = (b0 + b1*z^-1 + b2*z^-2) / (1 + a1*z^-1 + a2*z^-2)
            // z = e^jw = cos(w) + j*sin(w)

            double cos_w = Math.Cos(w);
            double cos_2w = Math.Cos(2.0 * w);
            double sin_w = Math.Sin(w);
            double sin_2w = Math.Sin(2.0 * w);

            // Numerator (Real and Imaginary parts)
            double num_r = coeffs.B0 + coeffs.B1 * cos_w + coeffs.B2 * cos_2w;
            double num_i = -(coeffs.B1 * sin_w + coeffs.B2 * sin_2w);

            // Denominator (a0 is normalized to 1)
            double den_r = 1.0 + coeffs.A1 * cos_w + coeffs.A2 * cos_2w;
            double den_i = -(coeffs.A1 * sin_w + coeffs.A2 * sin_2w);

            double num_mag_sq = num_r * num_r + num_i * num_i;
            double den_mag_sq = den_r * den_r + den_i * den_i;

            if (den_mag_sq > 1e-18)
            {
                magSquaredTotal *= (num_mag_sq / den_mag_sq);
            }
        }

        return (float)(10.0 * Math.Log10(magSquaredTotal));
    }

    /// <summary>
    /// Generate frequency response curve points for plotting
    /// </summary>
    public static (float[] frequencies, float[] magnitudes) GenerateResponseCurve(
        IEnumerable<FilterParams> filters,
        int numPoints = 201,
        float minFreq = 10.0f,
        float maxFreq = 20000.0f,
        float sampleRate = SampleRate)
    {
        var frequencies = new float[numPoints];
        var magnitudes = new float[numPoints];

        double logMin = Math.Log10(minFreq);
        double logMax = Math.Log10(maxFreq);

        for (int i = 0; i < numPoints; i++)
        {
            double pct = i / (double)(numPoints - 1);
            float freq = (float)Math.Pow(10, logMin + pct * (logMax - logMin));
            frequencies[i] = freq;
            magnitudes[i] = ResponseAt(freq, filters, sampleRate);
        }

        return (frequencies, magnitudes);
    }
}
