namespace LiveSubtitles.Core.Tests;

internal static class TestPaths
{
    public static string Model(string name) => Path.Combine(AppContext.BaseDirectory, "models", name);
    public static string Audio(string name) => Path.Combine(AppContext.BaseDirectory, "testaudio", name);
    public static bool Has(string path) => File.Exists(path);
}
