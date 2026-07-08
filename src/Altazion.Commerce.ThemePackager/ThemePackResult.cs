namespace Altazion.Commerce.ThemePackager;

internal sealed record ThemePackResult(
    string OutputPath,
    long Size,
    int EntryCount,
    string ThemeId,
    string ThemeName);