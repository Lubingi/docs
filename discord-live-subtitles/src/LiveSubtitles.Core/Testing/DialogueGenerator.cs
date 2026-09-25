using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiveSubtitles.Core.Audio;

namespace LiveSubtitles.Core.Testing;

public sealed record DialogueLine(int Speaker, string Text);

public sealed record DialogueRequest
{
    public string Language { get; init; } = "Turkish";
    public int Speakers { get; init; } = 3;
    public int Lines { get; init; } = 10;
    public string Topic { get; init; } = "planning tonight's game session";
    /// <summary>Write a fresh script with a chat model instead of using the built-in one.</summary>
    public bool GenerateScript { get; init; }
    public string ScriptModel { get; init; } = "gpt-5-mini";
    public string TtsModel { get; init; } = "gpt-4o-mini-tts";
}

public sealed record DialogueResult(string AudioPath, string ScriptPath, IReadOnlyList<DialogueLine> Script, IReadOnlyList<string> Voices);

/// <summary>
/// Test mode: builds a short fake voice-chat conversation (2–4 speakers with different OpenAI TTS voices) in a
/// chosen language and saves it as a WAV file, plus a .txt with who said what (the ground truth for checking
/// speaker labels). Nothing about real people is involved; the files are only created when you ask.
/// </summary>
public sealed class DialogueGenerator
{
    /// <summary>Contrasting built-in voices: two lower, two higher.</summary>
    public static readonly string[] Voices = { "cedar", "marin", "ash", "coral" };

    private static readonly string[] Personas =
    {
        "a relaxed man in his twenties, slightly low voice",
        "an upbeat woman, speaks quickly",
        "a calm man, a bit tired, speaks slowly",
        "a cheerful woman, expressive",
    };

    public static readonly IReadOnlyDictionary<string, DialogueLine[]> BuiltInScripts = new Dictionary<string, DialogueLine[]>
    {
        ["Turkish"] = new DialogueLine[]
        {
            new(0, "Selam millet, beni duyabiliyor musunuz?"),
            new(1, "Evet, sesin geliyor ama biraz cızırtılı."),
            new(0, "Tamam, mikrofonu değiştirdim. Şimdi nasıl?"),
            new(2, "Şimdi çok daha iyi. Bu akşam kaçta başlıyoruz?"),
            new(1, "Bence dokuzda başlayalım, ben yemekten sonra hazır olurum."),
            new(3, "Ben biraz geç kalabilirim, işten çıkınca hemen bağlanırım."),
            new(0, "Sorun değil Elif, ilk maçı biz üçümüz oynarız."),
            new(2, "Ama geçen haftaki gibi yine kaybetmeyelim, çok sinir bozucuydu."),
            new(1, "O maçta herkes farklı yere koştu. Bu sefer önceden plan yapalım."),
            new(3, "Tamam, ben gelince destek oynarım, siz saldırıya geçin."),
            new(0, "Anlaştık. O zaman saat dokuzda burada buluşuyoruz."),
            new(2, "Görüşürüz, ben şimdi bir kahve alıp geliyorum."),
        },
        ["Norwegian"] = new DialogueLine[]
        {
            new(0, "Hei alle sammen, hører dere meg?"),
            new(1, "Ja, vi hører deg fint. Hvordan gikk det på jobb i dag?"),
            new(0, "Helt greit, men jeg er skikkelig sliten. Skal vi spille litt i kveld?"),
            new(2, "Jeg er med, men jeg må spise middag først."),
            new(1, "Samme her. Hva med å starte klokka åtte?"),
            new(3, "Åtte passer bra for meg. Jeg har endelig fikset det nye headsettet."),
            new(0, "Så bra! Forrige gang hørte vi deg nesten ikke."),
            new(2, "Husker dere den kampen? Vi var så nære å vinne."),
            new(3, "Ja, det var helt vilt. Denne gangen må vi snakke mer sammen."),
            new(1, "Enig. Jeg kan følge med på kartet og si ifra."),
            new(0, "Perfekt. Da sees vi klokka åtte."),
            new(2, "Ha det bra så lenge!"),
        },
    };

    /// <summary>Languages offered in the UI (all are supported input languages of gpt-realtime-translate).</summary>
    public static readonly string[] Languages =
    {
        "Turkish", "Norwegian", "Norwegian Nynorsk", "Swedish", "Danish", "German", "Spanish", "French", "Italian",
        "Portuguese", "Polish", "Dutch", "Russian", "Ukrainian", "Arabic", "Persian", "Greek", "Japanese", "Korean",
        "Chinese", "Hindi", "Finnish", "English",
    };

    private readonly string _apiKey;
    private readonly HttpClient _http;

    public DialogueGenerator(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public async Task<DialogueResult> GenerateAsync(DialogueRequest request, string outputFolder, IProgress<string>? progress, CancellationToken ct)
    {
        int speakers = Math.Clamp(request.Speakers, 2, 4);
        IReadOnlyList<DialogueLine> script;
        if (request.GenerateScript || !BuiltInScripts.ContainsKey(request.Language))
        {
            progress?.Report($"Writing a {request.Language} script with {request.ScriptModel}…");
            script = await WriteScriptAsync(request with { Speakers = speakers }, ct).ConfigureAwait(false);
        }
        else
        {
            script = BuiltInScripts[request.Language]
                .Take(Math.Clamp(request.Lines, 4, 12))
                .Select(l => l with { Speaker = l.Speaker % speakers })
                .ToList();
        }

        const int rate = 24000; // TTS "pcm" output is 24 kHz 16-bit mono
        var audio = new List<float>(rate * 60);
        var rng = new Random();
        audio.AddRange(new float[rate / 2]);
        for (int i = 0; i < script.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var line = script[i];
            progress?.Report($"Speaking line {i + 1} of {script.Count} (voice {Voices[line.Speaker]})…");
            var pcm = await SpeakAsync(request, line, ct).ConfigureAwait(false);
            audio.AddRange(Pcm16.ToFloat(pcm));
            // Natural gaps between turns, sometimes short, sometimes a longer pause.
            int gapMs = i + 1 < script.Count && script[i + 1].Speaker == line.Speaker ? 350 : rng.Next(250, 900);
            audio.AddRange(new float[rate * gapMs / 1000]);
        }
        audio.AddRange(new float[rate]);

        Directory.CreateDirectory(outputFolder);
        string stem = $"dialogue-{Slug(request.Language)}-{speakers}sp-{DateTime.Now:yyyyMMdd-HHmmss}";
        string wavPath = Path.Combine(outputFolder, stem + ".wav");
        string txtPath = Path.Combine(outputFolder, stem + ".txt");
        WavFile.WriteMono16(wavPath, audio.ToArray(), rate);
        var sb = new StringBuilder();
        sb.AppendLine($"Test dialogue — {request.Language}, {speakers} speakers (generated with OpenAI TTS voices, not real people)");
        sb.AppendLine();
        foreach (var l in script) sb.AppendLine($"Speaker {l.Speaker + 1} ({Voices[l.Speaker]}): {l.Text}");
        await File.WriteAllTextAsync(txtPath, sb.ToString(), ct).ConfigureAwait(false);
        progress?.Report($"Saved {Path.GetFileName(wavPath)} ({audio.Count / (double)rate:0}s).");
        return new DialogueResult(wavPath, txtPath, script, Voices.Take(speakers).ToList());
    }

    private async Task<byte[]> SpeakAsync(DialogueRequest request, DialogueLine line, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = request.TtsModel,
            voice = Voices[line.Speaker],
            input = line.Text,
            instructions = $"Speak natural, casual {request.Language} with a native accent, like {Personas[line.Speaker]} chatting with friends on a voice call.",
            response_format = "pcm",
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Text-to-speech failed: {await ErrorText(resp).ConfigureAwait(false)}");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DialogueLine>> WriteScriptAsync(DialogueRequest request, CancellationToken ct)
    {
        string prompt =
            $"Write a short, casual voice-chat conversation between {request.Speakers} friends on Discord, entirely in {request.Language}. " +
            $"Topic: {request.Topic}. Exactly {Math.Clamp(request.Lines, 4, 20)} lines, one or two sentences each, everyday spoken style with a little slang, " +
            "include one or two first names. Speakers are numbered from 1. " +
            "Reply with JSON only: {\"lines\":[{\"speaker\":1,\"text\":\"...\"}]}";
        var body = JsonSerializer.Serialize(new
        {
            model = request.ScriptModel,
            messages = new object[]
            {
                new { role = "system", content = "You write realistic test dialogue for a speech translation app. Output valid JSON only." },
                new { role = "user", content = prompt },
            },
            response_format = new { type = "json_object" },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Script generation failed: {await ErrorText(resp).ConfigureAwait(false)}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return ParseScript(content, request.Speakers);
    }

    internal static IReadOnlyList<DialogueLine> ParseScript(string json, int speakers)
    {
        int start = json.IndexOf('{'), end = json.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model did not return a script.");
        using var doc = JsonDocument.Parse(json[start..(end + 1)]);
        var lines = new List<DialogueLine>();
        foreach (var l in doc.RootElement.GetProperty("lines").EnumerateArray())
        {
            int sp = l.TryGetProperty("speaker", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 1;
            var text = l.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
            if (text.Length > 0) lines.Add(new DialogueLine(Math.Clamp(sp - 1, 0, speakers - 1), text));
        }
        if (lines.Count == 0) throw new InvalidOperationException("The generated script was empty.");
        return lines;
    }

    private static async Task<string> ErrorText(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return $"HTTP {(int)resp.StatusCode}: {doc.RootElement.GetProperty("error").GetProperty("message").GetString()}";
        }
        catch
        {
            return $"HTTP {(int)resp.StatusCode}";
        }
    }

    private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
