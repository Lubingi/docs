using NAudio.Wave;

namespace LiveSubtitles.App.Audio;

internal static class WaveConvert
{
    /// <summary>Converts a captured WASAPI buffer (PCM 16/24/32-bit or IEEE float, any channel count) to mono floats.</summary>
    public static float[] ToMonoFloat(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = buffer.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || (format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(buffer.Slice(o, 4)),
                    4 => BitConverter.ToInt32(buffer.Slice(o, 4)) / 2147483648f,
                    3 => (buffer[o] | (buffer[o + 1] << 8) | ((sbyte)buffer[o + 2] << 16)) / 8388608f,
                    2 => BitConverter.ToInt16(buffer.Slice(o, 2)) / 32768f,
                    _ => 0,
                };
            }
            mono[f] = sum / channels;
        }
        return mono;
    }
}
