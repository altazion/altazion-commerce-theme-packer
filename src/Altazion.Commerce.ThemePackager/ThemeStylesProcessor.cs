using System.Text;

namespace Altazion.Commerce.ThemePackager;

internal sealed record ThemeStylesScan(StyleRegistry Registry, IReadOnlyList<string> Errors);

/// <summary>
/// Génère le registre de styles à partir des annotations des feuilles CSS du thème
/// et remplace ces feuilles par leur version nettoyée dans le package.
/// </summary>
internal static class ThemeStylesProcessor
{
    public const string RegistryEntryName = "theme.styles.json";

    public static ThemeStylesScan Process(string sourceDirectory, List<ThemePackEntry> entries)
    {
        var errors = new List<string>();
        var annotations = new List<CssAnnotation>();
        var utf8 = new UTF8Encoding(false);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Content is not null
                || !entry.EntryName.StartsWith("assets/", StringComparison.Ordinal)
                || !entry.EntryName.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceName = Path.GetRelativePath(sourceDirectory, entry.FullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var scan = CssAnnotationScanner.Scan(File.ReadAllText(entry.FullPath), sourceName);

            errors.AddRange(scan.Errors);
            annotations.AddRange(scan.Annotations);

            if (scan.Annotations.Count > 0)
                entries[index] = entry with { Content = utf8.GetBytes(scan.CleanedCss) };
        }

        var registry = StyleRegistryBuilder.Build(annotations, errors);
        if (!registry.IsEmpty)
        {
            entries.Add(new ThemePackEntry(
                Path.Combine(sourceDirectory, RegistryEntryName),
                RegistryEntryName,
                utf8.GetBytes(registry.ToJson())));
        }

        return new ThemeStylesScan(registry, errors);
    }
}
