namespace LiveSubtitles.Core.Vad;

/// <summary>
/// Fallback detector used only if the Silero model is missing: adaptive noise floor + energy ratio.
/// Much less accurate than Silero (music and noise count as speech) but keeps the app usable.
/// </summary>
public sealed class EnergyVad : IVoiceActivityDetector
{
    private double _noiseFloor = 1e-4;

    public string Name => "Energy VAD (fallback)";

    public float Process(ReadOnlySpan<float> frame)
    {
        double energy = 0;
        foreach (var s in frame) energy += s * s;
        energy /= frame.Length;
        // Track the floor quickly downwards and slowly upwards.
        _noiseFloor = energy < _noiseFloor ? 0.9 * _noiseFloor + 0.1 * energy : 0.999 * _noiseFloor + 0.001 * energy;
        _noiseFloor = Math.Max(_noiseFloor, 1e-7);
        double snrDb = 10 * Math.Log10(energy / _noiseFloor + 1e-12);
        if (energy < 1e-6) return 0;
        return (float)Math.Clamp((snrDb - 6) / 12, 0, 1);
    }

    public void Reset() => _noiseFloor = 1e-4;

    public void Dispose() { }
}
