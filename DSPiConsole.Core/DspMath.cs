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

            case FilterType.Notch:
                b0 = 1;
                b1 = -2 * cs;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;

            case FilterType.AllPass:
                b0 = 1 - alpha;
                b1 = -2 * cs;
                b2 = 1 + alpha;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;
        }

        // Normalize by a0
        return new Coefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>
    /// Calculate the frequency response magnitude in dB for a set of filters at a given frequency.
    /// Evaluates H(e^jω) for each filter and multiplies the magnitudes. PEQ bands
    /// (types 0-7) are single biquads; crossover bands (types 8-39) are multi-section
    /// cascades designed to match the firmware (see <see cref="CrossoverSections"/>).
    /// </summary>
    public static float ResponseAt(float freq, IEnumerable<FilterParams> filters, float sampleRate = SampleRate)
    {
        double magSquaredTotal = 1.0;
        double w = 2.0 * Math.PI * freq / sampleRate;

        foreach (var f in filters)
        {
            if (f.Type == FilterType.Flat || !f.IsActive || f.Bypass)
                continue;

            if (f.Type.IsCrossover())
            {
                // High-order crossover band → product of its biquad sections.
                foreach (var c in CrossoverSections(f.Type, f.Frequency, sampleRate))
                    magSquaredTotal *= BiquadMagSquared(c, w);
                continue;
            }

            magSquaredTotal *= BiquadMagSquared(CalculateCoefficients(f, sampleRate), w);
        }

        return (float)(10.0 * Math.Log10(magSquaredTotal));
    }

    /// <summary>
    /// |H(e^jw)|² for one normalized biquad (a0 = 1).
    /// H(z) = (b0 + b1·z⁻¹ + b2·z⁻²) / (1 + a1·z⁻¹ + a2·z⁻²), z = e^jw.
    /// </summary>
    private static double BiquadMagSquared(Coefficients c, double w)
    {
        double cos_w = Math.Cos(w);
        double cos_2w = Math.Cos(2.0 * w);
        double sin_w = Math.Sin(w);
        double sin_2w = Math.Sin(2.0 * w);

        double num_r = c.B0 + c.B1 * cos_w + c.B2 * cos_2w;
        double num_i = -(c.B1 * sin_w + c.B2 * sin_2w);
        double den_r = 1.0 + c.A1 * cos_w + c.A2 * cos_2w;
        double den_i = -(c.A1 * sin_w + c.A2 * sin_2w);

        double num = num_r * num_r + num_i * num_i;
        double den = den_r * den_r + den_i * den_i;
        return den > 1e-18 ? num / den : 1.0;
    }

    // ── Crossover filter design (ported from firmware crossover.c) ──────────
    // Each crossover "band" is a cascade of biquad sections built from an analog
    // prototype via the bilinear transform with frequency prewarping. We only
    // need the cascade's biquad coefficients to evaluate its magnitude; the SVF
    // path the firmware uses on low fc is the same transfer function, so the
    // bilinear (TDF2) coefficients give an identical |H|. Matches the device.

    /// <summary>
    /// Design the biquad-section cascade for a crossover filter type at cutoff
    /// <paramref name="fc"/> Hz. Returns an empty list for non-crossover types.
    /// </summary>
    public static IReadOnlyList<Coefficients> CrossoverSections(FilterType type, double fc, double sampleRate)
    {
        var sections = new List<Coefficients>(4);
        if (sampleRate <= 0.0 || !CrossoverFilter.TryGetMeta(type, out var meta))
            return sections;

        // Clamp fc to a safe range — same as the firmware / PEQ path.
        if (fc < 10.0) fc = 10.0;
        if (fc > sampleRate * 0.45) fc = sampleRate * 0.45;

        double omega_a = 2.0 * sampleRate * Math.Tan(Math.PI * fc / sampleRate); // prewarp
        bool hp = meta.IsHighPass;
        int order = meta.Order;

        switch (meta.Family)
        {
            case XoverFamily.Butterworth:
                DesignButterworth(sections, order, hp, omega_a, sampleRate);
                break;
            case XoverFamily.LinkwitzRiley:
                DesignLinkwitzRiley(sections, order, hp, omega_a, sampleRate);
                break;
            case XoverFamily.Bessel:
                DesignBessel(sections, order, hp, omega_a, sampleRate);
                break;
        }
        return sections;
    }

    private static void DesignButterworth(List<Coefficients> s, int order, bool hp, double omega_a, double Fs)
    {
        if ((order & 1) != 0)                                // odd: real pole at σ_n = 1
            s.Add(SectionEmit1st(1.0, omega_a, hp, Fs));
        for (int p = 0; p < order / 2; p++)
        {
            BwPolePair(order, p, out var sigma, out var omega);
            s.Add(SectionEmit2nd(sigma, omega, omega_a, hp, Fs));
        }
    }

    // LR_{2N} = (BW_N)²: design BW_N, then duplicate every section (squaring the
    // magnitude). LR2 is the canonical single biquad with a double real pole.
    private static void DesignLinkwitzRiley(List<Coefficients> s, int orderLr, bool hp, double omega_a, double Fs)
    {
        if (orderLr == 2)
        {
            s.Add(SectionEmit2nd(1.0, 0.0, omega_a, hp, Fs));
            return;
        }
        int start = s.Count;
        DesignButterworth(s, orderLr / 2, hp, omega_a, Fs);
        int count = s.Count - start;
        for (int i = 0; i < count; i++)
            s.Add(s[start + i]);                            // duplicate the BW cascade
    }

    private static void DesignBessel(List<Coefficients> s, int order, bool hp, double omega_a, double Fs)
    {
        foreach (var (sigma, omega) in BesselTable(order))
            s.Add(SectionEmit2nd(sigma, omega, omega_a, hp, Fs));
    }

    // Butterworth conjugate-pair pole angles measured from the negative-real
    // axis; σ_n = cos θ, ω_n = sin θ. Matches crossover.c bw_pole_pair().
    private static void BwPolePair(int order, int pairIdx, out double sigma, out double omega)
    {
        double theta = (order & 1) != 0
            ? Math.PI * (pairIdx + 1) / order
            : Math.PI * (2 * pairIdx + 1) / (2.0 * order);
        sigma = Math.Cos(theta);
        omega = Math.Sin(theta);
    }

    // Bessel (-3 dB normalized) analog pole pairs (σ_n, ω_n). Exact values from
    // firmware crossover.c (verified there against scipy). Even orders only.
    private static (double sigma, double omega)[] BesselTable(int order) => order switch
    {
        2 => new[] { (1.10160, 0.63601) },
        4 => new[] { (1.37007, 0.41025), (0.99521, 1.25711) },
        6 => new[] { (1.57149, 0.32090), (1.38186, 0.97147), (0.93066, 1.66186) },
        8 => new[] { (1.75741, 0.27287), (1.63694, 0.82280), (1.37384, 1.38836), (0.89287, 1.99833) },
        _ => Array.Empty<(double, double)>()
    };

    // 2nd-order section: analog pole pair (σ_n, ω_n) → biquad via bilinear
    // transform (K = 2·Fs). The LP→HP reciprocal (σ,ω)/(σ²+ω²) is applied for HP
    // — a no-op for BW/LR (poles on the unit circle) and required for Bessel.
    private static Coefficients SectionEmit2nd(double sigmaN, double omegaN, double omega_a, bool hp, double Fs)
    {
        if (hp)
        {
            double r2 = sigmaN * sigmaN + omegaN * omegaN;
            if (r2 > 0.0) { sigmaN /= r2; omegaN /= r2; }
        }
        double sigma = sigmaN * omega_a;
        double omega = omegaN * omega_a;
        double K = 2.0 * Fs;
        double A = 2.0 * sigma;
        double B = sigma * sigma + omega * omega;
        double A0 = K * K + A * K + B;
        double A1 = 2.0 * (B - K * K);
        double A2 = K * K - A * K + B;
        double inv = 1.0 / A0;

        double b0, b1, b2;
        if (hp) { double kk = K * K * inv; b0 = kk; b1 = -2.0 * kk; b2 = kk; }
        else { double bb = B * inv; b0 = bb; b1 = 2.0 * bb; b2 = bb; }
        return new Coefficients(b0, b1, b2, A1 * inv, A2 * inv);
    }

    // 1st-order real-pole section (Butterworth odd orders). HP reciprocal
    // 1/σ_n is a no-op for σ_n = 1 but kept for symmetry with the 2nd-order path.
    private static Coefficients SectionEmit1st(double sigmaN, double omega_a, bool hp, double Fs)
    {
        if (hp && sigmaN > 0.0) sigmaN = 1.0 / sigmaN;
        double sigma = sigmaN * omega_a;
        double K = 2.0 * Fs;
        double A0 = K + sigma;
        double A1 = sigma - K;
        double inv = 1.0 / A0;

        double b0, b1;
        if (hp) { b0 = K * inv; b1 = -K * inv; }
        else { b0 = sigma * inv; b1 = sigma * inv; }
        return new Coefficients(b0, b1, 0.0, A1 * inv, 0.0);
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
