namespace TurzxLite;

/// <summary>Read-only discovery of optional user-owned media tools and folders.</summary>
public static class AppPaths
{
    /// <summary>Resolved once; an empty string means the user should choose FFmpeg in the UI.</summary>
    public static string DefaultFfmpegPath { get; } = FindFfmpeg();

    public static string DefaultMediaDirectory
    {
        get
        {
            string videos = Folder(Environment.SpecialFolder.MyVideos);
            try { if (!string.IsNullOrEmpty(videos) && Directory.Exists(videos)) return videos; }
            catch (Exception ex) when (PathFailure(ex)) { }
            return Folder(Environment.SpecialFolder.MyDocuments);
        }
    }

    private static string FindFfmpeg()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string found = ExistingFile(baseDirectory, "ffmpeg.exe");
        if (found.Length != 0) return found;
        found = ExistingFile(baseDirectory, "FFmpeg", "ffmpeg.exe");
        if (found.Length != 0) return found;
        string documents = Folder(Environment.SpecialFolder.MyDocuments);
        if (documents.Length != 0)
        {
            found = ExistingFile(documents, "TURZX-V3.1.0-21inchENG", "ffmpeg.exe");
            if (found.Length != 0) return found;
        }
        string? searchPath;
        try { searchPath = Environment.GetEnvironmentVariable("PATH"); }
        catch (Exception ex) when (PathFailure(ex)) { return ""; }
        foreach (string entry in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = entry.Trim();
            if (directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"')
                directory = directory[1..^1].Trim();
            if (directory.Length == 0 || directory.Contains('"') || directory.Contains('\0')) continue;
            found = ExistingFile(directory, "ffmpeg.exe");
            if (found.Length != 0) return found;
        }
        return "";
    }

    private static string ExistingFile(params string[] components)
    {
        try
        {
            if (components.Any(string.IsNullOrWhiteSpace)) return "";
            string candidate = Path.GetFullPath(Path.Combine(components));
            return File.Exists(candidate) ? candidate : "";
        }
        catch (Exception ex) when (PathFailure(ex)) { return ""; }
    }

    private static string Folder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch (Exception ex) when (PathFailure(ex)) { return ""; }
    }

    private static bool PathFailure(Exception exception) => exception is
        ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
}
