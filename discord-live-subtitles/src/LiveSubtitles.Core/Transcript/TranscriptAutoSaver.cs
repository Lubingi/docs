using System.Globalization;

namespace LiveSubtitles.Core.Transcript;

/// <summary>Opt-in: appends each final subtitle line to a text file as it happens. Off by default.</summary>
public sealed class TranscriptAutoSaver : IDisposable
{
    private readonly TranscriptStore _store;
    private readonly HashSet<int> _written = new();
    private readonly object _lock = new();

    public TranscriptAutoSaver(TranscriptStore store, string folder)
    {
        _store = store;
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        store.LineUpdated += OnLine;
    }

    public string FilePath { get; }

    private void OnLine(TranscriptLine l)
    {
        if (!l.IsFinal || l.Hidden || (!l.HasText && l.Original.Length == 0)) return;
        lock (_lock)
        {
            if (!_written.Add(l.SegmentId)) return;
            try { File.AppendAllText(FilePath, TranscriptExporter.ToText(new[] { l })); }
            catch { /* disk full / folder removed: never break the live session */ }
        }
    }

    public void Dispose() => _store.LineUpdated -= OnLine;
}
