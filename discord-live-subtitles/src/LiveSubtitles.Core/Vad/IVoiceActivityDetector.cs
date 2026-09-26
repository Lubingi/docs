namespace LiveSubtitles.Core.Vad;

/// <summary>Frame-level speech detector running on 16 kHz mono audio in fixed 512-sample (32 ms) frames.</summary>
public interface IVoiceActivityDetector : IDisposable
{
    public const int SampleRate = 16000;
    public const int FrameSize = 512;

    string Name { get; }

    /// <summary>Returns the probability (0..1) that the frame contains speech.</summary>
    float Process(ReadOnlySpan<float> frame);

    void Reset();
}
