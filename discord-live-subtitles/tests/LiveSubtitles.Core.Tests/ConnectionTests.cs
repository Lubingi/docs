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
        int replays = 0;
        conn.FrameSent += (tag, start, dur, wall) => { lock (sent) { sent.Add(start); if (tag.Replay) replays++; } };

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
            Assert.Equal(10, replays); // the 10 frames in flight when the session died are sent again
            Assert.Equal(30, sent.Count);
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

/// <summary>VPS bugs 2 and 5: stop crashed (double dispose, false "reconnecting"); a drop lost the audio in flight.</summary>
public class StopAndDropTests
{
    private static readonly ReconnectPolicy Fast = new() { InitialDelay = TimeSpan.FromMilliseconds(20), MaxDelay = TimeSpan.FromMilliseconds(50) };
    private static float[] Tone(int n) => Enumerable.Range(0, n).Select(i => (float)(0.3 * Math.Sin(i * 0.1))).ToArray();

    /// <summary>Behaves like the real session: the server closes the socket after session.close, and a second
    /// dispose throws like a disposed CancellationTokenSource did.</summary>
    private sealed class StrictSession : ITranslationSession
    {
        public int Disposed;
        public int ChunksReceived;
        public event Action<TranslationServerEvent>? EventReceived { add { } remove { } }
        public event Action<TranslationSessionEnd>? Ended;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public void SendAudio(byte[] pcm16) => Interlocked.Increment(ref ChunksReceived);
        public Task CloseAsync(TimeSpan timeout) { Ended?.Invoke(new TranslationSessionEnd("Connection ended", null, false)); return Task.CompletedTask; }
        public void Drop() => Ended?.Invoke(new TranslationSessionEnd("server went away", null, false));
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref Disposed) > 1) throw new ObjectDisposedException("CancellationTokenSource");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task StopIsCleanAndNeverLooksLikeADrop()
    {
        var sessions = new List<StrictSession>();
        var conn = new TranslationConnection(() => { var s = new StrictSession(); sessions.Add(s); return s; }, Fast);
        var states = new List<ConnectionState>();
        conn.StateChanged += (s, _) => { lock (states) states.Add(s); };
        conn.Start();
        await Wait(() => conn.State == ConnectionState.Connected);
        conn.Append(Tone(4800), new FrameTag(1, true));
        int before;
        lock (states) before = states.Count;
        await conn.StopAsync(); // must not throw
        await Task.Delay(150);
        lock (states)
        {
            var afterStop = states.Skip(before).ToList();
            Assert.DoesNotContain(ConnectionState.Reconnecting, afterStop);
            Assert.Equal(1, afterStop.Count(s => s == ConnectionState.Stopped));
        }
        Assert.Single(sessions);
        Assert.Equal(1, sessions[0].Disposed);
        await conn.StopAsync(); // second stop is a no-op
    }

    [Fact]
    public async Task AudioInFlightIsResentAfterADrop()
    {
        var sessions = new List<StrictSession>();
        var conn = new TranslationConnection(() => { var s = new StrictSession(); sessions.Add(s); return s; }, Fast);
        var replayed = new List<FrameTag>();
        conn.FrameSent += (tag, _, _, _) => { if (tag.Replay) lock (replayed) replayed.Add(tag); };
        conn.Start();
        await Wait(() => conn.State == ConnectionState.Connected);
        for (int i = 0; i < 100; i++) conn.Append(Tone(768), new FrameTag(7, true)); // 3.2 s of speech
        sessions[0].Drop();
        await Wait(() => sessions.Count == 2 && conn.State == ConnectionState.Connected);
        lock (replayed)
        {
            double ms = replayed.Count * 32;
            Assert.InRange(ms, 3000, 4100); // everything not yet translated, capped at MaxReplay (4 s)
            Assert.All(replayed, t => Assert.Equal(7, t.SegmentId));
        }
        Assert.True(sessions[1].ChunksReceived >= 15);
        await conn.StopAsync();
    }

    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
