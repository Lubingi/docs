using System.Net.WebSockets;
using System.Threading.Channels;
using LiveSubtitles.Core.Diagnostics;

namespace LiveSubtitles.Core.Translation;

/// <summary>
/// One Soniox real-time session (speech-to-text + translation over one WebSocket). Produces the same events as
/// <see cref="RealtimeTranslationSession"/> via <see cref="SonioxTokenMapper"/>, so reconnects, replay and text
/// placement are shared with the OpenAI engine.
/// </summary>
public sealed class SonioxTranslationSession : ITranslationSession
{
    /// <summary>Soniox closes idle streams; the official SDKs send a keepalive every 5 s without audio.</summary>
    private static readonly TimeSpan KeepaliveEvery = TimeSpan.FromSeconds(4);
    /// <summary>After audio stops (silence skipping), ask Soniox to finalise the last words instead of waiting.</summary>
    private static readonly TimeSpan FinalizeAfter = TimeSpan.FromMilliseconds(600);

    private readonly SonioxSessionConfig _config;
    private readonly string _apiKey;
    private readonly ILog _log;
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _outgoing =
        Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SonioxTokenMapper _mapper = new();
    private Task? _sendLoop, _receiveLoop, _timerLoop;
    private long _lastSentTicks = DateTime.UtcNow.Ticks;
    private long _lastAudioTicks;
    private int _needsFinalize;
    private int _ended;
    private int _closing;

    public SonioxTranslationSession(SonioxSessionConfig config, string apiKey, ILog log)
    {
        _config = config;
        _apiKey = apiKey.Trim();
        _log = log;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    }

    public event Action<TranslationServerEvent>? EventReceived;
    public event Action<TranslationSessionEnd>? Ended;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        await _ws.ConnectAsync(new Uri(_config.Endpoint), linked.Token).ConfigureAwait(false);
        _log.Info($"Soniox WebSocket connected ({_config.Model}, output={_config.OutputLanguage}" +
                  (_config.LanguageHints.Count > 0 ? $", hints={string.Join(",", _config.LanguageHints)}" : "") + ")");
        // The config (with the key) must be the first message. Soniox reports a bad key as an error message, not an HTTP status.
        await _ws.SendAsync(SonioxProtocol.BuildConfig(_config, _apiKey), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
        Touch();
        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _timerLoop = Task.Run(TimerLoopAsync);
        EventReceived?.Invoke(new TranslationServerEvent { Type = "session.created", ReceivedAt = DateTimeOffset.UtcNow });
    }

    public void SendAudio(byte[] pcm16)
    {
        if (_ended != 0 || _closing != 0) return;
        _outgoing.Writer.TryWrite((pcm16, WebSocketMessageType.Binary));
        Interlocked.Exchange(ref _lastAudioTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _needsFinalize, 1);
    }

    public async Task CloseAsync(TimeSpan timeout)
    {
        if (_ws.State != WebSocketState.Open) return;
        try
        {
            // An empty text frame means "end of audio": Soniox finalises everything, sends "finished" and closes.
            Interlocked.Exchange(ref _closing, 1);
            _outgoing.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Text));
            _outgoing.Writer.TryComplete();
            await Task.WhenAny(_finished.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Soniox close: {ex.Message}");
        }
        finally
        {
            RaiseEnded(new TranslationSessionEnd("Closed by client", null, false));
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastSentTicks, DateTime.UtcNow.Ticks);

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var (data, type) in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(data, type, true, _cts.Token).ConfigureAwait(false);
                Touch();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RaiseEnded(new TranslationSessionEnd("Send failed: " + ex.Message, ex, false));
        }
    }

    private async Task TimerLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _ended == 0)
            {
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
                if (_closing != 0) continue;
                var now = DateTime.UtcNow.Ticks;
                if (_needsFinalize != 0 && now - Interlocked.Read(ref _lastAudioTicks) >= FinalizeAfter.Ticks
                    && Interlocked.Exchange(ref _needsFinalize, 0) == 1)
                {
                    _outgoing.Writer.TryWrite((SonioxProtocol.BuildFinalize(), WebSocketMessageType.Text));
                }
                else if (now - Interlocked.Read(ref _lastSentTicks) >= KeepaliveEvery.Ticks)
                {
                    Touch();
                    _outgoing.Writer.TryWrite((SonioxProtocol.BuildKeepalive(), WebSocketMessageType.Text));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _finished.TrySetResult();
                    var desc = _ws.CloseStatusDescription;
                    RaiseEnded(new TranslationSessionEnd($"Soniox closed the connection ({_ws.CloseStatus}{(string.IsNullOrEmpty(desc) ? "" : ": " + desc)})", null, false));
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                SonioxMessage m;
                var receivedAt = DateTimeOffset.UtcNow;
                try
                {
                    m = SonioxProtocol.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not parse Soniox message", ex);
                    message.SetLength(0);
                    continue;
                }
                message.SetLength(0);

                if (m.ErrorCode is { } code)
                {
                    var text = SonioxProtocol.Describe(code, m.ErrorMessage);
                    _log.Warn($"Soniox error {code}: {m.ErrorMessage}");
                    EventReceived?.Invoke(new TranslationServerEvent
                    {
                        Type = "error", ErrorCode = code.ToString(), ErrorMessage = m.ErrorMessage, ReceivedAt = receivedAt,
                    });
                    _finished.TrySetResult();
                    RaiseEnded(new TranslationSessionEnd(text, null, SonioxProtocol.IsFatal(code)));
                    return;
                }
                foreach (var ev in _mapper.Map(m, receivedAt)) EventReceived?.Invoke(ev);
                if (m.Finished) _finished.TrySetResult();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RaiseEnded(new TranslationSessionEnd("Connection lost: " + ex.Message, ex, false));
        }
        finally
        {
            _finished.TrySetResult();
            RaiseEnded(new TranslationSessionEnd("Connection ended", null, false));
        }
    }

    private void RaiseEnded(TranslationSessionEnd end)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _log.Info($"Soniox session ended: {end.Reason}");
        Ended?.Invoke(end);
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        try
        {
            if (_sendLoop != null) await _sendLoop.ConfigureAwait(false);
            if (_receiveLoop != null) await _receiveLoop.ConfigureAwait(false);
            if (_timerLoop != null) await _timerLoop.ConfigureAwait(false);
        }
        catch { }
        _ws.Dispose();
        _cts.Dispose();
    }
}
