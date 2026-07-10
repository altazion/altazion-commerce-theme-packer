using System.Text.Json;
using System.Text.RegularExpressions;

namespace Altazion.Commerce.ThemePackager;

internal static class ThemeSourceValidator
{
    private static readonly Regex DevThemeResourcePattern = new(
        "(?:(?<=^)|(?<=[\\s\"'=\\(]))/dev/theme/(?<path>[^\"'\\s?#<>]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SlotNamePattern = new(
        "^[A-Za-z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedRootJsonFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "theme.general.json",
        "theme.shared.json",
        "theme.seo.json",
        "theme.marketing.json",
    };

    private static readonly HashSet<string> DisallowedAssetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
    };

    private static readonly HashSet<string> DisallowedAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp",
        ".bak",
        ".orig",
        ".psd",
        ".ai",
    };

    public static void Validate(string sourceDirectory, ThemeMetadata themeMetadata, IReadOnlyCollection<ThemePackEntry> entries)
    {
        if (!Guid.TryParse(themeMetadata.ThemeId, out var themeId))
            throw new ThemePackagerException("theme.general.json must contain a valid theme.id GUID.");

        var state = new ValidationState(sourceDirectory, themeId);

        ValidateJsonFileLayout(state);
        ValidateCollectedEntries(state, entries);
        ValidateGenericJsonFile(state, Path.Combine(sourceDirectory, "theme.general.json"));
        ValidateSharedComponents(state);
        ValidateGenericJsonFile(state, Path.Combine(sourceDirectory, "theme.seo.json"));
        ValidateGenericJsonFile(state, Path.Combine(sourceDirectory, "theme.marketing.json"));
        ValidatePages(state);
        ValidateMenus(state);
        ValidatePendingRouteTargets(state);

        if (state.Errors.Count == 0)
            return;

        throw new ThemePackagerException(
            "Theme validation failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, state.Errors.Select(error => $"- {error}")));
    }

    private static void ValidateJsonFileLayout(ValidationState state)
    {
        foreach (var jsonFile in Directory.EnumerateFiles(state.SourceDirectory, "*.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = state.GetRelativePath(jsonFile);
            if (AllowedRootJsonFiles.Contains(relativePath))
                continue;

            if (IsTopLevelCollectionFile(relativePath, "pages") || IsTopLevelCollectionFile(relativePath, "menus"))
                continue;

            state.Errors.Add($"{relativePath} is not part of the supported theme pack structure.");
        }
    }

    private static bool IsTopLevelCollectionFile(string relativePath, string folderName)
    {
        var parts = relativePath.Split('/');
        return parts.Length == 2
            && string.Equals(parts[0], folderName, StringComparison.OrdinalIgnoreCase)
            && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateCollectedEntries(ValidationState state, IReadOnlyCollection<ThemePackEntry> entries)
    {
        var entryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            RegisterKey(entry.EntryName, state.GetRelativePath(entry.FullPath), entryNames, state.Errors, "archive entry path");

            if (!entry.EntryName.StartsWith("assets/", StringComparison.Ordinal))
                continue;

            var fileName = Path.GetFileName(entry.FullPath);
            var extension = Path.GetExtension(fileName);
            if (DisallowedAssetNames.Contains(fileName)
                || DisallowedAssetExtensions.Contains(extension)
                || fileName.StartsWith("~$", StringComparison.Ordinal)
                || fileName.EndsWith('~'))
            {
                state.Errors.Add($"{state.GetRelativePath(entry.FullPath)} should not be packaged as an asset.");
            }
        }
    }

    private static void ValidateSharedComponents(ValidationState state)
    {
        var sharedPath = Path.Combine(state.SourceDirectory, "theme.shared.json");
        if (!File.Exists(sharedPath))
            return;

        using var document = OpenDocument(state, sharedPath);
        if (document is null)
            return;

        var root = document.RootElement;
        if (!root.TryGetProperty("reusableComponents", out var reusableComponents) || reusableComponents.ValueKind != JsonValueKind.Array)
        {
            state.Errors.Add($"{state.GetRelativePath(sharedPath)} must contain a 'reusableComponents' array.");
            ValidateLocalResourceReferences(state, sharedPath, root);
            return;
        }

        var index = 0;
        foreach (var component in reusableComponents.EnumerateArray())
        {
            var context = $"{state.GetRelativePath(sharedPath)} reusableComponents[{index}]";
            if (component.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context} must be an object.");
                index++;
                continue;
            }

            var componentId = ReadRequiredGuid(component, "id", context, state.Errors);
            if (componentId is { } parsedComponentId)
                RegisterKey(parsedComponentId, context, state.ReusableComponents, state.Errors, "shared component GUID");

            var requestedRenderMode = ReadRequiredNonEmptyString(component, "requestedRenderMode", context, state.Errors);
            if (componentId is { } parsedId && requestedRenderMode is not null)
                state.ReusableComponentRenderModes[parsedId] = requestedRenderMode;

            ValidateThemeId(component, context, state);
            index++;
        }

        ValidateLocalResourceReferences(state, sharedPath, root);
    }

    private static void ValidatePages(ValidationState state)
    {
        var pagesDirectory = Path.Combine(state.SourceDirectory, "pages");
        if (!Directory.Exists(pagesDirectory))
            return;

        foreach (var pageFile in Directory.EnumerateFiles(pagesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = OpenDocument(state, pageFile);
            if (document is null)
                continue;

            var root = document.RootElement;
            var relativePath = state.GetRelativePath(pageFile);

            if (!root.TryGetProperty("pageDefinition", out var pageDefinition) || pageDefinition.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{relativePath} must contain a 'pageDefinition' object.");
                ValidateLocalResourceReferences(state, pageFile, root);
                continue;
            }

            var definitionContext = $"{relativePath} pageDefinition";
            var definitionId = ReadRequiredGuid(pageDefinition, "id", definitionContext, state.Errors);
            if (definitionId is { } parsedDefinitionId)
                RegisterKey(parsedDefinitionId, definitionContext, state.PageDefinitions, state.Errors, "page definition GUID");

            if (TryReadNestedNonEmptyString(pageDefinition, out var pageImportKey, "metadata", "extensions", "importKey"))
                RegisterKey(pageImportKey, definitionContext, state.PageImportKeys, state.Errors, "page importKey");

            ValidateThemeId(pageDefinition, definitionContext, state);

            var nodeIds = new Dictionary<Guid, string>();
            var usedNodeIds = new HashSet<Guid>();

            if (pageDefinition.TryGetProperty("nodes", out var nodesElement))
            {
                if (nodesElement.ValueKind != JsonValueKind.Array)
                {
                    state.Errors.Add($"{definitionContext}.nodes must be an array when present.");
                }
                else
                {
                    ValidateNodes(state, relativePath, nodesElement, nodeIds);
                }
            }

            if (pageDefinition.TryGetProperty("composition", out var compositionElement))
            {
                if (compositionElement.ValueKind != JsonValueKind.Array)
                {
                    state.Errors.Add($"{definitionContext}.composition must be an array when present.");
                }
                else
                {
                    ValidateComposition(state, relativePath, compositionElement, nodeIds, usedNodeIds);
                }
            }

            foreach (var localNode in nodeIds)
            {
                if (!usedNodeIds.Contains(localNode.Key))
                    state.Errors.Add($"{localNode.Value} is never referenced from pageDefinition.composition.");
            }

            if (root.TryGetProperty("routes", out var routesElement))
            {
                if (routesElement.ValueKind != JsonValueKind.Array)
                {
                    state.Errors.Add($"{relativePath} routes must be an array when present.");
                }
                else
                {
                    ValidateRoutes(state, relativePath, routesElement);
                }
            }

            ValidateLocalResourceReferences(state, pageFile, root);
        }
    }

    private static void ValidateMenus(ValidationState state)
    {
        var menusDirectory = Path.Combine(state.SourceDirectory, "menus");
        if (!Directory.Exists(menusDirectory))
            return;

        foreach (var menuFile in Directory.EnumerateFiles(menusDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = OpenDocument(state, menuFile);
            if (document is null)
                continue;

            var root = document.RootElement;
            var relativePath = state.GetRelativePath(menuFile);
            if (root.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{relativePath} must contain a menu object.");
                continue;
            }

            var menuContext = $"{relativePath} menu";
            var menuCode = ReadRequiredNonEmptyString(root, "code", menuContext, state.Errors);
            if (menuCode is not null)
                RegisterKey(menuCode, menuContext, state.MenuCodes, state.Errors, "menu code");

            ReadRequiredNonEmptyString(root, "name", menuContext, state.Errors);

            if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
            {
                state.Errors.Add($"{menuContext}.nodes must be an array.");
                ValidateLocalResourceReferences(state, menuFile, root);
                continue;
            }

            ValidateMenuNodes(state, relativePath, nodesElement, $"{menuContext}.nodes");
            ValidateLocalResourceReferences(state, menuFile, root);
        }
    }

    private static void ValidateMenuNodes(ValidationState state, string relativePath, JsonElement nodesElement, string context)
    {
        var siblingCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var siblingOrders = new Dictionary<int, string>();

        var index = 0;
        foreach (var node in nodesElement.EnumerateArray())
        {
            var nodeContext = $"{context}[{index}]";
            if (node.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{nodeContext} must be an object.");
                index++;
                continue;
            }

            var code = ReadRequiredNonEmptyString(node, "code", nodeContext, state.Errors);
            if (code is not null)
                RegisterKey(code, nodeContext, siblingCodes, state.Errors, "menu node code");

            var order = ReadRequiredInt32(node, "order", nodeContext, state.Errors);
            if (order is { } parsedOrder)
            {
                if (parsedOrder < 0)
                {
                    state.Errors.Add($"{nodeContext}.order must be greater than or equal to 0.");
                }
                else
                {
                    RegisterKey(parsedOrder, nodeContext, siblingOrders, state.Errors, "menu node order");
                }
            }

            var hasChildren = false;
            if (node.TryGetProperty("nodes", out var childNodesElement))
            {
                if (childNodesElement.ValueKind != JsonValueKind.Array)
                {
                    state.Errors.Add($"{nodeContext}.nodes must be an array when present.");
                }
                else if (childNodesElement.GetArrayLength() > 0)
                {
                    hasChildren = true;
                    ValidateMenuNodes(state, relativePath, childNodesElement, $"{nodeContext}.nodes");
                }
            }

            if (!hasChildren)
                ReadRequiredNonEmptyString(node, "targetUrl", nodeContext, state.Errors);

            index++;
        }
    }

    private static void ValidateGenericJsonFile(ValidationState state, string filePath)
    {
        if (!File.Exists(filePath))
            return;

        using var document = OpenDocument(state, filePath);
        if (document is null)
            return;

        ValidateLocalResourceReferences(state, filePath, document.RootElement);
    }

    private static void ValidateNodes(
        ValidationState state,
        string relativePath,
        JsonElement nodesElement,
        Dictionary<Guid, string> localNodeIds)
    {
        var index = 0;
        foreach (var node in nodesElement.EnumerateArray())
        {
            var context = $"{relativePath} pageDefinition.nodes[{index}]";
            if (node.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context} must be an object.");
                index++;
                continue;
            }

            var nodeId = ReadRequiredGuid(node, "id", context, state.Errors);
            if (nodeId is { } parsedNodeId)
            {
                RegisterKey(parsedNodeId, context, state.Nodes, state.Errors, "composition node GUID");
                RegisterKey(parsedNodeId, context, localNodeIds, state.Errors, "page node GUID");
            }

            var requestedRenderMode = ReadRequiredNonEmptyString(node, "requestedRenderMode", context, state.Errors);
            var hasComponentType = TryReadNonEmptyString(node, "componentType", out _);
            if (node.TryGetProperty("reference", out var referenceElement))
            {
                if (referenceElement.ValueKind != JsonValueKind.Object)
                {
                    state.Errors.Add($"{context}.reference must be an object.");
                }
                else if (referenceElement.TryGetProperty("reusableComponentId", out _))
                {
                    var reusableComponentId = ReadRequiredGuid(referenceElement, "reusableComponentId", $"{context}.reference", state.Errors);
                    if (reusableComponentId is { } parsedReusableComponentId)
                    {
                        if (!state.ReusableComponents.ContainsKey(parsedReusableComponentId))
                        {
                            state.Errors.Add($"{context}.reference.reusableComponentId points to unknown shared component '{parsedReusableComponentId:D}'.");
                        }
                        else if (requestedRenderMode is not null
                                 && state.ReusableComponentRenderModes.TryGetValue(parsedReusableComponentId, out var componentRenderMode)
                                 && !string.Equals(requestedRenderMode, componentRenderMode, StringComparison.OrdinalIgnoreCase))
                        {
                            state.Errors.Add($"{context}.requestedRenderMode '{requestedRenderMode}' does not match shared component render mode '{componentRenderMode}'.");
                        }
                    }
                }
                else if (!hasComponentType)
                {
                    state.Errors.Add($"{context} must define either componentType or reference.reusableComponentId.");
                }
            }
            else if (!hasComponentType)
            {
                state.Errors.Add($"{context} must define either componentType or reference.reusableComponentId.");
            }

            index++;
        }
    }

    private static void ValidateComposition(
        ValidationState state,
        string relativePath,
        JsonElement compositionElement,
        IReadOnlyDictionary<Guid, string> localNodeIds,
        ISet<Guid> usedNodeIds)
    {
        var compositionNodeIds = new Dictionary<Guid, string>();
        var slotOrders = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        var index = 0;
        foreach (var compositionEntry in compositionElement.EnumerateArray())
        {
            var context = $"{relativePath} pageDefinition.composition[{index}]";
            if (compositionEntry.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context} must be an object.");
                index++;
                continue;
            }

            var nodeId = ReadRequiredGuid(compositionEntry, "nodeId", context, state.Errors);
            if (nodeId is { } parsedNodeId)
            {
                if (!localNodeIds.ContainsKey(parsedNodeId))
                {
                    state.Errors.Add($"{context}.nodeId points to unknown page node '{parsedNodeId:D}'.");
                }
                else
                {
                    RegisterKey(parsedNodeId, context, compositionNodeIds, state.Errors, "composition node reference");
                    usedNodeIds.Add(parsedNodeId);
                }
            }

            var slot = ReadRequiredNonEmptyString(compositionEntry, "slot", context, state.Errors);
            if (slot is not null && !SlotNamePattern.IsMatch(slot))
                state.Errors.Add($"{context}.slot '{slot}' is invalid.");

            var order = ReadRequiredInt32(compositionEntry, "order", context, state.Errors);
            if (order is { } parsedOrder)
            {
                if (parsedOrder < 0)
                {
                    state.Errors.Add($"{context}.order must be greater than or equal to 0.");
                }
                else if (slot is not null)
                {
                    if (!slotOrders.TryGetValue(slot, out var ordersForSlot))
                    {
                        ordersForSlot = new Dictionary<int, string>();
                        slotOrders.Add(slot, ordersForSlot);
                    }

                    RegisterKey(parsedOrder, context, ordersForSlot, state.Errors, $"composition order for slot '{slot}'");
                }
            }

            index++;
        }
    }

    private static void ValidateRoutes(ValidationState state, string relativePath, JsonElement routesElement)
    {
        var index = 0;
        foreach (var route in routesElement.EnumerateArray())
        {
            var context = $"{relativePath} routes[{index}]";
            if (route.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context} must be an object.");
                index++;
                continue;
            }

            var routeId = ReadRequiredGuid(route, "id", context, state.Errors);
            if (routeId is { } parsedRouteId)
                RegisterKey(parsedRouteId, context, state.Routes, state.Errors, "route GUID");

            ValidateThemeId(route, context, state);

            if (!route.TryGetProperty("match", out var matchElement) || matchElement.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context}.match must be an object.");
            }
            else
            {
                var path = ReadRequiredNonEmptyString(matchElement, "path", $"{context}.match", state.Errors);
                var matchMode = ReadRequiredNonEmptyString(matchElement, "matchMode", $"{context}.match", state.Errors);
                if (path is not null && matchMode is not null)
                {
                    RegisterKey(
                        $"{matchMode}|{path}",
                        context,
                        state.RouteSignatures,
                        state.Errors,
                        "route signature");
                }
            }

            if (!route.TryGetProperty("target", out var targetElement) || targetElement.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context}.target must be an object.");
                index++;
                continue;
            }

            var definitionId = ReadRequiredGuid(targetElement, "definitionId", $"{context}.target", state.Errors);
            if (definitionId is { } parsedDefinitionId)
                state.PendingRouteTargets.Add((parsedDefinitionId, context));

            index++;
        }
    }

    private static void ValidatePendingRouteTargets(ValidationState state)
    {
        foreach (var pendingRouteTarget in state.PendingRouteTargets)
        {
            if (!state.PageDefinitions.ContainsKey(pendingRouteTarget.DefinitionId))
            {
                state.Errors.Add($"{pendingRouteTarget.Context}.target.definitionId points to unknown page definition '{pendingRouteTarget.DefinitionId:D}'.");
            }
        }
    }

    private static void ValidateThemeId(JsonElement element, string context, ValidationState state)
    {
        var themeId = ReadRequiredGuid(element, "themeId", context, state.Errors);
        if (themeId is { } parsedThemeId && parsedThemeId != state.ThemeId)
        {
            state.Errors.Add($"{context}.themeId must match theme.general.json theme.id '{state.ThemeId:D}'.");
        }
    }

    private static void ValidateLocalResourceReferences(ValidationState state, string filePath, JsonElement element)
    {
        foreach (var resourcePath in EnumerateLocalResourcePaths(element))
        {
            var decodedPath = Uri.UnescapeDataString(resourcePath)
                .Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(state.SourceDirectory, decodedPath));

            if (!File.Exists(fullPath))
            {
                state.Errors.Add($"{state.GetRelativePath(filePath)} references missing resource '/dev/theme/{resourcePath}'.");
            }
        }
    }

    private static IEnumerable<string> EnumerateLocalResourcePaths(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var resourcePath in EnumerateLocalResourcePaths(property.Value))
                        yield return resourcePath;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var resourcePath in EnumerateLocalResourcePaths(item))
                        yield return resourcePath;
                }
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    yield break;

                foreach (Match match in DevThemeResourcePattern.Matches(value))
                {
                    var resourcePath = match.Groups["path"].Value;
                    if (!string.IsNullOrWhiteSpace(resourcePath))
                        yield return resourcePath;
                }
                break;
        }
    }

    private static JsonDocument? OpenDocument(ValidationState state, string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            state.Errors.Add($"{state.GetRelativePath(filePath)} is not valid JSON: {ex.Message}");
            return null;
        }
    }

    private static Guid? ReadRequiredGuid(JsonElement element, string propertyName, string context, ICollection<string> errors)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            errors.Add($"{context} is missing required property '{propertyName}'.");
            return null;
        }

        if (propertyValue.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{context}.{propertyName} must be a GUID string.");
            return null;
        }

        var value = propertyValue.GetString();
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var parsedGuid))
        {
            errors.Add($"{context}.{propertyName} must be a valid GUID.");
            return null;
        }

        return parsedGuid;
    }

    private static string? ReadRequiredNonEmptyString(JsonElement element, string propertyName, string context, ICollection<string> errors)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            errors.Add($"{context} is missing required property '{propertyName}'.");
            return null;
        }

        if (propertyValue.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{context}.{propertyName} must be a string.");
            return null;
        }

        var value = propertyValue.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{context}.{propertyName} must be a non-empty string.");
            return null;
        }

        return value;
    }

    private static int? ReadRequiredInt32(JsonElement element, string propertyName, string context, ICollection<string> errors)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            errors.Add($"{context} is missing required property '{propertyName}'.");
            return null;
        }

        if (propertyValue.ValueKind != JsonValueKind.Number || !propertyValue.TryGetInt32(out var value))
        {
            errors.Add($"{context}.{propertyName} must be an integer.");
            return null;
        }

        return value;
    }

    private static bool TryReadNonEmptyString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var propertyValue) || propertyValue.ValueKind != JsonValueKind.String)
            return false;

        value = propertyValue.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadNestedNonEmptyString(JsonElement element, out string value, params string[] propertyPath)
    {
        value = string.Empty;
        var current = element;

        foreach (var propertyName in propertyPath)
        {
            if (!current.TryGetProperty(propertyName, out current))
                return false;
        }

        if (current.ValueKind != JsonValueKind.String)
            return false;

        value = current.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void RegisterKey<TKey>(
        TKey key,
        string context,
        IDictionary<TKey, string> registry,
        ICollection<string> errors,
        string label)
        where TKey : notnull
    {
        if (registry.TryGetValue(key, out var existingContext))
        {
            errors.Add($"Duplicate {label} '{key}' declared in {context} and {existingContext}.");
            return;
        }

        registry.Add(key, context);
    }

    private sealed class ValidationState
    {
        public ValidationState(string sourceDirectory, Guid themeId)
        {
            SourceDirectory = sourceDirectory;
            ThemeId = themeId;
        }

        public string SourceDirectory { get; }

        public Guid ThemeId { get; }

        public List<string> Errors { get; } = new();

        public Dictionary<Guid, string> ReusableComponents { get; } = new();

        public Dictionary<Guid, string> PageDefinitions { get; } = new();

        public Dictionary<Guid, string> Nodes { get; } = new();

        public Dictionary<Guid, string> Routes { get; } = new();

        public Dictionary<Guid, string> ReusableComponentRenderModes { get; } = new();

        public Dictionary<string, string> PageImportKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> MenuCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> RouteSignatures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(Guid DefinitionId, string Context)> PendingRouteTargets { get; } = new();

        public string GetRelativePath(string filePath)
            => Path.GetRelativePath(SourceDirectory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
    }
}