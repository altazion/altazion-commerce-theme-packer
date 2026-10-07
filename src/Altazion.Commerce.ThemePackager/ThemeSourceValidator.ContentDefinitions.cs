using System.Text.Json;
using System.Text.RegularExpressions;

namespace Altazion.Commerce.ThemePackager;

internal static partial class ThemeSourceValidator
{
    private const string ContentTypesFolder = "content-types";
    private const string DamFolder = "dam";

    private static readonly Regex ContentTypeIdPattern = new(
        "^[a-z][a-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ColumnCodePattern = new(
        "^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DamCodePattern = new(
        "^[A-Za-z][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ColumnDataTypeNames =
        { "Text", "Boolean", "Decimal", "Date", "Location", "Media", "RawData", "Link" };

    private static readonly HashSet<string> ColumnDataTypes = new(ColumnDataTypeNames, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ContentTypeVersioningStrategies = new(StringComparer.OrdinalIgnoreCase)
    {
        "None", "Draft", "Branches",
    };

    private static readonly HashSet<string> DamDocumentKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Custom", "Products", "WebPageContent", "TechnicalDocumentation", "BlogArticle", "CommercialOperation", "DigitalSignage",
    };

    private static readonly HashSet<string> OwnerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "definitionOwnerNamespace", "definitionOwnerCode",
    };

    private static readonly HashSet<string> ContentTypeImportManagedKeys = new(OwnerKeys, StringComparer.OrdinalIgnoreCase)
    {
        "isExtern", "externAppCode", "externAppNamespace",
    };

    private static readonly HashSet<string> ColumnImportManagedKeys = new(OwnerKeys, StringComparer.OrdinalIgnoreCase)
    {
        "isStandard", "externAppCode", "externAppService", "externAppReference",
    };

    private static readonly HashSet<string> ExtendingTypeAllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "extends", "columns",
    };

    private static void ValidateContentDefinitions(ValidationState state)
    {
        foreach (var file in EnumerateTopLevelJsonFiles(state, ContentTypesFolder))
            ValidateContentTypeFile(state, file);

        foreach (var file in EnumerateTopLevelJsonFiles(state, DamFolder))
            ValidateDamCollectionFile(state, file);
    }

    private static IEnumerable<string> EnumerateTopLevelJsonFiles(ValidationState state, string folderName)
    {
        var directory = Path.Combine(state.SourceDirectory, folderName);
        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateContentTypeFile(ValidationState state, string file)
    {
        using var document = OpenDocument(state, file);
        if (document is null)
            return;

        var relativePath = state.GetRelativePath(file);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            state.Errors.Add($"{relativePath} must contain a content type object.");
            return;
        }

        var id = ReadRequiredNonEmptyString(root, "id", relativePath, state.Errors);
        if (id is not null)
        {
            if (!ContentTypeIdPattern.IsMatch(id))
                state.Errors.Add($"{relativePath}.id '{id}' is invalid: use lowercase letters, digits, '-' or '_' and start with a letter.");

            ValidateFileNameMatchesCode(state, file, relativePath, id, "id");
            RegisterKey(id, relativePath, state.ContentTypes, state.Errors, "content type id");
        }

        ReportImportManagedKeys(root, ContentTypeImportManagedKeys, relativePath, state.Errors);

        var isExtending = false;
        if (root.TryGetProperty("extends", out var extendsElement))
        {
            if (extendsElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isExtending = extendsElement.GetBoolean();
            else
                state.Errors.Add($"{relativePath}.extends must be a boolean.");
        }

        if (isExtending)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (ExtendingTypeAllowedKeys.Contains(property.Name) || ContentTypeImportManagedKeys.Contains(property.Name))
                    continue;

                state.Errors.Add($"{relativePath}.{property.Name} is not allowed when 'extends' is true: a type owned by another module only accepts added columns.");
            }
        }
        else
        {
            ReadRequiredNonEmptyString(root, "name", relativePath, state.Errors);
        }

        var columnCodes = ValidateContentTypeColumns(state, relativePath, root);
        if (isExtending)
            return;

        if (TryReadNonEmptyString(root, "titleColumnCode", out var titleColumnCode) && !columnCodes.Contains(titleColumnCode))
            state.Errors.Add($"{relativePath}.titleColumnCode '{titleColumnCode}' does not match any declared column.");

        if (root.TryGetProperty("versioning", out var versioning)
            && (versioning.ValueKind != JsonValueKind.String || !ContentTypeVersioningStrategies.Contains(versioning.GetString() ?? string.Empty)))
        {
            state.Errors.Add($"{relativePath}.versioning must be one of: {string.Join(", ", ContentTypeVersioningStrategies.OrderBy(value => value))}.");
        }

        ValidateContentTypeGroups(state, relativePath, root, columnCodes);
    }

    private static HashSet<string> ValidateContentTypeColumns(ValidationState state, string relativePath, JsonElement root)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("columns", out var columns)
            || columns.ValueKind != JsonValueKind.Array
            || columns.GetArrayLength() == 0)
        {
            state.Errors.Add($"{relativePath}.columns must be an array with at least one column.");
            return codes;
        }

        var codeContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var column in columns.EnumerateArray())
        {
            var context = $"{relativePath} columns[{index}]";
            index++;

            if (column.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context} must be an object.");
                continue;
            }

            ReportImportManagedKeys(column, ColumnImportManagedKeys, context, state.Errors);

            var code = ReadRequiredNonEmptyString(column, "code", context, state.Errors);
            if (code is not null)
            {
                if (!ColumnCodePattern.IsMatch(code))
                    state.Errors.Add($"{context}.code '{code}' is invalid: use letters, digits or '_' and start with a letter.");

                RegisterKey(code, context, codeContexts, state.Errors, "column code");
                codes.Add(code);
            }

            var dataType = ReadRequiredNonEmptyString(column, "dataType", context, state.Errors);
            if (dataType is not null && !ColumnDataTypes.Contains(dataType))
                state.Errors.Add($"{context}.dataType '{dataType}' is not supported. Expected one of: {string.Join(", ", ColumnDataTypeNames)}.");

            if (column.TryGetProperty("isComputed", out var isComputed) && isComputed.ValueKind == JsonValueKind.True)
                ReadRequiredNonEmptyString(column, "expression", context, state.Errors);
        }

        return codes;
    }

    private static void ValidateContentTypeGroups(ValidationState state, string relativePath, JsonElement root, HashSet<string> columnCodes)
    {
        if (!root.TryGetProperty("groups", out var groups))
            return;

        if (groups.ValueKind != JsonValueKind.Array)
        {
            state.Errors.Add($"{relativePath}.groups must be an array when present.");
            return;
        }

        var index = 0;
        foreach (var group in groups.EnumerateArray())
        {
            var context = $"{relativePath} groups[{index}]";
            index++;

            if (group.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context} must be an object.");
                continue;
            }

            ReadRequiredNonEmptyString(group, "name", context, state.Errors);

            if (!group.TryGetProperty("columns", out var groupColumns))
                continue;

            if (groupColumns.ValueKind != JsonValueKind.Array)
            {
                state.Errors.Add($"{context}.columns must be an array when present.");
                continue;
            }

            foreach (var groupColumn in groupColumns.EnumerateArray())
            {
                if (groupColumn.ValueKind != JsonValueKind.String || !columnCodes.Contains(groupColumn.GetString() ?? string.Empty))
                    state.Errors.Add($"{context}.columns contains '{groupColumn}' which does not match any declared column.");
            }
        }
    }

    private static void ValidateDamCollectionFile(ValidationState state, string file)
    {
        using var document = OpenDocument(state, file);
        if (document is null)
            return;

        var relativePath = state.GetRelativePath(file);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            state.Errors.Add($"{relativePath} must contain a DAM collection object.");
            return;
        }

        var code = ReadRequiredNonEmptyString(root, "code", relativePath, state.Errors);
        if (code is not null)
        {
            if (!ContentTypeIdPattern.IsMatch(code))
                state.Errors.Add($"{relativePath}.code '{code}' is invalid: use lowercase letters, digits, '-' or '_' and start with a letter.");

            ValidateFileNameMatchesCode(state, file, relativePath, code, "code");
            RegisterKey(code, relativePath, state.DamCollections, state.Errors, "DAM collection code");
        }

        ReportImportManagedKeys(root, OwnerKeys, relativePath, state.Errors);
        ReadRequiredNonEmptyString(root, "name", relativePath, state.Errors);

        if (root.TryGetProperty("kind", out var kind)
            && (kind.ValueKind != JsonValueKind.String || !DamDocumentKinds.Contains(kind.GetString() ?? string.Empty)))
        {
            state.Errors.Add($"{relativePath}.kind must be one of: {string.Join(", ", DamDocumentKinds.OrderBy(value => value))}.");
        }

        ValidateDamCodedItems(state, relativePath, root, "groups", validateGroup: ValidateDamGroup);
        ValidateDamCodedItems(state, relativePath, root, "properties", validateGroup: null);
        ValidateDamCodedItems(state, relativePath, root, "folders", validateGroup: null);
    }

    private static void ValidateDamCodedItems(
        ValidationState state,
        string relativePath,
        JsonElement root,
        string propertyName,
        Action<ValidationState, string, JsonElement>? validateGroup)
    {
        if (!root.TryGetProperty(propertyName, out var items))
            return;

        if (items.ValueKind != JsonValueKind.Array)
        {
            state.Errors.Add($"{relativePath}.{propertyName} must be an array when present.");
            return;
        }

        var codeContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var context = $"{relativePath} {propertyName}[{index}]";
            index++;

            if (item.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context} must be an object.");
                continue;
            }

            ReportImportManagedKeys(item, OwnerKeys, context, state.Errors);

            var code = ReadRequiredNonEmptyString(item, "code", context, state.Errors);
            if (code is not null)
            {
                if (!DamCodePattern.IsMatch(code))
                    state.Errors.Add($"{context}.code '{code}' is invalid: use letters, digits, '-' or '_' and start with a letter.");

                RegisterKey(code, context, codeContexts, state.Errors, $"DAM {propertyName.TrimEnd('s')} code");
            }

            validateGroup?.Invoke(state, context, item);
        }
    }

    private static void ValidateDamGroup(ValidationState state, string context, JsonElement group)
    {
        if (!group.TryGetProperty("allowedMimeTypes", out var mimeTypes))
            return;

        if (mimeTypes.ValueKind != JsonValueKind.Array
            || mimeTypes.EnumerateArray().Any(mime => mime.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(mime.GetString())))
        {
            state.Errors.Add($"{context}.allowedMimeTypes must be an array of non-empty strings.");
        }
    }

    private static void ValidateFileNameMatchesCode(ValidationState state, string file, string relativePath, string code, string propertyName)
    {
        var expectedFileName = code + ".json";
        if (!string.Equals(Path.GetFileName(file), expectedFileName, StringComparison.OrdinalIgnoreCase))
            state.Errors.Add($"{relativePath} must be named '{expectedFileName}' to match its {propertyName}.");
    }

    private static void ReportImportManagedKeys(JsonElement element, HashSet<string> managedKeys, string context, ICollection<string> errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (managedKeys.Contains(property.Name))
                errors.Add($"{context}.{property.Name} is set by the import and cannot be declared in the theme source.");
        }
    }

    private static void ValidateContentReferences(ValidationState state)
    {
        ValidateContentSkinReferences(state);
        ValidatePageContentTypeReferences(state);
    }

    private static void ValidateContentSkinReferences(ValidationState state)
    {
        var sharedPath = Path.Combine(state.SourceDirectory, "theme.shared.json");
        if (!File.Exists(sharedPath))
            return;

        using var document = OpenDocumentQuietly(sharedPath);
        if (document is null
            || !document.RootElement.TryGetProperty("reusableComponents", out var components)
            || components.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var relativePath = state.GetRelativePath(sharedPath);
        var skinKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var component in components.EnumerateArray())
        {
            var context = $"{relativePath} reusableComponents[{index}]";
            index++;

            if (component.ValueKind != JsonValueKind.Object
                || !TryReadNonEmptyString(component, "componentType", out var componentType)
                || !string.Equals(componentType, "ContentSkin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!component.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            {
                state.Errors.Add($"{context}.config must be an object with contentTypeId and skinCode.");
                continue;
            }

            var contentTypeId = ReadRequiredNonEmptyString(config, "contentTypeId", $"{context}.config", state.Errors);
            var skinCode = ReadRequiredNonEmptyString(config, "skinCode", $"{context}.config", state.Errors);

            if (contentTypeId is not null)
                EnsureContentTypeDeclared(state, $"{context}.config.contentTypeId", contentTypeId);

            if (contentTypeId is not null && skinCode is not null)
                RegisterKey($"{contentTypeId}/{skinCode}", context, skinKeys, state.Errors, "content skin");
        }
    }

    private static void ValidatePageContentTypeReferences(ValidationState state)
    {
        var pagesDirectory = Path.Combine(state.SourceDirectory, "pages");
        if (!Directory.Exists(pagesDirectory))
            return;

        foreach (var pageFile in Directory.EnumerateFiles(pagesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = OpenDocumentQuietly(pageFile);
            if (document is null
                || !document.RootElement.TryGetProperty("pageDefinition", out var pageDefinition)
                || pageDefinition.ValueKind != JsonValueKind.Object
                || !pageDefinition.TryGetProperty("config", out var config)
                || config.ValueKind != JsonValueKind.Object
                || !TryReadNonEmptyString(config, "contentTypeId", out var contentTypeId))
            {
                continue;
            }

            EnsureContentTypeDeclared(state, $"{state.GetRelativePath(pageFile)} pageDefinition.config.contentTypeId", contentTypeId);
        }
    }

    private static void EnsureContentTypeDeclared(ValidationState state, string context, string contentTypeId)
    {
        if (!state.ContentTypes.ContainsKey(contentTypeId))
        {
            state.Errors.Add($"{context} points to unknown content type '{contentTypeId}'. Declare it in {ContentTypesFolder}/ (with \"extends\": true for a type owned by another module).");
        }
    }

    private static JsonDocument? OpenDocumentQuietly(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return JsonDocument.Parse(stream);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
