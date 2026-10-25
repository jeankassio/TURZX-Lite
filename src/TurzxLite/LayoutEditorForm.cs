using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace TurzxLite;

/// <summary>Edits a private clone; the caller applies Result only after DialogResult.OK.</summary>
public sealed class LayoutEditorForm : Form
{
    private static readonly Color Background = Color.FromArgb(11, 17, 27);
    private static readonly Color Card = Color.FromArgb(19, 29, 43);
    private static readonly Color Accent = Color.FromArgb(83, 239, 195);
    private static readonly Color Muted = Color.FromArgb(151, 169, 190);
    private ScreenLayout layout;
    private LayoutItem? selected;
    private bool updating;
    private readonly CheckedListBox itemList = new()
    {
        Dock = DockStyle.Fill, BackColor = Card, ForeColor = Color.White,
        BorderStyle = BorderStyle.None, IntegralHeight = false, CheckOnClick = false,
        FormattingEnabled = true
    };
    private readonly ComboBox metric = new()
    {
        Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
        BackColor = Color.FromArgb(30, 43, 59), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        DropDownWidth = 360
    };
    private readonly NumericUpDown x = Number(0, 480, 240);
    private readonly NumericUpDown y = Number(0, 480, 240);
    private readonly NumericUpDown fontSize = Number(10, 96, 28);
    private readonly TextBox label = Input();
    private readonly TextBox color = Input();
    private readonly CheckBox bold = Check("Negrito");
    private readonly CheckBox showLabel = Check("Mostrar rótulo");
    private readonly CheckBox guides = Check("Mostrar guias no editor");
    private readonly Label selectionTitle = TextLabel("Selecione um item", 12, Color.White, true);
    private readonly Label itemCounter = TextLabel("ITENS · marque para exibir", 9, Muted, true);
    private readonly Panel itemProperties = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly EditorCanvas canvas;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScreenLayout Result => layout.Clone();
    public void UpdateSnapshot(SensorSnapshot snapshot)
    {
        if (!IsDisposed && !Disposing) canvas.UpdateSnapshot(snapshot);
    }

    public LayoutEditorForm(ScreenLayout layout, SensorSnapshot snapshot, Bitmap? background = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        this.layout = layout.Clone();
        this.layout.Normalize();
        canvas = new EditorCanvas(this.layout, snapshot, background);
        SuspendLayout();
        Text = "Editor da tela redonda · TURZX Lite";
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(1020, 720);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 14),
            ColumnCount = 1, RowCount = 3
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        Controls.Add(root);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        heading.Controls.Add(TextLabel("MONTE SUA TELA", 18, Color.White, true));
        heading.Controls.Add(TextLabel("Adicione informações, arraste os textos e ajuste tamanho e cor. A tela física só muda depois de salvar e aplicar.", 9, Muted));
        root.Controls.Add(heading, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        root.Controls.Add(body, 0, 1);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(12),
            ColumnCount = 1, RowCount = 7, Margin = new Padding(0, 0, 12, 0)
        };
        foreach (var height in new[] { 26, 34, 36, 30 }) left.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.Controls.Add(TextLabel("ADICIONAR INFORMAÇÃO", 9, Muted, true), 0, 0);
        foreach (var definition in MetricCatalog.All) metric.Items.Add(new MetricOption(definition));
        metric.DropDownWidth = 420;
        if (metric.Items.Count > 0) metric.SelectedIndex = 0;
        left.Controls.Add(metric, 0, 1);
        var add = Button("+ Adicionar", true);
        add.Click += (_, _) => AddItem();
        left.Controls.Add(add, 0, 2);
        left.Controls.Add(itemCounter, 0, 3);
        left.Controls.Add(itemList, 0, 4);
        itemList.Format += (_, e) =>
        {
            if (e.ListItem is LayoutItem item)
                e.Value = item.MetricId == "text" ? Localization.T("Texto: ") + item.Label : MetricCatalog.Find(item.MetricId)?.Name ?? item.MetricId;
        };
        itemList.SelectedIndexChanged += (_, _) => { if (!updating) SelectItem(itemList.SelectedItem as LayoutItem); };
        itemList.ItemCheck += (_, e) =>
        {
            if (updating || itemList.Items[e.Index] is not LayoutItem item) return;
            item.Enabled = e.NewValue == CheckState.Checked;
            canvas.ContentChanged();
        };
        left.Controls.Add(Pair("Duplicar", DuplicateItem, "Remover", RemoveItem), 0, 5);
        left.Controls.Add(Pair("↑ Frente", BringForward, "↓ Fundo", SendBackward), 0, 6);
        body.Controls.Add(left, 0, 0);

        var center = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Card, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0, 0, 12, 0)
        };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        canvas.Dock = DockStyle.Fill;
        canvas.SelectionChanged += item => SelectItem(item);
        canvas.ItemMoved += _ => { UpdatePositionFields(); canvas.ContentChanged(); };
        canvas.DeleteRequested += RemoveItem;
        center.Controls.Add(canvas, 0, 0);
        var help = TextLabel("Arraste para mover · Setas: 1 px · Shift: 10 px\nDelete: remover · Prévia 480 × 480\nSensor indisponível: —", 9, Muted);
        help.Dock = DockStyle.Fill;
        help.Padding = new Padding(10, 3, 8, 0);
        center.Controls.Add(help, 0, 1);
        body.Controls.Add(center, 1, 0);

        var right = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(12), AutoScroll = true, Margin = new Padding(0) };
        var propertyRows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        propertyRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Row(propertyRows, selectionTitle, 38);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Row(fields, TextLabel("POSIÇÃO DO CENTRO · pixels", 9, Muted, true), 27);
        Row(fields, NumericPair("X", x, "Y", y), 34);
        Row(fields, TextLabel("TAMANHO DO TEXTO", 9, Muted, true), 30);
        Row(fields, fontSize, 34);
        Row(fields, bold, 28);
        Row(fields, showLabel, 28);
        Row(fields, TextLabel("RÓTULO", 9, Muted, true), 25);
        label.MaxLength = 80;
        Row(fields, label, 34);
        Row(fields, TextLabel("COR DO TEXTO", 9, Muted, true), 28);
        var colorRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        colorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        color.MaxLength = 7;
        colorRow.Controls.Add(color, 0, 0);
        var chooseColor = Button("Cor…");
        chooseColor.Click += (_, _) => ChooseItemColor();
        colorRow.Controls.Add(chooseColor, 1, 0);
        Row(fields, colorRow, 36);
        itemProperties.Controls.Add(fields);
        Row(propertyRows, itemProperties, 348);
        Row(propertyRows, TextLabel("FUNDO E PRÉVIA", 9, Muted, true), 28);
        var backgroundColor = Button("Cor de fundo…");
        backgroundColor.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = ReadColor(this.layout.BackgroundColor), FullOpen = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            this.layout.BackgroundColor = Hex(dialog.Color);
            canvas.ContentChanged();
        };
        Row(propertyRows, backgroundColor, 36);
        guides.Checked = this.layout.ShowGuides;
        guides.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            this.layout.ShowGuides = guides.Checked;
            canvas.Invalidate();
        };
        Row(propertyRows, guides, 29);
        Row(propertyRows, TextLabel("Dados da sessão atual; ausentes: —.\nArrastar aqui não envia comandos USB.", 8, Muted), 40);
        right.Controls.Add(propertyRows);
        body.Controls.Add(right, 2, 0);

        x.ValueChanged += (_, _) => ChangeSelected(item => item.X = (float)x.Value);
        y.ValueChanged += (_, _) => ChangeSelected(item => item.Y = (float)y.Value);
        fontSize.ValueChanged += (_, _) => ChangeSelected(item => item.FontSize = (float)fontSize.Value);
        bold.CheckedChanged += (_, _) => ChangeSelected(item => item.Bold = bold.Checked);
        showLabel.CheckedChanged += (_, _) => ChangeSelected(item => item.ShowLabel = showLabel.Checked);
        label.TextChanged += (_, _) => ChangeSelected(item => item.Label = label.Text);
        color.Leave += (_, _) => ApplyTypedColor();

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Padding = new Padding(0, 14, 0, 0), Margin = new Padding(0) };
        foreach (int width in new[] { 132, 114, 95 }) footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        var reset = Button("Restaurar padrão");
        reset.Click += (_, _) => ReplaceLayout(ScreenLayout.CreateDefault());
        var compact = Button("Compacto");
        compact.Click += (_, _) => ReplaceLayout(ScreenLayout.CreateCompact());
        var clear = Button("Limpar");
        clear.Click += (_, _) => { this.layout.Items.Clear(); RefreshItems(null); canvas.ContentChanged(); };
        var cancel = Button("Cancelar");
        cancel.DialogResult = DialogResult.Cancel;
        var save = Button("Salvar layout", true);
        save.Click += (_, _) => { ApplyTypedColor(); this.layout.Normalize(); DialogResult = DialogResult.OK; Close(); };
        footer.Controls.Add(reset, 0, 0);
        footer.Controls.Add(compact, 1, 0);
        footer.Controls.Add(clear, 2, 0);
        footer.Controls.Add(cancel, 4, 0);
        footer.Controls.Add(save, 5, 0);
        CancelButton = cancel;
        root.Controls.Add(footer, 0, 2);

        RefreshItems(this.layout.Items.FirstOrDefault());
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        };
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
        Localization.Changed += RefreshLanguage;
        RefreshLanguage();
    }
    private void RefreshLanguage()
    {
        if (IsDisposed) return;
        int index = metric.SelectedIndex;
        metric.BeginUpdate();
        metric.Items.Clear();
        foreach (var definition in MetricCatalog.All) metric.Items.Add(new MetricOption(definition));
        if (metric.Items.Count > 0) metric.SelectedIndex = Math.Clamp(index, 0, metric.Items.Count - 1);
        metric.EndUpdate();
        itemList.Refresh();
        Localization.Apply(this);
        canvas.ContentChanged();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Localization.Changed -= RefreshLanguage;
        base.Dispose(disposing);
    }

    private void AddItem()
    {
        if (!CanAddItem()) return;
        if (metric.SelectedItem is not MetricOption option) return;
        var item = new LayoutItem
        {
            Id = Guid.NewGuid().ToString("N"), MetricId = option.Definition.Id,
            Label = option.Definition.DefaultLabel, X = 240, Y = 240, FontSize = 28,
            Color = "#FFFFFF", Enabled = true, Bold = true, ShowLabel = true
        };
        layout.Items.Add(item);
        RefreshItems(item);
        canvas.ContentChanged();
        canvas.Focus();
    }

    private void DuplicateItem()
    {
        if (selected is null || !CanAddItem()) return;
        var item = selected.Clone();
        item.Id = Guid.NewGuid().ToString("N");
        item.X = Math.Clamp(item.X + 16, 0, 480);
        item.Y = Math.Clamp(item.Y + 16, 0, 480);
        layout.Items.Insert(layout.Items.IndexOf(selected) + 1, item);
        RefreshItems(item);
        canvas.ContentChanged();
    }

    private void RemoveItem()
    {
        if (selected is null) return;
        int index = layout.Items.IndexOf(selected);
        layout.Items.Remove(selected);
        RefreshItems(layout.Items.Count == 0 ? null : layout.Items[Math.Clamp(index, 0, layout.Items.Count - 1)]);
        canvas.ContentChanged();
    }

    private void BringForward() => ChangeLayer(1);
    private void SendBackward() => ChangeLayer(-1);
    private void ChangeLayer(int direction)
    {
        if (selected is null) return;
        int index = layout.Items.IndexOf(selected), next = index + direction;
        if (next < 0 || next >= layout.Items.Count) return;
        (layout.Items[index], layout.Items[next]) = (layout.Items[next], layout.Items[index]);
        RefreshItems(selected);
        canvas.ContentChanged();
    }

    private void ReplaceLayout(ScreenLayout replacement)
    {
        layout = replacement.Clone();
        layout.Normalize();
        canvas.SetLayout(layout);
        updating = true;
        try { guides.Checked = layout.ShowGuides; }
        finally { updating = false; }
        RefreshItems(layout.Items.FirstOrDefault());
    }

    private void RefreshItems(LayoutItem? keep)
    {
        updating = true;
        itemList.BeginUpdate();
        try
        {
            itemList.Items.Clear();
            foreach (var item in layout.Items) itemList.Items.Add(item, item.Enabled);
        }
        finally { itemList.EndUpdate(); updating = false; }
        itemCounter.Text = Localization.Format("ITENS · {0}/40 · marque para exibir", layout.Items.Count);
        SelectItem(keep);
    }

    private bool CanAddItem()
    {
        if (layout.Items.Count < 40) return true;
        MessageBox.Show(this, "Este layout já tem 40 itens. Remova um item antes de adicionar outro.",
            "Limite do layout", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void SelectItem(LayoutItem? item)
    {
        selected = item;
        updating = true;
        try
        {
            itemList.SelectedItem = item;
            itemProperties.Enabled = item is not null;
            selectionTitle.Text = item is null ? Localization.T("Selecione um item") : MetricCatalog.Find(item.MetricId)?.Name ?? item.MetricId;
            if (item is not null)
            {
                x.Value = DecimalValue(item.X, 0, 480);
                y.Value = DecimalValue(item.Y, 0, 480);
                fontSize.Value = DecimalValue(item.FontSize, 10, 96);
                bold.Checked = item.Bold;
                showLabel.Checked = item.ShowLabel;
                label.Text = item.Label;
                color.Text = item.Color;
            }
        }
        finally { updating = false; }
        canvas.SelectedItem = item;
    }

    private void UpdatePositionFields()
    {
        if (selected is null) return;
        updating = true;
        try { x.Value = DecimalValue(selected.X, 0, 480); y.Value = DecimalValue(selected.Y, 0, 480); }
        finally { updating = false; }
    }

    private void ChangeSelected(Action<LayoutItem> change)
    {
        if (updating || selected is null) return;
        change(selected);
        canvas.ContentChanged();
    }

    private void ChooseItemColor()
    {
        if (selected is null) return;
        using var dialog = new ColorDialog { Color = ReadColor(selected.Color), FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        selected.Color = Hex(dialog.Color);
        color.Text = selected.Color;
        canvas.ContentChanged();
    }

    private void ApplyTypedColor()
    {
        if (updating || selected is null) return;
        string candidate = color.Text.Trim();
        if (candidate.Length == 7 && candidate[0] == '#' && candidate.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            selected.Color = candidate.ToUpperInvariant();
            canvas.ContentChanged();
        }
        else color.Text = selected.Color;
    }

    private sealed record MetricOption(MetricDefinition Definition)
    {
        public override string ToString() => Definition.Group + " · " + Definition.Name;
    }

    private static decimal DecimalValue(float value, int minimum, int maximum) =>
        (decimal)Math.Clamp(float.IsFinite(value) ? value : minimum, minimum, maximum);
    private static string Hex(Color value) => $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    private static Color ReadColor(string value) { try { return ColorTranslator.FromHtml(value); } catch { return Color.White; } }
    private static NumericUpDown Number(int minimum, int maximum, int value) => new()
    {
        Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = 0,
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 43, 59), ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 5, 2)
    };
    private static TextBox Input() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 43, 59), ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 5, 3)
    };
    private static CheckBox Check(string text) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = Color.White, Margin = new Padding(0) };
    private static Label TextLabel(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, ForeColor = color,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0)
    };
    private static Button Button(string text, bool accent = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        BackColor = accent ? Accent : Color.FromArgb(29, 44, 61),
        ForeColor = accent ? Background : Color.White, UseVisualStyleBackColor = false,
        Cursor = Cursors.Hand, Margin = new Padding(0, 2, 6, 2), FlatAppearance = { BorderSize = 0 }
    };
    private static void Row(TableLayoutPanel table, Control control, int height)
    {
        int index = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 0, index);
    }
    private static Control Pair(string first, Action firstAction, string second, Action secondAction)
    {
        var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var a = Button(first); var b = Button(second);
        a.Click += (_, _) => firstAction(); b.Click += (_, _) => secondAction();
        pair.Controls.Add(a, 0, 0); pair.Controls.Add(b, 1, 0);
        return pair;
    }
    private static Control NumericPair(string first, Control a, string second, Control b)
    {
        var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.Controls.Add(TextLabel(first, 9, Muted), 0, 0); pair.Controls.Add(a, 1, 0);
        pair.Controls.Add(TextLabel(second, 9, Muted), 2, 0); pair.Controls.Add(b, 3, 0);
        return pair;
    }

    private sealed class EditorCanvas : Control
    {
        private readonly FrameRenderer renderer = new();
        private SensorSnapshot snapshot;
        private readonly Bitmap? background;
        private DateTime now = DateTime.Now;
        private ScreenLayout layout;
        private Bitmap? rendered;
        private LayoutItem? selected;
        private bool dirty = true;
        private bool dragging;
        private PointF dragStart;
        private PointF originalPosition;
        public event Action<LayoutItem?>? SelectionChanged;
        public event Action<LayoutItem>? ItemMoved;
        public event Action? DeleteRequested;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public LayoutItem? SelectedItem { get => selected; set { selected = value; Invalidate(); } }
        public EditorCanvas(ScreenLayout layout, SensorSnapshot snapshot, Bitmap? background)
        {
            this.layout = layout; this.snapshot = snapshot; this.background = background is null ? null : (Bitmap)background.Clone();
            BackColor = Card;
            DoubleBuffered = true;
            TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Default;
        }
        public void SetLayout(ScreenLayout value) { layout = value; selected = null; ContentChanged(); }
        public void ContentChanged() { dirty = true; Invalidate(); }
        public void UpdateSnapshot(SensorSnapshot value)
        {
            if (ReferenceEquals(value, snapshot)) return;
            snapshot = value;
            now = DateTime.Now;
            ContentChanged();
        }
        private RectangleF Area
        {
            get { float size = Math.Max(1, Math.Min(ClientSize.Width - 24, ClientSize.Height - 24)); return new RectangleF((Width - size) / 2, (Height - size) / 2, size, size); }
        }
        private PointF ModelPoint(Point point)
        {
            var area = Area;
            return new PointF((point.X - area.X) * 480 / area.Width, (point.Y - area.Y) * 480 / area.Height);
        }
        private static bool Inside(PointF point) => Math.Pow(point.X - 240, 2) + Math.Pow(point.Y - 240, 2) <= 240 * 240;
        private RectangleF ScreenBounds(RectangleF bounds)
        {
            var area = Area;
            float scale = area.Width / 480;
            return new RectangleF(area.X + bounds.X * scale, area.Y + bounds.Y * scale, bounds.Width * scale, bounds.Height * scale);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var area = Area;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var circle = new GraphicsPath();
            circle.AddEllipse(area);
            var state = e.Graphics.Save();
            e.Graphics.SetClip(circle);
            if (dirty)
            {
                rendered?.Dispose();
                rendered = renderer.Render(snapshot, background, true, 0, now, layout);
                dirty = false;
            }
            if (rendered is not null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(rendered, area);
            }
            if (layout.ShowGuides)
            {
                using var guide = new Pen(Color.FromArgb(95, 151, 169, 190), 1) { DashStyle = DashStyle.Dot };
                e.Graphics.DrawLine(guide, area.X + area.Width / 2, area.Y, area.X + area.Width / 2, area.Bottom);
                e.Graphics.DrawLine(guide, area.X, area.Y + area.Height / 2, area.Right, area.Y + area.Height / 2);
                var safe = RectangleF.Inflate(area, -area.Width * .1f, -area.Height * .1f);
                e.Graphics.DrawEllipse(guide, safe);
            }
            if (selected is not null)
            {
                var bounds = ScreenBounds(FrameRenderer.ItemBounds(selected, snapshot, now));
                using var selection = new Pen(Accent, 2) { DashStyle = DashStyle.Dash };
                e.Graphics.DrawRectangle(selection, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                float cx = area.X + selected.X / 480 * area.Width, cy = area.Y + selected.Y / 480 * area.Height;
                e.Graphics.DrawLine(selection, cx - 5, cy, cx + 5, cy);
                e.Graphics.DrawLine(selection, cx, cy - 5, cx, cy + 5);
            }
            e.Graphics.Restore(state);
            using var border = new Pen(Focused ? Accent : Color.FromArgb(52, 72, 91), 2);
            e.Graphics.DrawEllipse(border, area);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            var point = ModelPoint(e.Location);
            LayoutItem? hit = Inside(point) ? layout.Items.LastOrDefault(item => item.Enabled && FrameRenderer.ItemBounds(item, snapshot, now).Contains(point)) : null;
            SelectionChanged?.Invoke(hit);
            if (hit is null) return;
            selected = hit;
            dragStart = point;
            originalPosition = new PointF(hit.X, hit.Y);
            dragging = true;
            Capture = true;
            Cursor = Cursors.SizeAll;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging || !Capture || selected is null) return;
            var point = ModelPoint(e.Location);
            selected.X = Math.Clamp(originalPosition.X + point.X - dragStart.X, 0, 480);
            selected.Y = Math.Clamp(originalPosition.Y + point.Y - dragStart.Y, 0, 480);
            ItemMoved?.Invoke(selected);
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) EndDrag(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) EndDrag(); }
        private void EndDrag() { dragging = false; Capture = false; Cursor = Cursors.Default; }
        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (selected is null) return;
            if (e.KeyCode == Keys.Delete) { DeleteRequested?.Invoke(); e.Handled = true; e.SuppressKeyPress = true; return; }
            int step = e.Shift ? 10 : 1;
            int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
            int dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
            if (dx == 0 && dy == 0) return;
            selected.X = Math.Clamp(selected.X + dx, 0, 480);
            selected.Y = Math.Clamp(selected.Y + dy, 0, 480);
            ItemMoved?.Invoke(selected);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { rendered?.Dispose(); rendered = null; renderer.Dispose(); background?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
