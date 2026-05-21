using SenseEase.Core.Configuration;

namespace SenseEase.Core.Inference;

/// <summary>
/// Maps an ambient-light reading (lux) to comfortable monitor Contrast/Brightness/
/// Sharpness targets. Pure, deterministic logic with one stateful EMA term.
/// Comfort model: a dark room => lower contrast/brightness/sharpness (reduce the
/// painful luminance gap and harsh edges); a bright room => higher (preserve
/// legibility). Lux is perceptually logarithmic, so the curve is linear in log10(lux).
/// </summary>
public sealed class LuxComfortMapper
{
    private double _smoothedLux;
    private bool _seeded;

    /// <summary>Normalized 0–100 percent targets for the three monitor features.</summary>
    public readonly record struct ComfortTarget(
        int ContrastPercent, int BrightnessPercent, int SharpnessPercent);

    /// <summary>EMA-smoothed lux after the most recent <see cref="Update"/>.</summary>
    public double SmoothedLux => _smoothedLux;

    /// <summary>Feed one raw lux reading; returns the smoothed comfort target.</summary>
    public ComfortTarget Update(double rawLux)
    {
        double lux = double.IsNaN(rawLux) || double.IsInfinity(rawLux) ? 0.0 : rawLux;

        if (!_seeded)
        {
            _smoothedLux = lux;
            _seeded = true;
        }
        else
        {
            _smoothedLux = (SenseDefaults.LuxEmaAlpha * _smoothedLux)
                         + ((1.0 - SenseDefaults.LuxEmaAlpha) * lux);
        }

        double clamped = Math.Clamp(_smoothedLux, 1.0, 10000.0);
        double l = Math.Log10(clamped); // [0, 4]
        double t = Math.Clamp(
            (l - SenseDefaults.LuxLogMin) / (SenseDefaults.LuxLogMax - SenseDefaults.LuxLogMin),
            0.0, 1.0);

        return new ComfortTarget(
            Lerp(SenseDefaults.ContrastMinPercent,   SenseDefaults.ContrastMaxPercent,   t),
            Lerp(SenseDefaults.BrightnessMinPercent, SenseDefaults.BrightnessMaxPercent, t),
            Lerp(SenseDefaults.SharpnessMinPercent,  SenseDefaults.SharpnessMaxPercent,  t));
    }

    /// <summary>Clear the EMA state.</summary>
    public void Reset()
    {
        _smoothedLux = 0.0;
        _seeded = false;
    }

    private static int Lerp(int min, int max, double t)
        => (int)Math.Round(min + ((max - min) * t));
}
