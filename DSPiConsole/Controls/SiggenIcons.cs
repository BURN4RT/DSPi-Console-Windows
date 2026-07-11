using System;
using System.Collections.Generic;
using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DSPiConsole.Controls;

/// <summary>
/// Builds small stylized waveform glyphs (a <see cref="Geometry"/>) for each test
/// signal type. Everything is drawn in a fixed 60×28 coordinate box with the
/// midline at y=14 and a nominal amplitude of 9, so the caller renders the Path
/// with <c>Stretch="None"</c> at 60×28.
/// </summary>
public static class SiggenIcons
{
    private const double X0 = 2, X1 = 58, Mid = 14, Amp = 9;

    public static Geometry Get(SiggenType t) => t switch
    {
        // Steady tone — a gentle sine, ~1.5 cycles.
        SiggenType.Sine       => Wave(u => Math.Sin(2 * Math.PI * 1.5 * u)),
        // Square wave, two cycles.
        SiggenType.Square     => Line((2, 5), (16, 5), (16, 23), (30, 23), (30, 5), (44, 5), (44, 23), (58, 23)),
        // Broadband hash — dense, full-amplitude jitter.
        SiggenType.White      => Line(Noise(new[] { 14.0, 6, 20, 9, 23, 5, 17, 8, 22, 11, 4, 19, 7, 24, 10, 16, 6, 14 })),
        // Pink — smoother, more low-frequency wander.
        SiggenType.Pink       => Line(Noise(new[] { 14.0, 9, 6, 10, 18, 22, 17, 12, 8, 13, 14 })),
        // Log sweep — frequency climbs exponentially (compresses hard at the end).
        SiggenType.SweepLog   => Wave(u => Math.Sin(2 * Math.PI * 0.4 * (Math.Exp(2.5 * u) - 1))),
        // Linear sweep — frequency climbs linearly (more even compression).
        SiggenType.SweepLin   => Wave(u => Math.Sin(2 * Math.PI * (0.8 * u + 3.5 * u * u))),
        // Stepped sweep — discrete rising steps.
        SiggenType.SweepStep  => Line((2, 22), (14, 22), (14, 17), (26, 17), (26, 12), (38, 12), (38, 7), (50, 7), (50, 3), (58, 3)),
        // Single impulse spike.
        SiggenType.Impulse    => Line((2, 14), (28, 14), (30, 3), (32, 14), (58, 14)),
        // Alternating-polarity clicks — one up, one down.
        SiggenType.ClicksAlt  => Figures(
                                     Pts((2, 14), (14, 14), (15, 4), (16, 14), (30, 14)),
                                     Pts((30, 14), (31, 24), (32, 14), (46, 14), (47, 4), (48, 14), (58, 14))),
        // Polarity test — a sharp asymmetric pulse (up, overshoot, settle).
        SiggenType.Polarity   => Line((2, 14), (20, 14), (24, 4), (27, 19), (34, 14), (58, 14)),
        // Tone burst — a windowed packet of cycles surrounded by silence.
        SiggenType.ToneBurst  => Wave(Burst),
        // Two tones — a beating (amplitude-modulated) waveform.
        SiggenType.TonePair   => Wave(u => Math.Cos(2 * Math.PI * 0.7 * u) * Math.Sin(2 * Math.PI * 5 * u)),
        // Multitone — sum of several sines, complex but periodic.
        SiggenType.Multitone  => Wave(Multi),
        // Inter-sample peak — a dense near-Nyquist tone.
        SiggenType.Isp        => Wave(u => Math.Sin(2 * Math.PI * 5.5 * u)),
        // Channel ID — Morse-like ident dashes along the midline.
        SiggenType.ChannelId  => Figures(
                                     Pts((4, 14), (10, 14)),
                                     Pts((16, 14), (30, 14)),
                                     Pts((36, 14), (40, 14)),
                                     Pts((46, 14), (58, 14))),
        _                     => Wave(u => Math.Sin(2 * Math.PI * 1.5 * u))
    };

    private static double Burst(double u)
    {
        if (u < 0.28 || u > 0.72) return 0;
        double p = (u - 0.28) / 0.44;        // 0..1 across the burst
        double env = Math.Sin(Math.PI * p);  // Hann-ish window
        return env * env * Math.Sin(2 * Math.PI * 6 * p);
    }

    private static double Multi(double u)
    {
        double v = 0.50 * Math.Sin(2 * Math.PI * 1.5 * u)
                 + 0.32 * Math.Sin(2 * Math.PI * 4 * u + 0.6)
                 + 0.24 * Math.Sin(2 * Math.PI * 7 * u + 1.2);
        return v / 1.06;                     // keep within ±1
    }

    // ── geometry builders ──

    private static Geometry Wave(Func<double, double> f, int n = 56)
    {
        var pts = new List<Point>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double u = (double)i / n;
            pts.Add(new Point(X0 + (X1 - X0) * u, Mid - Amp * f(u)));
        }
        return Figures(pts);
    }

    private static List<Point> Noise(double[] ys)
    {
        var pts = new List<Point>(ys.Length);
        for (int i = 0; i < ys.Length; i++)
            pts.Add(new Point(X0 + (X1 - X0) * i / (ys.Length - 1), ys[i]));
        return pts;
    }

    private static List<Point> Pts(params (double x, double y)[] p)
    {
        var list = new List<Point>(p.Length);
        foreach (var (x, y) in p) list.Add(new Point(x, y));
        return list;
    }

    private static Geometry Line(params (double x, double y)[] p) => Figures(Pts(p));
    private static Geometry Line(List<Point> pts) => Figures(pts);

    private static Geometry Figures(params IList<Point>[] strokes)
    {
        var g = new PathGeometry();
        foreach (var s in strokes)
        {
            if (s.Count == 0) continue;
            var fig = new PathFigure { StartPoint = s[0], IsClosed = false, IsFilled = false };
            var seg = new PolyLineSegment();
            for (int i = 1; i < s.Count; i++) seg.Points.Add(s[i]);
            fig.Segments.Add(seg);
            g.Figures.Add(fig);
        }
        return g;
    }
}
