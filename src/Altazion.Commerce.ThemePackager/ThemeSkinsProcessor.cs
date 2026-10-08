using Fluid;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Altazion.Commerce.ThemePackager;

internal sealed record ThemeSkinsScan(IReadOnlyList<string> Errors);

/// <summary>
/// Inline les gabarits <c>skins/&lt;contentTypeId&gt;/&lt;skinCode&gt;.liquid</c> dans <c>config.template</c>
/// des composants <c>ContentSkin</c> de <c>theme.shared.json</c> et contrôle la syntaxe Fluid de chaque skin.
/// </summary>
internal static class ThemeSkinsProcessor
{
    public const string FolderName = "skins";
    private const string SharedEntryName = "theme.shared.json";
    private const string ContentSkinComponentType = "ContentSkin";

    private static readonly FluidParser Parser = new();

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ThemeSkinsScan Process(string sourceDirectory, List<ThemePackEntry> entries)
    {
        var errors = new List<string>();
        var skinsDirectory = Path.Combine(sourceDirectory, FolderName);
        var templateFiles = Directory.Exists(skinsDirectory)
            ? Directory.EnumerateFiles(skinsDirectory, "*.liquid", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        var sharedPath = Path.Combine(sourceDirectory, SharedEntryName);
        JsonNode? root = null;
        if (File.Exists(sharedPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(sharedPath));
            }
            catch (JsonException)
            {
                // L'invalidité de theme.shared.json est signalée par le validateur.
                return new ThemeSkinsScan(errors);
            }
        }

        var skins = FindSkins(root);
        var inlined = false;

        foreach (var file in templateFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            var segments = relativePath.Split('/');
            if (segments.Length != 3)
            {
                errors.Add($"{relativePath} must be named 'skins/<contentTypeId>/<skinCode>.liquid'.");
                continue;
            }

            var typeId = segments[1];
            var skinCode = Path.GetFileNameWithoutExtension(segments[2]);
            var skin = skins.FirstOrDefault(candidate =>
                string.Equals(candidate.ContentTypeId, typeId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.SkinCode, skinCode, StringComparison.OrdinalIgnoreCase));
            if (skin is null)
            {
                errors.Add($"{relativePath} has no matching ContentSkin component in {SharedEntryName} (contentTypeId '{typeId}', skinCode '{skinCode}').");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(skin.Config["template"]?.GetValue<string>()))
            {
                errors.Add($"{relativePath} conflicts with config.template of the ContentSkin '{typeId}/{skinCode}': declare the template only once.");
                continue;
            }

            skin.Config["template"] = File.ReadAllText(file);
            inlined = true;
        }

        foreach (var skin in skins)
        {
            var template = skin.Config["template"]?.GetValue<string>();
            var label = $"ContentSkin '{skin.ContentTypeId}/{skin.SkinCode}'";
            if (string.IsNullOrWhiteSpace(template))
            {
                errors.Add($"{label} has no template: add config.template or {FolderName}/{skin.ContentTypeId}/{skin.SkinCode}.liquid.");
            }
            else if (!Parser.TryParse(template, out _, out var parseError))
            {
                errors.Add($"{label} template is not valid Fluid: {parseError}");
            }
        }

        if (inlined && root is not null)
        {
            var index = entries.FindIndex(entry => entry.EntryName == SharedEntryName);
            if (index >= 0)
                entries[index] = entries[index] with { Content = new UTF8Encoding(false).GetBytes(root.ToJsonString(OutputOptions)) };
        }

        return new ThemeSkinsScan(errors);
    }

    private static List<SkinComponent> FindSkins(JsonNode? root)
    {
        var skins = new List<SkinComponent>();
        if (root?["reusableComponents"] is not JsonArray components)
            return skins;

        foreach (var component in components.OfType<JsonObject>())
        {
            if (!string.Equals(component["componentType"]?.GetValue<string>(), ContentSkinComponentType, StringComparison.OrdinalIgnoreCase)
                || component["config"] is not JsonObject config)
            {
                continue;
            }

            var typeId = config["contentTypeId"]?.GetValue<string>();
            var skinCode = config["skinCode"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(typeId) && !string.IsNullOrWhiteSpace(skinCode))
                skins.Add(new SkinComponent(typeId, skinCode, config));
        }

        return skins;
    }

    private sealed record SkinComponent(string ContentTypeId, string SkinCode, JsonObject Config);
}
