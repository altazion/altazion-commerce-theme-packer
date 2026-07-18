using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Altazion.Commerce.ThemePackager;

internal static class TemplateSourcePackager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static ThemePackResult Pack(string sourceDirectory, PackCommandOptions options)
    {
        var template = ReadTemplateDocument(sourceDirectory);
        ValidateTemplate(template, sourceDirectory);

        if (options.IsDryRun)
        {
            var dryRunArtifacts = new List<TemplatePackArtifact>();
            var dryRunEntries = 0;

            foreach (var profileRef in template.Profiles)
            {
                var profileSourceDirectory = ResolveProfileSourceDirectory(sourceDirectory, profileRef.Source!);
                var package = BuildTemplatePackage(template.Template!, profileRef, profileSourceDirectory);
                dryRunEntries += package.EntryCount;
                dryRunArtifacts.Add(new TemplatePackArtifact(profileRef.Code!, profileRef.Name ?? profileRef.Code!, string.Empty, package.EntryCount, 0));
            }

            return new ThemePackResult(
                string.Empty,
                0,
                dryRunEntries,
                template.Template!.Code!,
                template.Template.Name!,
                PackSourceKind.Template,
                Array.Empty<string>(),
                dryRunArtifacts);
        }

        var outputDirectory = ResolveOutputDirectory(options.OutputFile, template.Template!.Name!);
        Directory.CreateDirectory(outputDirectory);

        var artifacts = new List<TemplatePackArtifact>();
        var generatedFiles = new List<string>();
        var totalEntries = 0;
        long totalSize = 0;

        foreach (var profileRef in template.Profiles)
        {
            var profileSourceDirectory = ResolveProfileSourceDirectory(sourceDirectory, profileRef.Source!);
            var package = BuildTemplatePackage(template.Template, profileRef, profileSourceDirectory);
            var outputFile = Path.Combine(outputDirectory, BuildPackageFileName(template.Template.Code!, profileRef.Code!));

            if (File.Exists(outputFile))
                File.Delete(outputFile);

            WritePackageArchive(outputFile, package);

            var fileInfo = new FileInfo(outputFile);
            artifacts.Add(new TemplatePackArtifact(profileRef.Code!, profileRef.Name ?? profileRef.Code!, outputFile, package.EntryCount, fileInfo.Length));
            generatedFiles.Add(outputFile);
            totalEntries += package.EntryCount;
            totalSize += fileInfo.Length;
        }

        return new ThemePackResult(
            outputDirectory,
            totalSize,
            totalEntries,
            template.Template.Code!,
            template.Template.Name!,
            PackSourceKind.Template,
            generatedFiles,
            artifacts);
    }

    private static TemplateSourceDocument ReadTemplateDocument(string sourceDirectory)
    {
        var path = Path.Combine(sourceDirectory, "template.json");
        if (!File.Exists(path))
            throw new ThemePackagerException($"template.json not found in '{sourceDirectory}'.");

        return DeserializeFile<TemplateSourceDocument>(path, "template.json");
    }

    private static void ValidateTemplate(TemplateSourceDocument template, string sourceDirectory)
    {
        if (template.Template == null)
            throw new ThemePackagerException("template.json must contain a 'template' object.");

        if (string.IsNullOrWhiteSpace(template.Template.Code) || string.IsNullOrWhiteSpace(template.Template.Name))
            throw new ThemePackagerException("template.json must contain non-empty template.code and template.name values.");

        if (template.Profiles == null || template.Profiles.Count == 0)
            throw new ThemePackagerException("template.json must declare at least one profile.");

        foreach (var profile in template.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Code) || string.IsNullOrWhiteSpace(profile.Source))
                throw new ThemePackagerException("Each template profile must contain non-empty code and source values.");

            var profileSourceDirectory = ResolveProfileSourceDirectory(sourceDirectory, profile.Source);
            var profileJsonPath = Path.Combine(profileSourceDirectory, "profile.json");
            if (!File.Exists(profileJsonPath))
                throw new ThemePackagerException($"profile.json not found for profile '{profile.Code}' in '{profileSourceDirectory}'.");
        }
    }

    private static string ResolveProfileSourceDirectory(string rootSourceDirectory, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(rootSourceDirectory, relativePath));
        if (!Directory.Exists(fullPath))
            throw new ThemePackagerException($"Profile source directory not found: '{fullPath}'.");

        return fullPath;
    }

    private static string ResolveOutputDirectory(string? outputPath, string templateName)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
            return Path.GetFullPath(outputPath);

        var distDirectory = Path.Combine(Directory.GetCurrentDirectory(), "dist");
        return Path.Combine(distDirectory, SanitizePathSegment(templateName));
    }

    private static string BuildPackageFileName(string templateCode, string profileCode)
    {
        return $"{SanitizePathSegment(templateCode)}-{SanitizePathSegment(profileCode)}.altztemplate";
    }

    private static TemplatePackage BuildTemplatePackage(TemplateDefinition templateDefinition, TemplateProfileReference profileRef, string profileSourceDirectory)
    {
        var profileDocument = DeserializeFile<TemplateProfileSourceDocument>(Path.Combine(profileSourceDirectory, "profile.json"), $"profile '{profileRef.Code}'");
        if (profileDocument.Profile == null)
            throw new ThemePackagerException($"profile.json for '{profileRef.Code}' must contain a 'profile' object.");

        var profile = profileDocument.Profile;
        if (string.IsNullOrWhiteSpace(profile.Code))
            profile.Code = profileRef.Code;
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = profileRef.Name ?? profileRef.Code;

        var baseDirectory = Path.Combine(profileSourceDirectory, profile.BasePath ?? "base");
        var assetsDirectory = Path.Combine(profileSourceDirectory, profile.AssetsPath ?? "assets");
        if (!Directory.Exists(baseDirectory))
            throw new ThemePackagerException($"Base directory not found for profile '{profile.Code}': '{baseDirectory}'.");
        if (!Directory.Exists(assetsDirectory))
            throw new ThemePackagerException($"Assets directory not found for profile '{profile.Code}': '{assetsDirectory}'.");

        var themeGeneral = ReadJsonNode(Path.Combine(baseDirectory, "theme.general.json"), $"profile '{profile.Code}' base theme.general.json");
        var themeNode = themeGeneral?["theme"]?.DeepClone();
        if (themeNode == null)
            throw new ThemePackagerException($"theme.general.json for profile '{profile.Code}' must contain a 'theme' object.");

        var themeSharedPath = Path.Combine(baseDirectory, "theme.shared.json");
        var sharedNode = File.Exists(themeSharedPath)
            ? ReadJsonNode(themeSharedPath, $"profile '{profile.Code}' base theme.shared.json")
            : new JsonObject();

        var baseSnapshot = new JsonObject
        {
            ["theme"] = themeNode,
            ["pageDefinitions"] = new JsonArray(),
            ["routes"] = new JsonArray(),
            ["reusableComponents"] = CloneOrEmptyArray(sharedNode?["reusableComponents"]),
        };

        var menus = ReadMenus(Path.Combine(baseDirectory, "menus"));
        var commonAssets = ReadPackAssets(profile.Code!, profile.CommonAssets, assetsDirectory);

        var pageManifests = new JsonArray();
        var variantDocuments = new List<TemplateVariantDocument>();

        foreach (var page in profile.Pages ?? Enumerable.Empty<TemplatePageDefinition>())
        {
            if (string.IsNullOrWhiteSpace(page.PageKey))
                throw new ThemePackagerException($"Profile '{profile.Code}' contains a page without pageKey.");

            var variantsArray = new JsonArray();
            Guid? defaultVariantId = null;

            foreach (var variant in page.Variants ?? Enumerable.Empty<TemplatePageVariantReference>())
            {
                if (string.IsNullOrWhiteSpace(variant.Code) || string.IsNullOrWhiteSpace(variant.DocumentPath))
                    throw new ThemePackagerException($"Profile '{profile.Code}' page '{page.PageKey}' contains a variant without code or documentPath.");

                var variantId = CreateStableGuid(templateDefinition.Code!, profile.Code!, page.PageKey, variant.Code);
                if (defaultVariantId == null && string.Equals(page.DefaultVariantCode, variant.Code, StringComparison.OrdinalIgnoreCase))
                    defaultVariantId = variantId;

                var packageDocumentPath = $"pages/{SanitizePathSegment(page.PageKey)}/{SanitizePathSegment(variant.Code)}.json";
                variantsArray.Add(new JsonObject
                {
                    ["id"] = variantId.ToString("D"),
                    ["code"] = variant.Code,
                    ["name"] = variant.Name,
                    ["description"] = variant.Description,
                    ["documentPath"] = packageDocumentPath,
                });

                var sourceVariantPath = Path.Combine(profileSourceDirectory, variant.DocumentPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourceVariantPath))
                    throw new ThemePackagerException($"Variant document not found for profile '{profile.Code}' page '{page.PageKey}' variant '{variant.Code}': '{sourceVariantPath}'.");

                var variantNode = ReadJsonNode(sourceVariantPath, $"variant '{page.PageKey}/{variant.Code}'") ?? new JsonObject();
                var variantAssets = ReadPackAssets(profile.Code!, DeserializeAssets(variantNode["assets"], $"variant '{page.PageKey}/{variant.Code}' assets"), assetsDirectory);

                var variantDocument = new JsonObject
                {
                    ["pageDefinition"] = variantNode["pageDefinition"]?.DeepClone(),
                    ["routes"] = CloneOrEmptyArray(variantNode["routes"]),
                    ["reusableComponents"] = CloneOrEmptyArray(variantNode["reusableComponents"]),
                    ["menus"] = CloneOrEmptyArray(variantNode["menus"]),
                    ["assets"] = JsonSerializer.SerializeToNode(variantAssets.Select(x => x.Manifest).ToList(), SerializerOptions),
                };

                variantDocuments.Add(new TemplateVariantDocument(packageDocumentPath, variantAssets, variantDocument));
            }

            if (defaultVariantId == null && variantsArray.Count > 0)
                defaultVariantId = variantsArray[0]?["id"]?.GetValue<string>() is string value && Guid.TryParse(value, out var parsed)
                    ? parsed
                    : null;

            pageManifests.Add(new JsonObject
            {
                ["pageKey"] = page.PageKey,
                ["name"] = page.Name,
                ["description"] = page.Description,
                ["defaultVariantId"] = defaultVariantId?.ToString("D"),
                ["variants"] = variantsArray,
            });
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "template-1.0",
            ["packageKind"] = "theme-template",
            ["entryId"] = Guid.Empty.ToString("D"),
            ["packageId"] = Guid.Empty.ToString("D"),
            ["version"] = templateDefinition.Version,
            ["name"] = profile.Name,
            ["description"] = string.IsNullOrWhiteSpace(profile.Description) ? profileRef.Description : profile.Description,
            ["publishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["baseSnapshotPath"] = "snapshot.base.json",
            ["menusPath"] = "menus.json",
            ["containsMenus"] = menus.Count > 0,
            ["optionGroups"] = JsonSerializer.SerializeToNode(profile.OptionGroups ?? new List<TemplateOptionGroupDefinition>(), SerializerOptions),
            ["commonAssets"] = JsonSerializer.SerializeToNode(commonAssets.Select(x => x.Manifest).ToList(), SerializerOptions),
            ["pages"] = pageManifests,
            ["presets"] = JsonSerializer.SerializeToNode(profile.Presets ?? new List<TemplatePresetDefinition>(), SerializerOptions),
        };

        var entryCount = 1 + 1 + (menus.Count > 0 ? 1 : 0) + commonAssets.Count + variantDocuments.Count + variantDocuments.Sum(x => x.Assets.Count);
        return new TemplatePackage(manifest, baseSnapshot, menus, commonAssets, variantDocuments, entryCount);
    }

    private static void WritePackageArchive(string outputFile, TemplatePackage package)
    {
        using var archive = ZipFile.Open(outputFile, ZipArchiveMode.Create);

        WriteJsonEntry(archive, "manifest.json", package.Manifest);
        WriteJsonEntry(archive, "snapshot.base.json", package.BaseSnapshot);

        if (package.Menus.Count > 0)
            WriteJsonEntry(archive, "menus.json", package.Menus);

        foreach (var asset in package.CommonAssets)
            WriteBinaryEntry(archive, asset.Manifest.Path!, asset.Content);

        foreach (var variant in package.VariantDocuments)
        {
            WriteJsonEntry(archive, variant.DocumentPath, variant.Document);
            foreach (var asset in variant.Assets)
                WriteBinaryEntry(archive, asset.Manifest.Path!, asset.Content);
        }
    }

    private static void WriteJsonEntry(ZipArchive archive, string entryName, object payload)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, payload, SerializerOptions);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static List<PackedAsset> ReadPackAssets(string profileCode, IEnumerable<TemplateAssetDescriptor>? descriptors, string assetsDirectory)
    {
        var assets = new List<PackedAsset>();

        foreach (var descriptor in descriptors ?? Enumerable.Empty<TemplateAssetDescriptor>())
        {
            if (string.IsNullOrWhiteSpace(descriptor.Path))
                throw new ThemePackagerException($"Profile '{profileCode}' contains an asset without path.");

            var fileName = string.IsNullOrWhiteSpace(descriptor.FileName)
                ? Path.GetFileName(descriptor.Path)
                : descriptor.FileName;
            var physicalPath = Path.Combine(assetsDirectory, fileName!);
            if (!File.Exists(physicalPath))
                throw new ThemePackagerException($"Asset file '{physicalPath}' not found for profile '{profileCode}'.");

            assets.Add(new PackedAsset(ToManifest(descriptor, fileName!), File.ReadAllBytes(physicalPath)));
        }

        return assets;
    }

    private static TemplateAssetDescriptorManifest ToManifest(TemplateAssetDescriptor descriptor, string fileName)
    {
        return new TemplateAssetDescriptorManifest
        {
            Usage = descriptor.Usage,
            Path = descriptor.Path,
            FileName = fileName,
            MimeType = descriptor.MimeType,
            Name = descriptor.Name,
            Description = descriptor.Description,
            GroupCode = descriptor.GroupCode,
            LanguageCode = descriptor.LanguageCode,
            Roles = descriptor.Roles ?? new List<string>(),
            Tags = descriptor.Tags ?? new List<string>(),
            Metadata = descriptor.Metadata ?? new Dictionary<string, string>(),
        };
    }

    private static List<JsonNode> ReadMenus(string menusDirectory)
    {
        var menus = new List<JsonNode>();
        if (!Directory.Exists(menusDirectory))
            return menus;

        foreach (var menuPath in Directory.EnumerateFiles(menusDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            menus.Add(ReadJsonNode(menuPath, $"menu '{Path.GetFileName(menuPath)}'") ?? new JsonObject());
        }

        return menus;
    }

    private static List<TemplateAssetDescriptor> DeserializeAssets(JsonNode? node, string context)
    {
        if (node == null)
            return new List<TemplateAssetDescriptor>();

        try
        {
            return node.Deserialize<List<TemplateAssetDescriptor>>(SerializerOptions) ?? new List<TemplateAssetDescriptor>();
        }
        catch (JsonException ex)
        {
            throw new ThemePackagerException($"{context} is not valid: {ex.Message}");
        }
    }

    private static JsonArray CloneOrEmptyArray(JsonNode? node)
    {
        return node is JsonArray array
            ? (JsonArray)array.DeepClone()
            : new JsonArray();
    }

    private static JsonNode? ReadJsonNode(string path, string context)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException ex)
        {
            throw new ThemePackagerException($"{context} is not valid JSON: {ex.Message}");
        }
    }

    private static T DeserializeFile<T>(string path, string context)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), SerializerOptions)
                ?? throw new ThemePackagerException($"{context} is empty or invalid.");
        }
        catch (JsonException ex)
        {
            throw new ThemePackagerException($"{context} is not valid JSON: {ex.Message}");
        }
    }

    private static Guid CreateStableGuid(params string[] parts)
    {
        var seed = string.Join("|", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static string SanitizePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "artifact";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
                builder.Append(character);
            else
                builder.Append('-');
        }

        return builder.ToString().Trim('-');
    }

    private sealed record TemplatePackage(JsonObject Manifest, JsonObject BaseSnapshot, List<JsonNode> Menus, List<PackedAsset> CommonAssets, List<TemplateVariantDocument> VariantDocuments, int EntryCount);

    private sealed record PackedAsset(TemplateAssetDescriptorManifest Manifest, byte[] Content);

    private sealed record TemplateVariantDocument(string DocumentPath, List<PackedAsset> Assets, JsonObject Document);
}

internal sealed class TemplateSourceDocument
{
    public string? SchemaVersion { get; set; }

    public TemplateDefinition? Template { get; set; }

    public List<TemplateProfileReference> Profiles { get; set; } = new();
}

internal sealed class TemplateDefinition
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? DefaultProfileCode { get; set; }

    public string? Version { get; set; }
}

internal sealed class TemplateProfileReference
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Source { get; set; }

    public string? DefaultPresetCode { get; set; }
}

internal sealed class TemplateProfileSourceDocument
{
    public string? SchemaVersion { get; set; }

    public TemplateProfileDefinition? Profile { get; set; }
}

internal sealed class TemplateProfileDefinition
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? BasePath { get; set; }

    public string? AssetsPath { get; set; }

    public List<TemplateAssetDescriptor> CommonAssets { get; set; } = new();

    public List<TemplateOptionGroupDefinition> OptionGroups { get; set; } = new();

    public List<TemplatePageDefinition> Pages { get; set; } = new();

    public List<TemplatePresetDefinition> Presets { get; set; } = new();
}

internal sealed class TemplateOptionGroupDefinition
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public List<TemplateOptionDefinition> Options { get; set; } = new();
}

internal sealed class TemplateOptionDefinition
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Type { get; set; }

    public bool IsRequired { get; set; }

    public string? DefaultValue { get; set; }

    public List<string> Tokens { get; set; } = new();

    public List<TemplateOptionChoiceDefinition> Choices { get; set; } = new();
}

internal sealed class TemplateOptionChoiceDefinition
{
    public string? Value { get; set; }

    public string? Label { get; set; }

    public bool IsDefault { get; set; }
}

internal sealed class TemplatePageDefinition
{
    public string? PageKey { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? DefaultVariantCode { get; set; }

    public List<TemplatePageVariantReference> Variants { get; set; } = new();
}

internal sealed class TemplatePageVariantReference
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? DocumentPath { get; set; }
}

internal sealed class TemplatePresetDefinition
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public Dictionary<string, string> PageVariantSelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> OptionValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class TemplateAssetDescriptor
{
    public string? Usage { get; set; }

    public string? Path { get; set; }

    public string? FileName { get; set; }

    public string? MimeType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? GroupCode { get; set; }

    public string? LanguageCode { get; set; }

    public List<string>? Roles { get; set; }

    public List<string>? Tags { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }
}

internal sealed class TemplateAssetDescriptorManifest
{
    public string? Usage { get; set; }

    public string? Path { get; set; }

    public string? FileName { get; set; }

    public string? MimeType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? GroupCode { get; set; }

    public string? LanguageCode { get; set; }

    public List<string> Roles { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}