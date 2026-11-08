namespace Altazion.Commerce.ThemePackager;

internal static class ThemePackager
{
    public static ThemePackResult Pack(PackCommandOptions options)
    {
        var sourceDirectory = ResolveSourceDirectory(options.SourceDirectory);
        var sourceKind = DetectSourceKind(sourceDirectory);

        return sourceKind switch
        {
            PackSourceKind.Theme => ThemeSourcePackager.Pack(sourceDirectory, options),
            PackSourceKind.Template => TemplateSourcePackager.Pack(sourceDirectory, options),
            PackSourceKind.Signage => PackSignage(options),
            _ => throw new ThemePackagerException($"Unsupported source kind '{sourceKind}'."),
        };
    }

    internal static ThemePackResult PackSignage(PackCommandOptions options)
    {
        var result = SignagePackager.Pack(options);
        return new ThemePackResult(
            result.OutputPath, result.Size, result.FileCount, result.Code, result.Name,
            PackSourceKind.Signage,
            options.IsDryRun ? Array.Empty<string>() : new[] { result.OutputPath },
            Array.Empty<TemplatePackArtifact>(),
            new[] { "Final validation by the published Core-Business importer is required." });
    }

    internal static string ResolveSourceDirectory(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ThemePackagerException("The source directory cannot be empty.");

        var fullPath = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(fullPath))
            throw new ThemePackagerException($"Source directory not found: '{fullPath}'.");

        return fullPath;
    }

    internal static PackSourceKind DetectSourceKind(string sourceDirectory)
    {
        var themeGeneralPath = Path.Combine(sourceDirectory, "theme.general.json");
        var templatePath = Path.Combine(sourceDirectory, "template.json");
        var manifestPath = Path.Combine(sourceDirectory, "manifest.json");

        var hasThemeSource = File.Exists(themeGeneralPath);
        var hasTemplateSource = File.Exists(templatePath);
        var hasManifest = File.Exists(manifestPath);

        if (hasThemeSource && hasTemplateSource)
            throw new ThemePackagerException("Ambiguous source directory: both theme.general.json and template.json are present.");

        if (hasManifest && !(hasThemeSource || hasTemplateSource))
            return PackSourceKind.Signage;

        if (hasManifest && SignagePackager.HasSignageSchema(manifestPath))
            throw new ThemePackagerException("Ambiguous source directory: a signage manifest and a theme or template source are present.");

        if (hasThemeSource)
            return PackSourceKind.Theme;

        if (hasTemplateSource)
            return PackSourceKind.Template;

        throw new ThemePackagerException($"Unsupported source directory '{sourceDirectory}': expected theme.general.json, template.json or a signage manifest.json.");
    }
}

internal enum PackSourceKind
{
    Theme = 0,
    Template = 1,
    Signage = 2,
}

internal sealed record ThemePackEntry(string FullPath, string EntryName, byte[]? Content = null);

internal sealed record ThemeMetadata(string ThemeId, string ThemeName);

internal sealed record TemplatePackArtifact(string ProfileCode, string ProfileName, string OutputPath, int EntryCount, long Size);