using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TurzxLite;

public static partial class Localization
{
    private static readonly Dictionary<string, string> Translations = CreateTranslations();
    private static readonly Dictionary<string, string> Portuguese = Translations.GroupBy(x => x.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
    private static readonly List<MessageTemplate> Templates = BuildTemplates();
    private static readonly ConditionalWeakTable<Control, OriginalText> Originals = new();
    private static volatile bool english;
    public static bool English => english;
    public static string Language => english ? "en" : "pt-BR";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(english ? "en-US" : "pt-BR");
    public static event Action? Changed;
    private static Dictionary<string, string> CreateTranslations()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        AddUiTranslations(map);
        AddBackendTranslations(map);
        map["Idioma / Language"] = "Language / Idioma";
        map["TURZX redondo 480 × 480"] = "TURZX round 480 × 480";
        return map;
    }
    private static partial void AddUiTranslations(Dictionary<string, string> translations);
    private static partial void AddBackendTranslations(Dictionary<string, string> translations);
    public static void SetLanguage(string? language)
    {
        bool value = language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
        bool changed = value != english;
        english = value;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        if (changed) Changed?.Invoke();
    }
    public static string T(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (Translations.TryGetValue(text, out string? translated)) return english ? translated : text;
        if (Portuguese.TryGetValue(text, out string? original)) return english ? text : original;
        foreach (var template in Templates)
        {
            var match = (english ? template.PortuguesePattern : template.EnglishPattern).Match(text);
            if (!match.Success) continue;
            object?[] arguments = new object?[template.Arguments];
            for (int index = 0; index < arguments.Length; index++)
            {
                var part = match.Groups["p" + index].Value;
                arguments[index] = Translations.TryGetValue(part, out var nested) && english ? nested
                    : Portuguese.TryGetValue(part, out var nestedPt) && !english ? nestedPt : part;
            }
            try { return string.Format(Culture, english ? template.EnglishText : template.PortugueseText, arguments); }
            catch (FormatException) { return text; }
        }
        foreach (string separator in new[] { "   ", ": " })
        {
            int index = text.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0 && index + separator.Length < text.Length)
            {
                string first = text[..index], last = text[(index + separator.Length)..];
                string a = Translations.TryGetValue(first, out var en) && english ? en : Portuguese.TryGetValue(first, out var pt) && !english ? pt : first;
                string b = T(last);
                if (a != first || b != last) return a + separator + b;
            }
        }
        return text;
    }
    public static string Format(string portugueseTemplate, params object?[] arguments) => string.Format(Culture, T(portugueseTemplate), arguments);
    public static bool IsStopped(string text) => text.StartsWith("Parado", StringComparison.Ordinal) || text.StartsWith("Stopped", StringComparison.Ordinal);

    public static void Apply(Control root)
    {
        if (root.IsDisposed) return;
        if (root is not (TextBoxBase or ComboBox or ListBox or NumericUpDown))
        {
            var stored = Originals.GetValue(root, static _ => new OriginalText());
            if (stored.Source == null || root.Text != stored.Last) stored.Source = ToPortuguese(root.Text);
            string value = T(stored.Source);
            if (root.Text != value) root.Text = value;
            stored.Last = value;
        }
        if (root is TextBox input && !string.IsNullOrWhiteSpace(input.PlaceholderText)) input.PlaceholderText = T(input.PlaceholderText);
        if (root.AccessibleName != null) root.AccessibleName = T(root.AccessibleName);
        if (root.AccessibleDescription != null) root.AccessibleDescription = T(root.AccessibleDescription);
        if (root is ToolStrip strip) ApplyItems(strip.Items);
        foreach (Control child in root.Controls) Apply(child);
        if (root is ComboBox or ListBox) root.Invalidate();
    }
    public static void ApplyItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.Text = T(item.Text ?? "");
            if (item is ToolStripDropDownItem dropdown) ApplyItems(dropdown.DropDownItems);
        }
    }
    private static string ToPortuguese(string text) => Portuguese.TryGetValue(text, out var pt) ? pt : text;
    private sealed class OriginalText { public string? Source; public string? Last; }
    private sealed record MessageTemplate(string PortugueseText, string EnglishText, Regex PortuguesePattern, Regex EnglishPattern, int Arguments);
    private static List<MessageTemplate> BuildTemplates()
    {
        var placeholders = new Regex(@"\{(\d+)(?:[^{}]*)\}", RegexOptions.CultureInvariant);
        var result = new List<MessageTemplate>();
        foreach (var pair in Translations)
        {
            var tokens = placeholders.Matches(pair.Key);
            if (tokens.Count == 0 || placeholders.Replace(pair.Key, "").Length < 3) continue;
            int count = tokens.Max(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
            if (count > 10) continue;
            Regex Pattern(string source)
            {
                int at = 0;
                var pattern = new System.Text.StringBuilder("^");
                foreach (Match token in placeholders.Matches(source))
                {
                    pattern.Append(Regex.Escape(source[at..token.Index]));
                    pattern.Append("(?<p" + token.Groups[1].Value + ">.*?)");
                    at = token.Index + token.Length;
                }
                pattern.Append(Regex.Escape(source[at..]));
                pattern.Append('$');
                return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(50));
            }
            result.Add(new(pair.Key, pair.Value, Pattern(pair.Key), Pattern(pair.Value), count));
        }
        return result.OrderByDescending(x => x.PortugueseText.Length).ToList();
    }
}
