using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Tests;

/// <summary>Runs the real WebSocket client against a local fake of the Realtime Translation endpoint.</summary>
public class WebSocketSessionTests
{
    [Fact]
    public async Task SpeaksTheProtocol()
    {
        int port = Random.Shared.Next(20000, 60000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = new List<string>();
        string? auth = null, query = null;

        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            auth = ctx.Request.Headers["Authorization"];
            query = ctx.Request.Url?.Query;
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            var ws = wsCtx.WebSocket;
            async Task Send(string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);
            await Send("""{"type":"session.created","event_id":"e0","session":{"id":"sess_x","type":"translation","model":"gpt-realtime-translate","expires_at":1900000000,"audio":{}}}""");
            var buf = new byte[1 << 20];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, default);
                if (r.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, r.Count);
                received.Add(text);
                using var doc = JsonDocument.Parse(text);
                switch (doc.RootElement.GetProperty("type").GetString())
                {
                    case "session.input_audio_buffer.append":
                        await Send("""{"type":"session.output_transcript.delta","event_id":"e1","delta":"Hello","elapsed_ms":200}""");
                        await Send("""{"type":"session.input_transcript.delta","event_id":"e2","delta":"Merhaba","elapsed_ms":200}""");
                        break;
                    case "session.close":
                        await Send("""{"type":"session.closed","event_id":"e3"}""");
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default);
                        return;
                }
            }
        });

        var config = new TranslationSessionConfig { Endpoint = $"ws://127.0.0.1:{port}/v1/realtime/translations" };
        await using var session = new RealtimeTranslationSession(config, "sk-test", NullLog.Instance);
        var events = new List<TranslationServerEvent>();
        var gotDeltas = new TaskCompletionSource();
        session.EventReceived += e => { lock (events) { events.Add(e); if (events.Count(x => x.Delta != null) >= 2) gotDeltas.TrySetResult(); } };
        await session.ConnectAsync(default);
        session.SendAudio(new byte[9600]);
        await gotDeltas.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.CloseAsync(TimeSpan.FromSeconds(3));
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Bearer sk-test", auth);
        Assert.Equal("?model=gpt-realtime-translate", query);
        Assert.StartsWith("""{"type":"session.update""", received[0]);
        Assert.Contains(received, r => r.Contains("session.input_audio_buffer.append"));
        Assert.Contains(received, r => r.Contains("\"session.close\""));
        lock (events)
        {
            Assert.Contains(events, e => e.Type == "session.created" && e.SessionId == "sess_x");
            Assert.Contains(events, e => e.Type == "session.output_transcript.delta" && e.Delta == "Hello" && e.ElapsedMs == 200);
            Assert.Contains(events, e => e.Type == "session.input_transcript.delta" && e.Delta == "Merhaba");
        }
    }
}
