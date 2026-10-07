using System.Text;
using System.Text.RegularExpressions;

namespace Altazion.Commerce.ThemePackager;

internal enum CssAnnotationKind
{
    Group = 0,
    Style = 1,
}

internal sealed record CssAnnotationTag(string Name, string? Language, string Value);

internal sealed record CssAnnotation(
    CssAnnotationKind Kind,
    string Location,
    string Code,
    IReadOnlyList<CssAnnotationTag> Tags,
    string? RulePrelude);

internal sealed record CssScanResult(
    string CleanedCss,
    IReadOnlyList<CssAnnotation> Annotations,
    IReadOnlyList<string> Errors);

/// <summary>
/// Extrait les annotations <c>@style-group</c> et <c>@style</c> des commentaires <c>/** ... */</c> d'une feuille CSS
/// et retire ces commentaires du CSS à embarquer.
/// </summary>
internal static class CssAnnotationScanner
{
    private static readonly Regex TagLinePattern = new(
        @"^@([a-z][a-z-]*)(?:\.([A-Za-z][A-Za-z-]*))?(?:\s+(.*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClassSelectorPattern = new(
        @"\.(-?[_a-zA-Z][_a-zA-Z0-9-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeSelectorPattern = new(
        @"\[[^\]]*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedTextPattern = new(
        "\"[^\"]*\"|'[^']*'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> GroupTags = new(StringComparer.Ordinal)
    {
        "label", "order",
    };

    private static readonly HashSet<string> StyleTags = new(StringComparer.Ordinal)
    {
        "label", "description", "group", "scope", "targets", "blocks", "skins",
        "exclusive", "requires", "conflicts", "deprecated", "preview",
    };

    private static readonly HashSet<string> LocalizedTags = new(StringComparer.Ordinal)
    {
        "label", "description",
    };

    public static CssScanResult Scan(string css, string sourceName)
    {
        var output = new StringBuilder(css.Length);
        var annotations = new List<CssAnnotation>();
        var errors = new List<string>();
        var index = 0;

        while (index < css.Length)
        {
            var current = css[index];

            if (current is '"' or '\'')
            {
                var end = SkipString(css, index);
                output.Append(css, index, end - index);
                index = end;
                continue;
            }

            if (current == '/' && index + 1 < css.Length && css[index + 1] == '*')
            {
                var commentEnd = FindCommentEnd(css, index);
                var comment = css.Substring(index, commentEnd - index);
                var location = $"{sourceName}:{LineOf(css, index)}";

                if (TryParseAnnotation(comment, location, css, commentEnd, errors, out var annotation))
                {
                    annotations.Add(annotation);
                    index = SkipLineBreak(css, commentEnd);
                    continue;
                }

                output.Append(comment);
                index = commentEnd;
                continue;
            }

            if (IsUrlStart(css, index))
            {
                var end = SkipUrl(css, index);
                output.Append(css, index, end - index);
                index = end;
                continue;
            }

            output.Append(current);
            index++;
        }

        return new CssScanResult(annotations.Count == 0 ? css : output.ToString(), annotations, errors);
    }

    public static IReadOnlyList<string> ExtractClasses(string prelude)
    {
        var cleaned = AttributeSelectorPattern.Replace(prelude, " ");
        cleaned = QuotedTextPattern.Replace(cleaned, " ");

        var classes = new List<string>();
        foreach (Match match in ClassSelectorPattern.Matches(cleaned))
        {
            var name = match.Groups[1].Value;
            if (!classes.Contains(name, StringComparer.Ordinal))
                classes.Add(name);
        }

        return classes;
    }

    private static bool TryParseAnnotation(
        string comment,
        string location,
        string css,
        int commentEnd,
        List<string> errors,
        out CssAnnotation annotation)
    {
        annotation = null!;

        if (!comment.StartsWith("/**", StringComparison.Ordinal) || comment.StartsWith("/**/", StringComparison.Ordinal))
            return false;

        var bodyEnd = comment.EndsWith("*/", StringComparison.Ordinal) && comment.Length >= 5
            ? comment.Length - 2
            : comment.Length;
        var body = comment.Substring(3, bodyEnd - 3);

        var tags = new List<(string Name, string? Language, StringBuilder Value)>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('*'))
                line = line.Substring(1).Trim();

            if (line.Length == 0)
                continue;

            if (line[0] == '@')
            {
                var match = TagLinePattern.Match(line);
                if (!match.Success)
                {
                    errors.Add($"{location}: invalid annotation line '{line}'.");
                    continue;
                }

                tags.Add((
                    match.Groups[1].Value,
                    match.Groups[2].Success ? match.Groups[2].Value : null,
                    new StringBuilder(match.Groups[3].Success ? match.Groups[3].Value.Trim() : string.Empty)));
            }
            else if (tags.Count > 0)
            {
                var value = tags[^1].Value;
                if (value.Length > 0)
                    value.Append(' ');

                value.Append(line);
            }
        }

        if (tags.Count == 0)
            return false;

        var firstName = tags[0].Name;
        CssAnnotationKind kind;
        if (firstName == "style-group")
            kind = CssAnnotationKind.Group;
        else if (firstName == "style")
            kind = CssAnnotationKind.Style;
        else
            return false;

        var allowedTags = kind == CssAnnotationKind.Group ? GroupTags : StyleTags;
        var parsedTags = new List<CssAnnotationTag>();
        for (var i = 1; i < tags.Count; i++)
        {
            var (name, language, value) = tags[i];
            if (!allowedTags.Contains(name))
            {
                errors.Add($"{location}: unknown tag '@{name}' for @{firstName}.");
                continue;
            }

            if (language is not null && !LocalizedTags.Contains(name))
            {
                errors.Add($"{location}: tag '@{name}' does not accept a language suffix.");
                continue;
            }

            parsedTags.Add(new CssAnnotationTag(name, language, value.ToString().Trim()));
        }

        var prelude = kind == CssAnnotationKind.Style ? ReadRulePrelude(css, commentEnd) : null;
        annotation = new CssAnnotation(kind, location, tags[0].Value.ToString().Trim(), parsedTags, prelude);
        return true;
    }

    private static string? ReadRulePrelude(string css, int start)
    {
        var index = start;
        while (index < css.Length)
        {
            if (char.IsWhiteSpace(css[index]))
            {
                index++;
                continue;
            }

            if (css[index] == '/' && index + 1 < css.Length && css[index + 1] == '*')
            {
                var isDocComment = index + 2 < css.Length && css[index + 2] == '*' && !(index + 3 < css.Length && css[index + 3] == '/');
                if (isDocComment)
                    return null;

                index = FindCommentEnd(css, index);
                continue;
            }

            break;
        }

        var preludeStart = index;
        while (index < css.Length)
        {
            var current = css[index];
            if (current is '"' or '\'')
            {
                index = SkipString(css, index);
                continue;
            }

            if (current == '{')
                return css.Substring(preludeStart, index - preludeStart).Trim();

            if (current is ';' or '}')
                return null;

            index++;
        }

        return null;
    }

    private static int SkipString(string css, int start)
    {
        var quote = css[start];
        var index = start + 1;
        while (index < css.Length)
        {
            var current = css[index];
            if (current == '\\')
            {
                index += 2;
                continue;
            }

            if (current == quote)
                return index + 1;

            if (current == '\n')
                return index;

            index++;
        }

        return css.Length;
    }

    private static int FindCommentEnd(string css, int start)
    {
        var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return end < 0 ? css.Length : end + 2;
    }

    private static bool IsUrlStart(string css, int index)
        => string.Compare(css, index, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;

    private static int SkipUrl(string css, int start)
    {
        var index = start + 4;
        while (index < css.Length && char.IsWhiteSpace(css[index]))
            index++;

        if (index < css.Length && css[index] is '"' or '\'')
            return start + 4;

        var close = css.IndexOf(')', index);
        return close < 0 ? css.Length : close + 1;
    }

    private static int SkipLineBreak(string css, int position)
    {
        if (position < css.Length && css[position] == '\r')
            position++;

        if (position < css.Length && css[position] == '\n')
            position++;

        return position;
    }

    private static int LineOf(string css, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < css.Length; i++)
        {
            if (css[i] == '\n')
                line++;
        }

        return line;
    }
}
