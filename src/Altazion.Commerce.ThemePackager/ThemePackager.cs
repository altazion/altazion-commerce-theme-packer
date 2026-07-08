using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Altazion.Commerce.ThemePackager;

internal static class ThemePackager
{
    public static ThemePackResult Pack(PackCommandOptions options)
    {
        var sourceDirectory = ResolveSourceDirectory(options.SourceDirectory);
        var themeMetadata = ReadThemeMetadata(sourceDirectory);
        var outputFile = ResolveOutputFile(options.OutputFile, themeMetadata.ThemeName);

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

        var entries = CollectEntries(sourceDirectory);
        var manifestJson = BuildManifestJson(sourceDirectory, entries, themeMetadata);

        if (File.Exists(outputFile))
            File.Delete(outputFile);

        using (var archive = ZipFile.Open(outputFile, ZipArchiveMode.Create))
        {
            foreach (var entry in entries)
            {
                ZipFileExtensions.CreateEntryFromFile(
                    archive,
                    entry.FullPath,
                    entry.EntryName,
                    CompressionLevel.Optimal);
            }

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var stream = manifestEntry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(manifestJson);
        }

        var fileInfo = new FileInfo(outputFile);
        return new ThemePackResult(
            outputFile,
            fileInfo.Length,
            entries.Count,
            themeMetadata.ThemeId,
            themeMetadata.ThemeName);
    }

    private static string ResolveSourceDirectory(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ThemePackagerException("The source directory cannot be empty.");

        var fullPath = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(fullPath))
            throw new ThemePackagerException($"Source directory not found: '{fullPath}'.");

        return fullPath;
    }

    private static ThemeMetadata ReadThemeMetadata(string sourceDirectory)
    {
        var generalFilePath = Path.Combine(sourceDirectory, "theme.general.json");
        if (!File.Exists(generalFilePath))
            throw new ThemePackagerException($"theme.general.json not found in '{sourceDirectory}'.");

        using var stream = File.OpenRead(generalFilePath);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("theme", out var themeElement))
            throw new ThemePackagerException("theme.general.json does not contain 'theme'.");

        var themeId = themeElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var themeName = themeElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(themeId) || string.IsNullOrWhiteSpace(themeName))
            throw new ThemePackagerException("theme.general.json must contain valid theme.id and theme.name values.");

        return new ThemeMetadata(themeId, themeName);
    }

    private static string ResolveOutputFile(string? outputFile, string themeName)
    {
        if (!string.IsNullOrWhiteSpace(outputFile))
            return Path.GetFullPath(outputFile);

        var distDirectory = Path.Combine(Directory.GetCurrentDirectory(), "dist");
        var safeThemeName = string.Concat(themeName.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var fileName = $"{safeThemeName}-{DateTime.UtcNow:yyyyMMdd}.themepack";
        return Path.Combine(distDirectory, fileName);
    }

    private static List<ThemePackEntry> CollectEntries(string sourceDirectory)
    {
        var entries = new List<ThemePackEntry>();

        AddIfExists(entries, Path.Combine(sourceDirectory, "theme.general.json"), "theme.general.json");
        AddIfExists(entries, Path.Combine(sourceDirectory, "theme.shared.json"), "theme.shared.json");
        AddIfExists(entries, Path.Combine(sourceDirectory, "theme.seo.json"), "theme.seo.json");
        AddIfExists(entries, Path.Combine(sourceDirectory, "theme.marketing.json"), "theme.marketing.json");

        var pagesDirectory = Path.Combine(sourceDirectory, "pages");
        if (Directory.Exists(pagesDirectory))
        {
            foreach (var pageFile in Directory.EnumerateFiles(pagesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ThemePackEntry(pageFile, $"pages/{Path.GetFileName(pageFile)}"));
            }
        }

        var menusDirectory = Path.Combine(sourceDirectory, "menus");
        if (Directory.Exists(menusDirectory))
        {
            foreach (var menuFile in Directory.EnumerateFiles(menusDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ThemePackEntry(menuFile, $"menus/{Path.GetFileName(menuFile)}"));
            }
        }

        foreach (var assetFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !string.Equals(Path.GetFileName(path), ".gitignore", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !IsUnderDirectory(path, pagesDirectory))
                     .Where(path => !IsUnderDirectory(path, menusDirectory))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, assetFile)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

            entries.Add(new ThemePackEntry(assetFile, $"assets/{relativePath}"));
        }

        return entries;
    }

    private static string BuildManifestJson(string sourceDirectory, IReadOnlyCollection<ThemePackEntry> entries, ThemeMetadata themeMetadata)
    {
        var sharedPath = Path.Combine(sourceDirectory, "theme.shared.json");
        var seoPath = Path.Combine(sourceDirectory, "theme.seo.json");
        var marketingPath = Path.Combine(sourceDirectory, "theme.marketing.json");
        var pagesDirectory = Path.Combine(sourceDirectory, "pages");
        var menusDirectory = Path.Combine(sourceDirectory, "menus");

        var manifest = new
        {
            schemaVersion = 1,
            themeId = themeMetadata.ThemeId,
            themeName = themeMetadata.ThemeName,
            packedAt = DateTimeOffset.UtcNow.ToString("O"),
            files = new
            {
                general = "theme.general.json",
                shared = File.Exists(sharedPath) ? "theme.shared.json" : null,
                seo = File.Exists(seoPath) ? "theme.seo.json" : null,
                marketing = File.Exists(marketingPath) ? "theme.marketing.json" : null,
                pages = Directory.Exists(pagesDirectory)
                    ? Directory.EnumerateFiles(pagesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => $"pages/{Path.GetFileName(path)}")
                        .ToArray()
                    : Array.Empty<string>(),
                menus = Directory.Exists(menusDirectory)
                    ? Directory.EnumerateFiles(menusDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => $"menus/{Path.GetFileName(path)}")
                        .ToArray()
                    : Array.Empty<string>(),
                assets = entries
                    .Where(entry => entry.EntryName.StartsWith("assets/", StringComparison.Ordinal))
                    .Select(entry => entry.EntryName)
                    .ToArray()
            }
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    private static bool IsUnderDirectory(string filePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return false;

        var normalizedDirectory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var normalizedFile = Path.GetFullPath(filePath);
        return normalizedFile.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfExists(List<ThemePackEntry> entries, string fullPath, string entryName)
    {
        if (File.Exists(fullPath))
            entries.Add(new ThemePackEntry(fullPath, entryName));
    }
}

internal sealed record ThemePackEntry(string FullPath, string EntryName);

internal sealed record ThemeMetadata(string ThemeId, string ThemeName);