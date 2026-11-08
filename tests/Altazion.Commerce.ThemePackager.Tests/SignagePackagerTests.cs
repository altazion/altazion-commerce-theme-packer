using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altazion.Commerce.ThemePackager.Tests;

[TestClass]
public sealed class SignagePackagerTests
{
    private const string Manifest = """
        {"schemaVersion":"signage-1","template":{"code":"demo","name":"Demo","version":"1.0+build"},
         "entryPoint":"app/index.html","optionGroups":[{"code":"appearance","label":"Appearance","options":[
           {"key":"accent","label":"Accent","type":"color","defaultValue":"#336699"}]}],
         "animationZones":[{"code":"HOME","label":"Home","renderMode":"html","contentTypes":["video"]}]}
        """;

    [TestMethod]
    public void Package_preserves_all_files_and_manifest_bytes()
    {
        using var fixture = new Fixture();
        fixture.Write("manifest.json", Encoding.UTF8.GetBytes(Manifest));
        fixture.Write("app/index.html", [0, 1, 2, 255]);
        fixture.Write("dev/nested/resource.bin", [9, 8, 0, 7]);
        var result = SignagePackager.Pack(fixture.Options());
        Assert.AreEqual(3, result.FileCount);
        using var zip = ZipFile.OpenRead(fixture.Output);
        CollectionAssert.AreEquivalent(new[] { "manifest.json", "app/index.html", "dev/nested/resource.bin" },
            zip.Entries.Select(e => e.FullName).ToArray());
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(fixture.Source, entry.FullName.Replace('/', Path.DirectorySeparatorChar))), bytes.ToArray());
        }
    }

    [TestMethod]
    public void Dry_run_does_not_write_and_rejects_output_inside_source()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        var result = SignagePackager.Pack(fixture.Options(dryRun: true));
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.IsTrue(result.Size > 0);
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = fixture.Source, OutputFile = Path.Combine(fixture.Source, "out.zip"), IsDryRun = true
        }));
    }

    [TestMethod]
    public void Invalid_source_does_not_overwrite_existing_output()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        File.WriteAllText(fixture.Output, "keep");
        fixture.Write("manifest.json", Encoding.UTF8.GetBytes(Manifest.Replace("app/index.html", "app/INDEX.html")));
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options()));
        Assert.AreEqual("keep", File.ReadAllText(fixture.Output));
    }

    [TestMethod]
    public void Default_output_is_a_sibling_and_existing_output_is_replaced()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        var output = Path.Combine(Path.GetDirectoryName(fixture.Source)!, "source.altzsignage");
        File.WriteAllText(output, "old");
        var result = SignagePackager.Pack(new PackCommandOptions { SourceDirectory = fixture.Source });
        Assert.AreEqual(output, result.OutputPath);
        using var zip = ZipFile.OpenRead(output);
        Assert.AreEqual(2, zip.Entries.Count);
    }

    [TestMethod]
    public void Manifest_structure_and_unknown_properties_are_validated()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        foreach (var invalid in new[]
        {
            Manifest.Replace("\"signage-1\"", "\"signage-2\""),
            Manifest.Replace("\"renderMode\":\"html\"", "\"renderMode\":\"wrong\""),
            Manifest.Replace("\"optionGroups\":", "\"unexpected\":1,\"optionGroups\":"),
            Manifest.Replace("\"type\":\"color\"", "\"type\":\"choice\""),
            Manifest.Replace("\"contentTypes\":[\"video\"]", "\"contentTypes\":[\"video\",\"VIDEO\"]")
        })
        {
            fixture.Write("manifest.json", Encoding.UTF8.GetBytes(invalid));
            Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
        }
    }

        [DataTestMethod]
        [DataRow("\"type\":\"text\",\"required\":true,\"defaultValue\":\"  \"")]
        [DataRow("\"type\":\"multilineText\",\"defaultValue\":false")]
        [DataRow("\"type\":\"number\",\"min\":3,\"max\":2")]
        [DataRow("\"type\":\"number\",\"min\":1,\"defaultValue\":0")]
        [DataRow("\"type\":\"number\",\"defaultValue\":1e1000")]
        [DataRow("\"type\":\"boolean\",\"defaultValue\":0")]
        [DataRow("\"type\":\"color\",\"defaultValue\":\"red\"")]
        [DataRow("\"type\":\"url\",\"defaultValue\":\"https://user@example.com/\"")]
        [DataRow("\"type\":\"segmentation\",\"defaultValue\":1.0")]
        [DataRow("\"type\":\"productSelection\",\"defaultValue\":\"00000000-0000-0000-0000-000000000000\"")]
        [DataRow("\"type\":\"image\",\"defaultValue\":{\"kind\":\"asset\",\"id\":\"a\"}")]
        [DataRow("\"type\":\"media\",\"defaultValue\":{\"kind\":\"url\",\"url\":\"file:///x\"}")]
        [DataRow("\"type\":\"choice\",\"choices\":[{\"value\":\"a\",\"label\":\"A\"}],\"defaultValue\":\"A\"")]
        public void Invalid_option_defaults_and_bounds_are_rejected(string option)
        {
            using var fixture = new Fixture();
            fixture.Valid();
            fixture.Write("manifest.json", Encoding.UTF8.GetBytes(Manifest.Replace(
                "\"type\":\"color\",\"defaultValue\":\"#336699\"", option)));
            Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
        }

        [DataTestMethod]
        [DataRow("\"type\":\"text\",\"defaultValue\":\"\"")]
        [DataRow("\"type\":\"html\",\"defaultValue\":\"<b>Hello</b>\"")]
        [DataRow("\"type\":\"number\",\"min\":0,\"max\":10,\"defaultValue\":0")]
        [DataRow("\"type\":\"boolean\",\"defaultValue\":false")]
        [DataRow("\"type\":\"color\",\"defaultValue\":\"#abcd\"")]
        [DataRow("\"type\":\"url\",\"defaultValue\":\"https://example.com/a\"")]
        [DataRow("\"type\":\"segmentation\",\"defaultValue\":123")]
        [DataRow("\"type\":\"productSelection\",\"defaultValue\":\"12345678-1234-1234-1234-123456789abc\"")]
        [DataRow("\"type\":\"image\",\"defaultValue\":{\"kind\":\"asset\",\"documentType\":\"type\",\"documentId\":\"doc\",\"id\":\"a\"}")]
        [DataRow("\"type\":\"media\",\"defaultValue\":{\"kind\":\"url\",\"url\":\"https://example.com/a\"}")]
        [DataRow("\"type\":\"choice\",\"choices\":[{\"value\":\"a\",\"label\":\"A\"}],\"defaultValue\":\"a\"")]
        public void Valid_option_defaults_are_accepted(string option)
        {
            using var fixture = new Fixture();
            fixture.Valid();
            fixture.Write("manifest.json", Encoding.UTF8.GetBytes(Manifest.Replace(
                "\"type\":\"color\",\"defaultValue\":\"#336699\"", option)));
            SignagePackager.Pack(fixture.Options(dryRun: true));
        }
    [DataTestMethod]
    [DataRow("{\"schemaVersion\":\"signage-1\",\"schemaVersion\":\"signage-1\"}")]
    [DataRow("{\"schemaVersion\":\"signage-1\",\"SchemaVersion\":\"signage-1\"}")]
    [DataRow("{\"schemaVersion\":\"signage-1\"}")]
    [DataRow("{")]
    public void Invalid_manifests_are_rejected(string manifest)
    {
        using var fixture = new Fixture();
        fixture.Valid();
        fixture.Write("manifest.json", Encoding.UTF8.GetBytes(manifest));
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
    }

    [TestMethod]
    public void Invalid_encoding_and_manifest_size_are_rejected()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        fixture.Write("manifest.json", [0xff, 0xfe]);
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
        fixture.Write("manifest.json", new byte[1024 * 1024 + 1]);
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
    }

    [TestMethod]
    public void Invalid_names_and_links_are_rejected()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        fixture.Write("bad%name", [1]);
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
        File.Delete(Path.Combine(fixture.Source, "bad%name"));
        var link = Path.Combine(fixture.Source, "linked");
        try { File.CreateSymbolicLink(link, Path.Combine(fixture.Source, "manifest.json")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive("Creating symbolic links is unavailable: " + ex.Message);
        }
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
    }

    [TestMethod]
    public void Rejects_more_than_5000_files()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        for (var i = 0; i < 4999; i++) fixture.Write("files/" + i, []);
        Assert.ThrowsException<ThemePackagerException>(() => SignagePackager.Pack(fixture.Options(dryRun: true)));
    }

    [TestMethod]
    public void Cli_accepts_signage_and_keeps_pack_dispatch()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        Assert.AreEqual(0, Program.Main(["pack-signage", "--source", fixture.Source, "--output", fixture.Output, "--dry-run"]));
        Assert.AreEqual(0, Program.Main(["pack", "--source", fixture.Source, "--dry-run"]));
        Assert.IsFalse(File.Exists(fixture.Output));
    }

    [TestMethod]
    public void Pack_detects_signage_and_produces_default_extension()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        var result = ThemePackager.Pack(new PackCommandOptions { SourceDirectory = fixture.Source });
        Assert.AreEqual(PackSourceKind.Signage, result.SourceKind);
        Assert.AreEqual("demo", result.ThemeId);
        Assert.AreEqual("Demo", result.ThemeName);
        Assert.AreEqual(Path.Combine(Path.GetDirectoryName(fixture.Source)!, "source.altzsignage"), result.OutputPath);
        using var zip = ZipFile.OpenRead(result.OutputPath);
        Assert.IsNotNull(zip.GetEntry("manifest.json"));
        Assert.IsNotNull(zip.GetEntry("app/index.html"));
    }

    [TestMethod]
    public void Cli_pack_autodetects_and_writes_signage_archive()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        Assert.AreEqual(0, Program.Main(["pack", "--source", fixture.Source]));
        var output = Path.Combine(Path.GetDirectoryName(fixture.Source)!, "source.altzsignage");
        using var zip = ZipFile.OpenRead(output);
        Assert.IsNotNull(zip.GetEntry("app/index.html"));
    }

    [TestMethod]
    public void Pack_validates_all_animation_zones()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        fixture.Write("manifest.json", Encoding.UTF8.GetBytes(Manifest.Replace(
            "\"contentTypes\":[\"video\"]}]", "\"contentTypes\":[\"video\"]},{\"code\":\"OTHER\",\"label\":\"Other\",\"renderMode\":\"invalid\"}]")));
        var error = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(fixture.Options()));
        StringAssert.Contains(error.Message, "renderMode");
        Assert.IsFalse(File.Exists(fixture.Output));
    }

    [DataTestMethod]
    [DataRow("theme.general.json")]
    [DataRow("template.json")]
    public void Auto_detection_rejects_signage_and_legacy_sources_together(string legacyFile)
    {
        using var fixture = new Fixture();
        fixture.Valid();
        fixture.Write(legacyFile, Encoding.UTF8.GetBytes("{}"));
        var error = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(fixture.Options()));
        StringAssert.Contains(error.Message, "Ambiguous");
        Assert.IsFalse(File.Exists(fixture.Output));
    }

    [DataTestMethod]
    [DataRow("theme.general.json", false)]
    [DataRow("template.json", true)]
    public void Legacy_source_ignores_non_signage_manifest(string legacyFile, bool isTemplate)
    {
        using var fixture = new Fixture();
        fixture.Write(legacyFile, Encoding.UTF8.GetBytes("{}"));
        foreach (var manifest in new[] { "{\"packageKind\":\"theme\"}", "{", "{\"schemaVersion\":\"signage-2\"}" })
        {
            fixture.Write("manifest.json", Encoding.UTF8.GetBytes(manifest));
            Assert.AreEqual(isTemplate ? PackSourceKind.Template : PackSourceKind.Theme, ThemePackager.DetectSourceKind(fixture.Source));
        }
    }

    [TestMethod]
    public void Auto_detection_reports_invalid_signage_manifest()
    {
        using var fixture = new Fixture();
        fixture.Valid();
        fixture.Write("manifest.json", Encoding.UTF8.GetBytes(Manifest.Replace("signage-1", "signage-2")));
        var error = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(fixture.Options()));
        StringAssert.Contains(error.Message, "schemaVersion");
        Assert.IsFalse(File.Exists(fixture.Output));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), ".signage-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(root, "source");
        public string Output => Path.Combine(root, "result.zip");
        public Fixture() => Directory.CreateDirectory(Source);
        public void Valid()
        {
            Write("manifest.json", Encoding.UTF8.GetBytes(Manifest));
            Write("app/index.html", Encoding.UTF8.GetBytes("<html></html>"));
        }
        public void Write(string name, byte[] bytes)
        {
            var path = Path.Combine(Source, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public PackCommandOptions Options(bool dryRun = false) => new()
        {
            SourceDirectory = Source, OutputFile = Output, IsDryRun = dryRun
        };
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
