using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Transcript;
using LiveSubtitles.Core.Translation;
using LiveSubtitles.Core.Vad;

namespace LiveSubtitles.Core.Tests;

public class SonioxProtocolTests
{
    [Fact]
    public void ConfigCarriesAudioFormatTranslationAndGlossary()
    {
        var settings = new AppSettings
        {
            Engine = TranslationEngine.Soniox,
            OutputLanguage = "en",
            SonioxLanguageHints = "tr, NO, xyz, tr",
            Glossary =
            {
                new GlossaryEntry { Mode = GlossaryMode.KeepAsIs, Term = "Emre" },
                new GlossaryEntry { Mode = GlossaryMode.Replace, Term = "kanka", Replacement = "buddy" },
                new GlossaryEntry { Mode = GlossaryMode.Replace, Term = "off", Replacement = "x", Enabled = false },
            },
        };
        var json = Encoding.UTF8.GetString(SonioxProtocol.BuildConfig(settings.ToSonioxConfig(), "key-123"));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("key-123", r.GetProperty("api_key").GetString());
        Assert.Equal(SonioxProtocol.DefaultModel, r.GetProperty("model").GetString());
        Assert.Equal("pcm_s16le", r.GetProperty("audio_format").GetString());
        Assert.Equal(24000, r.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, r.GetProperty("num_channels").GetInt32());
        Assert.Equal(new[] { "tr", "no" }, r.GetProperty("language_hints").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("one_way", r.GetProperty("translation").GetProperty("type").GetString());
        Assert.Equal("en", r.GetProperty("translation").GetProperty("target_language").GetString());
        Assert.True(r.GetProperty("enable_endpoint_detection").GetBoolean());
        var context = r.GetProperty("context");
        Assert.Equal(new[] { "Emre" }, context.GetProperty("terms").EnumerateArray().Select(e => e.GetString()));
        var term = Assert.Single(context.GetProperty("translation_terms").EnumerateArray());
        Assert.Equal("kanka", term.GetProperty("source").GetString());
        Assert.Equal("buddy", term.GetProperty("target").GetString());
    }

    [Fact]
    public void ParsesTokensAndErrors()
    {
        var m = SonioxProtocol.Parse("""
            {"tokens":[
              {"text":"Mer","start_ms":120,"end_ms":300,"confidence":0.9,"is_final":true,"speaker":"1","language":"tr","translation_status":"original"},
              {"text":"Hel","is_final":false,"language":"en","source_language":"tr","translation_status":"translation"}],
             "final_audio_proc_ms":300,"total_audio_proc_ms":900}
            """u8);
        Assert.Equal(2, m.Tokens.Count);
        Assert.Equal(new SonioxToken { Text = "Mer", StartMs = 120, EndMs = 300, IsFinal = true, Speaker = "1", Language = "tr" }, m.Tokens[0]);
        Assert.True(m.Tokens[1].IsTranslation);
        Assert.False(m.Tokens[1].IsFinal);
        Assert.Null(m.Tokens[1].StartMs);
        Assert.Equal(300, m.FinalAudioProcMs);

        var e = SonioxProtocol.Parse("""{"error_code":401,"error_message":"Invalid API key."}"""u8);
        Assert.Equal(401, e.ErrorCode);
        Assert.True(SonioxProtocol.IsFatal(401));
        Assert.True(SonioxProtocol.IsFatal(402));
        Assert.False(SonioxProtocol.IsFatal(408));
        Assert.False(SonioxProtocol.IsFatal(503));
    }

    [Fact]
    public void MapperUsesFinalTokensAndSpreadsTranslationOverItsChunk()
    {
        var mapper = new SonioxTokenMapper();
        var now = DateTimeOffset.UtcNow;
        SonioxToken O(string text, double start, bool final = true) => new() { Text = text, StartMs = start, EndMs = start + 300, IsFinal = final };
        SonioxToken T(string text) => new() { Text = text, IsFinal = true, IsTranslation = true };

        var first = new SonioxMessage();
        first.Tokens.AddRange(new[] { O("Bugün", 0), O(" hava", 400), O(" güzel.", 800), O(" Evet", 3000, final: false) });
        var ev1 = mapper.Map(first, now).ToList();
        Assert.Equal(3, ev1.Count); // the non-final token is ignored
        Assert.All(ev1, e => Assert.Equal("session.input_transcript.delta", e.Type));
        Assert.All(ev1, e => Assert.True(e.Timed));
        Assert.Equal(new double?[] { 0, 400, 800 }, ev1.Select(e => e.ElapsedMs));

        var second = new SonioxMessage();
        second.Tokens.AddRange(new[] { T("The"), T(" weather"), T(" is"), T(" nice."), new SonioxToken { Text = "<end>", IsFinal = true } });
        var ev2 = mapper.Map(second, now).ToList();
        Assert.Equal(4, ev2.Count); // <end> is dropped
        Assert.All(ev2, e => Assert.Equal("session.output_transcript.delta", e.Type));
        var times = ev2.Select(e => e.ElapsedMs!.Value).ToList();
        Assert.Equal(300, times[0]);                    // starts at the chunk's first word
        Assert.True(times.SequenceEqual(times.Order()));  // never goes backwards
        Assert.True(times[^1] <= 1100);                   // never past the chunk's last word
        Assert.True(times[^1] > times[0]);                // and moves through the chunk

        // The next original chunk starts a new mapping.
        var third = new SonioxMessage();
        third.Tokens.AddRange(new[] { O("Evet.", 3000), T("Yes.") });
        var ev3 = mapper.Map(third, now).ToList();
        Assert.Equal(3000, ev3[0].ElapsedMs);
        Assert.Equal(3300, ev3[1].ElapsedMs);
    }

    [Fact]
    public void TranslationBelongsToTheSameSpeakersWords()
    {
        var mapper = new SonioxTokenMapper();
        var m = new SonioxMessage();
        m.Tokens.AddRange(new[]
        {
            new SonioxToken { Text = "Hi.", StartMs = 0, EndMs = 300, IsFinal = true, Speaker = "1", Language = "en" },
            new SonioxToken { Text = "Hei", StartMs = 1000, EndMs = 1300, IsFinal = true, Speaker = "2", Language = "no" },
            new SonioxToken { Text = "Hello", IsFinal = true, IsTranslation = true, Speaker = "2" },
        });
        var ev = mapper.Map(m, DateTimeOffset.UtcNow).ToList();
        Assert.Equal(1300, ev[^1].ElapsedMs);
    }
}

public class SonioxAttributionTests
{
    private static TimelineSegment Seg(int id, double start, double end) =>
        new(id) { ModelStartMs = start, ModelSpeechEndMs = end, SpeechEnded = true, EndSentWall = DateTimeOffset.UtcNow };

    [Fact]
    public void TimedOriginalWordsLandOnTheLineTheyWereSpokenIn()
    {
        // A's sentence has no final punctuation: the cursor rules would keep B's first words on A.
        var segs = new List<TimelineSegment> { Seg(1, 0, 2000), Seg(2, 2300, 5000) };
        var (_, original) = StreamAttributor.CreatePair(new AttributionOptions(), () => segs, withOriginal: true, originalTimed: true);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, original.Add("ben de", 400, now));
        Assert.Equal(1, original.Add(" geliyorum", 1200, now));
        Assert.Equal(2, original.Add(" Tamam", 2700, now));
        Assert.Equal(2, original.Add(".", 3100, now));
        Assert.Equal("ben de geliyorum", original.TextFor(1));
    }

    [Fact]
    public void LineWithoutTranslationIsSkippedAsSoonAsTheNextLinesTranslationArrives()
    {
        // A speaks English (Soniox does not translate it), B speaks Norwegian.
        var segs = new List<TimelineSegment> { Seg(1, 0, 1500), Seg(2, 1900, 4000) };
        var (translation, original) = StreamAttributor.CreatePair(new AttributionOptions(), () => segs, withOriginal: true, originalTimed: true);
        var now = DateTimeOffset.UtcNow;
        original.Add("Sounds good.", 300, now);
        original.Add(" Det høres bra ut.", 2300, now);
        Assert.Equal(2, translation.Add("That sounds good.", 2600, now));
        Assert.Equal("", translation.TextFor(1));
    }
}

/// <summary>The real Soniox client against a local fake of the Soniox endpoint.</summary>
public class SonioxWebSocketTests
{
    private static (HttpListener Listener, string Url) Listen()
    {
        int port = Random.Shared.Next(20000, 60000);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return (listener, $"ws://127.0.0.1:{port}/transcribe-websocket");
    }

    [Fact]
    public async Task SpeaksTheProtocol()
    {
        var (listener, url) = Listen();
        using var _ = listener;
        var texts = new ConcurrentQueue<string>();
        int binaryBytes = 0;
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            async Task Send(string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);
            var buf = new byte[1 << 20];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, default);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    if (binaryBytes == 0)
                        await Send("""{"tokens":[{"text":"Merhaba","start_ms":0,"end_ms":400,"is_final":true,"translation_status":"original"},{"text":"Hel","is_final":false,"translation_status":"translation"}],"final_audio_proc_ms":400,"total_audio_proc_ms":200}""");
                    binaryBytes += r.Count;
                    continue;
                }
                var text = Encoding.UTF8.GetString(buf, 0, r.Count);
                texts.Enqueue(text);
                if (text.Contains("\"finalize\""))
                    await Send("""{"tokens":[{"text":"Hello","is_final":true,"translation_status":"translation"},{"text":"<fin>","is_final":true}],"final_audio_proc_ms":400,"total_audio_proc_ms":400}""");
                if (text.Length == 0)
                {
                    await Send("""{"tokens":[],"final_audio_proc_ms":400,"total_audio_proc_ms":400,"finished":true}""");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default);
                    return;
                }
            }
        });

        await using var session = new SonioxTranslationSession(new SonioxSessionConfig { Endpoint = url }, "soniox-test-key", NullLog.Instance);
        var events = new ConcurrentQueue<TranslationServerEvent>();
        var gotTranslation = new TaskCompletionSource();
        session.EventReceived += e => { events.Enqueue(e); if (e.Type == "session.output_transcript.delta") gotTranslation.TrySetResult(); };
        await session.ConnectAsync(default);
        session.SendAudio(new byte[9600]);
        // No more audio: after ~0.6 s the client asks Soniox to finalise, which releases the translation.
        await gotTranslation.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await session.CloseAsync(TimeSpan.FromSeconds(3));
        await server.WaitAsync(TimeSpan.FromSeconds(15));

        var first = texts.First();
        using (var doc = JsonDocument.Parse(first)) Assert.Equal("soniox-test-key", doc.RootElement.GetProperty("api_key").GetString());
        Assert.Equal(9600, binaryBytes);
        Assert.Contains(texts, t => t.Contains("\"finalize\""));
        Assert.Contains(texts, t => t.Length == 0);
        Assert.Contains(events, e => e.Type == "session.input_transcript.delta" && e.Delta == "Merhaba" && e.ElapsedMs == 0 && e.Timed);
        Assert.Contains(events, e => e.Type == "session.output_transcript.delta" && e.Delta == "Hello");
        Assert.DoesNotContain(events, e => e.Delta is "Hel" or "<fin>");
    }

    [Fact]
    public async Task BadKeyEndsTheSessionAsFatal()
    {
        var (listener, url) = Listen();
        using var _ = listener;
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var buf = new byte[1 << 16];
            await ws.ReceiveAsync(buf, default); // config
            await ws.SendAsync("""{"error_code":401,"error_message":"Invalid API key."}"""u8.ToArray(), WebSocketMessageType.Text, true, default);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default); }
            catch (WebSocketException) { } // the client may already have dropped the socket
        });

        var connection = new TranslationConnection(
            () => new SonioxTranslationSession(new SonioxSessionConfig { Endpoint = url }, "wrong", NullLog.Instance),
            new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(50) }, NullLog.Instance, "Soniox");
        var failed = new TaskCompletionSource<string>();
        connection.StateChanged += (s, m) => { if (s == ConnectionState.Failed) failed.TrySetResult(m); };
        connection.Start();
        var message = await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await server.WaitAsync(TimeSpan.FromSeconds(15));
        await connection.StopAsync();
        Assert.Contains("rejected the API key", message);
    }
}

[Collection("SpeakerModel")]
public class SonioxPipelineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimedTokensLandOnTheRightLines(bool punctuated)
    {
        var wav = TestPaths.Audio("two-speakers-en.wav");
        var model = TestPaths.Model("silero_vad.onnx");
        if (!TestPaths.Has(wav) || !TestPaths.Has(model)) return; // assets not downloaded

        var (samples, rate) = WavFile.ReadMono(wav);
        var store = new TranscriptStore();
        var debug = new ConcurrentDictionary<int, SegmentDebugInfo>();
        using var vad = new SileroVad(model);
        var options = new AppSettings { Engine = TranslationEngine.Soniox }.ToPipelineOptions() with
        {
            Attribution = new AttributionOptions { FinalizeIdleMs = 600 },
        };
        Assert.True(options.ShowOriginal);
        Assert.True(options.OriginalTimed);
        var pipeline = new SubtitlePipeline(options, vad, new NoSpeakerIdentifier(), () => new FakeSonioxSession { PunctuateTranslation = punctuated }, store, NullLog.Instance)
        {
            IsLiveSource = false,
        };
        pipeline.SegmentDebug += d => debug[d.SegmentId] = d;
        pipeline.Start();
        await Task.Delay(200);
        for (int i = 0; i < samples.Length; i += 480) pipeline.PushAudio(samples.AsSpan(i, Math.Min(480, samples.Length - i)).ToArray(), rate);
        pipeline.PushAudio(new float[rate * 4], rate);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (store.Snapshot().Count == 0 || store.Snapshot().Any(l => !l.IsFinal)))
            await Task.Delay(100);
        await pipeline.StopAsync();

        var lines = store.Snapshot().Where(l => l.HasText).ToList();
        Assert.True(lines.Count >= 2, $"expected several subtitle lines, got {lines.Count}");
        int total = 0, misplaced = 0, originals = 0, originalsMisplaced = 0;
        foreach (var line in lines)
        {
            var info = debug[line.SegmentId];
            bool Outside(int chunk, double slack) => chunk * 200.0 < info.ModelStartMs - slack || chunk * 200.0 > info.ModelEndMs + slack;
            foreach (Match m in Regex.Matches(line.Translation, @"t(\d+)"))
            {
                total++;
                if (Outside(int.Parse(m.Groups[1].Value), 250)) misplaced++;
            }
            foreach (Match m in Regex.Matches(line.Original, @"o(\d+)"))
            {
                originals++;
                if (Outside(int.Parse(m.Groups[1].Value), 0)) originalsMisplaced++;
            }
        }
        var dump = string.Join("\n", lines.Select(l => $"seg {l.SegmentId} [{debug[l.SegmentId].ModelStartMs:0}-{debug[l.SegmentId].ModelEndMs:0}] T: {l.Translation} | O: {l.Original}"));
        Assert.True(total > 20, $"too few tokens ({total})\n{dump}");
        Assert.True(misplaced <= total / 10, $"{misplaced}/{total} translated tokens on the wrong line\n{dump}");
        Assert.True(originalsMisplaced == 0, $"{originalsMisplaced}/{originals} original tokens on the wrong line");
    }
}
