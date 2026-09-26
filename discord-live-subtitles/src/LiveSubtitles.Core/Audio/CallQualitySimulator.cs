namespace LiveSubtitles.Core.Audio;

/// <summary>Effect applied to test audio before it is played and analysed.</summary>
public interface IAudioEffect
{
    void Process(Span<float> mono, int sampleRate);
}

/// <summary>
/// Makes clean test audio sound more like a real Discord call: voice band-limiting, per-utterance volume differences
/// (different people's mics), light compression (voice processing), a low noise floor, and rare tiny dropouts
/// (packet loss concealment). Deterministic for a given seed.
/// </summary>
public sealed class CallQualitySimulator : IAudioEffect
{
    private readonly Random _rng;
    private int _rate;
    private Biquad _highPass, _lowPass;
    private double _envelope;
    private double _gainDb, _targetGainDb;
    private int _silentSamples;
    private int _dropoutRemaining;
    private double _pink0, _pink1, _pink2;

    public CallQualitySimulator(int seed = 7) => _rng = new Random(seed);

    public double NoiseDbfs { get; set; } = -54;
    public double DropoutProbabilityPerSecond { get; set; } = 0.08;

    public void Process(Span<float> x, int sampleRate)
    {
        if (sampleRate != _rate)
        {
            _rate = sampleRate;
            _highPass = Biquad.HighPass(sampleRate, 110, 0.707);
            _lowPass = Biquad.LowPass(sampleRate, Math.Min(7200, sampleRate * 0.45), 0.707);
        }
        double attack = Math.Exp(-1.0 / (0.005 * sampleRate));
        double release = Math.Exp(-1.0 / (0.080 * sampleRate));
        double noiseAmp = Math.Pow(10, NoiseDbfs / 20);
        double dropChance = DropoutProbabilityPerSecond / sampleRate;

        for (int i = 0; i < x.Length; i++)
        {
            double s = x[i];

            // A new utterance after a pause gets a new "microphone level" (-10 dB .. +4 dB).
            if (Math.Abs(s) < 0.003) _silentSamples++;
            else
            {
                if (_silentSamples > sampleRate * 0.25) _targetGainDb = -10 + _rng.NextDouble() * 14;
                _silentSamples = 0;
            }
            _gainDb += (_targetGainDb - _gainDb) * 0.0005;
            s *= Math.Pow(10, _gainDb / 20);

            s = _lowPass.Process(_highPass.Process(s));

            // Light compressor: threshold -20 dBFS, ratio 3:1, +4 dB make-up.
            double level = Math.Abs(s);
            _envelope = level > _envelope ? attack * _envelope + (1 - attack) * level : release * _envelope + (1 - release) * level;
            double envDb = 20 * Math.Log10(_envelope + 1e-9);
            double reduction = envDb > -20 ? (envDb + 20) * (1 - 1.0 / 3) : 0;
            s *= Math.Pow(10, (4 - reduction) / 20);

            // Rare 20–40 ms dropouts with a short fade (sounds like packet loss).
            if (_dropoutRemaining == 0 && _rng.NextDouble() < dropChance) _dropoutRemaining = (int)(sampleRate * (0.02 + _rng.NextDouble() * 0.02));
            if (_dropoutRemaining > 0)
            {
                _dropoutRemaining--;
                s *= 0.05;
            }

            // Pink-ish noise floor.
            double white = _rng.NextDouble() * 2 - 1;
            _pink0 = 0.99765 * _pink0 + white * 0.0990460;
            _pink1 = 0.96300 * _pink1 + white * 0.2965164;
            _pink2 = 0.57000 * _pink2 + white * 1.0526913;
            s += (_pink0 + _pink1 + _pink2 + white * 0.1848) * 0.25 * noiseAmp;

            x[i] = (float)Math.Tanh(s); // soft clip
        }
    }

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public static Biquad LowPass(double fs, double f0, double q) => Make(fs, f0, q, lowPass: true);
        public static Biquad HighPass(double fs, double f0, double q) => Make(fs, f0, q, lowPass: false);

        private static Biquad Make(double fs, double f0, double q, bool lowPass)
        {
            double w0 = 2 * Math.PI * f0 / fs, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q), a0 = 1 + alpha;
            double b0 = lowPass ? (1 - cos) / 2 : (1 + cos) / 2;
            double b1 = lowPass ? 1 - cos : -(1 + cos);
            return new Biquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b0 / a0, _a1 = -2 * cos / a0, _a2 = (1 - alpha) / a0 };
        }

        public double Process(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
