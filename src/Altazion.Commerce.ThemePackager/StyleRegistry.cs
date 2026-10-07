using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Altazion.Commerce.ThemePackager;

internal sealed class StyleRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<StyleGroup> Groups { get; } = new();

    public List<StyleClass> Classes { get; } = new();

    [JsonIgnore]
    public bool IsEmpty => Groups.Count == 0 && Classes.Count == 0;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

internal sealed class StyleGroup
{
    public string Code { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public Dictionary<string, string>? Labels { get; set; }

    public int? Order { get; set; }

    [JsonIgnore]
    public string Location { get; set; } = string.Empty;
}

internal sealed class StyleClass
{
    public string Code { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public Dictionary<string, string>? Labels { get; set; }

    public string? Description { get; set; }

    public string? Group { get; set; }

    public string Scope { get; set; } = string.Empty;

    public StyleTargets Targets { get; set; } = new();

    public string? ExclusiveGroup { get; set; }

    public List<string>? Requires { get; set; }

    public List<string>? ConflictsWith { get; set; }

    public string? Preview { get; set; }

    public bool Deprecated { get; set; }

    public string? ReplacedBy { get; set; }

    [JsonIgnore]
    public string Location { get; set; } = string.Empty;
}

internal sealed class StyleTargets
{
    public List<string> Tags { get; set; } = new();

    public List<string> Blocks { get; set; } = new();

    public List<string> Skins { get; set; } = new();
}

internal static class StyleRegistryBuilder
{
    public static StyleRegistry Build(IReadOnlyList<CssAnnotation> annotations, ICollection<string> errors)
    {
        var registry = new StyleRegistry();
        foreach (var annotation in annotations)
        {
            if (HasDuplicateTags(annotation, errors))
                continue;

            if (annotation.Kind == CssAnnotationKind.Group)
                AddGroup(registry, annotation, errors);
            else
                AddStyle(registry, annotation, errors);
        }

        return registry;
    }

    private static void AddGroup(StyleRegistry registry, CssAnnotation annotation, ICollection<string> errors)
    {
        if (annotation.Code.Length == 0)
        {
            errors.Add($"{annotation.Location}: @style-group requires a code, for example '@style-group buttons'.");
            return;
        }

        var label = Single(annotation, "label");
        if (label is null)
        {
            errors.Add($"{annotation.Location}: @style-group '{annotation.Code}' requires a @label.");
            return;
        }

        int? order = null;
        var orderText = Single(annotation, "order");
        if (orderText is not null)
        {
            if (!int.TryParse(orderText, out var parsedOrder))
            {
                errors.Add($"{annotation.Location}: @order of @style-group '{annotation.Code}' must be an integer.");
                return;
            }

            order = parsedOrder;
        }

        registry.Groups.Add(new StyleGroup
        {
            Code = annotation.Code,
            Label = label,
            Labels = Languages(annotation, "label"),
            Order = order,
            Location = annotation.Location,
        });
    }

    private static void AddStyle(StyleRegistry registry, CssAnnotation annotation, ICollection<string> errors)
    {
        if (annotation.RulePrelude is null)
        {
            errors.Add($"{annotation.Location}: @style is not followed by a CSS rule.");
            return;
        }

        var classes = CssAnnotationScanner.ExtractClasses(annotation.RulePrelude);
        string code;
        if (annotation.Code.Length > 0)
        {
            if (!classes.Contains(annotation.Code, StringComparer.Ordinal))
            {
                errors.Add($"{annotation.Location}: @style code '{annotation.Code}' is not a class of the following rule.");
                return;
            }

            code = annotation.Code;
        }
        else if (classes.Count == 1)
        {
            code = classes[0];
        }
        else if (classes.Count == 0)
        {
            errors.Add($"{annotation.Location}: the rule following @style has no class selector.");
            return;
        }
        else
        {
            errors.Add($"{annotation.Location}: the rule following @style targets several classes ({string.Join(", ", classes)}): add the class code after @style, for example '@style {classes[0]}'.");
            return;
        }

        var label = Single(annotation, "label");
        var scope = Single(annotation, "scope");
        var hasError = false;
        if (label is null)
        {
            errors.Add($"{annotation.Location}: @style '{code}' requires a @label.");
            hasError = true;
        }

        if (scope is null)
        {
            errors.Add($"{annotation.Location}: @style '{code}' requires a @scope (container or inline).");
            hasError = true;
        }

        if (hasError)
            return;

        var deprecatedTag = annotation.Tags.FirstOrDefault(tag => tag.Name == "deprecated");
        registry.Classes.Add(new StyleClass
        {
            Code = code,
            Label = label!,
            Labels = Languages(annotation, "label"),
            Description = Single(annotation, "description"),
            Group = Single(annotation, "group"),
            Scope = scope!,
            Targets = new StyleTargets
            {
                Tags = SplitList(Single(annotation, "targets")).Select(tag => tag == "*" ? tag : tag.ToLowerInvariant()).ToList(),
                Blocks = SplitList(Single(annotation, "blocks")),
                Skins = SplitList(Single(annotation, "skins")),
            },
            ExclusiveGroup = Single(annotation, "exclusive"),
            Requires = NullIfEmpty(SplitList(Single(annotation, "requires"))),
            ConflictsWith = NullIfEmpty(SplitList(Single(annotation, "conflicts"))),
            Preview = Single(annotation, "preview"),
            Deprecated = deprecatedTag is not null,
            ReplacedBy = deprecatedTag is not null && deprecatedTag.Value.Length > 0 ? deprecatedTag.Value : null,
            Location = annotation.Location,
        });
    }

    private static bool HasDuplicateTags(CssAnnotation annotation, ICollection<string> errors)
    {
        var duplicate = annotation.Tags
            .GroupBy(tag => (tag.Name, Language: tag.Language?.ToLowerInvariant()))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is null)
            return false;

        errors.Add($"{annotation.Location}: tag '@{duplicate.Key.Name}' is declared more than once.");
        return true;
    }

    private static string? Single(CssAnnotation annotation, string name)
    {
        var value = annotation.Tags.FirstOrDefault(tag => tag.Name == name && tag.Language is null)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Dictionary<string, string>? Languages(CssAnnotation annotation, string name)
    {
        var values = annotation.Tags
            .Where(tag => tag.Name == name && tag.Language is not null && tag.Value.Length > 0)
            .ToDictionary(tag => tag.Language!, tag => tag.Value, StringComparer.OrdinalIgnoreCase);
        return values.Count == 0 ? null : values;
    }

    private static List<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static List<string>? NullIfEmpty(List<string> values)
        => values.Count == 0 ? null : values;
}
