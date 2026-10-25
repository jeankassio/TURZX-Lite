using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace TurzxLite;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(11, 17, 27);
    private static readonly Color Card = Color.FromArgb(19, 29, 43);
    private static readonly Color Muted = Color.FromArgb(151, 169, 190);
    private static readonly Color Accent = Color.FromArgb(83, 239, 195);
    private readonly AppSettings settings = AppSettings.Load();
    private readonly DisplaySession session = new();
    private readonly ComboBox devices = Combo();
    private readonly ComboBox language = Combo();
    private readonly MediaPlaybackControls playback = new() { Dock = DockStyle.Fill };
    private readonly ComboBox rotation = Combo();
    private readonly ComboBox fps = Combo();
    private readonly TextBox ffmpeg = Input();
    private readonly TrackBar brightness = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Value = 70, Dock = DockStyle.Fill, Height = 40, BackColor = Card };
    private readonly CheckBox overlay = Check("Sobrepor métricas à imagem / vídeo");
    private readonly CheckBox autoConnect = Check("Conectar automaticamente ao abrir");
    private readonly CheckBox startMinimized = Check("Abrir minimizado na bandeja");
    private readonly CheckBox startup = Check("Iniciar com o Windows");
    private readonly Label status = Label("Pronto para conectar", 12, Color.White);
    private readonly Label hint = Label("", 9, Muted);
    private readonly Label performance = Label("", 9, Muted);
    private readonly Label brightnessText = Label("Brilho · 70%", 10, Muted);
    private readonly Label previewCaption = Label("", 9, Muted);
    private readonly Button connect = Button("Conectar / aplicar", true);
    private readonly Button previewButton = Button("Testar prévia");
    private readonly Button stop = Button("Parar");
    private readonly Button cpuEnable = Button("Ativar temperatura da CPU");
    private readonly Button editLayout = Button("Editar layout / escolher informações", true);
    private readonly Label cpuStatus = Label("Sem HWiNFO ou AIDA64 · acesso autorizado pelo Windows", 9, Muted);
    private readonly CirclePreview preview = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly Process ownProcess = Process.GetCurrentProcess();
    private readonly ToolTip tooltips = new() { AutoPopDelay = 30000 };
    private TimeSpan lastCpu;
    private DateTime lastMeasure = DateTime.UtcNow;
    private string processUsage = "";
    private bool closing;
    private bool shuttingDown;
    private bool busy;
    private LayoutEditorForm? activeEditor;

    public MainForm(bool diagnosticMode = false, bool connectOnShow = false, bool activateCpu = false, bool openEditor = false)
    {
        SuspendLayout();
        Text = "TURZX Lite";
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(1120, 850);
        MinimumSize = new Size(1000, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 16), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 87));
        Controls.Add(root);
        var heading = new Panel { Dock = DockStyle.Fill };
        var title = Label("TURZX  /  LITE", 25, Color.White, true);
        title.Location = new Point(0, 0);
        heading.Controls.Add(title);
        var subtitle = Label("Sua tela. Menos trabalho para o PC.", 10, Muted);
        subtitle.Location = new Point(2, 45);
        heading.Controls.Add(subtitle);
        var languageBox = new TableLayoutPanel { Dock = DockStyle.Right, Width = 205, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 4, 0, 0) };
        languageBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        languageBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        languageBox.Controls.Add(Label("Idioma / Language", 9, Muted), 0, 0);
        language.Items.AddRange(["Português (Brasil)", "English"]);
        language.SelectedIndex = Localization.English ? 1 : 0;
        languageBox.Controls.Add(language, 0, 1);
        heading.Controls.Add(languageBox);
        root.Controls.Add(heading, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        root.Controls.Add(content, 0, 1);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(16), Margin = new Padding(0, 0, 18, 0), ColumnCount = 1, RowCount = 3 };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        left.Controls.Add(Label("PRÉVIA  ·  480 × 480", 9, Muted, true));
        left.Controls.Add(preview, 0, 1);
        previewCaption.Dock = DockStyle.Fill;
        left.Controls.Add(previewCaption, 0, 2);
        content.Controls.Add(left, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Card, Padding = new Padding(18, 12, 18, 12), Margin = new Padding(0) };
        var options = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 0, BackColor = Card };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(options);
        content.Controls.Add(scroll, 1, 0);
        Add(options, Label("TELA", 9, Muted, true), 24);
        var deviceRow = Row(devices, Button("Buscar"), 90);
        ((Button)deviceRow.Controls[1]).Click += async (_, _) => await RefreshDevicesAsync();
        Add(options, deviceRow, 38);
        Add(options, Label("CONTEÚDO", 9, Muted, true), 30);
        playback.SelectedMode = settings.Mode;
        playback.MediaPath = settings.MediaPath;
        Add(options, playback, 122);
        Add(options, editLayout, 43);
        editLayout.Click += async (_, _) => await EditLayoutAsync();
        overlay.Checked = settings.Overlay;
        Add(options, overlay, 30);
        overlay.CheckedChanged += (_, _) => session.SetOverlay(overlay.Checked);
        var detailRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
        detailRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        detailRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        detailRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        detailRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        detailRow.Controls.Add(Label("Rotação", 9, Muted), 0, 0);
        rotation.Items.AddRange(["0°", "90°", "180°", "270°"]);
        rotation.SelectedIndex = settings.Rotation / 90;
        detailRow.Controls.Add(rotation, 1, 0);
        detailRow.Controls.Add(Label("Vídeo", 9, Muted), 2, 0);
        fps.Items.AddRange(["24 fps", "30 fps", "60 fps"]);
        fps.SelectedIndex = settings.VideoFps >= 60 ? 2 : settings.VideoFps >= 30 ? 1 : 0;
        detailRow.Controls.Add(fps, 3, 0);
        Add(options, detailRow, 38);
        brightness.Value = settings.Brightness;
        brightnessText.Text = $"Brilho · {brightness.Value}%";
        Add(options, brightnessText, 27);
        Add(options, brightness, 42);
        brightness.ValueChanged += (_, _) => brightnessText.Text = $"Brilho · {brightness.Value}%";
        Add(options, Label("FFMPEG  ·  DECODIFICADOR DE VÍDEO", 9, Muted, true), 27);
        ffmpeg.Text = settings.FfmpegPath;
        var decoderRow = Row(ffmpeg, Button("…"), 42);
        ((Button)decoderRow.Controls[1]).Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = Localization.T("Selecione ffmpeg.exe"), Filter = "FFmpeg|ffmpeg.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) ffmpeg.Text = dialog.FileName;
        };
        Add(options, decoderRow, 37);
        Add(options, cpuEnable, 38);
        Add(options, cpuStatus, 42);
        cpuEnable.Click += async (_, _) => await EnableCpuAsync();
        autoConnect.Checked = settings.AutoConnect;
        startMinimized.Checked = settings.StartMinimized;
        startup.Checked = IsStartupEnabled();
        Add(options, autoConnect, 27);
        Add(options, startMinimized, 27);
        Add(options, startup, 27);
        Add(options, Label("Feche o TURZX original antes de conectar.", 9, Accent), 28);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        actions.Controls.Add(connect, 0, 0);
        actions.Controls.Add(previewButton, 1, 0);
        actions.Controls.Add(stop, 2, 0);
        root.Controls.Add(actions, 0, 2);
        connect.Click += async (_, _) => await StartAsync(false);
        previewButton.Click += async (_, _) => await StartAsync(true);
        stop.Click += async (_, _) => await StopAsync();
        playback.ModeChanged += (_, _) => UpdateMode();
        playback.MediaChosen += (_, _) => { status.Text = "Arquivo selecionado. Clique em " + (playback.SelectedMode == "Vídeo" ? "Reproduzir vídeo" : "Conectar / aplicar") + "."; };
        UpdateMode();

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 12, 0, 0) };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        status.Dock = hint.Dock = performance.Dock = DockStyle.Fill;
        status.AutoEllipsis = hint.AutoEllipsis = performance.AutoEllipsis = true;
        footer.Controls.Add(status);
        footer.Controls.Add(hint);
        footer.Controls.Add(performance);
        root.Controls.Add(footer, 0, 3);
        var menu = trayMenu;
        menu.Items.Add("Abrir TURZX Lite", null, (_, _) => RestoreWindow());
        menu.Items.Add("Parar envio", null, async (_, _) => await StopAsync());
        menu.Items.Add("Sair", null, (_, _) => Close());
        tray = new NotifyIcon { Icon = Icon, Text = "TURZX Lite", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => RestoreWindow();
        language.SelectedIndexChanged += (_, _) =>
        {
            settings.Language = language.SelectedIndex == 1 ? "en" : "pt-BR";
            Localization.SetLanguage(settings.Language);
            settings.Save();
        };
        Localization.Changed += RefreshLanguage;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += OnClosing;
        timer.Tick += (_, _) => UpdateView();
        timer.Start();
        Shown += async (_, _) =>
        {
            if (diagnosticMode) return;
            var area = Screen.FromControl(this).WorkingArea;
            if (Width > area.Width || Height > area.Height)
            {
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }
            await RefreshDevicesAsync();
            if ((settings.AutoConnect || connectOnShow) && devices.SelectedItem is DisplayDevice) await StartAsync(false);
            if (settings.DirectCpuTemperature || activateCpu) await EnableCpuAsync();
            if (openEditor) await EditLayoutAsync();
            if (settings.StartMinimized) { WindowState = FormWindowState.Minimized; Hide(); }
        };
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
        RefreshLanguage();
    }
    private async Task RefreshDevicesAsync()
    {
        if (busy || shuttingDown) return;
        try
        {
            var found = await Task.Run(TurzxUsb.Enumerate);
            devices.DataSource = found.Select(device => device with { Name = Localization.T(device.Name) }).ToArray();
            devices.DisplayMember = nameof(DisplayDevice.Name);
            status.Text = found.Count > 0 ? $"{found.Count} tela(s) encontrada(s) · pronta para conectar" : "Tela não encontrada. Verifique o cabo USB e clique em Buscar.";
        }
        catch (Exception ex) { status.Text = ex.Message; AppLog.Write(ex.ToString()); }
    }
    private async Task StartAsync(bool previewOnly)
    {
        if (busy || shuttingDown) return;
        if (!previewOnly && devices.SelectedItem is not DisplayDevice) { status.Text = "Nenhuma tela selecionada. Clique em Buscar."; return; }
        busy = true;
        connect.Enabled = previewButton.Enabled = false;
        try
        {
            await session.StopAsync();
            if (shuttingDown) return;
            settings.Mode = playback.SelectedMode;
            settings.MediaPath = playback.MediaPath;
            settings.FfmpegPath = ffmpeg.Text.Trim();
            settings.Overlay = overlay.Checked;
            settings.Rotation = Math.Max(0, rotation.SelectedIndex) * 90;
            settings.Brightness = brightness.Value;
            settings.VideoFps = fps.SelectedIndex switch { 0 => 24, 2 => 60, _ => 30 };
            settings.AutoConnect = autoConnect.Checked;
            settings.StartMinimized = startMinimized.Checked;
            if (!previewOnly) { settings.Save(); SetStartup(startup.Checked); }
            session.Start(settings, devices.SelectedItem as DisplayDevice, previewOnly);
        }
        catch (Exception ex) { status.Text = ex.Message; AppLog.Write(ex.ToString()); }
        finally { busy = false; connect.Enabled = previewButton.Enabled = true; }
    }
    private async Task StopAsync()
    {
        if (busy) return;
        busy = true;
        try { await session.StopAsync(); }
        finally { busy = false; }
    }
    private void UpdateView()
    {
        session.PreviewEnabled = Visible && WindowState != FormWindowState.Minimized;
        if (session.TakePreview() is Bitmap frame) preview.SetFrame(frame);
        if (session.LatestSensors is SensorSnapshot liveValues) activeEditor?.UpdateSnapshot(liveValues);
        if (session.Running || Localization.IsStopped(session.Status)) status.Text = Localization.T(session.Status);
        hint.Text = Localization.T(session.SensorHint);
        tooltips.SetToolTip(hint, Localization.T(session.SensorHint));
        var cpuReading = DirectCpuSensors.Current;
        cpuStatus.Text = cpuReading.Temperature.HasValue ? Localization.Format("CPU {0:0}°C · {1} · integrado, 1 Hz", cpuReading.Temperature, cpuReading.Sensor) : Localization.T(cpuReading.Status);
        cpuEnable.Text = Localization.T(DirectCpuSensors.Active ? "Reiniciar sensor da CPU" : "Ativar temperatura da CPU");
        tooltips.SetToolTip(cpuStatus, Localization.T(cpuReading.Status));
        if ((DateTime.UtcNow - lastMeasure).TotalSeconds >= 3)
        {
            ownProcess.Refresh();
            var cpu = ownProcess.TotalProcessorTime;
            double percent = (cpu - lastCpu).TotalSeconds / (DateTime.UtcNow - lastMeasure).TotalSeconds / Environment.ProcessorCount * 100;
            processUsage = Localization.Format("Aplicativo: CPU {0:0.00}% · RAM {1:0} MB", percent, ownProcess.WorkingSet64 / 1048576d);
            lastCpu = cpu;
            lastMeasure = DateTime.UtcNow;
        }
        performance.Text = $"{Localization.T(session.Performance)}   {processUsage}".Trim();
        status.Text = Localization.T(status.Text);
        brightnessText.Text = Localization.Format("Brilho · {0}%", brightness.Value);
    }
    private void UpdateMode()
    {
        bool panel = playback.SelectedMode == "Painel";
        overlay.Enabled = !panel;
        ffmpeg.Enabled = fps.Enabled = playback.SelectedMode == "Vídeo";
        connect.Text = playback.SelectedMode == "Vídeo" ? "Reproduzir vídeo" : "Conectar / aplicar";
        previewCaption.Text = playback.SelectedMode == "Vídeo" ? "Prévia leve: 2 fps · o display usa os FPS selecionados.\nO vídeo é decodificado pela própria tela." : "USB direto · atualização controlada\nA prévia funciona mesmo sem a tela conectada.";
        Localization.Apply(connect);
        Localization.Apply(previewCaption);
    }
    private async Task EnableCpuAsync()
    {
        cpuEnable.Enabled = false;
        try
        {
            if (await DirectCpuSensors.EnableAsync(restart: DirectCpuSensors.Active))
            {
                settings.DirectCpuTemperature = true;
                settings.Save();
            }
        }
        catch (Exception ex) { status.Text = "CPU: " + ex.Message; AppLog.Write(ex.ToString()); }
        finally { if (!IsDisposed) cpuEnable.Enabled = true; }
    }
    private async Task EditLayoutAsync()
    {
        if (shuttingDown || !editLayout.Enabled) return;
        editLayout.Enabled = false;
        Bitmap? background = null;
        try
        {
            status.Text = "Preparando o editor de informações…";
            SensorSnapshot values;
            if (session.Running && session.LatestSensors is SensorSnapshot recent) values = recent;
            else values = await Task.Run(async () =>
            {
                using var telemetry = new Telemetry();
                telemetry.Read();
                await Task.Delay(1050);
                return telemetry.Read();
            });
            string path = playback.MediaPath;
            if (File.Exists(path))
            {
                try
                {
                    if (playback.SelectedMode == "Imagem") background = await Task.Run(() => FrameRenderer.LoadImage(path));
                    else if (playback.SelectedMode == "Vídeo" && File.Exists(ffmpeg.Text.Trim()))
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        using var decoder = new VideoDecoder(ffmpeg.Text.Trim(), path, 1, timeout.Token);
                        background = await decoder.ReadAsync(timeout.Token);
                    }
                }
                catch (Exception ex) { AppLog.Write("Fundo do editor indisponível: " + ex.Message); }
            }
            if (shuttingDown || IsDisposed) return;
            using var editor = new LayoutEditorForm(settings.Layout, values, background);
            activeEditor = editor;
            DialogResult result;
            try { result = editor.ShowDialog(this); }
            finally { activeEditor = null; }
            if (result != DialogResult.OK) return;
            settings.Layout = editor.Result;
            settings.Overlay = true;
            overlay.Checked = true;
            settings.Save();
            session.UpdateLayout(settings.Layout);
            session.SetOverlay(true);
            status.Text = "Layout salvo. As informações serão atualizadas na tela.";
            if (!session.Running) await StartAsync(true);
        }
        catch (Exception ex) { status.Text = "Editor: " + ex.Message; AppLog.Write(ex.ToString()); }
        finally { background?.Dispose(); if (!IsDisposed) editLayout.Enabled = true; }
    }
    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void RefreshLanguage()
    {
        Localization.Apply(this);
        Localization.Apply(trayMenu);
        playback.RefreshLanguage();
        if (devices.DataSource is DisplayDevice[] choices)
        {
            string? serial = (devices.SelectedItem as DisplayDevice)?.Serial;
            devices.DataSource = choices.Select(device => device with { Name = Localization.T(device.Name) }).ToArray();
            devices.DisplayMember = nameof(DisplayDevice.Name);
            if (serial != null) devices.SelectedItem = ((DisplayDevice[])devices.DataSource).FirstOrDefault(device => device.Serial == serial);
        }
        preview.Invalidate();
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (shuttingDown) return;
        shuttingDown = true;
        Enabled = false;
        await session.StopAsync();
        closing = true;
        Close();
    }
    private static bool IsStartupEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("TurzxLite") != null; } catch { return false; }
    }
    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("TurzxLite", $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue("TurzxLite", false);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Localization.Changed -= RefreshLanguage; timer.Dispose(); tray.Dispose(); trayMenu.Dispose(); session.Dispose(); ownProcess.Dispose(); tooltips.Dispose(); }
        base.Dispose(disposing);
    }
    private static ComboBox Combo() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30, 43, 59), ForeColor = Color.White, Margin = new Padding(0, 2, 6, 4) };
    private static TextBox Input() => new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 43, 59), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 6, 4) };
    private static CheckBox Check(string text) => new() { Text = text, Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(209, 220, 232), Font = new Font("Segoe UI", 9), Margin = new Padding(0) };
    private static Label Label(string text, float size, Color color, bool bold = false) => new() { Text = text, AutoSize = true, ForeColor = color, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 3, 0, 0) };
    private static Button Button(string text, bool primary = false) => new() { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.FromArgb(29, 44, 61), ForeColor = primary ? Background : Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(0, 2, 6, 2), FlatAppearance = { BorderSize = 0 } };
    private static void Add(TableLayoutPanel panel, Control control, int height)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(control, 0, row);
    }
    private static TableLayoutPanel Row(Control main, Control button, int buttonWidth)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, buttonWidth));
        row.Controls.Add(main, 0, 0);
        row.Controls.Add(button, 1, 0);
        return row;
    }
}

public sealed class CirclePreview : Control
{
    private Bitmap? frame;
    public CirclePreview() { DoubleBuffered = true; BackColor = Color.FromArgb(19, 29, 43); }
    public void SetFrame(Bitmap bitmap) { var old = frame; frame = bitmap; old?.Dispose(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int size = Math.Max(1, Math.Min(Width - 8, Height - 8));
        var area = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var clip = new GraphicsPath();
        clip.AddEllipse(area);
        var state = e.Graphics.Save();
        e.Graphics.SetClip(clip);
        e.Graphics.FillEllipse(Brushes.Black, area);
        if (frame != null) { e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; e.Graphics.DrawImage(frame, area); }
        else
        {
            using var f = new Font("Segoe UI", 15, FontStyle.Regular);
            using var b = new SolidBrush(Color.FromArgb(151, 169, 190));
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(Localization.T("TURZX LITE\n\nClique em Testar prévia"), f, b, area, fmt);
        }
        e.Graphics.Restore(state);
        using var pen = new Pen(Color.FromArgb(44, 64, 83), 2);
        e.Graphics.DrawEllipse(pen, area);
    }
    protected override void Dispose(bool disposing) { if (disposing) frame?.Dispose(); base.Dispose(disposing); }
}
