namespace LiveSubtitles.Core.Audio;

/// <summary>
/// Streaming band-limited resampler (windowed sinc, Blackman window) for mono float audio.
/// Works for any rate pair; the kernel is precomputed into a table and linearly interpolated.
/// Not thread-safe: feed it from a single thread.
/// </summary>
public sealed class StreamResampler
{
    private const int Phases = 512;

    private readonly double _step;          // input samples advanced per output sample
    private readonly int _halfWidth;        // kernel half width in input samples
    private readonly float[] _table;        // kernel sampled at 1/Phases input-sample resolution
    private float[] _buffer = new float[8192];
    private int _count;                     // valid samples in _buffer
    private double _position;               // position of the next output sample, relative to _buffer[0]

    public StreamResampler(int inputRate, int outputRate, int quality = 16)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.94;
        _halfWidth = (int)Math.Ceiling(quality / cutoff);
        _table = new float[2 * _halfWidth * Phases + 2];
        for (int i = 0; i < _table.Length; i++)
        {
            double x = (double)i / Phases - _halfWidth;
            _table[i] = (float)(cutoff * Sinc(cutoff * x) * Blackman(x / _halfWidth));
        }
        // Start with half a kernel of silence so the first output sample is centred on input sample 0.
        _count = _halfWidth;
        _position = _halfWidth;
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    public bool IsPassthrough => InputRate == OutputRate;

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (IsPassthrough) return input.ToArray();
        EnsureCapacity(_count + input.Length);
        input.CopyTo(_buffer.AsSpan(_count));
        _count += input.Length;

        int estimate = (int)((_count - _position) / _step) + 2;
        var output = new float[Math.Max(0, estimate)];
        int produced = 0;
        while (_position + _halfWidth < _count && produced < output.Length)
        {
            output[produced++] = Interpolate(_position);
            _position += _step;
        }

        // Drop input that no future output sample can reach.
        int discard = (int)Math.Floor(_position) - _halfWidth;
        if (discard > 0)
        {
            Array.Copy(_buffer, discard, _buffer, 0, _count - discard);
            _count -= discard;
            _position -= discard;
        }
        if (produced != output.Length) Array.Resize(ref output, produced);
        return output;
    }

    private float Interpolate(double center)
    {
        int first = (int)Math.Floor(center) - _halfWidth + 1;
        float sum = 0;
        for (int k = 0; k < 2 * _halfWidth; k++)
        {
            int idx = first + k;
            if (idx < 0 || idx >= _count) continue;
            // distance from the centre, shifted into table coordinates
            double x = (idx - center + _halfWidth) * Phases;
            int t = (int)x;
            if (t < 0 || t >= _table.Length - 1) continue;
            float w = (float)(x - t);
            float h = _table[t] + (_table[t + 1] - _table[t]) * w;
            sum += _buffer[idx] * h;
        }
        return sum;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Blackman(double t)
    {
        if (t < -1 || t > 1) return 0;
        double n = (t + 1) / 2; // 0..1
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * n) + 0.08 * Math.Cos(4 * Math.PI * n);
    }
}
