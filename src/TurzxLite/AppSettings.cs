using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurzxLite;

public sealed class AppSettings
{
    public string Mode { get; set; } = "Painel";
    public string MediaPath { get; set; } = "";
    public int Brightness { get; set; } = 70;
    public int Rotation { get; set; } = 0;
    public int VideoFps { get; set; } = 30;
    public bool DirectCpuTemperature { get; set; }
    public string Language { get; set; } = "pt-BR";
    public int SettingsVersion { get; set; } = 3;
    public ScreenLayout Layout { get; set; } = ScreenLayout.CreateDefault();
    public bool Overlay { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool AutoConnect { get; set; }
    public string FfmpegPath { get; set; } = AppPaths.DefaultFfmpegPath;
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurzxLite");
    public static AppSettings Load()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "settings.json"))) as JsonObject
                ?? throw new InvalidDataException("Configurações inválidas.");
            // A damaged layout must not discard the user's selected media or USB preferences.
            var layoutNode = root["Layout"]?.DeepClone();
            root.Remove("Layout");
            var s = root.Deserialize<AppSettings>() ?? new();
            s.Language = s.Language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "pt-BR";
            s.Brightness = Math.Clamp(s.Brightness, 0, 100);
            if (s.VideoFps <= 5) s.VideoFps = 30;
            s.VideoFps = Math.Clamp(s.VideoFps, 1, 60);
            if (s.Rotation is not (0 or 90 or 180 or 270)) s.Rotation = 0;
            if (s.Mode is not ("Painel" or "Imagem" or "Vídeo")) s.Mode = "Painel";
            ScreenLayout? savedLayout = null;
            try { savedLayout = layoutNode?.Deserialize<ScreenLayout>(); } catch (JsonException) { }
            s.Layout = savedLayout ?? (s.Mode == "Painel" ? ScreenLayout.CreateDefault() : ScreenLayout.CreateCompact());
            s.Layout.Normalize();
            s.SettingsVersion = 3;
            return s;
        }
        catch { return new(); }
    }
    public AppSettings Snapshot() => new()
    {
        Mode = Mode, MediaPath = MediaPath, Brightness = Brightness, Rotation = Rotation,
        VideoFps = VideoFps, DirectCpuTemperature = DirectCpuTemperature, Language = Language, SettingsVersion = SettingsVersion,
        Overlay = Overlay, StartMinimized = StartMinimized, AutoConnect = AutoConnect,
        FfmpegPath = FfmpegPath, Layout = Layout.Clone()
    };
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

public static class AppLog
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DirectoryPath);
                string path = Path.Combine(AppSettings.DirectoryPath, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
