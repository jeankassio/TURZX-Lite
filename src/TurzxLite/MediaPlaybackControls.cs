using System.ComponentModel;

namespace TurzxLite;

/// <summary>
/// Visible content selector. Setting properties never opens a dialog; clicking
/// Imagem/Vídeo lets the user choose media and raises MediaChosen after success.
/// The host remains responsible for applying the selection or starting playback.
/// </summary>
public sealed class MediaPlaybackControls : UserControl
{
    private static readonly Color Card = Color.FromArgb(19, 29, 43);
    private static readonly Color Accent = Color.FromArgb(83, 239, 195);
    private static readonly Color Muted = Color.FromArgb(151, 169, 190);
    private readonly Dictionary<string, Button> buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> paths = new(StringComparer.Ordinal)
    {
        ["Imagem"] = "",
        ["Vídeo"] = ""
    };
    private readonly TextBox path = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(30, 43, 59),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 5, 7, 4)
    };
    private readonly Button browse = new() { Text = "Escolher arquivo…", Dock = DockStyle.Fill };
    private readonly Label description = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = Muted,
        Margin = new Padding(0, 5, 0, 1)
    };
    private readonly TableLayoutPanel fileRow;
    private string selectedMode = "Painel";
    private bool changingMode;
    private bool dialogOpen;

    public event EventHandler? ModeChanged;
    public event EventHandler? MediaPathChanged;
    public event EventHandler? MediaChosen;

    /// <summary>Accepts the existing settings values: Painel, Imagem or Vídeo.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedMode
    {
        get => selectedMode;
        set => SetMode(value);
    }

    /// <summary>The file for the selected mode. Image/video paths are kept separately in memory.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string MediaPath
    {
        get => path.Text.Trim();
        set
        {
            var candidate = value ?? "";
            if (selectedMode == "Painel")
            {
                // This also supports hosts which load MediaPath before SelectedMode.
                var mode = IsVideo(candidate) ? "Vídeo" : "Imagem";
                paths[mode] = candidate;
                return;
            }
            path.Text = candidate;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string InitialDirectory { get; set; } = AppPaths.DefaultMediaDirectory;

    public MediaPlaybackControls()
    {
        BackColor = Card;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9);
        AutoScaleMode = AutoScaleMode.Inherit;
        MinimumSize = new Size(260, 122);
        Size = new Size(410, 122);
        Margin = new Padding(0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var tabs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        foreach (var mode in new[] { "Painel", "Imagem", "Vídeo" })
        {
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var button = new Button { Text = mode, Dock = DockStyle.Fill, Margin = new Padding(0, 0, mode == "Vídeo" ? 0 : 6, 0) };
            StyleButton(button);
            button.AccessibleName = $"Modo {mode}";
            button.Click += (_, _) =>
            {
                SetMode(mode);
                if (mode != "Painel") BrowseMedia();
            };
            buttons.Add(mode, button);
            tabs.Controls.Add(button);
        }
        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(description, 0, 1);
        fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        StyleButton(browse);
        browse.Margin = new Padding(0, 1, 0, 1);
        browse.Click += (_, _) => BrowseMedia();
        path.AccessibleName = "Arquivo de mídia";
        path.TextChanged += (_, _) =>
        {
            if (changingMode) return;
            if (selectedMode != "Painel") paths[selectedMode] = path.Text;
            MediaPathChanged?.Invoke(this, EventArgs.Empty);
        };
        fileRow.Controls.Add(path, 0, 0);
        fileRow.Controls.Add(browse, 1, 0);
        layout.Controls.Add(fileRow, 0, 2);
        Controls.Add(layout);
        UpdateSelection();
    }

    private void SetMode(string value)
    {
        if (value is not ("Painel" or "Imagem" or "Vídeo"))
            throw new ArgumentOutOfRangeException(nameof(value), "Selecione Painel, Imagem ou Vídeo.");
        if (value == selectedMode) return;
        if (selectedMode != "Painel") paths[selectedMode] = path.Text;
        selectedMode = value;
        changingMode = true;
        try { path.Text = value == "Painel" ? "" : paths[value]; }
        finally { changingMode = false; }
        UpdateSelection();
        ModeChanged?.Invoke(this, EventArgs.Empty);
        MediaPathChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Choose media without opening USB or starting a decoder.</summary>
    public bool BrowseMedia(IWin32Window? owner = null)
    {
        if (selectedMode == "Painel" || dialogOpen || IsDisposed || Disposing || DesignMode) return false;
        dialogOpen = true;
        try
        {
            bool video = selectedMode == "Vídeo";
            using var dialog = new OpenFileDialog
            {
                Title = Localization.T(video ? "Escolha o vídeo para a tela TURZX" : "Escolha a imagem para a tela TURZX"),
                Filter = Localization.T(video
                    ? "Vídeos|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.gif;*.h264|Todos os arquivos|*.*"
                    : "Imagens|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Todos os arquivos|*.*"),
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true
            };
            string? currentDirectory = null;
            try { if (!string.IsNullOrWhiteSpace(MediaPath)) currentDirectory = Path.GetDirectoryName(Path.GetFullPath(MediaPath)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
            if (currentDirectory is not null && Directory.Exists(currentDirectory)) dialog.InitialDirectory = currentDirectory;
            else if (Directory.Exists(InitialDirectory)) dialog.InitialDirectory = InitialDirectory;
            var result = owner is not null ? dialog.ShowDialog(owner) : FindForm() is Form form ? dialog.ShowDialog(form) : dialog.ShowDialog();
            if (result != DialogResult.OK || IsDisposed || Disposing) return false;
            MediaPath = dialog.FileName;
            MediaChosen?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally { dialogOpen = false; }
    }

    private void UpdateSelection()
    {
        foreach (var (mode, button) in buttons)
        {
            bool selected = mode == selectedMode;
            button.BackColor = selected ? Accent : Color.FromArgb(29, 44, 61);
            button.ForeColor = selected ? Color.FromArgb(11, 17, 27) : Color.White;
            button.FlatAppearance.BorderSize = selected ? 2 : 1;
            button.FlatAppearance.BorderColor = selected ? Accent : Color.FromArgb(50, 68, 86);
            button.AccessibleDescription = selected ? "Modo selecionado" : "Clique para selecionar";
        }
        fileRow.Visible = selectedMode != "Painel";
        path.PlaceholderText = selectedMode == "Vídeo" ? "Escolha um vídeo para reproduzir em repetição" : "Escolha sua imagem";
        browse.Text = selectedMode == "Vídeo" ? "Escolher vídeo…" : "Escolher imagem…";
        description.Text = selectedMode switch
        {
            "Vídeo" => "Escolha um vídeo e clique em Conectar / aplicar. Reprodução em repetição.",
            "Imagem" => "Escolha uma imagem e clique em Conectar / aplicar.",
            _ => "CPU, memória e relógio. Para animações, selecione Vídeo."
        };
        Localization.Apply(this);
    }
    public void RefreshLanguage() { UpdateSelection(); Localization.Apply(this); }

    private static bool IsVideo(string file) => Path.GetExtension(file).ToLowerInvariant() is
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".h264";

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.Cursor = Cursors.Hand;
        button.BackColor = Color.FromArgb(29, 44, 61);
        button.ForeColor = Color.White;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderColor = Color.FromArgb(50, 68, 86);
        button.FlatAppearance.BorderSize = 1;
    }
}
