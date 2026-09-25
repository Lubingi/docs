using System.Diagnostics;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using NAudio.Wave;

namespace LiveSubtitles.App.Audio;

/// <summary>
/// Test mode: plays a local .mp3/.wav/.m4a/.aac/.wma file in real time through the full pipeline, as if it were a
/// live call, optionally also through the speakers. Supports play/pause/seek. The file is only read, never modified.
/// </summary>
public sealed class AudioFileSource : IAudioSource
{
    private const int ChunkMs = 20;
    private readonly string _path;
    private readonly bool _playToSpeakers;
    private readonly ILog _log;
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _playing = true;
    private long _seekTicks = -1;
    private long _positionTicks;

    public AudioFileSource(string path, bool playToSpeakers, IAudioEffect? effect, ILog log)
    {
        _path = path;
        _playToSpeakers = playToSpeakers;
        Effect = effect;
        _log = log;
        DisplayName = Path.GetFileName(path);
    }

    public string DisplayName { get; }
    public IAudioEffect? Effect { get; set; }
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));
    public bool IsPlaying => _playing;
    public bool Ended { get; private set; }

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception?>? Stopped;
    public event EventHandler? PlaybackStateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "AudioFilePlayer" };
        _thread.SetApartmentState(ApartmentState.MTA); // Media Foundation prefers MTA
        _thread.Start();
        return ready.Task;
    }

    public void Play()
    {
        if (Ended) Seek(TimeSpan.Zero);
        _playing = true;
        _wake.Set();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        _playing = false;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position)
    {
        Interlocked.Exchange(ref _seekTicks, Math.Max(0, position.Ticks));
        _wake.Set();
    }

    private void Run(TaskCompletionSource ready)
    {
        MediaFoundationReader? reader = null;
        WaveOut? output = null;
        BufferedWaveProvider? buffered = null;
        try
        {
            reader = new MediaFoundationReader(_path);
            Duration = reader.TotalTime;
            var samples = reader.ToSampleProvider();
            int rate = samples.WaveFormat.SampleRate;
            int channels = samples.WaveFormat.Channels;
            if (_playToSpeakers)
            {
                buffered = new BufferedWaveProvider(new WaveFormat(rate, 16, 1), TimeSpan.FromSeconds(2)) { DiscardOnBufferOverflow = true };
                output = new WaveOut { BufferMilliseconds = 60, NumberOfBuffers = 2 };
                output.Init(buffered);
                output.Play();
            }
            _log.Info($"Playing test file {DisplayName} ({rate} Hz, {channels} ch, {Duration:mm\\:ss})");
            StatusChanged?.Invoke(this, $"Playing {DisplayName}");
            ready.TrySetResult();

            int chunkFrames = rate * ChunkMs / 1000;
            var interleaved = new float[chunkFrames * channels];
            var clock = Stopwatch.StartNew();
            long framesDelivered = 0;

            while (!_stop)
            {
                long seek = Interlocked.Exchange(ref _seekTicks, -1);
                if (seek >= 0)
                {
                    reader.CurrentTime = TimeSpan.FromTicks(Math.Min(seek, reader.TotalTime.Ticks));
                    buffered?.ClearBuffer();
                    Ended = false;
                    clock.Restart();
                    framesDelivered = 0;
                    Interlocked.Exchange(ref _positionTicks, reader.CurrentTime.Ticks);
                }
                if (!_playing || Ended)
                {
                    _wake.Wait(100);
                    _wake.Reset();
                    clock.Restart();
                    framesDelivered = 0;
                    continue;
                }

                // Real-time pacing: deliver audio only as fast as it would play.
                long due = (long)(clock.Elapsed.TotalSeconds * rate);
                if (framesDelivered >= due)
                {
                    Thread.Sleep(5);
                    continue;
                }

                int read = samples.Read(interleaved.AsSpan());
                if (read <= 0)
                {
                    Ended = true;
                    _playing = false;
                    StatusChanged?.Invoke(this, $"Finished {DisplayName}");
                    PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
                    continue;
                }
                var mono = Pcm16.DownmixToMono(interleaved.AsSpan(0, read), channels);
                Effect?.Process(mono, rate);
                framesDelivered += mono.Length;
                Interlocked.Exchange(ref _positionTicks, reader.CurrentTime.Ticks);
                buffered?.AddSamples(Pcm16.FromFloat(mono));
                SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(mono, rate));
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Could not play {DisplayName}", ex);
            ready.TrySetException(ex);
            Stopped?.Invoke(this, ex);
        }
        finally
        {
            output?.Stop();
            output?.Dispose();
            reader?.Dispose();
        }
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(1000);
    }

    public void Dispose() => Stop();
}
