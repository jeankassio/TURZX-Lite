using System.Diagnostics;

namespace TurzxLite;

/// <summary>
/// Streams an already converted 480x480 H.264 Annex-B file. Decoding and frame
/// timing run on the display; no FFmpeg process or repeated host rendering here.
/// </summary>
public static class NativeVideoPlayback
{
    /// <summary>
    /// Runs on a worker. The synchronous status callback also runs on that worker,
    /// after each packet and during queue waits, so the host can send a 1-Hz PNG
    /// overlay there without creating a competing USB loop. The host must throttle
    /// expensive callback work. This method always stops the decoder when it exits.
    /// </summary>
    public static Task RunAsync(TurzxUsb usb, string h264Path, int fps, Action<string> status, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(usb);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 60);
        return Task.Run(() => RunCoreAsync(usb, h264Path, fps, status, token));
    }

    private static async Task RunCoreAsync(TurzxUsb usb, string h264Path, int fps, Action<string> status, CancellationToken token)
    {
        await using var file = new FileStream(h264Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidDataException(Localization.T("O arquivo H.264 está vazio."));
        byte[] prefix = new byte[(int)Math.Min(file.Length, 64 * 1024)];
        await file.ReadExactlyAsync(prefix, token);
        ValidateAnnexB(prefix);
        file.Position = 0;
        token.ThrowIfCancellationRequested();

        bool started = false;
        long repetitions = 0;
        try
        {
            int chunkSize = usb.BeginVideo(fps);
            started = true;
            byte[] buffer = new byte[chunkSize];
            string Message() => Localization.Format("Vídeo nativo · {0} fps · decodificação na tela", fps);
            status(Message());
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = await file.ReadAsync(buffer.AsMemory(), token);
                if (read == 0)
                {
                    file.Position = 0;
                    repetitions++;
                    continue;
                }
                int queue = usb.SendVideoChunk(buffer.AsSpan(0, read), file.Position == file.Length);
                status(Message() + Localization.Format(" · repetição {0}", repetitions + 1));
                // Match the original sender: pause 30 ms after a chunk, then let
                // the decoder's reported queue apply backpressure, not a host FPS timer.
                await Task.Delay(30, token);
                if (queue <= 3) continue;
                var waiting = Stopwatch.StartNew();
                do
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(50, token);
                    queue = usb.GetVideoQueueDepth();
                    status(Message() + Localization.Format(" · buffer da tela {0}", queue));
                    // A compressed block can contain seconds of quiet video; the
                    // deadline is deliberately generous, while cancellation stays responsive.
                    if (waiting.Elapsed > TimeSpan.FromMinutes(2))
                        throw new TimeoutException(Localization.T("O decodificador da tela não liberou o buffer de vídeo. Pare e conecte novamente."));
                } while (queue > 2);
            }
        }
        finally
        {
            if (started)
            {
                try { usb.StopVideo(); }
                catch (Exception ex) { AppLog.Write("Falha ao encerrar o vídeo nativo: " + ex.Message); }
            }
        }
    }

    /// <summary>Checks required parameter sets before any USB command is issued.</summary>
    internal static void ValidateAnnexB(ReadOnlySpan<byte> prefix)
    {
        bool startsWithCode = prefix.Length >= 5 && prefix[0] == 0 && prefix[1] == 0 &&
            (prefix[2] == 1 || (prefix[2] == 0 && prefix[3] == 1));
        bool sps = false, pps = false;
        for (int i = 0; i + 4 < prefix.Length; i++)
        {
            if (prefix[i] != 0 || prefix[i + 1] != 0) continue;
            int codeLength = prefix[i + 2] == 1 ? 3 : prefix[i + 2] == 0 && prefix[i + 3] == 1 ? 4 : 0;
            if (codeLength == 0) continue;
            byte type = (byte)(prefix[i + codeLength] & 31);
            sps |= type == 7;
            pps |= type == 8;
            if (sps && pps) break;
            i += codeLength;
        }
        if (!startsWithCode || !sps || !pps)
            throw new InvalidDataException(Localization.T("O vídeo precisa ser H.264 Annex-B com SPS/PPS. Prepare o arquivo no cache antes de reproduzir."));
    }
}
