using System.Net;
using System.Net.Http.Headers;

namespace LiveSubtitles.Core.Translation;

public static class ApiKeyValidator
{
    /// <summary>Checks the key by retrieving the translate model's metadata (free, no audio sent).</summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(string apiKey, string model, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.openai.com/v1/models/{Uri.EscapeDataString(model)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => (true, $"Key works and has access to {model}."),
                HttpStatusCode.Unauthorized => (false, "OpenAI rejected this key (401). Copy it again from platform.openai.com."),
                HttpStatusCode.NotFound => (false, $"Key is valid but {model} is not available to this project (404)."),
                HttpStatusCode.Forbidden => (false, "Key is valid but this project is not allowed to use the model (403)."),
                HttpStatusCode.TooManyRequests => (false, "Rate limited or no credit (429). Check billing at platform.openai.com."),
                _ => (false, $"Unexpected response: HTTP {(int)resp.StatusCode}."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "Could not reach api.openai.com: " + ex.Message);
        }
    }
}
