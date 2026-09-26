using System.Text.RegularExpressions;

namespace LiveSubtitles.Core.Text;

/// <summary>
/// Tiny local check for "is this transcript English?", used to show English speakers' exact words instead of the
/// model's output (which the live test showed is sometimes empty and sometimes a paraphrase for English speech).
/// Letters used by Turkish and the Nordic languages rule English out; otherwise common English function words must
/// make up a good share of the words. Words that also exist in Norwegian ("i", "for", "at", "to", "her") are not counted.
/// </summary>
public static class LanguageGuess
{
    private static readonly Regex Words = new(@"[\p{L}']+", RegexOptions.Compiled);
    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "is", "are", "was", "were", "be", "been", "am", "you", "he", "she", "it", "we",
        "they", "me", "my", "your", "our", "their", "his", "this", "that", "these", "those", "of", "in", "on", "with",
        "from", "by", "about", "as", "if", "not", "no", "yes", "yeah", "do", "does", "did", "don't", "can", "can't", "will",
        "would", "should", "could", "have", "has", "had", "what", "who", "where", "when", "why", "how", "there", "here",
        "just", "like", "get", "got", "going", "gonna", "know", "think", "i'm", "it's", "that's", "you're", "we're", "let's",
        "i'll", "i've", "okay", "ok", "oh", "hey", "all", "some", "more", "up", "out", "then", "now", "too", "very",
        "really", "see", "want", "wanna", "need", "sure", "right", "good", "cool", "nice", "thanks", "thank", "lol", "so",
        "sounds", "maybe", "still", "though", "one", "which", "because", "also", "time", "tonight", "today", "anyone",
        // short replies seen as blank subtitles in the live test
        "awesome", "totally", "perfect", "sweet", "great", "exactly", "definitely", "absolutely", "yep", "nope", "alright",
        "please", "sorry", "wait", "ready", "fine", "haha", "bro", "dude", "around", "snacks", "anyway", "probably",
    };

    public static bool HasNonEnglishLetters(string text) => text.Any(c => "çğıöşüÇĞİÖŞÜæøåÆØÅäÄ".Contains(c));

    /// <summary>Number of common English words in the text.</summary>
    public static int EnglishWordCount(string text) =>
        Words.Matches(text).Count(m => English.Contains(m.Value.ToLowerInvariant().Replace('’', '\'')));

    public static bool LooksEnglish(string text)
    {
        if (HasNonEnglishLetters(text)) return false;
        var words = Words.Matches(text).Select(m => m.Value.ToLowerInvariant().Replace('’', '\'')).ToList();
        if (words.Count == 0) return false;
        int hits = words.Count(w => English.Contains(w));
        if (words.Count <= 3) return hits >= 2 || (words.Count == 1 && hits == 1); // "Sweet." alone is too little to tell
        return hits >= 2 && hits >= words.Count * 0.25;
    }
}
