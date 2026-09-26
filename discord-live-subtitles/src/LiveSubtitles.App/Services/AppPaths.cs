namespace LiveSubtitles.App.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveSubtitles");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string VoicesFile => Path.Combine(DataDir, "voices.json");
    public static string TestAudioDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "LiveSubtitles test audio");
    public static string ModelsDir => Path.Combine(AppContext.BaseDirectory, "models");
    public static string VadModel => Path.Combine(ModelsDir, "silero_vad.onnx");
    public static string SpeakerModel => Path.Combine(ModelsDir, "campplus_voxceleb_16k.onnx");
}
