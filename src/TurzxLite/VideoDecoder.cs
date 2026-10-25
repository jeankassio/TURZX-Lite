using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TurzxLite;

public sealed class VideoDecoder : IDisposable
{
    private readonly Process process;
    private readonly CancellationTokenRegistration registration;
    private readonly byte[] buffer = new byte[480 * 480 * 3];
    private readonly Task errorReader;
    private string error = "";
    public TimeSpan CpuTime { get { try { process.Refresh(); return process.TotalProcessorTime; } catch { return TimeSpan.Zero; } } }
    public long WorkingSetBytes { get { try { process.Refresh(); return process.WorkingSet64; } catch { return 0; } } }
    public VideoDecoder(string executable, string file, int fps, CancellationToken token)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException(Localization.T("FFmpeg não encontrado. Selecione ffmpeg.exe nas opções de vídeo."), executable);
        if (!File.Exists(file)) throw new FileNotFoundException(Localization.T("Escolha um arquivo de vídeo existente."), file);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-filter_threads", "1", "-stream_loop", "-1", "-re", "-i", Path.GetFullPath(file), "-an", "-sn", "-dn", "-vf", $"fps={Math.Clamp(fps, 1, 5)},scale=480:480:force_original_aspect_ratio=increase,crop=480:480", "-pix_fmt", "bgr24", "-threads", "1", "-f", "rawvideo", "pipe:1"];
        foreach (string arg in args) info.ArgumentList.Add(arg);
        process = Process.Start(info) ?? throw new InvalidOperationException(Localization.T("Não foi possível iniciar FFmpeg."));
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        registration = token.Register(Kill);
        errorReader = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync() is string line) { error = line.Length > 2048 ? line[..2048] : line; } } catch { }
        });
    }
    public async Task<Bitmap> ReadAsync(CancellationToken token)
    {
        try { await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer, token); }
        catch (EndOfStreamException) { throw new IOException(Localization.Format("O vídeo não pôde ser decodificado. {0}", error)); }
        var frame = new Bitmap(480, 480, PixelFormat.Format24bppRgb);
        var bits = frame.LockBits(new Rectangle(0, 0, 480, 480), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try { Marshal.Copy(buffer, 0, bits.Scan0, buffer.Length); }
        finally { frame.UnlockBits(bits); }
        return frame;
    }
    private void Kill() { try { if (!process.HasExited) process.Kill(true); } catch { } }
    public void Dispose()
    {
        registration.Dispose();
        Kill();
        try { process.WaitForExit(1500); } catch { }
        process.Dispose();
    }
}
