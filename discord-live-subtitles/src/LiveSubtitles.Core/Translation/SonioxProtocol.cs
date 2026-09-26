using System.Buffers;
using System.Text.Json;
using LiveSubtitles.Core.Settings;

namespace LiveSubtitles.Core.Translation;

/// <summary>Which cloud service turns speech into subtitles.</summary>
public enum TranslationEngine { OpenAI, Soniox }

/// <summary>
/// Wire protocol for Soniox real-time speech-to-text with translation (taken from Soniox's official Python, Node and web SDKs).
/// Endpoint: wss://stt-rt.soniox.com/transcribe-websocket. The first message is a JSON config that carries the API key;
/// after that the client sends raw audio as binary frames, <c>{"type":"keepalive"}</c> while it has no audio,
/// <c>{"type":"finalize"}</c> to finalise pending words, and an empty text frame to end the stream.
/// The server answers with <c>{"tokens":[…],"final_audio_proc_ms":…,"total_audio_proc_ms":…}</c>; final tokens are sent
/// once, non-final ones are re-sent (revised) with every message. A failure is <c>{"error_code":…,"error_message":…}</c>.
/// </summary>
public static class SonioxProtocol
{
    public const string DefaultEndpoint = "wss://stt-rt.soniox.com/transcribe-websocket";
    public const string DefaultModel = "stt-rt-v5";
    public const string EndToken = "<end>";
    public const string FinalizeToken = "<fin>";

    public static byte[] BuildConfig(SonioxSessionConfig c, string apiKey)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("api_key", apiKey);
            w.WriteString("model", c.Model);
            w.WriteString("audio_format", "pcm_s16le");
            w.WriteNumber("sample_rate", TranslationProtocol.SampleRate);
            w.WriteNumber("num_channels", 1);
            if (c.LanguageHints.Count > 0)
            {
                w.WriteStartArray("language_hints");
                foreach (var l in c.LanguageHints) w.WriteStringValue(l);
                w.WriteEndArray();
            }
            // Soniox's own speaker changes split its translation into per-speaker chunks, which places text on the right line.
            // Labels shown in the app still come from the local voice recognition.
            w.WriteBoolean("enable_speaker_diarization", true);
            w.WriteBoolean("enable_language_identification", true);
            w.WriteBoolean("enable_endpoint_detection", true);
            w.WriteNumber("max_endpoint_delay_ms", c.MaxEndpointDelayMs);
            if (c.Terms.Count > 0 || c.TranslationTerms.Count > 0)
            {
                w.WriteStartObject("context");
                if (c.Terms.Count > 0)
                {
                    w.WriteStartArray("terms");
                    foreach (var t in c.Terms) w.WriteStringValue(t);
                    w.WriteEndArray();
                }
                if (c.TranslationTerms.Count > 0)
                {
                    w.WriteStartArray("translation_terms");
                    foreach (var (source, target) in c.TranslationTerms)
                    {
                        w.WriteStartObject();
                        w.WriteString("source", source);
                        w.WriteString("target", target);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteStartObject("translation");
            w.WriteString("type", "one_way");
            w.WriteString("target_language", c.OutputLanguage);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] BuildKeepalive() => """{"type":"keepalive"}"""u8.ToArray();
    public static byte[] BuildFinalize() => """{"type":"finalize"}"""u8.ToArray();

    public static SonioxMessage Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        var m = new SonioxMessage();
        if (root.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.Number) m.ErrorCode = ec.GetInt32();
        if (root.TryGetProperty("error_message", out var em) && em.ValueKind == JsonValueKind.String) m.ErrorMessage = em.GetString();
        if (root.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True) m.Finished = true;
        if (root.TryGetProperty("final_audio_proc_ms", out var fa) && fa.ValueKind == JsonValueKind.Number) m.FinalAudioProcMs = fa.GetDouble();
        if (root.TryGetProperty("total_audio_proc_ms", out var ta) && ta.ValueKind == JsonValueKind.Number) m.TotalAudioProcMs = ta.GetDouble();
        if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tokens.EnumerateArray())
            {
                m.Tokens.Add(new SonioxToken
                {
                    Text = t.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "",
                    StartMs = t.TryGetProperty("start_ms", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null,
                    EndMs = t.TryGetProperty("end_ms", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null,
                    IsFinal = t.TryGetProperty("is_final", out var fin) && fin.ValueKind == JsonValueKind.True,
                    Speaker = t.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null,
                    Language = t.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String ? lg.GetString() : null,
                    IsTranslation = t.TryGetProperty("translation_status", out var ts) && ts.ValueKind == JsonValueKind.String && ts.GetString() == "translation",
                });
            }
        }
        return m;
    }

    /// <summary>Soniox error codes that retrying cannot fix: bad request, bad key, no credit.</summary>
    public static bool IsFatal(int code) => code is 400 or 401 or 402 or 403;

    public static string Describe(int code, string? message) => code switch
    {
        401 or 403 => "Soniox rejected the API key. Check the key (Session tab → Set API key).",
        402 => "Soniox account is out of credit. Add credit at console.soniox.com.",
        429 => "Soniox rate limit reached (too many sessions or requests).",
        400 => $"Soniox refused the settings: {message}",
        408 => "Soniox timed out waiting for audio.",
        _ => $"Soniox error {code}: {message}",
    };

    /// <summary>
    /// Turns the glossary into Soniox context: "keep as is" terms help recognition, "replace" rules become translation
    /// terms. The local glossary still runs on the text afterwards, so nothing changes if Soniox ignores a hint.
    /// </summary>
    public static (List<string> Terms, List<(string Source, string Target)> TranslationTerms) GlossaryContext(IEnumerable<GlossaryEntry> glossary)
    {
        var terms = new List<string>();
        var translations = new List<(string, string)>();
        foreach (var g in glossary.Where(g => g.Enabled && !string.IsNullOrWhiteSpace(g.Term)))
        {
            if (g.Mode == GlossaryMode.KeepAsIs) terms.Add(g.Term.Trim());
            else if (!string.IsNullOrWhiteSpace(g.Replacement)) translations.Add((g.Term.Trim(), g.Replacement.Trim()));
        }
        return (terms.Distinct().Take(200).ToList(), translations.Take(200).ToList());
    }
}

public sealed record SonioxSessionConfig
{
    public string Endpoint { get; init; } = SonioxProtocol.DefaultEndpoint;
    public string Model { get; init; } = SonioxProtocol.DefaultModel;
    public string OutputLanguage { get; init; } = "en";
    /// <summary>ISO 639-1 codes of the languages people are expected to speak (optional, improves accuracy).</summary>
    public IReadOnlyList<string> LanguageHints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Terms { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Source, string Target)> TranslationTerms { get; init; } = Array.Empty<(string, string)>();
    /// <summary>How long Soniox waits after speech before finalising it (500–3000 ms).</summary>
    public int MaxEndpointDelayMs { get; init; } = 1000;

    public static IReadOnlyList<string> ParseLanguageHints(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length == 2 && s.All(char.IsLetter)).Distinct().ToList();
}

public sealed class SonioxMessage
{
    public List<SonioxToken> Tokens { get; } = new();
    public double? FinalAudioProcMs { get; set; }
    public double? TotalAudioProcMs { get; set; }
    public bool Finished { get; set; }
    public int? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed record SonioxToken
{
    public string Text { get; init; } = "";
    public double? StartMs { get; init; }
    public double? EndMs { get; init; }
    public bool IsFinal { get; init; }
    public string? Speaker { get; init; }
    public string? Language { get; init; }
    /// <summary>translation_status == "translation" (otherwise the token is original speech).</summary>
    public bool IsTranslation { get; init; }
}
