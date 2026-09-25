using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using LiveSubtitles.Core.Diagnostics;

namespace LiveSubtitles.Core.Translation;

public sealed record TranslationSessionEnd(string Reason, Exception? Exception, bool Fatal, HttpStatusCode? HttpStatus = null);

/// <summary>One translation session (one WebSocket). Reconnects are handled by <see cref="TranslationConnection"/>.</summary>
public interface ITranslationSession : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    /// <summary>Queues one chunk of 24 kHz PCM16 audio. Never blocks.</summary>
    void SendAudio(byte[] pcm16);
    /// <summary>Sends session.close and waits (up to the timeout) for the server to flush remaining output.</summary>
    Task CloseAsync(TimeSpan timeout);
    event Action<TranslationServerEvent>? EventReceived;
    event Action<TranslationSessionEnd>? Ended;
}

public sealed class RealtimeTranslationSession : ITranslationSession
{
    private readonly TranslationSessionConfig _config;
    private readonly ILog _log;
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closedByServer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _sendLoop, _receiveLoop;
    private int _ended;

    public RealtimeTranslationSession(TranslationSessionConfig config, string apiKey, ILog log)
    {
        _config = config;
        _log = log;
        _ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _ws.Options.CollectHttpResponseDetails = true;
    }

    public event Action<TranslationServerEvent>? EventReceived;
    public event Action<TranslationSessionEnd>? Ended;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var uri = TranslationProtocol.BuildUri(_config.Endpoint, _config.Model);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _ws.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex) when (_ws.HttpStatusCode != 0 && _ws.HttpStatusCode != HttpStatusCode.SwitchingProtocols)
        {
            throw new TranslationConnectException(_ws.HttpStatusCode, DescribeHttpFailure(_ws.HttpStatusCode), ex);
        }
        _log.Info($"Translation WebSocket connected ({_config.Model}, output={_config.OutputLanguage})");
        // session.update is the first message so it is applied before any audio.
        await _ws.SendAsync(TranslationProtocol.BuildSessionUpdate(_config), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public void SendAudio(byte[] pcm16)
    {
        if (_ended != 0) return;
        _outgoing.Writer.TryWrite(TranslationProtocol.BuildAppend(pcm16));
    }

    public async Task CloseAsync(TimeSpan timeout)
    {
        if (_ws.State != WebSocketState.Open) return;
        try
        {
            _outgoing.Writer.TryWrite(TranslationProtocol.BuildClose());
            _outgoing.Writer.TryComplete();
            await Task.WhenAny(_closedByServer.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Close: {ex.Message}");
        }
        finally
        {
            RaiseEnded(new TranslationSessionEnd("Closed by client", null, false));
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(message, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RaiseEnded(new TranslationSessionEnd("Send failed: " + ex.Message, ex, false));
        }
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
                    var status = _ws.CloseStatus;
                    var desc = _ws.CloseStatusDescription;
                    _closedByServer.TrySetResult();
                    bool fatal = status == WebSocketCloseStatus.PolicyViolation && (desc?.Contains("auth", StringComparison.OrdinalIgnoreCase) ?? false);
                    RaiseEnded(new TranslationSessionEnd($"Server closed the connection ({status}{(string.IsNullOrEmpty(desc) ? "" : ": " + desc)})", null, fatal));
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                TranslationServerEvent ev;
                try
                {
                    ev = TranslationProtocol.Parse(message.GetBuffer().AsSpan(0, (int)message.Length), DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not parse server event", ex);
                    message.SetLength(0);
                    continue;
                }
                message.SetLength(0);
                if (ev.Type == "session.closed") _closedByServer.TrySetResult();
                if (ev.Type == "error") _log.Warn($"Server error event: {ev.ErrorType}/{ev.ErrorCode}: {ev.ErrorMessage}");
                EventReceived?.Invoke(ev);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RaiseEnded(new TranslationSessionEnd("Connection lost: " + ex.Message, ex, false));
        }
        finally
        {
            _closedByServer.TrySetResult();
            RaiseEnded(new TranslationSessionEnd("Connection ended", null, false));
        }
    }

    private void RaiseEnded(TranslationSessionEnd end)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _log.Info($"Translation session ended: {end.Reason}");
        Ended?.Invoke(end);
    }

    internal static string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "OpenAI rejected the API key (401). Check the key in Settings.",
        HttpStatusCode.Forbidden => "OpenAI refused access (403). The key's project may not have access to gpt-realtime-translate.",
        HttpStatusCode.NotFound => "Translation endpoint or model not found (404).",
        HttpStatusCode.TooManyRequests => "Rate limited or out of credit (429). Check your OpenAI billing/usage limits.",
        _ => $"OpenAI returned HTTP {(int)status} while connecting.",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        try
        {
            if (_sendLoop != null) await _sendLoop.ConfigureAwait(false);
            if (_receiveLoop != null) await _receiveLoop.ConfigureAwait(false);
        }
        catch { }
        _ws.Dispose();
        _cts.Dispose();
    }
}

public sealed class TranslationConnectException(HttpStatusCode status, string message, Exception inner) : Exception(message, inner)
{
    public HttpStatusCode Status { get; } = status;
    public bool IsFatal => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
}
