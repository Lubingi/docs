using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Text;

namespace LiveSubtitles.App.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<GlossaryEntry> Glossary { get; } = new();
    public IReadOnlyList<GlossaryMode> GlossaryModes { get; } = Enum.GetValues<GlossaryMode>();

    private void InitGlossary()
    {
        foreach (var e in Settings.Glossary) Glossary.Add(e);
        if (Glossary.Count == 0)
        {
            // A couple of disabled examples so the table explains itself.
            Glossary.Add(new GlossaryEntry { Mode = GlossaryMode.KeepAsIs, Term = "Emre", Aliases = "Emir, Emrah", Enabled = false });
            Glossary.Add(new GlossaryEntry { Mode = GlossaryMode.Replace, Term = "brother", Replacement = "abi", Aliases = "big brother", ApplyToOriginal = false, Enabled = false });
        }
        Glossary.CollectionChanged += (_, _) => GlossaryEdited();
    }

    private ITextPostProcessor CreateTextProcessor() => new GlossaryProcessor(Settings.Glossary);

    /// <summary>Called after any glossary edit: saves and applies the new rules to the running session immediately.</summary>
    public void GlossaryEdited()
    {
        Settings.Glossary = Glossary.Where(e => !string.IsNullOrWhiteSpace(e.Term)).ToList();
        SaveSettings();
        Session.SetTextProcessor(CreateTextProcessor());
    }

    [RelayCommand]
    private void AddGlossaryEntry() => Glossary.Add(new GlossaryEntry());

    [RelayCommand]
    private void RemoveGlossaryEntry(GlossaryEntry? entry)
    {
        if (entry != null) Glossary.Remove(entry);
    }
}
