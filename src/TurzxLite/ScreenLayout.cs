using System.Globalization;
using System.Text.Json;

namespace TurzxLite;

public sealed class LayoutItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MetricId { get; set; } = "cpu.temperature";
    public string Label { get; set; } = "CPU";
    public bool ShowLabel { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool Bold { get; set; } = true;
    public float X { get; set; } = 240;
    public float Y { get; set; } = 240;
    public float FontSize { get; set; } = 28;
    public string Color { get; set; } = "#FFFFFF";
    public LayoutItem Clone() => (LayoutItem)MemberwiseClone();
    public override string ToString() => MetricId == "text" ? (string.IsNullOrWhiteSpace(Label) ? Localization.T("Texto livre") : Label) : MetricCatalog.Find(MetricId)?.Name ?? MetricId;
}

public sealed class ScreenLayout
{
    public List<LayoutItem> Items { get; set; } = [];
    public string BackgroundColor { get; set; } = "#0A101B";
    public bool ShowGuides { get; set; } = true;
    public ScreenLayout Clone() => new() { BackgroundColor = BackgroundColor, ShowGuides = ShowGuides, Items = (Items ?? []).Where(i => i != null).Select(i => i.Clone()).ToList() };
    public void Normalize()
    {
        BackgroundColor = ValidColor(BackgroundColor) ? BackgroundColor : "#0A101B";
        Items ??= [];
        Items = Items.Where(i => i != null && MetricCatalog.Find(i.MetricId) != null).Take(40).ToList();
        var ids = new HashSet<string>();
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id)) { item.Id = Guid.NewGuid().ToString("N"); ids.Add(item.Id); }
            item.X = float.IsFinite(item.X) ? Math.Clamp(item.X, 0, 480) : 240;
            item.Y = float.IsFinite(item.Y) ? Math.Clamp(item.Y, 0, 480) : 240;
            item.FontSize = float.IsFinite(item.FontSize) ? Math.Clamp(item.FontSize, 10, 96) : 28;
            item.Color = ValidColor(item.Color) ? item.Color : "#FFFFFF";
            item.Label = (item.Label ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (item.Label.Length > 120) item.Label = item.Label[..120];
        }
    }
    public static bool ValidColor(string? color) => color is { Length: 7 } && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    public static ScreenLayout CreateDefault() => new()
    {
        Items =
        [
            Item("clock", "", 240, 65, 36, false),
            Item("cpu.name", "", 240, 111, 15, false, "#97A9BE"),
            Item("cpu.temperature", "CPU", 140, 170, 32, true, "#53EFC3"),
            Item("gpu.temperature", "GPU", 340, 170, 32, true, "#53EFC3"),
            Item("cpu.usage", "CPU", 140, 218, 24),
            Item("gpu.usage", "GPU", 340, 218, 24),
            Item("ram.usage", "RAM", 240, 286, 34),
            Item("ram.used.total", "", 240, 329, 22, false, "#97A9BE"),
            Item("cpu.clock", "CPU", 140, 377, 20),
            Item("gpu.power", "GPU", 340, 377, 20),
            Item("text", "TURZX LITE", 240, 425, 14, false, "#53EFC3")
        ]
    };
    public static ScreenLayout CreateCompact() => new()
    {
        Items =
        [
            Item("cpu.temperature", "CPU", 150, 330, 24),
            Item("gpu.temperature", "GPU", 330, 330, 24),
            Item("cpu.usage", "CPU", 150, 365, 20),
            Item("ram.usage", "RAM", 330, 365, 20),
            Item("clock", "", 240, 408, 27, false, "#53EFC3")
        ]
    };
    private static LayoutItem Item(string metric, string label, float x, float y, float size, bool showLabel = true, string color = "#FFFFFF") =>
        new() { MetricId = metric, Label = label, X = x, Y = y, FontSize = size, ShowLabel = showLabel, Color = color };
}

public sealed record MetricDefinition
{
    private readonly string name;
    private readonly string group;
    private readonly string defaultLabel;
    public string Id { get; }
    public string Name => Localization.T(name);
    public string Group => Localization.T(group);
    public string DefaultLabel => Localization.T(defaultLabel);
    public string Unit { get; }
    public MetricDefinition(string id, string name, string group, string defaultLabel, string unit)
    {
        Id = id; this.name = name; this.group = group; this.defaultLabel = defaultLabel; Unit = unit;
    }
    public override string ToString() => $"{Group} · {Name}";
}

public static class MetricCatalog
{
    private static CultureInfo Pt => Localization.Culture;
    public static IReadOnlyList<MetricDefinition> All { get; } = new MetricDefinition[]
    {
        new("cpu.temperature", "Temperatura da CPU", "Processador", "CPU", "°C"),
        new("cpu.usage", "Uso da CPU", "Processador", "CPU", "%"),
        new("cpu.clock", "Frequência média da CPU", "Processador", "CPU", "GHz"),
        new("cpu.power", "Potência da CPU", "Processador", "CPU", "W"),
        new("cpu.name", "Modelo do processador", "Processador", "", ""),
        new("cpu.threads", "Processadores lógicos", "Processador", "Threads", ""),
        new("gpu.temperature", "Temperatura da GPU", "Placa de vídeo", "GPU", "°C"),
        new("gpu.usage", "Uso da GPU", "Placa de vídeo", "GPU", "%"),
        new("gpu.memory.used", "VRAM utilizada", "Placa de vídeo", "VRAM", "GiB"),
        new("gpu.memory.total", "VRAM total", "Placa de vídeo", "VRAM total", "GiB"),
        new("gpu.memory.usage", "Uso de VRAM em %", "Placa de vídeo", "VRAM", "%"),
        new("gpu.clock", "Frequência da GPU", "Placa de vídeo", "GPU", "MHz"),
        new("gpu.memory.clock", "Frequência da VRAM", "Placa de vídeo", "VRAM", "MHz"),
        new("gpu.power", "Potência da GPU", "Placa de vídeo", "GPU", "W"),
        new("gpu.fan", "Ventoinha da GPU", "Placa de vídeo", "Fan GPU", "%"),
        new("gpu.name", "Modelo da placa de vídeo", "Placa de vídeo", "", ""),
        new("ram.usage", "Uso da RAM em %", "Memória", "RAM", "%"),
        new("ram.used", "RAM utilizada", "Memória", "RAM", "GiB"),
        new("ram.free", "RAM disponível", "Memória", "RAM livre", "GiB"),
        new("ram.total", "RAM total", "Memória", "RAM total", "GiB"),
        new("ram.used.total", "RAM utilizada / total", "Memória", "RAM", "GiB"),
        new("clock", "Relógio", "Sistema", "", ""),
        new("date", "Data", "Sistema", "", ""),
        new("uptime", "Tempo ligado", "Sistema", "Ligado", ""),
        new("text", "Texto livre", "Personalizado", "Seu texto", "")
    };
    public static MetricDefinition? Find(string id) => All.FirstOrDefault(m => m.Id == id);
    private static string Number(double? value, string format, string unit = "") => value is double n && double.IsFinite(n) ? n.ToString(format, Pt) + unit : "—";
    public static string FormatValue(string metricId, SensorSnapshot s, DateTime now) => metricId switch
    {
        "cpu.temperature" => Number(s.CpuTemperature, "0", "°C"),
        "cpu.usage" => Number(s.CpuPercent, "0", "%"),
        "cpu.clock" => Number(s.CpuClockMhz / 1000, "0.0", " GHz"),
        "cpu.power" => Number(s.CpuPowerWatts, "0", " W"),
        "cpu.name" => string.IsNullOrWhiteSpace(s.CpuName) ? "—" : s.CpuName,
        "cpu.threads" => s.LogicalProcessors > 0 ? s.LogicalProcessors.ToString(Pt) : "—",
        "gpu.temperature" => Number(s.GpuTemperature, "0", "°C"),
        "gpu.usage" => Number(s.GpuPercent, "0", "%"),
        "gpu.memory.used" => Number(s.GpuMemoryUsedGb, "0.0", " GiB"),
        "gpu.memory.total" => Number(s.GpuMemoryTotalGb, "0.0", " GiB"),
        "gpu.memory.usage" => Number(s.GpuMemoryTotalGb is > 0 ? s.GpuMemoryUsedGb / s.GpuMemoryTotalGb * 100 : null, "0", "%"),
        "gpu.clock" => Number(s.GpuClockMhz, "0", " MHz"),
        "gpu.memory.clock" => Number(s.GpuMemoryClockMhz, "0", " MHz"),
        "gpu.power" => Number(s.GpuPowerWatts, "0", " W"),
        "gpu.fan" => Number(s.GpuFanPercent, "0", "%"),
        "gpu.name" => string.IsNullOrWhiteSpace(s.GpuName) ? "—" : s.GpuName,
        "ram.usage" => Number(s.MemoryPercent, "0", "%"),
        "ram.used" => Number(s.MemoryUsedGb, "0.0", " GiB"),
        "ram.free" => Number(s.MemoryTotalGb - s.MemoryUsedGb, "0.0", " GiB"),
        "ram.total" => Number(s.MemoryTotalGb, "0.0", " GiB"),
        "ram.used.total" => $"{Number(s.MemoryUsedGb, "0.0")} / {Number(s.MemoryTotalGb, "0.0", " GiB")}",
        "clock" => now.ToString("HH:mm", Pt),
        "date" => now.ToString("ddd, dd MMM", Pt).ToUpper(Pt),
        "uptime" => $"{(int)s.Uptime.TotalDays}d {s.Uptime.Hours:00}:{s.Uptime.Minutes:00}",
        _ => ""
    };
    public static string ItemText(LayoutItem item, SensorSnapshot s, DateTime now)
    {
        if (item.MetricId == "text") return item.Label;
        string value = FormatValue(item.MetricId, s, now);
        return item.ShowLabel && !string.IsNullOrWhiteSpace(item.Label) ? item.Label + " " + value : value;
    }
}
