using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LiveSubtitles.Core.Vad;

/// <summary>
/// Silero VAD v6 (MIT licence) running locally through ONNX Runtime.
/// Each call takes a 512-sample frame at 16 kHz, prefixed with the last 64 samples of the previous frame.
/// </summary>
public sealed class SileroVad : IVoiceActivityDetector
{
    private const int ContextSize = 64;
    private readonly InferenceSession _session;
    private readonly float[] _input = new float[ContextSize + IVoiceActivityDetector.FrameSize];
    private float[] _state = new float[2 * 1 * 128];
    private readonly DenseTensor<long> _sr = new(new long[] { IVoiceActivityDetector.SampleRate }, Array.Empty<int>());

    public SileroVad(string modelPath)
    {
        var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, options);
    }

    public string Name => "Silero VAD";

    public float Process(ReadOnlySpan<float> frame)
    {
        if (frame.Length != IVoiceActivityDetector.FrameSize)
            throw new ArgumentException($"Frame must be {IVoiceActivityDetector.FrameSize} samples", nameof(frame));
        frame.CopyTo(_input.AsSpan(ContextSize));

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>((float[])_input.Clone(), new[] { 1, _input.Length })),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
            NamedOnnxValue.CreateFromTensor("sr", _sr),
        };
        using var results = _session.Run(inputs);
        float prob = 0;
        foreach (var r in results)
        {
            if (r.Name == "output") prob = r.AsEnumerable<float>().First();
            else if (r.Name == "stateN") _state = r.AsEnumerable<float>().ToArray();
        }
        // Keep the tail of this frame as context for the next one.
        Array.Copy(_input, _input.Length - ContextSize, _input, 0, ContextSize);
        return prob;
    }

    public void Reset()
    {
        _state = new float[2 * 1 * 128];
        Array.Clear(_input);
    }

    public void Dispose() => _session.Dispose();
}
