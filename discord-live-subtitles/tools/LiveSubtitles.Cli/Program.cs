using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Transcript;
using LiveSubtitles.Core.Translation;
using LiveSubtitles.Core.Vad;

// Developer / troubleshooting tool. Runs the same pipeline as the app on a WAV file, without the UI.
//   livesubs-cli vad <file.wav>                 print detected speech segments
//   livesubs-cli translate <file.wav> [lang]    stream the file to OpenAI in real time (needs OPENAI_API_KEY)

if (args.Length < 2)
{
    Console.WriteLine("usage: livesubs-cli vad|translate <file.wav> [output-language]");
    return 1;
}

string modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
var (samples, rate) = WavFile.ReadMono(args[1]);
Console.WriteLine($"{Path.GetFileName(args[1])}: {samples.Length / (double)rate:0.0}s @ {rate} Hz");

switch (args[0])
{
    case "vad":
    {
        using var vad = new SileroVad(Path.Combine(modelsDir, "silero_vad.onnx"));
        var to16 = new StreamResampler(rate, 16000).Process(samples);
        var to24 = new StreamResampler(rate, 24000).Process(samples);
        var seg = new SpeechSegmenter(new SegmenterOptions());
        var events = new List<SegmenterEvent>();
        int frames = Math.Min(to16.Length / 512, to24.Length / 768);
        int sent = 0;
        for (int i = 0; i < frames; i++)
        {
            events.Clear();
            seg.Process(to16[(i * 512)..((i + 1) * 512)], to24[(i * 768)..((i + 1) * 768)], vad.Process(to16.AsSpan(i * 512, 512)), events);
            foreach (var e in events)
            {
                if (e is SegmentEnded s) Console.WriteLine($"  segment {s.SegmentId,3}: {s.StreamStartMs / 1000,7:0.00}s – {s.StreamEndMs / 1000,7:0.00}s");
                if (e is SendFrame) sent++;
            }
        }
        Console.WriteLine($"audio that would be sent: {sent * 0.032:0.0}s of {samples.Length / (double)rate:0.0}s");
        return 0;
    }
    case "translate":
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(key)) { Console.WriteLine("Set OPENAI_API_KEY first."); return 1; }
        var config = new TranslationSessionConfig { OutputLanguage = args.Length > 2 ? args[2] : "en" };
        var log = new ConsoleLog();
        var store = new TranscriptStore();
        store.LineUpdated += l => { if (l.IsFinal && l.HasText) Console.WriteLine($"[{l.StartedAt.ToLocalTime():HH:mm:ss}] {l.DisplayText}\n           ({l.Original})"); };
        using var vad = new SileroVad(Path.Combine(modelsDir, "silero_vad.onnx"));
        var pipeline = new SubtitlePipeline(new PipelineOptions(), vad, new NoSpeakerIdentifier(),
            () => new RealtimeTranslationSession(config, key, log), store, log);
        pipeline.RawEvent += e => { if (e.StartsWith("error") || e.StartsWith("connection")) Console.WriteLine("  " + e); };
        pipeline.Start();
        int chunk = rate / 50;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < samples.Length; i += chunk)
        {
            pipeline.PushAudio(samples[i..Math.Min(samples.Length, i + chunk)], rate);
            var due = TimeSpan.FromSeconds((double)(i + chunk) / rate);
            if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed);
        }
        await Task.Delay(6000);
        await pipeline.StopAsync();
        return 0;
    }
    default:
        Console.WriteLine("unknown command");
        return 1;
}

sealed class ConsoleLog : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level >= LogLevel.Warning) Console.WriteLine($"  {level}: {message} {exception?.Message}");
    }
}
