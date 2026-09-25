using System.Text;
using System.Text.Json;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void SessionUpdateMatchesDocumentedShape()
    {
        var json = Encoding.UTF8.GetString(TranslationProtocol.BuildSessionUpdate(new TranslationSessionConfig { NoiseReduction = "near_field" }));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("session.update", r.GetProperty("type").GetString());
        var audio = r.GetProperty("session").GetProperty("audio");
        Assert.Equal("en", audio.GetProperty("output").GetProperty("language").GetString());
        Assert.Equal("gpt-realtime-whisper", audio.GetProperty("input").GetProperty("transcription").GetProperty("model").GetString());
        Assert.Equal("near_field", audio.GetProperty("input").GetProperty("noise_reduction").GetProperty("type").GetString());
        // model is chosen in the URL and cannot be changed by session.update
        Assert.False(r.GetProperty("session").TryGetProperty("model", out _));
    }

    [Fact]
    public void DisabledOptionsAreSentAsNull()
    {
        var json = Encoding.UTF8.GetString(TranslationProtocol.BuildSessionUpdate(new TranslationSessionConfig { TranscriptionModel = null }));
        using var doc = JsonDocument.Parse(json);
        var input = doc.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input");
        Assert.Equal(JsonValueKind.Null, input.GetProperty("transcription").ValueKind);
        Assert.Equal(JsonValueKind.Null, input.GetProperty("noise_reduction").ValueKind);
    }

    [Fact]
    public void AppendIsBase64Pcm()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        using var doc = JsonDocument.Parse(TranslationProtocol.BuildAppend(pcm));
        Assert.Equal("session.input_audio_buffer.append", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(pcm, Convert.FromBase64String(doc.RootElement.GetProperty("audio").GetString()!));
    }

    [Fact]
    public void ParsesDeltaErrorAndSession()
    {
        var now = DateTimeOffset.UtcNow;
        var d = TranslationProtocol.Parse("""{"type":"session.output_transcript.delta","event_id":"e1","delta":" hello","elapsed_ms":1400}"""u8, now);
        Assert.Equal(" hello", d.Delta);
        Assert.Equal(1400, d.ElapsedMs);
        var noElapsed = TranslationProtocol.Parse("""{"type":"session.input_transcript.delta","event_id":"e2","delta":"merhaba","elapsed_ms":null}"""u8, now);
        Assert.Null(noElapsed.ElapsedMs);
        var e = TranslationProtocol.Parse("""{"type":"error","error":{"type":"invalid_request_error","code":"bad_audio","message":"nope"}}"""u8, now);
        Assert.Equal("bad_audio", e.ErrorCode);
        var s = TranslationProtocol.Parse("""{"type":"session.created","session":{"id":"sess_1","type":"translation","model":"gpt-realtime-translate","expires_at":1900000000,"audio":{}}}"""u8, now);
        Assert.Equal("sess_1", s.SessionId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900000000), s.ExpiresAt);
    }
}
