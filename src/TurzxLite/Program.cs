using System.Diagnostics;
using System.Text;

namespace TurzxLite;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Localization.SetLanguage(AppSettings.Load().Language);
        if (args.Length > 0 && args[0] == "--cpu-sensor-worker")
        {
            try { return DirectCpuSensors.RunWorkerAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { AppLog.Write("CPU auxiliar: " + ex); return 1; }
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { AppLog.Write(e.Exception.ToString()); MessageBox.Show(e.Exception.Message, "TURZX Lite"); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write(e.ExceptionObject.ToString() ?? "Erro fatal");
        if (args.Contains("--self-test")) return Diagnostics.RunSelfTest(args);
        using var mutex = new Mutex(true, @"Local\TurzxLite.Controller", out bool first);
        if (!first)
        {
            if (args.Length == 0) MessageBox.Show(Localization.T("O TURZX Lite já está aberto. Procure o ícone ao lado do relógio."), "TURZX Lite");
            return 2;
        }
        try
        {
            if (args.Contains("--probe")) return Diagnostics.Probe(args);
            if (args.Contains("--hardware-test")) return Diagnostics.HardwareTest(args);
            if (args.Contains("--soak-test")) return Diagnostics.SoakTest(args).GetAwaiter().GetResult();
            if (args.Contains("--native-video-test")) return Diagnostics.NativeVideoTest(args).GetAwaiter().GetResult();
            using var form = new MainForm(connectOnShow: args.Contains("--connect"), activateCpu: args.Contains("--activate-cpu"), openEditor: args.Contains("--edit-layout"));
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex.ToString());
            if (args.Length == 0) MessageBox.Show(Localization.T(ex.Message), "TURZX Lite", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Diagnostics.WriteReport(args, "error.txt", ex.ToString());
            return 1;
        }
        finally { DirectCpuSensors.Stop(); mutex.ReleaseMutex(); }
    }
}

internal static class Diagnostics
{
    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    public static void WriteReport(string[] args, string name, string text)
    {
        string dir = Option(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "diagnostics");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text);
        Console.WriteLine(text);
    }
    public static int RunSelfTest(string[] args)
    {
        var report = new StringBuilder();
        try
        {
            byte[] packet = TurzxUsb.BuildPacket(10, [], 0x01020304);
            Require(packet.Length == 512 && Convert.ToHexString(packet.AsSpan(0, 16)) == "CCFFA6FB53021074FD8EC712B392C6A6", "DES-CBC known-answer vector");
            Require(Convert.ToHexString(packet.AsSpan(488, 24)) == "FC104ABAC4EFFCCF781A5F0C81C05598000000000000A11A", "DES padding + envelope trailer");
            foreach (byte command in new byte[] { 11, 12, 150, 251, 252 })
            {
                bool refused = false;
                try { TurzxUsb.BuildPacket(command, [], 0); } catch (ArgumentOutOfRangeException) { refused = true; }
                Require(refused, $"forbidden command {command}");
            }
            report.AppendLine("PASS: protocol known-answer vector; forbidden commands rejected.");
            using var telemetry = new Telemetry();
            var sensors = telemetry.Read();
            Thread.Sleep(1100);
            sensors = telemetry.Read();
            Require(double.IsFinite(sensors.CpuPercent) && sensors.CpuPercent is >= 0 and <= 100, "CPU deltas");
            Require(sensors.MemoryTotalGb > 0 && sensors.MemoryPercent is >= 0 and <= 100, "RAM snapshot");
            report.AppendLine($"PASS: CPU {sensors.CpuPercent:0.0}%; RAM {sensors.MemoryUsedGb:0.0}/{sensors.MemoryTotalGb:0.0} GB; GPU {sensors.GpuName}, {sensors.GpuTemperature}°C.");
            report.AppendLine("Temperature status: " + sensors.TemperatureHint);
            using var renderer = new FrameRenderer();
            using var frame = renderer.Render(sensors, null, false, 0, new DateTime(2026, 10, 24, 16, 30, 0));
            byte[] jpeg = renderer.EncodeJpeg(frame);
            using var stream = new MemoryStream(jpeg);
            using var decoded = Image.FromStream(stream);
            Require(decoded.Width == 480 && decoded.Height == 480, "JPEG decode and dimensions");
            byte[] transfer = TurzxUsb.BuildPacket(101, jpeg, 123);
            Require(transfer.AsSpan(512).SequenceEqual(jpeg), "JPEG transport payload preserved");
            string output = Option(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, "dashboard.png"));
            report.AppendLine($"PASS: panel render; JPEG {jpeg.Length} bytes; complete USB payload.");
            using (var loaded = FrameRenderer.LoadImage(Path.Combine(output, "dashboard.png")))
            using (var rotated = renderer.Render(sensors, loaded, true, 180))
            using (var encoded = new MemoryStream(renderer.EncodeJpeg(rotated)))
            using (var imageDecoded = Image.FromStream(encoded))
                Require(imageDecoded.Width == 480 && imageDecoded.Height == 480, "custom image + overlay + rotation JPEG");
            report.AppendLine("PASS: custom image decode, metric overlay and 180-degree rotation.");
            // Exercise cancellation immediately after starting, including Task.Run scheduling races.
            using (var session = new DisplaySession())
                for (int i = 0; i < 12; i++) { session.Start(new AppSettings(), null, true); session.StopAsync().GetAwaiter().GetResult(); }
            report.AppendLine("PASS: immediate start/stop cancellation repeated without exception.");
            using (var form = new MainForm(true))
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.CreateControl();
                form.Show();
                Application.DoEvents();
                form.PerformLayout();
                using var ui = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(ui, new Rectangle(0, 0, ui.Width, ui.Height));
                ui.Save(Path.Combine(output, "interface.png"));
            }
            report.AppendLine("PASS: native UI constructed and rendered.");
            report.AppendLine("Offline self-test passed. No display USB commands were sent.");
            WriteReport(args, "self-test.txt", report.ToString());
            return 0;
        }
        catch (Exception ex) { report.AppendLine("FAIL: " + ex); WriteReport(args, "self-test.txt", report.ToString()); return 1; }
    }
    public static int Probe(string[] args)
    {
        var report = new StringBuilder();
        var found = TurzxUsb.Enumerate();
        foreach (var device in found)
        {
            report.AppendLine($"Device: {device.Name}; serial {device.Serial}");
            report.AppendLine("Firmware: " + TurzxUsb.ProbeFirmware(device));
        }
        if (found.Count == 0) throw new IOException("Display TURZX não encontrado.");
        report.AppendLine("Probe complete: only firmware query command 10 was sent.");
        WriteReport(args, "probe.txt", report.ToString());
        return 0;
    }
    public static int HardwareTest(string[] args)
    {
        using var telemetry = new Telemetry();
        var s = telemetry.Read();
        Thread.Sleep(1100);
        s = telemetry.Read();
        using var renderer = new FrameRenderer();
        using var frame = renderer.Render(s, null, false, 0);
        using var usb = new TurzxUsb();
        var device = TurzxUsb.Enumerate().FirstOrDefault() ?? throw new IOException("Display não encontrado.");
        usb.Connect(device, AppSettings.Load().Brightness);
        var watch = Stopwatch.StartNew();
        byte[] jpeg = renderer.EncodeJpeg(frame);
        usb.SendJpeg(jpeg);
        WriteReport(args, "hardware-test.txt", $"PASS: {device.Serial}, {usb.Firmware}; JPEG 480x480 {jpeg.Length} bytes acknowledged in {watch.ElapsedMilliseconds} ms. Last image left on display. Physical appearance requires user confirmation.");
        return 0;
    }
    public static async Task<int> SoakTest(string[] args)
    {
        int duration = Math.Clamp(int.TryParse(Option(args, "--seconds"), out int seconds) ? seconds : 30, 5, 180);
        string? videoPath = Option(args, "--video");
        bool previewOnly = args.Contains("--preview-only");
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(duration + 10));
        using var telemetry = new Telemetry();
        using var renderer = new FrameRenderer();
        using var usb = previewOnly ? null : new TurzxUsb();
        var settings = AppSettings.Load();
        if (usb != null) usb.Connect(TurzxUsb.Enumerate().FirstOrDefault() ?? throw new IOException("Display não encontrado."), settings.Brightness);
        using var video = videoPath != null ? new VideoDecoder(settings.FfmpegPath, videoPath, 2, token.Token) : null;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        long memoryStart = process.WorkingSet64;
        var clock = Stopwatch.StartNew();
        long maxFrameMs = 0;
        int frames = 0;
        long bytes = 0;
        while (clock.Elapsed.TotalSeconds < duration)
        {
            var frameTime = Stopwatch.StartNew();
            using var background = video == null ? null : await video.ReadAsync(token.Token);
            using var frame = renderer.Render(telemetry.Read(), background, true, 0);
            byte[] jpeg = renderer.EncodeJpeg(frame);
            usb?.SendJpeg(jpeg);
            bytes += jpeg.Length;
            maxFrameMs = Math.Max(maxFrameMs, frameTime.ElapsedMilliseconds);
            frames++;
            await Task.Delay(Math.Max(1, (video != null ? 500 : 1000) - (int)frameTime.ElapsedMilliseconds), token.Token);
        }
        process.Refresh();
        double cpuPercent = (process.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
        string decoderStats = video != null ? $"\nFFmpeg CPU (total machine capacity): {video.CpuTime.TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100:0.000}%\nFFmpeg memory: {video.WorkingSetBytes / 1048576d:0.0} MB" : "";
        WriteReport(args, "soak-test" + (video != null ? "-video" : "-panel") + (previewOnly ? "-preview" : "-usb") + ".txt", $"PASS: {frames} frames; {clock.Elapsed.TotalSeconds:0.0}s; {(video != null ? "video 2fps" : "panel 1fps")}; USB={!previewOnly}.\nApp CPU (total machine capacity): {cpuPercent:0.000}%\nApp memory: {memoryStart / 1048576d:0.0} -> {process.WorkingSet64 / 1048576d:0.0} MB\nLongest frame including USB: {maxFrameMs} ms\nJPEG data: {bytes} bytes{decoderStats}\nNo frame queue, retransmission loop, firmware writes, driver installations or cooler-control commands.");
        return 0;
    }
    private static void Require(bool value, string name) { if (!value) throw new InvalidOperationException("Failed check: " + name); }
    public static async Task<int> NativeVideoTest(string[] args)
    {
        string input = Option(args, "--video") ?? throw new ArgumentException("Selecione --video arquivo.");
        int fps = int.TryParse(Option(args, "--fps"), out int f) ? f : 60;
        int seconds = Math.Clamp(int.TryParse(Option(args, "--seconds"), out int s) ? s : 15, 5, 120);
        var settings = AppSettings.Load();
        string cached = await VideoCache.PrepareAsync(settings.FfmpegPath, input, fps, 0, Console.WriteLine, CancellationToken.None);
        using var usb = new TurzxUsb();
        using var renderer = new FrameRenderer();
        using var telemetry = new Telemetry();
        usb.Connect(TurzxUsb.Enumerate().FirstOrDefault() ?? throw new IOException("Tela não encontrada."), settings.Brightness);
        usb.SendOverlayPng(renderer.OverlayPng(null, 0));
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        long overlayAt = -1000;
        int callbacks = 0, overlays = 0;
        string last = "";
        try
        {
            await NativeVideoPlayback.RunAsync(usb, cached, fps, message =>
            {
                callbacks++;
                last = message;
                if (!args.Contains("--no-overlay") && watch.ElapsedMilliseconds - overlayAt >= 1000)
                {
                    usb.SendOverlayPng(renderer.OverlayPng(telemetry.Read(), 0));
                    overlayAt = watch.ElapsedMilliseconds;
                    overlays++;
                }
            }, timer.Token);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested) { }
        process.Refresh();
        WriteReport(args, $"native-video-{fps}fps.txt", $"PASS: H.264 nativo, {fps} fps configurados; {watch.Elapsed.TotalSeconds:0.0}s; {callbacks} eventos; {overlays} PNG overlays.\n{last}\nCPU do emissor: {(process.TotalProcessorTime - cpu).TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100:0.000}%\nMemória: {process.WorkingSet64 / 1048576d:0.0} MiB\nConversão prévia/cache: {new FileInfo(cached).Length} bytes. Nenhum FFmpeg necessário para o envio H.264. A fluidez visual precisa de confirmação no display.");
        return 0;
    }
}
