using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TurzxLite;

public static class VideoCache
{
    private const long MaxVideoBytes = 512L * 1024 * 1024;
    public static async Task<string> PrepareAsync(string executable, string input, int fps, int rotation, Action<string> status, CancellationToken token)
    {
        if (!File.Exists(input)) throw new FileNotFoundException(Localization.T("Escolha um vídeo existente."), input);
        if (!File.Exists(executable)) throw new FileNotFoundException(Localization.T("O decodificador de vídeo não foi encontrado. Selecione ffmpeg.exe."), executable);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 60);
        var source = new FileInfo(input);
        string fingerprint = $"v1|{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{fps}|{rotation}";
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..32];
        string directory = Path.Combine(AppSettings.DirectoryPath, "VideoCache");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, name + ".h264");
        if (File.Exists(destination) && new FileInfo(destination).Length > 32)
        {
            File.SetLastAccessTimeUtc(destination, DateTime.UtcNow);
            return destination;
        }
        Prune(directory);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string? error = null;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        string rotate = rotation switch { 90 => ",transpose=1", 180 => ",hflip,vflip", 270 => ",transpose=2", _ => "" };
        string filter = $"scale=480:480:force_original_aspect_ratio=increase,crop=480:480,setsar=1{rotate},fps={fps}";
        string[] args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-filter_threads", "1", "-i", Path.GetFullPath(input), "-an", "-sn", "-dn", "-vf", filter,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "baseline", "-level:v", "3.1", "-pix_fmt", "yuv420p", "-bf", "0", "-g", fps.ToString(), "-keyint_min", fps.ToString(), "-sc_threshold", "0", "-maxrate", "6M", "-bufsize", "8M", "-x264-params", "repeat-headers=1:aud=1:force-cfr=1", "-threads", "1", "-f", "h264", "-y", temporary];
        foreach (string arg in args) start.ArgumentList.Add(arg);
        status(Localization.T("Preparando o vídeo para a tela… Esta conversão é feita uma vez por arquivo e configuração."));
        using var process = Process.Start(start) ?? throw new IOException(Localization.T("Não foi possível preparar o vídeo."));
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch { } }
        using var registration = token.Register(Kill);
        var errors = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync() is string line) error = line.Length > 2048 ? line[..2048] : line; } catch { }
        });
        try
        {
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(temporary) && new FileInfo(temporary).Length > MaxVideoBytes)
                    throw new IOException(Localization.T("O vídeo preparado ultrapassou 512 MB. Selecione um clipe mais curto."));
                await Task.Delay(300, token);
            }
            await errors;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length < 32)
                throw new IOException(Localization.Format("Não foi possível preparar esse vídeo. {0}", error));
            if (new FileInfo(temporary).Length > MaxVideoBytes) throw new IOException(Localization.T("O vídeo preparado ultrapassou 512 MB. Selecione um clipe mais curto."));
            File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            Kill();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
    private static void Prune(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("*.h264").OrderBy(x => x.LastAccessTimeUtc).ToArray();
        long total = files.Sum(x => x.Length);
        foreach (var file in files)
        {
            if (total < 512L * 1024 * 1024) break;
            try { file.Delete(); total -= file.Length; } catch { }
        }
    }
}
