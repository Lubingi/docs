using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiveSubtitles.Core.Speakers;

/// <summary>
/// Local speaker-embedding model: 3D-Speaker CAM++ trained on VoxCeleb (ONNX, Apache-2.0), run with ONNX Runtime on the CPU.
/// CAM++ is a successor to ECAPA-TDNN from the same research line with better VoxCeleb accuracy and a smaller, faster network.
/// Produces a 512-d L2-normalised voice embedding. No audio or embeddings leave the PC.
/// </summary>
public sealed class SpeakerEmbedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly KaldiFbank _fbank = new();
    private readonly string _inputName;
    private readonly object _lock = new();

    public SpeakerEmbedder(string modelPath)
    {
        var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public const int MinSamples = KaldiFbank.SampleRate / 4;

    /// <summary>Embedding for 16 kHz mono audio, or null if the clip is too short.</summary>
    public float[]? Embed(ReadOnlySpan<float> audio16k)
    {
        if (audio16k.Length < MinSamples) return null;
        // Very long segments add little; cap at 20 s to bound CPU time.
        if (audio16k.Length > 20 * KaldiFbank.SampleRate) audio16k = audio16k[..(20 * KaldiFbank.SampleRate)];
        var feats = _fbank.Compute(audio16k);
        int frames = feats.GetLength(0), bins = feats.GetLength(1);
        var input = new DenseTensor<float>(new[] { 1, frames, bins });
        // Per-utterance mean normalisation ("global-mean" in the model metadata).
        for (int b = 0; b < bins; b++)
        {
            double mean = 0;
            for (int t = 0; t < frames; t++) mean += feats[t, b];
            mean /= frames;
            for (int t = 0; t < frames; t++) input[0, t, b] = (float)(feats[t, b] - mean);
        }
        float[] embedding;
        lock (_lock)
        {
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            embedding = results.First().AsEnumerable<float>().ToArray();
        }
        return Normalize(embedding);
    }

    public static float[] Normalize(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += x * x;
        n = Math.Sqrt(n);
        if (n < 1e-9) return v;
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (float)(v[i] / n);
        return r;
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return (float)dot;
    }

    public void Dispose() => _session.Dispose();
}
