using System.Buffers;
using System.Text.Json;

namespace LiveSubtitles.Core.Translation;

/// <summary>
/// Wire protocol for OpenAI Realtime Translation (gpt-realtime-translate), WebSocket flavour.
/// Endpoint: wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate
/// Client events: session.update, session.input_audio_buffer.append, session.close.
/// Server events: session.created, session.updated, session.closed, session.input_transcript.delta,
/// session.output_transcript.delta, session.output_audio.delta, error.
/// Audio in: base64 24 kHz mono PCM16 little-endian, ideally in 200 ms chunks.
/// </summary>
public static class TranslationProtocol
{
    public const string DefaultEndpoint = "wss://api.openai.com/v1/realtime/translations";
    public const string DefaultModel = "gpt-realtime-translate";
    public const string DefaultTranscriptionModel = "gpt-realtime-whisper";
    public const int SampleRate = 24000;
    public const int ChunkMs = 200;
    public const int ChunkSamples = SampleRate * ChunkMs / 1000; // 4800

    /// <summary>The 13 output languages the model currently supports.</summary>
    public static readonly IReadOnlyDictionary<string, string> OutputLanguages = new Dictionary<string, string>
    {
        ["en"] = "English", ["es"] = "Spanish", ["pt"] = "Portuguese", ["fr"] = "French", ["ja"] = "Japanese",
        ["ru"] = "Russian", ["zh"] = "Chinese", ["de"] = "German", ["ko"] = "Korean", ["hi"] = "Hindi",
        ["id"] = "Indonesian", ["vi"] = "Vietnamese", ["it"] = "Italian",
    };

    public static Uri BuildUri(string endpoint, string model) =>
        new($"{endpoint.TrimEnd('/')}?model={Uri.EscapeDataString(model)}");

    public static byte[] BuildSessionUpdate(TranslationSessionConfig config)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "session.update");
            w.WriteStartObject("session");
            w.WriteStartObject("audio");
            w.WriteStartObject("input");
            if (string.IsNullOrEmpty(config.TranscriptionModel)) w.WriteNull("transcription");
            else
            {
                w.WriteStartObject("transcription");
                w.WriteString("model", config.TranscriptionModel);
                w.WriteEndObject();
            }
            if (string.IsNullOrEmpty(config.NoiseReduction)) w.WriteNull("noise_reduction");
            else
            {
                w.WriteStartObject("noise_reduction");
                w.WriteString("type", config.NoiseReduction);
                w.WriteEndObject();
            }
            w.WriteEndObject(); // input
            w.WriteStartObject("output");
            w.WriteString("language", config.OutputLanguage);
            w.WriteEndObject(); // output
            w.WriteEndObject(); // audio
            w.WriteEndObject(); // session
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] BuildAppend(ReadOnlySpan<byte> pcm16)
    {
        var buffer = new ArrayBufferWriter<byte>(pcm16.Length * 4 / 3 + 96);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "session.input_audio_buffer.append");
            w.WriteBase64String("audio", pcm16);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] BuildClose() => """{"type":"session.close"}"""u8.ToArray();

    public static TranslationServerEvent Parse(ReadOnlySpan<byte> json, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var ev = new TranslationServerEvent { Type = type, ReceivedAt = receivedAt };
        if (root.TryGetProperty("event_id", out var id) && id.ValueKind == JsonValueKind.String) ev.EventId = id.GetString();
        if (root.TryGetProperty("elapsed_ms", out var el) && el.ValueKind == JsonValueKind.Number) ev.ElapsedMs = el.GetDouble();
        switch (type)
        {
            case "session.input_transcript.delta":
            case "session.output_transcript.delta":
                ev.Delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                break;
            case "session.output_audio.delta":
                // Translated speech is not used (subtitles only). Record the size so it can be shown in debug stats.
                if (root.TryGetProperty("delta", out var a) && a.ValueKind == JsonValueKind.String)
                    ev.AudioBytes = (a.GetString()?.Length ?? 0) * 3 / 4;
                break;
            case "session.created":
            case "session.updated":
                if (root.TryGetProperty("session", out var s))
                {
                    ev.SessionJson = s.GetRawText();
                    if (s.TryGetProperty("id", out var sid)) ev.SessionId = sid.GetString();
                    if (s.TryGetProperty("expires_at", out var exp) && exp.ValueKind == JsonValueKind.Number)
                        ev.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
                }
                break;
            case "error":
                if (root.TryGetProperty("error", out var e))
                {
                    ev.ErrorType = e.TryGetProperty("type", out var et) ? et.GetString() : null;
                    ev.ErrorCode = e.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() : null;
                    ev.ErrorMessage = e.TryGetProperty("message", out var em) ? em.GetString() : null;
                }
                break;
        }
        return ev;
    }
}

public sealed record TranslationSessionConfig
{
    public string Endpoint { get; init; } = TranslationProtocol.DefaultEndpoint;
    public string Model { get; init; } = TranslationProtocol.DefaultModel;
    public string OutputLanguage { get; init; } = "en";
    /// <summary>Source-language transcript model, or null to disable original-language text.</summary>
    public string? TranscriptionModel { get; init; } = TranslationProtocol.DefaultTranscriptionModel;
    /// <summary>"near_field", "far_field", or null for off.</summary>
    public string? NoiseReduction { get; init; }
}

public sealed class TranslationServerEvent
{
    public string Type { get; init; } = "";
    public string? EventId { get; set; }
    public DateTimeOffset ReceivedAt { get; init; }
    public double? ElapsedMs { get; set; }
    public string? Delta { get; set; }
    public int AudioBytes { get; set; }
    public string? SessionJson { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
