using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TemporaryTheme = Altazion.Commerce.ThemePackager.Tests.ThemePackagerValidationTests.TemporaryTheme;

namespace Altazion.Commerce.ThemePackager.Tests;

[TestClass]
public sealed class ThemeSkinsTests
{
    private const string BlogType = "blog-article";

    private static IReadOnlyDictionary<string, string> BlogArticleType() => new Dictionary<string, string>
    {
        ["content-types/blog-article.json"] = ThemePackagerValidationTests.ValidBlogArticleJson,
    };

    private static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> files, string path, string content)
        => new Dictionary<string, string>(files) { [path] = content };

    [TestMethod]
    public void Pack_inlines_the_liquid_file_into_the_skin_template()
    {
        var files = With(BlogArticleType(), "skins/blog-article/full.liquid", "<article>{{ item.title }}</article>\n");
        using var theme = TemporaryTheme.Create(
            sharedJson: ThemePackagerValidationTests.SharedJsonWithSkin(BlogType, template: null),
            additionalFiles: files);

        var outputFile = Pack(theme);

        using var archive = ZipFile.OpenRead(outputFile);
        var shared = JsonDocument.Parse(ReadText(archive, "theme.shared.json")).RootElement;
        var skin = shared.GetProperty("reusableComponents").EnumerateArray()
            .Single(component => component.GetProperty("componentType").GetString() == "ContentSkin");
        Assert.AreEqual("<article>{{ item.title }}</article>\n", skin.GetProperty("config").GetProperty("template").GetString());
        Assert.AreEqual("full", skin.GetProperty("config").GetProperty("skinCode").GetString());
        Assert.IsNull(archive.GetEntry("assets/skins/blog-article/full.liquid"));
    }

    [TestMethod]
    public void Pack_keeps_a_skin_that_declares_its_template_in_the_json()
    {
        using var theme = TemporaryTheme.Create(
            sharedJson: ThemePackagerValidationTests.SharedJsonWithSkin(BlogType),
            additionalFiles: BlogArticleType());

        var outputFile = Pack(theme);

        using var archive = ZipFile.OpenRead(outputFile);
        StringAssert.Contains(ReadText(archive, "theme.shared.json"), "{{ item.title }}");
    }

    [TestMethod]
    public void Pack_rejects_a_skin_without_template()
        => AssertRejected(
            ThemePackagerValidationTests.SharedJsonWithSkin(BlogType, template: null),
            BlogArticleType(),
            "ContentSkin 'blog-article/full' has no template");

    [TestMethod]
    public void Pack_rejects_a_liquid_file_without_matching_skin()
        => AssertRejected(
            ThemePackagerValidationTests.SharedJsonWithSkin(BlogType),
            With(BlogArticleType(), "skins/blog-article/card.liquid", "<p></p>"),
            "has no matching ContentSkin component");

    [TestMethod]
    public void Pack_rejects_a_skin_declared_in_both_the_json_and_a_file()
        => AssertRejected(
            ThemePackagerValidationTests.SharedJsonWithSkin(BlogType),
            With(BlogArticleType(), "skins/blog-article/full.liquid", "<p></p>"),
            "declare the template only once");

    [TestMethod]
    public void Pack_rejects_a_liquid_file_at_the_wrong_location()
        => AssertRejected(
            ThemePackagerValidationTests.SharedJsonWithSkin(BlogType),
            With(BlogArticleType(), "skins/full.liquid", "<p></p>"),
            "must be named 'skins/<contentTypeId>/<skinCode>.liquid'");

    [TestMethod]
    public void Pack_rejects_invalid_fluid_syntax()
        => AssertRejected(
            ThemePackagerValidationTests.SharedJsonWithSkin(BlogType, template: null),
            With(BlogArticleType(), "skins/blog-article/full.liquid", "{% if item.title %}<p>{{ item.title }}</p>"),
            "template is not valid Fluid");

    [TestMethod]
    public void Pack_matches_skins_ignoring_case()
    {
        var files = With(BlogArticleType(), "skins/Blog-Article/FULL.liquid", "<p>{{ item.title }}</p>");
        using var theme = TemporaryTheme.Create(
            sharedJson: ThemePackagerValidationTests.SharedJsonWithSkin(BlogType, template: null),
            additionalFiles: files);

        Pack(theme);
    }

    private static string Pack(TemporaryTheme theme)
    {
        var outputFile = Path.Combine(theme.RootDirectory, "skins.altztheme");
        ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = outputFile,
        });

        return outputFile;
    }

    private static void AssertRejected(string sharedJson, IReadOnlyDictionary<string, string> files, string expectedMessage)
    {
        using var theme = TemporaryTheme.Create(sharedJson: sharedJson, additionalFiles: files);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => Pack(theme));
        StringAssert.Contains(exception.Message, expectedMessage);
    }

    private static string ReadText(ZipArchive archive, string entryName)
    {
        using var reader = new StreamReader(archive.GetEntry(entryName)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
