using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace FFLogsPlugin;

internal static class Loc
{
    private static IReadOnlyDictionary<string, string> translations = new Dictionary<string, string>();
    internal static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    internal static void Initialize(string language)
    {
        Culture = CultureInfo.GetCultureInfo(language);
        var assembly = typeof(Loc).Assembly;
        for (var culture = Culture; culture != CultureInfo.InvariantCulture; culture = culture.Parent)
        {
            using var stream = assembly.GetManifestResourceStream($"FFLogsPlugin.Localization.{culture.Name}.json");
            if (stream == null) continue;
            translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            return;
        }
        translations = new Dictionary<string, string>();
    }

    internal static string Text(string source) =>
        translations.TryGetValue(source, out var translation) ? translation : source;

    internal static string Format(string source, params object[] args) =>
        string.Format(Culture, Text(source), args);
}
