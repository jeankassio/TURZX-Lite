using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TurzxLite;

public sealed class FrameRenderer : IDisposable
{
    public const int Size = 480;
    private readonly Dictionary<(float Size, bool Bold), Font> fonts = [];
    private readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
    private static readonly object MeasureGate = new();
    private static readonly Dictionary<(string Text, float Size, bool Bold), SizeF> Measurements = [];

    public Bitmap Render(SensorSnapshot s, Bitmap? background, bool overlay, int rotation, DateTime? now = null, ScreenLayout? layout = null)
    {
        layout ??= background == null ? ScreenLayout.CreateDefault() : ScreenLayout.CreateCompact();
        var frame = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(frame))
        {
            Quality(g);
            g.Clear(ParseColor(layout.BackgroundColor, Color.FromArgb(10, 16, 27)));
            if (background != null) DrawCover(g, background);
            if (background == null || overlay) DrawLayout(g, layout, s, now ?? DateTime.Now);
        }
        Rotate(frame, rotation);
        return frame;
    }
    public byte[] OverlayPng(SensorSnapshot? s, int rotation, ScreenLayout? layout = null)
    {
        using var frame = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            Quality(g);
            g.Clear(Color.Transparent);
            if (s != null) DrawLayout(g, layout ?? ScreenLayout.CreateCompact(), s, DateTime.Now);
        }
        Rotate(frame, rotation);
        using var stream = new MemoryStream();
        frame.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
    private static void Quality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
    }
    private static StringFormat TextFormat() => new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
    };
    private Font FontFor(LayoutItem item)
    {
        float size = float.IsFinite(item.FontSize) ? Math.Clamp(item.FontSize, 10, 96) : 28;
        var key = (size, item.Bold);
        if (fonts.TryGetValue(key, out var cached)) return cached;
        if (fonts.Count > 256) { foreach (var font in fonts.Values) font.Dispose(); fonts.Clear(); }
        var created = new Font("Segoe UI", size, item.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        fonts.Add(key, created);
        return created;
    }
    private void DrawLayout(Graphics g, ScreenLayout layout, SensorSnapshot snapshot, DateTime now)
    {
        using var circle = new GraphicsPath();
        circle.AddEllipse(0, 0, Size, Size);
        var original = g.Save();
        g.SetClip(circle, CombineMode.Intersect);
        using var format = TextFormat();
        using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        foreach (var item in layout.Items)
        {
            if (!item.Enabled) continue;
            string text = MetricCatalog.ItemText(item, snapshot, now);
            if (string.IsNullOrEmpty(text)) continue;
            var font = FontFor(item);
            using var brush = new SolidBrush(ParseColor(item.Color, Color.White));
            g.DrawString(text, font, shadow, new PointF(item.X + 1, item.Y + 2), format);
            g.DrawString(text, font, brush, new PointF(item.X, item.Y), format);
        }
        g.Restore(original);
    }
    public static RectangleF ItemBounds(LayoutItem item, SensorSnapshot snapshot, DateTime now)
    {
        string text = MetricCatalog.ItemText(item, snapshot, now);
        if (string.IsNullOrEmpty(text)) text = " ";
        float size = float.IsFinite(item.FontSize) ? Math.Clamp(item.FontSize, 10, 96) : 28;
        SizeF measured;
        lock (MeasureGate)
        {
            var key = (text, size, item.Bold);
            if (!Measurements.TryGetValue(key, out measured))
            {
                using var pixel = new Bitmap(1, 1);
                using var g = Graphics.FromImage(pixel);
                using var font = new Font("Segoe UI", size, item.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                using var format = TextFormat();
                measured = g.MeasureString(text, font, 10000, format);
                if (Measurements.Count > 4096) Measurements.Clear();
                Measurements[key] = measured;
            }
        }
        return new RectangleF(item.X - measured.Width / 2 - 3, item.Y - measured.Height / 2 - 3, measured.Width + 6, measured.Height + 6);
    }
    private static Color ParseColor(string? value, Color fallback) => ScreenLayout.ValidColor(value) ? ColorTranslator.FromHtml(value!) : fallback;
    private static void Rotate(Bitmap frame, int rotation)
    {
        if (rotation != 0) frame.RotateFlip(rotation switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
    }
    private static void DrawCover(Graphics g, Bitmap image)
    {
        double scale = Math.Max((double)Size / image.Width, (double)Size / image.Height);
        float w = (float)(image.Width * scale), h = (float)(image.Height * scale);
        g.DrawImage(image, (Size - w) / 2, (Size - h) / 2, w, h);
    }
    public byte[] EncodeJpeg(Bitmap frame)
    {
        using var output = new MemoryStream(60_000);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        frame.Save(output, jpeg, parameters);
        return output.ToArray();
    }
    public static Bitmap LoadImage(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Escolha um arquivo de imagem existente.", path);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Use uma imagem de até 64 MB.");
        using var source = Image.FromFile(path);
        if (source.Width > 16384 || source.Height > 16384 || (long)source.Width * source.Height > 64_000_000) throw new InvalidDataException("Imagem muito grande. Reduza a resolução antes de abrir.");
        var result = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.Black);
        double scale = Math.Max((double)Size / source.Width, (double)Size / source.Height);
        float w = (float)(source.Width * scale), h = (float)(source.Height * scale);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, (Size - w) / 2, (Size - h) / 2, w, h);
        return result;
    }
    public void Dispose() { foreach (var font in fonts.Values) font.Dispose(); fonts.Clear(); }
}
