using System.Diagnostics;

namespace TurzxLite;

public sealed class DisplaySession : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private Bitmap? latest;
    private SensorSnapshot? latestSensors;
    private ScreenLayout currentLayout = ScreenLayout.CreateDefault();
    private int layoutRevision;
    private int appliedLayoutRevision;
    private volatile bool overlayEnabled;
    public string Status { get; private set; } = Localization.T("Pronto para conectar");
    public string SensorHint { get; private set; } = "";
    public string Performance { get; private set; } = "";
    public bool Running => worker is { IsCompleted: false };
    public bool PreviewEnabled { get; set; } = true;
    public SensorSnapshot? LatestSensors => Volatile.Read(ref latestSensors);
    public int AppliedLayoutRevision => Volatile.Read(ref appliedLayoutRevision);
    public int UpdateLayout(ScreenLayout layout)
    {
        var snapshot = layout.Clone();
        snapshot.Normalize();
        Volatile.Write(ref currentLayout, snapshot);
        return Interlocked.Increment(ref layoutRevision);
    }
    public void SetOverlay(bool enabled) { overlayEnabled = enabled; Interlocked.Increment(ref layoutRevision); }
    public Bitmap? TakePreview() => Interlocked.Exchange(ref latest, null);
    private void Preview(Bitmap frame) => Interlocked.Exchange(ref latest, frame)?.Dispose();

    public void Start(AppSettings settings, DisplayDevice? device, bool previewOnly)
    {
        if (Running) throw new InvalidOperationException(Localization.T("Pare a sessão antes de aplicar as configurações."));
        settings = settings.Snapshot();
        UpdateLayout(settings.Layout);
        overlayEnabled = settings.Overlay;
        cancellation?.Dispose();
        cancellation = new();
        var token = cancellation.Token;
        Status = Localization.T("Iniciando…");
        worker = Task.Run(async () =>
        {
            try
            {
                using var renderer = new FrameRenderer();
                using var image = settings.Mode == "Imagem" ? FrameRenderer.LoadImage(settings.MediaPath) : null;
                if (settings.Mode == "Vídeo" && (!File.Exists(settings.MediaPath) || !File.Exists(settings.FfmpegPath)))
                    throw new FileNotFoundException(Localization.T("Selecione um vídeo existente e o arquivo ffmpeg.exe."));
                string? nativePath = settings.Mode == "Vídeo" && !previewOnly
                    ? await VideoCache.PrepareAsync(settings.FfmpegPath, settings.MediaPath, settings.VideoFps, settings.Rotation, message => Status = message, token)
                    : null;
                using var usb = previewOnly ? null : new TurzxUsb();
                if (usb != null)
                {
                    if (TurzxUsb.OriginalIsRunning()) throw new InvalidOperationException(Localization.T("Feche o programa TURZX original antes de conectar."));
                    usb.Connect(device ?? throw new InvalidOperationException(Localization.T("Selecione uma tela TURZX.")), settings.Brightness);
                }
                using var telemetry = new Telemetry();
                // The original sends a transparent PNG after command 52. Clear
                // the video overlay before every mode, including full-screen JPEG.
                usb?.SendOverlayPng(renderer.OverlayPng(null, 0));
                if (nativePath != null)
                {
                    using var previewStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var previewTask = RunVideoPreviewAsync(settings, telemetry, previewStop.Token);
                    var cadence = Stopwatch.StartNew();
                    long lastOverlay = -1000;
                    int lastRevision = -1;
                    bool? lastOverlayEnabled = null;
                    bool first = true;
                    try
                    {
                        await NativeVideoPlayback.RunAsync(usb!, nativePath, settings.VideoFps, message =>
                        {
                            Status = Localization.Format("Conectado · {0}", message);
                            Performance = Localization.Format("{0} fps configurados na tela · métricas 1 Hz · prévia 2 fps", settings.VideoFps);
                            int revision = Volatile.Read(ref layoutRevision);
                            if (cadence.ElapsedMilliseconds - lastOverlay >= 1000)
                            {
                                var values = telemetry.Read();
                                Volatile.Write(ref latestSensors, values);
                                SensorHint = values.TemperatureHint;
                                if (overlayEnabled) usb!.SendOverlayPng(renderer.OverlayPng(values, settings.Rotation, Volatile.Read(ref currentLayout)));
                                else if (lastOverlayEnabled != false || revision != lastRevision) usb!.SendOverlayPng(renderer.OverlayPng(null, 0));
                                Volatile.Write(ref appliedLayoutRevision, revision);
                                lastOverlayEnabled = overlayEnabled;
                                lastRevision = revision;
                                lastOverlay = cadence.ElapsedMilliseconds;
                            }
                            if (first && message.Contains(Localization.T("repetição"), StringComparison.Ordinal)) { AppLog.Write($"Vídeo H.264 nativo ativo: {settings.VideoFps} fps; sobreposição={settings.Overlay}; primeiro bloco confirmado pelo USB."); first = false; }
                        }, token);
                    }
                    finally { previewStop.Cancel(); await previewTask; }
                    return;
                }
                using var video = settings.Mode == "Vídeo" ? new VideoDecoder(settings.FfmpegPath, settings.MediaPath, 2, token) : null;
                bool sent = false;
                bool? lastImageOverlay = null;
                int frames = 0;
                var watch = Stopwatch.StartNew();
                var measuredAt = TimeSpan.Zero;
                var lastDecoderCpu = TimeSpan.Zero;
                string decoderUsage = "";
                while (!token.IsCancellationRequested)
                {
                    var tick = Stopwatch.StartNew();
                    var sensors = telemetry.Read();
                    Volatile.Write(ref latestSensors, sensors);
                    SensorHint = sensors.TemperatureHint;
                    bool showOverlay = overlayEnabled;
                    if (!sent || settings.Mode != "Imagem" || showOverlay || lastImageOverlay != showOverlay)
                    {
                        using var videoFrame = video != null ? await video.ReadAsync(token) : null;
                        int renderingRevision = Volatile.Read(ref layoutRevision);
                        using var frame = renderer.Render(sensors, videoFrame ?? image, showOverlay, settings.Rotation, layout: Volatile.Read(ref currentLayout));
                        token.ThrowIfCancellationRequested();
                        usb?.SendJpeg(renderer.EncodeJpeg(frame));
                        Volatile.Write(ref appliedLayoutRevision, renderingRevision);
                        Preview((Bitmap)frame.Clone());
                        if (!sent) AppLog.Write($"Sessão iniciada: {settings.Mode}, rotação {settings.Rotation}°, {(previewOnly ? "prévia local" : usb!.Firmware + "; primeiro quadro confirmado pelo USB")}.");
                        sent = true;
                        lastImageOverlay = showOverlay;
                        frames++;
                    }
                    Status = previewOnly ? Localization.T("Prévia local · tela sem conexão") : Localization.Format("Conectado · {0} · {1}", usb!.Firmware, Localization.T(settings.Mode));
                    if (video != null && watch.Elapsed - measuredAt > TimeSpan.FromSeconds(3))
                    {
                        var decoderCpu = video.CpuTime;
                        double load = (decoderCpu - lastDecoderCpu).TotalSeconds / (watch.Elapsed - measuredAt).TotalSeconds / Environment.ProcessorCount * 100;
                        decoderUsage = Localization.Format(" · FFmpeg CPU {0:0.00}% / {1:0} MB", load, video.WorkingSetBytes / 1048576d);
                        lastDecoderCpu = decoderCpu;
                        measuredAt = watch.Elapsed;
                    }
                    Performance = settings.Mode == "Imagem" && !overlayEnabled ? Localization.T("Imagem enviada uma vez · USB em repouso") : Localization.Format("{0:0.0} quadros/s{1}", frames / Math.Max(watch.Elapsed.TotalSeconds, 1), decoderUsage);
                    int interval = settings.Mode == "Vídeo" ? 500 : 1000;
                    int delay = Math.Max(1, interval - (int)tick.ElapsedMilliseconds);
                    await Task.Delay(delay, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Status = Localization.Format("Parado: {0}", Localization.T(ex.Message));
                AppLog.Write(ex.ToString());
            }
            finally { if (token.IsCancellationRequested) Status = Localization.T("Parado · última imagem mantida na tela"); }
        });
    }
    private async Task RunVideoPreviewAsync(AppSettings settings, Telemetry telemetry, CancellationToken token)
    {
        try
        {
            using var renderer = new FrameRenderer();
            while (!token.IsCancellationRequested)
            {
                if (!PreviewEnabled) { await Task.Delay(500, token); continue; }
                using var decoder = new VideoDecoder(settings.FfmpegPath, settings.MediaPath, 2, token);
                while (PreviewEnabled && !token.IsCancellationRequested)
                {
                    using var source = await decoder.ReadAsync(token);
                    var frame = renderer.Render(telemetry.Read(), source, overlayEnabled, settings.Rotation, layout: Volatile.Read(ref currentLayout));
                    Preview(frame);
                    await Task.Delay(100, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Write("Prévia de vídeo indisponível (reprodução na tela continua): " + ex.Message); }
    }
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (worker != null) await worker;
    }
    public void Dispose()
    {
        cancellation?.Cancel();
        Interlocked.Exchange(ref latest, null)?.Dispose();
        // Worker owns its USB/decoder handles and closes them on cancellation.
    }
}
