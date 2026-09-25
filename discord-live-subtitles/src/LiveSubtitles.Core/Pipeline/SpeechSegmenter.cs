using LiveSubtitles.Core.Vad;

namespace LiveSubtitles.Core.Pipeline;

public sealed record SegmenterOptions
{
    public const double FrameMs = 32.0; // 512 samples @ 16 kHz == 768 samples @ 24 kHz

    /// <summary>Speech probability needed to start speech.</summary>
    public float Threshold { get; init; } = 0.5f;
    /// <summary>Probability below which an ongoing segment counts as silence (hysteresis).</summary>
    public float NegativeThreshold { get; init; } = 0.35f;
    /// <summary>Speech must last this long before a segment starts (filters clicks/coughs).</summary>
    public int MinSpeechMs { get; init; } = 192;
    /// <summary>A pause this long ends a segment (speaker turns are split on pauses).</summary>
    public int MinSilenceMs { get; init; } = 480;
    /// <summary>Audio kept from before the detected onset so the first syllable is not cut.</summary>
    public int PreRollMs { get; init; } = 320;
    /// <summary>After speech stops, keep sending real audio this long so the model can finish the sentence.</summary>
    public int TailMs { get; init; } = 1500;
    /// <summary>Long monologues are split into pieces of at most this length.</summary>
    public int MaxSegmentMs { get; init; } = 15000;
    /// <summary>When muted speakers exist, identify the speaker after this much speech before sending audio.</summary>
    public int EarlyIdMs { get; init; } = 1200;
    /// <summary>Send all audio, including long silences (best quality, higher cost).</summary>
    public bool SendContinuously { get; init; }
}

public abstract record SegmenterEvent;
/// <summary>A 24 kHz frame that should go to the translation API, in order.</summary>
public sealed record SendFrame(float[] Audio24, int? SegmentId, bool IsSpeech, double StreamMs) : SegmenterEvent;
public sealed record SegmentStarted(int SegmentId, double StreamStartMs) : SegmenterEvent;
/// <summary>The first <see cref="SegmenterOptions.EarlyIdMs"/> of a segment, for early speaker identification.</summary>
public sealed record SegmentEarly(int SegmentId, float[] Audio16) : SegmenterEvent;
public sealed record SegmentEnded(int SegmentId, double StreamStartMs, double StreamEndMs, float[] Audio16, bool Split) : SegmenterEvent;
/// <summary>The sending gate closed (long silence); the sender should pad and flush.</summary>
public sealed record GateClosed : SegmenterEvent;

/// <summary>
/// Turns per-frame VAD probabilities into speech segments and decides which audio is sent to OpenAI.
/// Audio is sent from shortly before speech starts until <see cref="SegmenterOptions.TailMs"/> after it stops;
/// longer silences are not sent (saves cost). Segments are split on pauses of <see cref="SegmenterOptions.MinSilenceMs"/>.
/// </summary>
public sealed class SpeechSegmenter
{
    private readonly SegmenterOptions _o;
    private readonly int _minSpeechFrames, _minSilenceFrames, _preRollFrames, _tailFrames, _maxSegmentFrames, _earlyFrames, _endPadFrames;
    private readonly LinkedList<Frame> _lookback = new();
    private long _frameIndex;
    private int _nextSegmentId = 1;
    private bool _sending;
    private int _tailEmitted;
    private int _onsetFrames;

    private int? _segId;
    private long _segStartFrame;
    private long _lastSpeechFrame;
    private int _silenceRun;
    private readonly List<float> _segAudio = new();
    private int _segLastSpeechSample;
    private bool _earlyEmitted;

    public SpeechSegmenter(SegmenterOptions options)
    {
        _o = options;
        _minSpeechFrames = Frames(options.MinSpeechMs);
        _minSilenceFrames = Frames(options.MinSilenceMs);
        _preRollFrames = Frames(options.PreRollMs);
        _tailFrames = Math.Max(Frames(options.TailMs), _minSilenceFrames);
        _maxSegmentFrames = Frames(options.MaxSegmentMs);
        _earlyFrames = Frames(options.EarlyIdMs);
        _endPadFrames = 3;
        _sending = options.SendContinuously;
    }

    public SegmenterOptions Options => _o;
    public bool InSegment => _segId != null;
    public bool Sending => _sending;
    public double StreamMs => _frameIndex * SegmenterOptions.FrameMs;

    private static int Frames(int ms) => Math.Max(1, (int)Math.Round(ms / SegmenterOptions.FrameMs));

    /// <summary>Processes one 32 ms frame (512 samples @16k + matching 768 samples @24k).</summary>
    public void Process(float[] audio16, float[] audio24, float speechProb, List<SegmenterEvent> output)
    {
        var frame = new Frame(audio16, audio24, _frameIndex);
        _frameIndex++;

        if (_segId == null)
        {
            bool speech = speechProb >= _o.Threshold;
            _onsetFrames = speech ? _onsetFrames + 1 : 0;
            _lookback.AddLast(frame);
            if (_onsetFrames >= _minSpeechFrames)
            {
                StartSegment(output, split: false);
                return;
            }
            while (_lookback.Count > _preRollFrames + _minSpeechFrames)
            {
                var old = _lookback.First!.Value;
                _lookback.RemoveFirst();
                EmitGap(old, output);
            }
            return;
        }

        bool inSpeech = speechProb >= _o.NegativeThreshold;
        output.Add(new SendFrame(audio24, _segId, inSpeech, frame.Index * SegmenterOptions.FrameMs));
        _segAudio.AddRange(audio16);
        if (inSpeech)
        {
            _silenceRun = 0;
            _lastSpeechFrame = frame.Index;
            _segLastSpeechSample = _segAudio.Count;
        }
        else _silenceRun++;

        if (!_earlyEmitted && _segAudio.Count >= _earlyFrames * IVoiceActivityDetector.FrameSize)
        {
            _earlyEmitted = true;
            output.Add(new SegmentEarly(_segId.Value, _segAudio.ToArray()));
        }

        if (_silenceRun >= _minSilenceFrames)
        {
            EndSegment(output, split: false);
            _tailEmitted = _silenceRun;
        }
        else if (frame.Index - _segStartFrame + 1 >= _maxSegmentFrames)
        {
            EndSegment(output, split: true);
            StartSegment(output, split: true);
        }
    }

    private void StartSegment(List<SegmenterEvent> output, bool split)
    {
        int id = _nextSegmentId++;
        _segId = id;
        _segAudio.Clear();
        _silenceRun = 0;
        _earlyEmitted = false;
        _onsetFrames = 0;
        _segStartFrame = split ? _frameIndex : _lookback.First?.Value.Index ?? _frameIndex - 1;
        _lastSpeechFrame = _frameIndex - 1;
        output.Add(new SegmentStarted(id, _segStartFrame * SegmenterOptions.FrameMs));
        _sending = true;
        _tailEmitted = 0;
        foreach (var f in _lookback)
        {
            output.Add(new SendFrame(f.Audio24, id, true, f.Index * SegmenterOptions.FrameMs));
            _segAudio.AddRange(f.Audio16);
        }
        _segLastSpeechSample = _segAudio.Count;
        _lookback.Clear();
    }

    private void EndSegment(List<SegmenterEvent> output, bool split)
    {
        if (_segId == null) return;
        int keep = split ? _segAudio.Count : Math.Min(_segAudio.Count, _segLastSpeechSample + _endPadFrames * IVoiceActivityDetector.FrameSize);
        var audio = _segAudio.GetRange(0, keep).ToArray();
        double start = _segStartFrame * SegmenterOptions.FrameMs;
        double end = split ? _frameIndex * SegmenterOptions.FrameMs : (_lastSpeechFrame + 1) * SegmenterOptions.FrameMs;
        if (!_earlyEmitted)
        {
            _earlyEmitted = true;
            output.Add(new SegmentEarly(_segId.Value, audio));
        }
        output.Add(new SegmentEnded(_segId.Value, start, end, audio, split));
        _segId = null;
        _segAudio.Clear();
    }

    private void EmitGap(Frame f, List<SegmenterEvent> output)
    {
        if (!_sending) return;
        output.Add(new SendFrame(f.Audio24, null, false, f.Index * SegmenterOptions.FrameMs));
        if (_o.SendContinuously) return;
        _tailEmitted++;
        if (_tailEmitted >= _tailFrames)
        {
            _sending = false;
            output.Add(new GateClosed());
        }
    }

    /// <summary>Ends any open segment and flushes buffered audio (used on stop/pause).</summary>
    public void Flush(List<SegmenterEvent> output)
    {
        if (_segId != null) EndSegment(output, split: false);
        foreach (var f in _lookback) EmitGap(f, output);
        _lookback.Clear();
        if (_sending && !_o.SendContinuously)
        {
            _sending = false;
            output.Add(new GateClosed());
        }
    }

    private readonly record struct Frame(float[] Audio16, float[] Audio24, long Index);
}
