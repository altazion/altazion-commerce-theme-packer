using System.Text.RegularExpressions;

namespace Altazion.Commerce.ThemePackager;

internal static partial class ThemeSourceValidator
{
    private static readonly Regex StyleGroupCodePattern = new(
        "^[A-Za-z][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CssClassCodePattern = new(
        "^-?[_a-zA-Z][_a-zA-Z0-9-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StyleTagNamePattern = new(
        "^[a-z][a-z0-9]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StyleBlockNamePattern = new(
        "^[A-Za-z][A-Za-z0-9_.-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlTagTokenPattern = new(
        @"<(/?)([A-Za-z][A-Za-z0-9-]*)(?:\s[^<>]*?)?(/?)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> VoidHtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    private static void ValidateStyleRegistry(ValidationState state, StyleRegistry registry)
    {
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in registry.Groups)
        {
            if (!StyleGroupCodePattern.IsMatch(group.Code))
                state.Errors.Add($"{group.Location}: @style-group code '{group.Code}' is invalid: use letters, digits, '-' or '_' and start with a letter.");

            RegisterKey(group.Code, group.Location, groups, state.Errors, "style group code");
        }

        var classLocations = new Dictionary<string, string>(StringComparer.Ordinal);
        var classes = new Dictionary<string, StyleClass>(StringComparer.Ordinal);
        foreach (var styleClass in registry.Classes)
        {
            RegisterKey(styleClass.Code, styleClass.Location, classLocations, state.Errors, "style class code");
            classes.TryAdd(styleClass.Code, styleClass);
        }

        foreach (var styleClass in registry.Classes)
            ValidateStyleClass(state, styleClass, groups, classes);

        ValidateExclusiveGroups(state, registry);
    }

    private static void ValidateStyleClass(
        ValidationState state,
        StyleClass styleClass,
        IReadOnlyDictionary<string, string> groups,
        IReadOnlyDictionary<string, StyleClass> classes)
    {
        var location = styleClass.Location;

        if (styleClass.Scope is not ("container" or "inline"))
            state.Errors.Add($"{location}: @scope of '{styleClass.Code}' must be 'container' or 'inline' (got '{styleClass.Scope}').");

        if (styleClass.Group is not null && !groups.ContainsKey(styleClass.Group))
            state.Errors.Add($"{location}: @group '{styleClass.Group}' of '{styleClass.Code}' is not declared with @style-group.");

        ValidateStyleTargets(state, styleClass);

        if (styleClass.ExclusiveGroup is not null && !StyleGroupCodePattern.IsMatch(styleClass.ExclusiveGroup))
            state.Errors.Add($"{location}: @exclusive '{styleClass.ExclusiveGroup}' of '{styleClass.Code}' is invalid: use letters, digits, '-' or '_' and start with a letter.");

        var requires = styleClass.Requires ?? new List<string>();
        var conflicts = styleClass.ConflictsWith ?? new List<string>();
        ValidateStyleClassReferences(state, styleClass, "@requires", requires, classes);
        ValidateStyleClassReferences(state, styleClass, "@conflicts", conflicts, classes);

        foreach (var shared in requires.Intersect(conflicts, StringComparer.Ordinal))
            state.Errors.Add($"{location}: '{styleClass.Code}' cannot both require and conflict with '{shared}'.");

        if (!styleClass.Deprecated)
        {
            foreach (var required in requires)
            {
                if (classes.TryGetValue(required, out var requiredClass) && requiredClass.Deprecated)
                    state.Warnings.Add($"{location}: '{styleClass.Code}' requires the deprecated class '{required}'.");
            }
        }

        if (styleClass.ReplacedBy is not null)
        {
            if (string.Equals(styleClass.ReplacedBy, styleClass.Code, StringComparison.Ordinal))
            {
                state.Errors.Add($"{location}: '{styleClass.Code}' cannot be replaced by itself.");
            }
            else if (!classes.TryGetValue(styleClass.ReplacedBy, out var replacement))
            {
                state.Errors.Add($"{location}: @deprecated replacement '{styleClass.ReplacedBy}' of '{styleClass.Code}' is not an annotated class.");
            }
            else if (replacement.Deprecated)
            {
                state.Warnings.Add($"{location}: '{styleClass.Code}' is replaced by '{styleClass.ReplacedBy}', which is itself deprecated.");
            }
        }

        if (styleClass.Preview is not null && !IsWellFormedHtmlFragment(styleClass.Preview))
            state.Errors.Add($"{location}: @preview of '{styleClass.Code}' is not well-formed HTML.");
    }

    private static void ValidateStyleTargets(ValidationState state, StyleClass styleClass)
    {
        var location = styleClass.Location;
        ValidateStyleTargetList(state, location, styleClass.Code, "@targets", styleClass.Targets.Tags, StyleTagNamePattern);
        ValidateStyleTargetList(state, location, styleClass.Code, "@blocks", styleClass.Targets.Blocks, StyleBlockNamePattern);

        foreach (var skin in styleClass.Targets.Skins)
        {
            if (skin != "*" && !state.SkinCodes.Contains(skin))
                state.Errors.Add($"{location}: @skins '{skin}' of '{styleClass.Code}' is not a declared content skin.");
        }

        if (styleClass.Targets.Skins.Count > 1 && styleClass.Targets.Skins.Contains("*"))
            state.Errors.Add($"{location}: @skins of '{styleClass.Code}' cannot combine '*' with other values.");
    }

    private static void ValidateStyleTargetList(
        ValidationState state,
        string location,
        string classCode,
        string tagName,
        IReadOnlyList<string> values,
        Regex pattern)
    {
        foreach (var value in values)
        {
            if (value != "*" && !pattern.IsMatch(value))
                state.Errors.Add($"{location}: {tagName} value '{value}' of '{classCode}' is invalid.");
        }

        if (values.Count > 1 && values.Contains("*"))
            state.Errors.Add($"{location}: {tagName} of '{classCode}' cannot combine '*' with other values.");
    }

    private static void ValidateStyleClassReferences(
        ValidationState state,
        StyleClass styleClass,
        string tagName,
        IEnumerable<string> references,
        IReadOnlyDictionary<string, StyleClass> classes)
    {
        foreach (var reference in references)
        {
            if (string.Equals(reference, styleClass.Code, StringComparison.Ordinal))
                state.Errors.Add($"{styleClass.Location}: {tagName} of '{styleClass.Code}' cannot reference itself.");
            else if (!classes.ContainsKey(reference))
                state.Errors.Add($"{styleClass.Location}: {tagName} '{reference}' of '{styleClass.Code}' is not an annotated class.");
        }
    }

    private static void ValidateExclusiveGroups(ValidationState state, StyleRegistry registry)
    {
        foreach (var exclusiveGroup in registry.Classes
                     .Where(styleClass => styleClass.ExclusiveGroup is not null)
                     .GroupBy(styleClass => styleClass.ExclusiveGroup!, StringComparer.OrdinalIgnoreCase))
        {
            var members = exclusiveGroup.ToList();
            var scopes = members.Select(member => member.Scope).Distinct(StringComparer.Ordinal).ToList();

            if (scopes.Count > 1)
            {
                state.Errors.Add($"Exclusive group '{exclusiveGroup.Key}' mixes scopes: {string.Join(", ", members.Select(member => $"{member.Code} ({member.Scope})"))}.");
            }
            else if (members.Count == 1)
            {
                state.Warnings.Add($"{members[0].Location}: exclusive group '{exclusiveGroup.Key}' is used by a single class ('{members[0].Code}').");
            }
        }
    }

    private static bool IsWellFormedHtmlFragment(string html)
    {
        var openElements = new Stack<string>();
        foreach (Match token in HtmlTagTokenPattern.Matches(html))
        {
            var isClosing = token.Groups[1].Length > 0;
            var name = token.Groups[2].Value.ToLowerInvariant();
            var isSelfClosing = token.Groups[3].Length > 0;

            if (isClosing)
            {
                if (openElements.Count == 0 || openElements.Pop() != name)
                    return false;
            }
            else if (!isSelfClosing && !VoidHtmlElements.Contains(name))
            {
                openElements.Push(name);
            }
        }

        if (openElements.Count > 0)
            return false;

        var remainder = HtmlTagTokenPattern.Replace(html, string.Empty);
        return !remainder.Contains('<') && !remainder.Contains('>');
    }
}
