using LiveSubtitles.Core.Audio;

namespace LiveSubtitles.Core.Tests;

public class ResamplerTests
{
    private static float[] Sine(double freq, int rate, int n, double amp = 0.5) =>
        Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray();

    private static double Rms(ReadOnlySpan<float> x) { double s = 0; foreach (var v in x) s += v * v; return Math.Sqrt(s / x.Length); }

    [Theory]
    [InlineData(48000, 24000)]
    [InlineData(48000, 16000)]
    [InlineData(44100, 24000)]
    [InlineData(44100, 16000)]
    [InlineData(16000, 24000)]
    public void PreservesInBandToneAndLength(int inRate, int outRate)
    {
        var rs = new StreamResampler(inRate, outRate);
        var input = Sine(440, inRate, inRate * 2);
        var output = new List<float>();
        // feed in odd-sized chunks to exercise the streaming state
        for (int i = 0; i < input.Length; i += 997)
            output.AddRange(rs.Process(input.AsSpan(i, Math.Min(997, input.Length - i))));

        double expectedLen = input.Length * (double)outRate / inRate;
        Assert.InRange(output.Count, expectedLen - 100, expectedLen + 2);
        var steady = output.Skip(500).Take(outRate).ToArray();
        Assert.InRange(Rms(steady), 0.5 / Math.Sqrt(2) * 0.97, 0.5 / Math.Sqrt(2) * 1.03);

        // Correlate with an ideal 440 Hz sine at the output rate (phase-free via I/Q projection).
        double i0 = 0, q0 = 0;
        for (int n = 0; n < steady.Length; n++)
        {
            i0 += steady[n] * Math.Sin(2 * Math.PI * 440 * n / outRate);
            q0 += steady[n] * Math.Cos(2 * Math.PI * 440 * n / outRate);
        }
        double amp = 2 * Math.Sqrt(i0 * i0 + q0 * q0) / steady.Length;
        Assert.InRange(amp, 0.48, 0.52);
    }

    [Fact]
    public void RemovesContentAboveNewNyquist()
    {
        var rs = new StreamResampler(48000, 16000);
        var output = rs.Process(Sine(12000, 48000, 48000)); // 12 kHz cannot exist at 16 kHz
        Assert.True(Rms(output.AsSpan(500, 10000)) < 0.01, "aliasing leaked through");
    }
}
