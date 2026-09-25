using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Testing;
using LiveSubtitles.Core.Transcript;
using LiveSubtitles.Core.Translation;
using LiveSubtitles.Core.Vad;

// Developer / troubleshooting tool. Runs the same code as the app, without the UI, on any OS.
// Add --call-quality to vad/diarize/translate/probe to degrade the audio like a real Discord call first.
//   livesubs-cli vad <file.wav>                                 print detected speech segments
//   livesubs-cli diarize <file.wav>                             print who spoke when (local speaker model)
//   livesubs-cli translate <file.wav> [lang] [--report r.json]  full pipeline (VAD, speakers, OpenAI) in real time
//   livesubs-cli probe <file.wav> <events.jsonl> [--no-original] stream the whole file straight to OpenAI and log every raw event
//   livesubs-cli dialogue <language> <speakers 2-4> <out-folder> [--gpt]   generate a test conversation with OpenAI TTS
// Commands that call OpenAI need the OPENAI_API_KEY environment variable. The key is never printed or written.

var json = new JsonSerializerOptions { WriteIndented = true };
string modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
if (args.Length < 2) return Usage();

switch (args[0])
{
    case "vad":
    case "diarize":
    {
        var (samples, rate) = Load(args[1]);
        using var embedder = args[0] == "diarize" ? new SpeakerEmbedder(Path.Combine(modelsDir, "campplus_voxceleb_16k.onnx")) : null;
        var registry = embedder == null ? null : new SpeakerRegistry(a => embedder.Embed(a), new DiarizationSettings());
        using var vad = new SileroVad(Path.Combine(modelsDir, "silero_vad.onnx"));
        var to16 = new StreamResampler(rate, 16000).Process(samples);
        var to24 = new StreamResampler(rate, 24000).Process(samples);
        var seg = new SpeechSegmenter(new SegmenterOptions());
        var events = new List<SegmenterEvent>();
        int frames = Math.Min(to16.Length / 512, to24.Length / 768);
        int sent = 0;
        for (int i = 0; i <= frames; i++)
        {
            events.Clear();
            if (i == frames) seg.Flush(events);
            else seg.Process(to16[(i * 512)..((i + 1) * 512)], to24[(i * 768)..((i + 1) * 768)], vad.Process(to16.AsSpan(i * 512, 512)), events);
            foreach (var e in events)
            {
                if (e is SegmentEnded s)
                {
                    string who = "";
                    if (registry != null)
                    {
                        var m = registry.Assign(s.SegmentId, s.Audio16);
                        who = $"  {registry.Describe(m.SpeakerId, m.Uncertain).Label,-10} (best {m.Similarity:0.00}, next {m.SecondBest:0.00})";
                    }
                    Console.WriteLine($"  segment {s.SegmentId,3}: {s.StreamStartMs / 1000,7:0.00}s – {s.StreamEndMs / 1000,7:0.00}s{who}");
                }
                if (e is SendFrame) sent++;
            }
        }
        Console.WriteLine($"audio that would be sent: {sent * 0.032:0.0}s of {samples.Length / (double)rate:0.0}s");
        if (registry != null)
        {
            Console.WriteLine("final speakers (after retroactive fixes and merges):");
            foreach (var sp in registry.Snapshot()) Console.WriteLine($"  {sp.Label}: {sp.Segments} segments, {sp.SpeechSeconds:0.0}s");
        }
        return 0;
    }

    case "translate":
    {
        var key = RequireKey();
        if (key == null) return 1;
        var (samples, rate) = Load(args[1]);
        string lang = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "en";
        string? reportPath = Option("--report");
        var config = new TranslationSessionConfig { OutputLanguage = lang };
        var log = new ConsoleLog();
        var store = new TranscriptStore();
        var debug = new ConcurrentDictionary<int, SegmentDebugInfo>();
        var raw = new ConcurrentQueue<string>();
        var clock = Stopwatch.StartNew();
        store.LineUpdated += l =>
        {
            if (l.IsFinal && (l.HasText || l.Original.Length > 0))
                Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0}s] {l.SpeakerLabel,-10} {l.DisplayText}\n                    ({l.Original})");
        };
        using var vad = new SileroVad(Path.Combine(modelsDir, "silero_vad.onnx"));
        using var embedder = new SpeakerEmbedder(Path.Combine(modelsDir, "campplus_voxceleb_16k.onnx"));
        var registry = new SpeakerRegistry(a => embedder.Embed(a), new DiarizationSettings());
        var pipeline = new SubtitlePipeline(new PipelineOptions(), vad, registry,
            () => new RealtimeTranslationSession(config, key, log), store, log);
        pipeline.RawEvent += e =>
        {
            raw.Enqueue($"{clock.Elapsed.TotalMilliseconds:0} {e}");
            if (e.StartsWith("error") || e.StartsWith("connection")) Console.WriteLine("  " + e);
        };
        pipeline.SegmentDebug += d => debug[d.SegmentId] = d;
        PipelineStatus? lastStatus = null;
        pipeline.StatusChanged += s => lastStatus = s;
        pipeline.Start();
        int chunk = rate / 50;
        for (int i = 0; i < samples.Length; i += chunk)
        {
            pipeline.PushAudio(samples[i..Math.Min(samples.Length, i + chunk)], rate);
            var due = TimeSpan.FromSeconds((double)(i + chunk) / rate);
            if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed);
        }
        await Task.Delay(8000);
        await pipeline.StopAsync();
        var usage = (lastStatus?.AudioSentSeconds ?? 0) / 60.0;
        Console.WriteLine($"audio sent: {usage:0.00} min of {samples.Length / (double)rate / 60:0.00} min · est. cost ${usage * 0.051:0.000}");
        foreach (var sp in registry.Snapshot()) Console.WriteLine($"  {sp.Label}: {sp.Segments} segments, {sp.SpeechSeconds:0.0}s");
        if (reportPath != null)
        {
            var report = new
            {
                file = Path.GetFileName(args[1]),
                durationSeconds = samples.Length / (double)rate,
                audioSentMinutes = usage,
                avgTextLagMs = lastStatus?.AvgTextLagMs,
                speakers = registry.Snapshot(),
                segments = debug.Values.OrderBy(d => d.SegmentId),
                lines = store.Snapshot().Select(l => new { l.SegmentId, l.StreamStartMs, l.StreamEndMs, l.SpeakerLabel, l.SpeakerConfidence, l.SpeakerUncertain, l.Translation, l.Original, l.SameLanguage, l.IsFinal, l.Hidden }),
                rawEvents = raw.ToArray(),
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, json));
            Console.WriteLine($"report written to {reportPath}");
        }
        return 0;
    }

    case "probe":
    {
        // Streams the complete file (including silences) in real-time 200 ms chunks, then 6 s of silence, then
        // session.close, and records every server event with timing. This shows exactly how the API behaves
        // (what elapsed_ms means, how far text lags the audio, what happens at the end) independent of the app's logic.
        var key = RequireKey();
        if (key == null || args.Length < 3) return key == null ? 1 : Usage();
        var (samples, rate) = Load(args[1]);
        var pcm24 = rate == 24000 ? samples : new StreamResampler(rate, 24000).Process(samples);
        var config = new TranslationSessionConfig { TranscriptionModel = args.Contains("--no-original") ? null : TranslationProtocol.DefaultTranscriptionModel };
        await using var output = new StreamWriter(args[2]);
        var gate = new object();
        var clock = Stopwatch.StartNew();
        double sentMs = 0;
        int deltas = 0;
        void Write(object o) { lock (gate) output.WriteLine(JsonSerializer.Serialize(o)); }

        await using var session = new RealtimeTranslationSession(config, key, new ConsoleLog());
        var closed = new TaskCompletionSource();
        session.EventReceived += e =>
        {
            double t = clock.Elapsed.TotalMilliseconds;
            if (e.Delta != null) Interlocked.Increment(ref deltas);
            Write(new
            {
                t_ms = Math.Round(t),
                sent_ms = sentMs,
                type = e.Type,
                elapsed_ms = e.ElapsedMs,
                delta = e.Delta,
                audio_bytes = e.AudioBytes > 0 ? e.AudioBytes : (int?)null,
                expires_at = e.ExpiresAt,
                session = e.SessionJson,
                error = e.ErrorMessage == null ? null : $"{e.ErrorType}/{e.ErrorCode}: {e.ErrorMessage}",
            });
            if (e.Type == "session.output_transcript.delta") Console.Write(e.Delta);
            if (e.Type == "error") Console.WriteLine($"\n  ERROR {e.ErrorCode}: {e.ErrorMessage}");
            if (e.Type == "session.closed") closed.TrySetResult();
        };
        session.Ended += end => { Write(new { t_ms = Math.Round(clock.Elapsed.TotalMilliseconds), type = "client.ended", reason = end.Reason }); closed.TrySetResult(); };
        await session.ConnectAsync(CancellationToken.None);
        Write(new { t_ms = Math.Round(clock.Elapsed.TotalMilliseconds), type = "client.connected", file = Path.GetFileName(args[1]), audio_seconds = pcm24.Length / 24000.0 });
        var withTail = pcm24.Concat(new float[24000 * 6]).ToArray();
        clock.Restart();
        for (int i = 0; i < withTail.Length; i += TranslationProtocol.ChunkSamples)
        {
            var piece = withTail.AsSpan(i, Math.Min(TranslationProtocol.ChunkSamples, withTail.Length - i)).ToArray();
            if (piece.Length < TranslationProtocol.ChunkSamples) Array.Resize(ref piece, TranslationProtocol.ChunkSamples);
            session.SendAudio(Pcm16.FromFloat(piece));
            sentMs += TranslationProtocol.ChunkMs;
            if (sentMs % 5000 == 0) Write(new { t_ms = Math.Round(clock.Elapsed.TotalMilliseconds), type = "client.progress", sent_ms = sentMs });
            var due = TimeSpan.FromMilliseconds(sentMs);
            if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed);
        }
        Write(new { t_ms = Math.Round(clock.Elapsed.TotalMilliseconds), type = "client.close_sent", sent_ms = sentMs });
        var closeTask = session.CloseAsync(TimeSpan.FromSeconds(15));
        await Task.WhenAny(closed.Task, Task.Delay(16000));
        await closeTask;
        Console.WriteLine($"\n{deltas} text deltas; events written to {args[2]}");
        return 0;
    }

    case "dialogue":
    {
        var key = RequireKey();
        if (key == null || args.Length < 4) return key == null ? 1 : Usage();
        var gen = new DialogueGenerator(key);
        var result = await gen.GenerateAsync(new DialogueRequest
        {
            Language = args[1],
            Speakers = int.Parse(args[2]),
            Lines = 12,
            GenerateScript = args.Contains("--gpt"),
        }, args[3], new Progress<string>(Console.WriteLine), CancellationToken.None);
        Console.WriteLine(File.ReadAllText(result.ScriptPath));
        Console.WriteLine(result.AudioPath);
        return 0;
    }

    default:
        return Usage();
}

int Usage()
{
    Console.WriteLine("usage: livesubs-cli vad|diarize <file.wav> [--call-quality]");
    Console.WriteLine("       livesubs-cli translate <file.wav> [lang] [--report report.json] [--call-quality]");
    Console.WriteLine("       livesubs-cli probe <file.wav> <events.jsonl> [--no-original]");
    Console.WriteLine("       livesubs-cli dialogue <language> <speakers 2-4> <out-folder> [--gpt]");
    return 1;
}

(float[] Samples, int Rate) Load(string path)
{
    var r = WavFile.ReadMono(path);
    bool callQuality = args.Contains("--call-quality");
    if (callQuality) new CallQualitySimulator().Process(r.Samples, r.SampleRate);
    Console.WriteLine($"{Path.GetFileName(path)}: {r.Samples.Length / (double)r.SampleRate:0.0}s @ {r.SampleRate} Hz{(callQuality ? " (call-quality simulation on)" : "")}");
    return r;
}

string? RequireKey()
{
    var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(key)) Console.WriteLine("Set the OPENAI_API_KEY environment variable first.");
    return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

sealed class ConsoleLog : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level >= LogLevel.Warning) Console.WriteLine($"  {level}: {message} {exception?.Message}");
    }
}
