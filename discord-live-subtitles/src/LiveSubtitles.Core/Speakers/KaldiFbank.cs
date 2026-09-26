namespace LiveSubtitles.Core.Speakers;

/// <summary>
/// Kaldi-compatible log-mel filterbank (same defaults as torchaudio.compliance.kaldi.fbank / kaldi-native-fbank):
/// 16 kHz, 25 ms Povey window, 10 ms shift, pre-emphasis 0.97, DC removal, 512-point FFT, 80 mel bins 20 Hz–8 kHz,
/// snip_edges, no dither. This is the input the 3D-Speaker CAM++ model was trained on.
/// </summary>
public sealed class KaldiFbank
{
    public const int SampleRate = 16000;
    private const int FrameLength = 400;
    private const int FrameShift = 160;
    private const int FftSize = 512;
    private const float PreEmphasis = 0.97f;

    private readonly int _numBins;
    private readonly float[] _window = new float[FrameLength];
    private readonly float[][] _melWeights;   // [bin][fftBin]
    private readonly int[] _melStart;          // first non-zero fft bin per mel bin
    private readonly double[] _cos, _sin;
    private readonly int[] _bitReverse;

    public KaldiFbank(int numBins = 80, float lowFreq = 20, float highFreq = 0)
    {
        _numBins = numBins;
        for (int i = 0; i < FrameLength; i++)
            _window[i] = (float)Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameLength - 1)), 0.85);

        float nyquist = SampleRate / 2f;
        if (highFreq <= 0) highFreq += nyquist;
        int numFftBins = FftSize / 2;
        float fftBinWidth = (float)SampleRate / FftSize;
        double melLow = Mel(lowFreq), melHigh = Mel(highFreq);
        double melDelta = (melHigh - melLow) / (numBins + 1);
        _melWeights = new float[numBins][];
        _melStart = new int[numBins];
        for (int b = 0; b < numBins; b++)
        {
            double left = melLow + b * melDelta, center = left + melDelta, right = center + melDelta;
            var w = new float[numFftBins];
            int first = -1, last = -1;
            for (int i = 0; i < numFftBins; i++)
            {
                double mel = Mel(fftBinWidth * i);
                if (mel > left && mel < right)
                {
                    w[i] = (float)(mel <= center ? (mel - left) / (center - left) : (right - mel) / (right - center));
                    if (first < 0) first = i;
                    last = i;
                }
            }
            _melStart[b] = Math.Max(0, first);
            _melWeights[b] = first < 0 ? Array.Empty<float>() : w[first..(last + 1)];
        }

        _cos = new double[FftSize / 2];
        _sin = new double[FftSize / 2];
        for (int i = 0; i < FftSize / 2; i++)
        {
            _cos[i] = Math.Cos(-2 * Math.PI * i / FftSize);
            _sin[i] = Math.Sin(-2 * Math.PI * i / FftSize);
        }
        _bitReverse = new int[FftSize];
        int bits = (int)Math.Log2(FftSize);
        for (int i = 0; i < FftSize; i++)
        {
            int r = 0;
            for (int k = 0; k < bits; k++) if ((i & (1 << k)) != 0) r |= 1 << (bits - 1 - k);
            _bitReverse[i] = r;
        }
    }

    private static double Mel(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);

    public static int FrameCount(int samples) => samples < FrameLength ? 0 : 1 + (samples - FrameLength) / FrameShift;

    /// <summary>Computes [frames, numBins] log-mel features for 16 kHz samples in [-1, 1].</summary>
    public float[,] Compute(ReadOnlySpan<float> samples)
    {
        int frames = FrameCount(samples.Length);
        var output = new float[frames, _numBins];
        var frame = new double[FrameLength];
        var re = new double[FftSize];
        var im = new double[FftSize];
        var power = new double[FftSize / 2 + 1];
        for (int f = 0; f < frames; f++)
        {
            int offset = f * FrameShift;
            double mean = 0;
            for (int i = 0; i < FrameLength; i++) { frame[i] = samples[offset + i]; mean += frame[i]; }
            mean /= FrameLength;
            for (int i = 0; i < FrameLength; i++) frame[i] -= mean;
            for (int i = FrameLength - 1; i > 0; i--) frame[i] -= PreEmphasis * frame[i - 1];
            frame[0] -= PreEmphasis * frame[0];

            Array.Clear(im);
            for (int i = 0; i < FftSize; i++) re[_bitReverse[i]] = i < FrameLength ? frame[i] * _window[i] : 0;
            Fft(re, im);
            for (int i = 0; i <= FftSize / 2; i++) power[i] = re[i] * re[i] + im[i] * im[i];

            for (int b = 0; b < _numBins; b++)
            {
                var w = _melWeights[b];
                int s = _melStart[b];
                double e = 0;
                for (int k = 0; k < w.Length; k++) e += w[k] * power[s + k];
                output[f, b] = (float)Math.Log(Math.Max(e, 1.19209290e-07));
            }
        }
        return output;
    }

    /// <summary>In-place iterative radix-2 FFT; input must already be in bit-reversed order.</summary>
    private void Fft(double[] re, double[] im)
    {
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size / 2, step = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * step], wi = _sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
    }
}
