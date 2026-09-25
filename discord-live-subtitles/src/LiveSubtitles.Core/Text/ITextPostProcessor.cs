namespace LiveSubtitles.Core.Text;

/// <summary>Local clean-up applied to subtitle text (glossary rules, name fixes).</summary>
public interface ITextPostProcessor
{
    string ProcessTranslation(string text);
    string ProcessOriginal(string text);
}

public sealed class NoTextPostProcessor : ITextPostProcessor
{
    public static readonly NoTextPostProcessor Instance = new();
    public string ProcessTranslation(string text) => text;
    public string ProcessOriginal(string text) => text;
}
