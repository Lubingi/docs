namespace LiveSubtitles.Core.Audio;

public static class Pcm16
{
    /// <summary>Converts mono float samples to 16-bit little-endian PCM bytes.</summary>
    public static byte[] FromFloat(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = Math.Clamp(samples[i], -1f, 1f);
            short v = (short)Math.Round(s < 0 ? s * 32768f : s * 32767f);
            bytes[2 * i] = (byte)(v & 0xFF);
            bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    public static float[] ToFloat(ReadOnlySpan<byte> bytes)
    {
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            samples[i] = v / 32768f;
        }
        return samples;
    }

    /// <summary>Interleaved multi-channel floats to mono by averaging channels.</summary>
    public static float[] DownmixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1) return interleaved.ToArray();
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }
}
