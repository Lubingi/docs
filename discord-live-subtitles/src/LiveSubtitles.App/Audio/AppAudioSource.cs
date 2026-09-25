using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;

namespace LiveSubtitles.App.Audio;

/// <summary>
/// Captures one application (Discord by default, or any app picked in test mode) and keeps following it:
/// waits for the app to start, re-attaches when it restarts, and optionally falls back to whole-device loopback
/// if per-process capture is unavailable.
/// </summary>
public sealed class AppAudioSource : IAudioSource
{
    private readonly string[] _processNames;
    private readonly string _label;
    private readonly bool _fallbackToDevice;
    private readonly string? _fallbackDeviceId;
    private readonly ILog _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private IAudioSource? _inner;
    private int _attachedPid;
    private bool _usingFallback;
    private int _failures;
    private string _status = "";

    public AppAudioSource(string label, string[] processNames, bool fallbackToDevice, string? fallbackDeviceId, ILog log)
    {
        _label = label;
        _processNames = processNames;
        _fallbackToDevice = fallbackToDevice;
        _fallbackDeviceId = fallbackDeviceId;
        _log = log;
    }

    public string DisplayName => _label;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception?>? Stopped;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => WatchAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_usingFallback) { await Task.Delay(2000, ct); continue; }

                bool attachedAlive = _attachedPid != 0 && ProcessTree.IsAlive(_attachedPid);
                if (_attachedPid != 0 && !attachedAlive)
                {
                    _log.Info($"{_label} (PID {_attachedPid}) exited; waiting for it to restart");
                    Detach();
                }
                if (_attachedPid == 0)
                {
                    var root = ProcessTree.FindRoot(_processNames);
                    if (root is { } r) await AttachAsync(r.Pid, r.Name, ct);
                    else SetStatus($"Waiting for {_label} to start…");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error($"Could not capture {_label}", ex);
                Detach();
                _failures++;
                if (_fallbackToDevice && (_failures >= 3 || !ProcessLoopbackSource.IsSupported))
                {
                    try { await StartFallbackAsync(ct, ex.Message); }
                    catch (Exception fx) { _log.Error("Device loopback fallback failed", fx); SetStatus($"Capture failed: {fx.Message}"); }
                }
                else SetStatus($"Capture failed ({ex.Message}); retrying…");
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task AttachAsync(int pid, string name, CancellationToken ct)
    {
        if (!ProcessLoopbackSource.IsSupported)
        {
            if (_fallbackToDevice)
            {
                await StartFallbackAsync(ct, "per-app capture needs Windows 10 2004 or newer");
                return;
            }
            throw new PlatformNotSupportedException("Per-app capture needs Windows 10 version 2004 or newer.");
        }
        var source = new ProcessLoopbackSource(pid, name, _log);
        source.SamplesAvailable += (s, e) => SamplesAvailable?.Invoke(this, e);
        source.Stopped += (s, ex) =>
        {
            // Capture can stop if the process exits; the watcher re-attaches.
            if (ex != null) _log.Warn($"{name} capture stopped: {ex.Message}");
            lock (_lock) if (_inner == source) _attachedPid = -1;
        };
        await source.StartAsync(ct);
        lock (_lock)
        {
            _inner = source;
            _attachedPid = pid;
        }
        _failures = 0;
        SetStatus($"Capturing {_label} ({name}, PID {pid}) — only this app's audio");
    }

    private async Task StartFallbackAsync(CancellationToken ct, string reason)
    {
        var source = new DeviceLoopbackSource(_fallbackDeviceId, _log);
        source.SamplesAvailable += (s, e) => SamplesAvailable?.Invoke(this, e);
        await source.StartAsync(ct);
        lock (_lock) { _inner = source; _usingFallback = true; }
        SetStatus($"Per-app capture unavailable ({reason}). Capturing the whole output device '{source.DisplayName}' — other apps will be included.");
    }

    private void Detach()
    {
        IAudioSource? old;
        lock (_lock)
        {
            old = _inner;
            _inner = null;
            _attachedPid = 0;
        }
        old?.Dispose();
    }

    private void SetStatus(string status)
    {
        if (status == _status) return;
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Stop()
    {
        _cts?.Cancel();
        Detach();
        _usingFallback = false;
        Stopped?.Invoke(this, null);
    }

    public void Dispose() => Stop();
}
