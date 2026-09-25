using System.Text.RegularExpressions;
using LiveSubtitles.Core.Settings;

namespace LiveSubtitles.Core.Text;

/// <summary>
/// Applies the user's glossary to subtitle text. gpt-realtime-translate does not accept prompts or glossaries,
/// so terms are fixed up locally after translation:
/// <list type="bullet">
/// <item>Replace rules: whole-word/phrase replacement ("abi" → "bro", "GG WP" → "good game, well played").</item>
/// <item>Keep-as-is names: listed aliases ("Emir, Emra" → "Emre") and capitalised near-miss spellings
/// (one letter off for names up to 7 letters, two for longer ones; same first letter) are corrected to the exact term.</item>
/// </list>
/// </summary>
public sealed class GlossaryProcessor : ITextPostProcessor
{
    private readonly List<(Regex Pattern, string Replacement, bool Original, bool RequireCapital)> _replacements = new();
    private readonly List<(string Term, bool Original)> _keepTerms = new();
    private static readonly Regex WordRegex = new(@"\p{L}[\p{L}\p{M}'’-]*", RegexOptions.Compiled);

    public GlossaryProcessor(IEnumerable<GlossaryEntry> entries)
    {
        var pairs = new List<(string Phrase, string Replacement, RegexOptions Options, bool Original, bool RequireCapital)>();
        foreach (var e in entries.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Term)))
        {
            var options = e.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            string term = e.Term.Trim();
            if (e.Mode == GlossaryMode.Replace)
            {
                pairs.Add((term, e.Replacement ?? "", options, e.ApplyToOriginal, false));
                foreach (var alias in SplitAliases(e.Aliases))
                    pairs.Add((alias, e.Replacement ?? "", options, e.ApplyToOriginal, false));
            }
            else
            {
                // Names (capitalised terms) only replace capitalised aliases, so common words are left alone.
                bool isName = char.IsUpper(term[0]);
                foreach (var alias in SplitAliases(e.Aliases))
                    pairs.Add((alias, term, options, e.ApplyToOriginal, isName));
                // With case-sensitive on, other casings are normalised to the exact spelling.
                if (e.CaseSensitive) pairs.Add((term, term, RegexOptions.IgnoreCase, e.ApplyToOriginal, false));
                _keepTerms.Add((term, e.ApplyToOriginal));
            }
        }
        // Longest phrases first so "big brother" wins over "brother".
        foreach (var p in pairs.OrderByDescending(p => p.Phrase.Length))
            _replacements.Add((WholeWord(p.Phrase, p.Options), p.Replacement, p.Original, p.RequireCapital));
    }

    public bool IsEmpty => _replacements.Count == 0 && _keepTerms.Count == 0;

    public string ProcessTranslation(string text) => Apply(text, original: false);
    public string ProcessOriginal(string text) => Apply(text, original: true);

    private string Apply(string text, bool original)
    {
        if (text.Length == 0) return text;
        foreach (var (pattern, replacement, forOriginal, requireCapital) in _replacements)
        {
            if (original && !forOriginal) continue;
            text = pattern.Replace(text, m => requireCapital && !char.IsUpper(m.Value[0]) ? m.Value : MatchCase(m.Value, replacement));
        }
        var terms = _keepTerms.Where(t => !original || t.Original).Select(t => t.Term).ToList();
        if (terms.Count == 0) return text;
        return WordRegex.Replace(text, m =>
        {
            var word = m.Value;
            if (word.Length < 3 || !char.IsUpper(word[0])) return word; // only fix capitalised words (names)
            foreach (var term in terms)
            {
                if (term.Contains(' ')) continue;
                if (string.Equals(word, term, StringComparison.Ordinal)) return word;
                if (char.ToLowerInvariant(word[0]) != char.ToLowerInvariant(term[0])) continue;
                int maxDist = term.Length <= 7 ? 1 : 2;
                if (Math.Abs(word.Length - term.Length) > maxDist) continue;
                if (Distance(word.ToLowerInvariant(), term.ToLowerInvariant()) <= maxDist) return term;
            }
            return word;
        });
    }

    private static IEnumerable<string> SplitAliases(string? aliases) =>
        (aliases ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Regex WholeWord(string phrase, RegexOptions options) =>
        new(@"(?<![\p{L}\p{N}])" + Regex.Escape(phrase) + @"(?![\p{L}\p{N}])", options | RegexOptions.CultureInvariant);

    /// <summary>Keep sentence-initial capitalisation when replacing a lowercase glossary value.</summary>
    private static string MatchCase(string matched, string replacement)
    {
        if (replacement.Length == 0 || matched.Length == 0) return replacement;
        if (char.IsUpper(matched[0]) && char.IsLower(replacement[0]))
            return char.ToUpper(replacement[0]) + replacement[1..];
        return replacement;
    }

    /// <summary>Optimal string alignment (Damerau-Levenshtein) distance.</summary>
    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[a.Length, b.Length];
    }
}
