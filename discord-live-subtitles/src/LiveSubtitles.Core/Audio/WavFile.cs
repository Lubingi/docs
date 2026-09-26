using System.Text;

namespace LiveSubtitles.Core.Audio;

/// <summary>Minimal WAV reader/writer (PCM 16-bit and IEEE float), used by the CLI, tests and the dialogue generator.</summary>
public static class WavFile
{
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var fs = File.Create(path);
        WriteMono16(fs, samples, sampleRate);
    }

    public static void WriteMono16(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        var data = Pcm16.FromFloat(samples);
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);    // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
    }

    /// <summary>Reads a PCM16 / PCM24 / PCM32 / float32 WAV file and returns mono samples.</summary>
    public static (float[] Samples, int SampleRate) ReadMono(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file");
        int format = 0, channels = 0, rate = 0, bits = 0;
        while (fs.Position + 8 <= fs.Length)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                if (size > 16) r.ReadBytes(size - 16);
                if (format == unchecked((short)0xFFFE)) format = bits == 32 && size >= 40 ? 3 : 1; // extensible: guess from bits
            }
            else if (id == "data")
            {
                var bytes = r.ReadBytes(Math.Min(size, (int)(fs.Length - fs.Position)));
                var interleaved = Decode(bytes, format, bits);
                return (Pcm16.DownmixToMono(interleaved, channels), rate);
            }
            else
            {
                fs.Position += size + (size & 1);
            }
        }
        throw new InvalidDataException("WAV file has no data chunk");
    }

    private static float[] Decode(byte[] bytes, int format, int bits)
    {
        if (format == 3 && bits == 32)
        {
            var f = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4);
            return f;
        }
        switch (bits)
        {
            case 16: return Pcm16.ToFloat(bytes);
            case 24:
            {
                var f = new float[bytes.Length / 3];
                for (int i = 0; i < f.Length; i++)
                {
                    int v = bytes[3 * i] | (bytes[3 * i + 1] << 8) | ((sbyte)bytes[3 * i + 2] << 16);
                    f[i] = v / 8388608f;
                }
                return f;
            }
            case 32:
            {
                var f = new float[bytes.Length / 4];
                for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt32(bytes, 4 * i) / 2147483648f;
                return f;
            }
            default: throw new NotSupportedException($"Unsupported WAV bit depth {bits}");
        }
    }
}
