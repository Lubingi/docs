using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Tests;

public class ConnectionTests
{
    private static readonly ReconnectPolicy Fast = new() { InitialDelay = TimeSpan.FromMilliseconds(20), MaxDelay = TimeSpan.FromMilliseconds(50) };

    private static float[] Tone(int samples) => Enumerable.Range(0, samples).Select(i => (float)(0.3 * Math.Sin(i * 0.1))).ToArray();

    [Fact]
    public async Task ReconnectsAndReplaysBacklogOnContinuousTimeline()
    {
        var sessions = new List<FakeTranslationSession>();
        int attempts = 0;
        var conn = new TranslationConnection(() =>
        {
            var s = new FakeTranslationSession { FailNextConnect = attempts++ == 0 };
            sessions.Add(s);
            return s;
        }, Fast);
        var sent = new List<double>();
        conn.FrameSent += (tag, start, dur, wall) => { lock (sent) sent.Add(start); };

        // Audio arrives before we are connected: must be buffered, not lost.
        for (int i = 0; i < 10; i++) conn.Append(Tone(768), new FrameTag(1, true));
        conn.Start();
        await WaitFor(() => conn.State == ConnectionState.Connected);
        Assert.Equal(2, sessions.Count); // first attempt failed
        lock (sent) Assert.Equal(10, sent.Count);

        sessions[^1].Drop();
        await WaitFor(() => sessions.Count == 3 && conn.State == ConnectionState.Connected);
        for (int i = 0; i < 10; i++) conn.Append(Tone(768), new FrameTag(2, true));
        lock (sent)
        {
            Assert.Equal(20, sent.Count);
            // model time keeps increasing across sessions
            Assert.True(sent.Zip(sent.Skip(1)).All(p => p.Second > p.First));
        }
        Assert.True(conn.ReconnectCount >= 1);
        await conn.StopAsync();
        Assert.Equal(ConnectionState.Stopped, conn.State);
    }

    [Fact]
    public async Task AuthFailureIsNotRetried()
    {
        int attempts = 0;
        var conn = new TranslationConnection(() => { attempts++; return new AuthFailSession(); }, Fast);
        conn.Start();
        await WaitFor(() => conn.State == ConnectionState.Failed);
        await Task.Delay(200);
        Assert.Equal(1, attempts);
        Assert.Contains("API key", conn.StatusMessage);
        await conn.StopAsync();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class AuthFailSession : ITranslationSession
    {
        public event Action<TranslationServerEvent>? EventReceived { add { } remove { } }
        public event Action<TranslationSessionEnd>? Ended { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct) =>
            throw new TranslationConnectException(System.Net.HttpStatusCode.Unauthorized,
                RealtimeTranslationSession.DescribeHttpFailure(System.Net.HttpStatusCode.Unauthorized), new Exception());
        public void SendAudio(byte[] pcm16) { }
        public Task CloseAsync(TimeSpan timeout) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
