using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TemporaryTheme = Altazion.Commerce.ThemePackager.Tests.ThemePackagerValidationTests.TemporaryTheme;

namespace Altazion.Commerce.ThemePackager.Tests;

[TestClass]
public sealed class ThemeStylesTests
{
    private const string GroupCss = """
        /**
         * @style-group buttons
         * @label Boutons
         * @label.en Buttons
         * @order 10
         */
        """;

    private const string ButtonCss = """
        /**
         * @style
         * @label Bouton principal
         * @description Bouton plein
         * @group buttons
         * @scope inline
         * @targets a, button
         * @exclusive btn-variant
         * @preview <a href="#">Exemple</a>
         */
        .btn-primary { color: red; }
        """;

    [TestMethod]
    public void Pack_generates_registry_and_cleans_css()
    {
        using var theme = TemporaryTheme.Create(additionalFiles: Css(GroupCss + "\n" + ButtonCss));

        var outputFile = Pack(theme);

        using var archive = ZipFile.OpenRead(outputFile);
        var registry = ReadJson(archive, "theme.styles.json");
        Assert.AreEqual("buttons", registry.GetProperty("groups")[0].GetProperty("code").GetString());
        Assert.AreEqual("Buttons", registry.GetProperty("groups")[0].GetProperty("labels").GetProperty("en").GetString());

        var style = registry.GetProperty("classes")[0];
        Assert.AreEqual("btn-primary", style.GetProperty("code").GetString());
        Assert.AreEqual("inline", style.GetProperty("scope").GetString());
        Assert.AreEqual("btn-variant", style.GetProperty("exclusiveGroup").GetString());
        Assert.AreEqual(2, style.GetProperty("targets").GetProperty("tags").GetArrayLength());

        var css = ReadText(archive, "assets/styles/site.css");
        Assert.IsFalse(css.Contains("@style"));
        StringAssert.Contains(css, ".btn-primary { color: red; }");

        var manifest = ReadJson(archive, "manifest.json");
        Assert.AreEqual("theme.styles.json", manifest.GetProperty("files").GetProperty("styles").GetString());
    }

    [TestMethod]
    public void Pack_without_annotations_produces_no_registry_and_keeps_css()
    {
        using var theme = TemporaryTheme.Create(additionalFiles: Css("/* simple */ .a { color: red; }"));

        var outputFile = Pack(theme);

        using var archive = ZipFile.OpenRead(outputFile);
        Assert.IsNull(archive.GetEntry("theme.styles.json"));
        Assert.AreEqual("/* simple */ .a { color: red; }", ReadText(archive, "assets/styles/site.css"));
    }

    [TestMethod]
    public void Pack_ignores_annotations_inside_strings_and_urls()
    {
        const string css = ".a { content: \"/** @style */\"; background: url(a/**b.png); }";
        using var theme = TemporaryTheme.Create(additionalFiles: Css(css));

        var outputFile = Pack(theme);

        using var archive = ZipFile.OpenRead(outputFile);
        Assert.IsNull(archive.GetEntry("theme.styles.json"));
        Assert.AreEqual(css, ReadText(archive, "assets/styles/site.css"));
    }

    [TestMethod]
    public void Pack_requires_code_when_rule_targets_several_classes()
        => AssertRejected(
            Css(GroupCss + "\n/**\n * @style\n * @label A\n * @scope inline\n */\n.a, .b { color: red; }"),
            "targets several classes");

    [TestMethod]
    public void Pack_accepts_explicit_code_on_multi_class_rule()
        => AssertAccepted(
            Css(GroupCss + "\n/**\n * @style b\n * @label B\n * @scope inline\n */\n.a.b { color: red; }"));

    [TestMethod]
    public void Pack_rejects_style_without_label()
        => AssertRejected(
            Css("/**\n * @style\n * @scope inline\n */\n.a { color: red; }"),
            "requires a @label");

    [TestMethod]
    public void Pack_rejects_style_without_scope()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n */\n.a { color: red; }"),
            "requires a @scope");

    [TestMethod]
    public void Pack_rejects_invalid_scope()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope page\n */\n.a { color: red; }"),
            "@scope of 'a' must be 'container' or 'inline'");

    [TestMethod]
    public void Pack_rejects_unknown_tag()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n * @colour red\n */\n.a { color: red; }"),
            "unknown tag '@colour'");

    [TestMethod]
    public void Pack_rejects_duplicate_class_codes()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n */\n.a { color: red; }\n/**\n * @style\n * @label A2\n * @scope inline\n */\n.a { color: blue; }"),
            "Duplicate style class code 'a'");

    [TestMethod]
    public void Pack_rejects_unknown_group()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n * @group nope\n */\n.a { color: red; }"),
            "@group 'nope' of 'a' is not declared");

    [TestMethod]
    public void Pack_rejects_unknown_requires_reference()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n * @requires ghost\n */\n.a { color: red; }"),
            "@requires 'ghost' of 'a' is not an annotated class");

    [TestMethod]
    public void Pack_rejects_requires_and_conflicts_on_same_class()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n * @requires b\n * @conflicts b\n */\n.a { color: red; }\n/**\n * @style\n * @label B\n * @scope inline\n */\n.b { color: blue; }"),
            "cannot both require and conflict with 'b'");

    [TestMethod]
    public void Pack_rejects_exclusive_group_mixing_scopes()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n * @exclusive x\n */\n.a { color: red; }\n/**\n * @style\n * @label B\n * @scope container\n * @exclusive x\n */\n.b { color: blue; }"),
            "Exclusive group 'x' mixes scopes");

    [TestMethod]
    public void Pack_rejects_unknown_skin()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope container\n * @skins ghost\n */\n.a { color: red; }"),
            "@skins 'ghost' of 'a' is not a declared content skin");

    [TestMethod]
    public void Pack_accepts_declared_skin()
    {
        var files = new Dictionary<string, string>(Css("/**\n * @style\n * @label A\n * @scope container\n * @skins full\n */\n.a { color: red; }"))
        {
            ["content-types/blog-article.json"] = ThemePackagerValidationTests.ValidBlogArticleJson,
        };

        using var theme = TemporaryTheme.Create(
            sharedJson: ThemePackagerValidationTests.SharedJsonWithSkin("blog-article"),
            additionalFiles: files);

        Pack(theme);
    }

    [TestMethod]
    public void Pack_rejects_malformed_preview()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n * @preview <div><span></div>\n */\n.a { color: red; }"),
            "@preview of 'a' is not well-formed HTML");

    [TestMethod]
    public void Pack_rejects_style_not_followed_by_rule()
        => AssertRejected(
            Css("/**\n * @style\n * @label A\n * @scope inline\n */\n"),
            "@style is not followed by a CSS rule");

    [TestMethod]
    public void Pack_warns_about_deprecated_replacement_that_is_deprecated()
    {
        const string css = "/**\n * @style\n * @label Old\n * @scope inline\n * @deprecated new\n */\n.old { color: red; }\n/**\n * @style\n * @label New\n * @scope inline\n * @deprecated\n */\n.new { color: blue; }";
        using var theme = TemporaryTheme.Create(additionalFiles: Css(css));

        var result = ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = Path.Combine(theme.RootDirectory, "warn.altztheme"),
        });

        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("is itself deprecated")));
    }

    private static IReadOnlyDictionary<string, string> Css(string css)
        => new Dictionary<string, string> { ["styles/site.css"] = css };

    private static string Pack(TemporaryTheme theme)
    {
        var outputFile = Path.Combine(theme.RootDirectory, "styles.altztheme");
        ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = outputFile,
        });

        return outputFile;
    }

    private static void AssertAccepted(IReadOnlyDictionary<string, string> files)
    {
        using var theme = TemporaryTheme.Create(additionalFiles: files);
        Pack(theme);
    }

    private static void AssertRejected(IReadOnlyDictionary<string, string> files, string expectedMessage)
    {
        using var theme = TemporaryTheme.Create(additionalFiles: files);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => Pack(theme));
        StringAssert.Contains(exception.Message, expectedMessage);
    }

    private static string ReadText(ZipArchive archive, string entryName)
    {
        using var reader = new StreamReader(archive.GetEntry(entryName)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonElement ReadJson(ZipArchive archive, string entryName)
        => JsonDocument.Parse(ReadText(archive, entryName)).RootElement.Clone();
}
