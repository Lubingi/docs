namespace LiveSubtitles.Core.Pipeline;

/// <summary>
/// Running cost estimate for the current session. OpenAI bills gpt-realtime-translate (and gpt-realtime-whisper, used for the
/// original-language text) per minute of audio, so the estimate is audio minutes sent × the per-minute prices in Settings.
/// It is an estimate: check platform.openai.com/usage for the real figure.
/// </summary>
public sealed class UsageTracker
{
    private readonly object _lock = new();
    private double _ms;
    private bool _capRaised;

    public UsageTracker(double translateUsdPerMinute, double transcribeUsdPerMinute, bool transcribing, double? capUsd)
    {
        TranslateUsdPerMinute = translateUsdPerMinute;
        TranscribeUsdPerMinute = transcribing ? transcribeUsdPerMinute : 0;
        CapUsd = capUsd is > 0 ? capUsd : null;
    }

    public double TranslateUsdPerMinute { get; }
    public double TranscribeUsdPerMinute { get; }
    public double? CapUsd { get; set; }

    public double Minutes { get { lock (_lock) return _ms / 60000.0; } }
    public double CostUsd => Minutes * (TranslateUsdPerMinute + TranscribeUsdPerMinute);
    public bool IsOverCap => CapUsd is { } cap && CostUsd >= cap;

    /// <summary>Raised once when the spending cap is reached.</summary>
    public event Action? CapReached;

    public void AddAudio(double milliseconds)
    {
        bool raise;
        lock (_lock)
        {
            _ms += milliseconds;
            raise = !_capRaised && IsOverCapLocked();
            if (raise) _capRaised = true;
        }
        if (raise) CapReached?.Invoke();
    }

    /// <summary>After the user raises the cap, allow the event to fire again at the new limit.</summary>
    public void ResetCapNotification()
    {
        lock (_lock) _capRaised = IsOverCapLocked();
    }

    private bool IsOverCapLocked() => CapUsd is { } cap && _ms / 60000.0 * (TranslateUsdPerMinute + TranscribeUsdPerMinute) >= cap;
}
