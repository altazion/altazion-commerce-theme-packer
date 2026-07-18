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
            _ => throw new ThemePackagerException($"Unsupported source kind '{sourceKind}'."),
        };
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

        var hasThemeSource = File.Exists(themeGeneralPath);
        var hasTemplateSource = File.Exists(templatePath);

        if (hasThemeSource && hasTemplateSource)
            throw new ThemePackagerException("Ambiguous source directory: both theme.general.json and template.json are present.");

        if (hasThemeSource)
            return PackSourceKind.Theme;

        if (hasTemplateSource)
            return PackSourceKind.Template;

        throw new ThemePackagerException($"Unsupported source directory '{sourceDirectory}': expected either theme.general.json or template.json.");
    }
}

internal enum PackSourceKind
{
    Theme = 0,
    Template = 1,
}

internal sealed record ThemePackEntry(string FullPath, string EntryName);

internal sealed record ThemeMetadata(string ThemeId, string ThemeName);

internal sealed record TemplatePackArtifact(string ProfileCode, string ProfileName, string OutputPath, int EntryCount, long Size);