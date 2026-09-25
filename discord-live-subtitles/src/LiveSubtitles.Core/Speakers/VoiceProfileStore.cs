using System.Text.Json;

namespace LiveSubtitles.Core.Speakers;

/// <summary>A remembered voice: a name and a 512-number voice embedding. No audio is ever stored.</summary>
public sealed class VoiceProfile
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#FFFFFF";
    public bool Muted { get; set; }
    public float[] Centroid { get; set; } = Array.Empty<float>();
    public double WeightSeconds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Persists named voice profiles in %APPDATA%\LiveSubtitles\voices.json.</summary>
public sealed class VoiceProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;
    private readonly object _lock = new();

    public VoiceProfileStore(string path) => _path = path;

    public IReadOnlyList<VoiceProfile> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return Array.Empty<VoiceProfile>();
                return JsonSerializer.Deserialize<List<VoiceProfile>>(File.ReadAllText(_path), Json) ?? new List<VoiceProfile>();
            }
            catch
            {
                return Array.Empty<VoiceProfile>();
            }
        }
    }

    public void Upsert(VoiceProfile profile)
    {
        lock (_lock)
        {
            var all = Load().Where(p => p.Key != profile.Key).ToList();
            all.Add(profile);
            Write(all);
        }
    }

    public void Delete(string key)
    {
        lock (_lock) Write(Load().Where(p => p.Key != key).ToList());
    }

    public void DeleteAll()
    {
        lock (_lock)
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }

    private void Write(List<VoiceProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profiles, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
