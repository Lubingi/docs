using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Tests;

public class UsageTests
{
    [Fact]
    public void CostAndCap()
    {
        var u = new UsageTracker(0.034, 0.017, transcribing: true, capUsd: 0.10);
        int fired = 0;
        u.CapReached += () => fired++;
        u.AddAudio(60_000);                         // 1 min → $0.051
        Assert.Equal(0.051, u.CostUsd, 3);
        Assert.Equal(0, fired);
        u.AddAudio(60_000);                         // 2 min → $0.102
        u.AddAudio(1000);
        Assert.Equal(1, fired);
        u.CapUsd = 0.5;
        u.ResetCapNotification();
        Assert.False(u.IsOverCap);
        var noOriginal = new UsageTracker(0.034, 0.017, transcribing: false, capUsd: null);
        noOriginal.AddAudio(30_000);
        Assert.Equal(0.017, noOriginal.CostUsd, 3);
    }
}

public class CallQualityTests
{
    [Fact]
    public void ProducesBoundedCallLikeAudio()
    {
        const int rate = 24000;
        var x = new float[rate * 6];
        // two "speakers": 1 s tone bursts separated by pauses
        for (int burst = 0; burst < 3; burst++)
            for (int i = 0; i < rate; i++)
                x[burst * 2 * rate + i] = (float)(0.3 * Math.Sin(2 * Math.PI * (200 + burst * 90) * i / rate));
        var y = (float[])x.Clone();
        new CallQualitySimulator().Process(y, rate);
        Assert.All(y, v => Assert.InRange(v, -1f, 1f));
        Assert.DoesNotContain(y, float.IsNaN);
        // silence now has a (very quiet) noise floor
        double silenceRms = Math.Sqrt(y.AsSpan(rate + 100, rate / 2).ToArray().Average(v => v * v));
        Assert.InRange(silenceRms, 1e-5, 0.01);
        // speech is still clearly there
        double speechRms = Math.Sqrt(y.AsSpan(rate / 4, rate / 2).ToArray().Average(v => v * v));
        Assert.True(speechRms > 0.05);
    }
}

public class RotationTests
{
    [Fact]
    public async Task SessionIsRotatedBeforeExpiryWhileQuiet()
    {
        var sessions = new List<ExpiringSession>();
        var conn = new TranslationConnection(() => { var s = new ExpiringSession(); sessions.Add(s); return s; },
            new ReconnectPolicy { RotateBeforeExpiry = TimeSpan.FromSeconds(30), MinSessionAge = TimeSpan.Zero, InitialDelay = TimeSpan.FromMilliseconds(10) });
        var starts = new List<double>();
        conn.FrameSent += (t, start, d, w) => { lock (starts) starts.Add(start); };
        conn.Start();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (sessions.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(sessions.Count >= 2, "session was not rotated");
        await Task.Delay(200);
        Assert.True(sessions[0].Closed, "old session was not closed gracefully");
        Assert.Equal(ConnectionState.Connected, conn.State);
        conn.Append(new float[768], new FrameTag(null, false));
        await conn.StopAsync();
    }

    private sealed class ExpiringSession : ITranslationSession
    {
        public bool Closed { get; private set; }
        public event Action<TranslationServerEvent>? EventReceived;
        public event Action<TranslationSessionEnd>? Ended;
        public Task ConnectAsync(CancellationToken ct)
        {
            // Expires in 10 s, i.e. already inside the 30 s rotation window.
            Task.Run(async () => { await Task.Delay(50); EventReceived?.Invoke(new TranslationServerEvent { Type = "session.created", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10) }); });
            return Task.CompletedTask;
        }
        public void SendAudio(byte[] pcm16) { }
        public Task CloseAsync(TimeSpan timeout) { Closed = true; Ended?.Invoke(new TranslationSessionEnd("closed", null, false)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
