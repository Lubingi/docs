using System.Text.Json;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Speakers;

namespace LiveSubtitles.Core.Tests;

/// <summary>Checks the C# feature extraction + ONNX embedding against the Python reference implementation
/// (kaldi-native-fbank + onnxruntime) on real speech.</summary>
[Collection("SpeakerModel")]
public class SpeakerModelTests
{
    private sealed record Reference(int StartSample, int EndSample, float[][] Fbank, float[] Embedding);

    private static Reference? Load()
    {
        var wav = TestPaths.Audio("two-speakers-en.wav");
        var json = Path.Combine(AppContext.BaseDirectory, "TestData", "fbank_reference.json");
        if (!File.Exists(wav) || !File.Exists(json)) return null;
        return JsonSerializer.Deserialize<Reference>(File.ReadAllText(json), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    [Fact]
    public void FbankMatchesKaldiNativeFbank()
    {
        var r = Load();
        if (r == null) return;
        var (samples, _) = WavFile.ReadMono(TestPaths.Audio("two-speakers-en.wav"));
        var feats = new KaldiFbank().Compute(samples.AsSpan(r.StartSample, r.EndSample - r.StartSample));
        Assert.Equal(r.Fbank.Length, feats.GetLength(0));
        double maxErr = 0;
        for (int t = 0; t < r.Fbank.Length; t++)
        for (int b = 0; b < 80; b++)
            maxErr = Math.Max(maxErr, Math.Abs(feats[t, b] - r.Fbank[t][b]));
        Assert.True(maxErr < 0.01, $"max abs difference {maxErr}");
    }

    [Fact]
    public void EmbeddingMatchesPythonReference()
    {
        var r = Load();
        var model = TestPaths.Model("campplus_voxceleb_16k.onnx");
        if (r == null || !File.Exists(model)) return;
        var (samples, _) = WavFile.ReadMono(TestPaths.Audio("two-speakers-en.wav"));
        using var embedder = new SpeakerEmbedder(model);
        var e = embedder.Embed(samples.AsSpan(r.StartSample, r.EndSample - r.StartSample))!;
        Assert.True(SpeakerEmbedder.Cosine(e, r.Embedding) > 0.999f);
    }
}
