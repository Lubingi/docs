namespace LiveSubtitles.Core.Audio;

/// <summary>Mono float audio delivered by a capture source.</summary>
public sealed class AudioSamplesEventArgs(float[] samples, int sampleRate) : EventArgs
{
    public float[] Samples { get; } = samples;
    public int SampleRate { get; } = sampleRate;
}

/// <summary>
/// Anything that produces audio for the subtitle pipeline: Discord process capture, any-app capture,
/// output-device loopback, or a test audio file. Samples are mono floats in [-1, 1].
/// </summary>
public interface IAudioSource : IDisposable
{
    string DisplayName { get; }

    /// <summary>Raised on a background thread whenever new audio is available.</summary>
    event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    /// <summary>Human-readable status changes ("Attached to Discord (PID 1234)", "Waiting for Discord...").</summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>Raised when the source stops by itself (error, file ended). The exception is null for a normal end.</summary>
    event EventHandler<Exception?>? Stopped;

    Task StartAsync(CancellationToken cancellationToken);

    void Stop();
}
