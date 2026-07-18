using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altazion.Commerce.ThemePackager.Tests;

[TestClass]
public sealed class TemplatePackagerTests
{
    [TestMethod]
    public void Pack_detects_template_source_and_generates_one_archive_per_profile()
    {
        using var template = TemporaryTemplate.Create();

        var outputDirectory = Path.Combine(template.RootDirectory, "dist");
        var result = ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = template.SourceDirectory,
            OutputFile = outputDirectory,
        });

        Assert.AreEqual(PackSourceKind.Template, result.SourceKind);
        Assert.AreEqual(outputDirectory, result.OutputPath);
        Assert.AreEqual(2, result.TemplateArtifacts.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                Path.Combine(outputDirectory, "altazion-horizon-signage.altztemplate"),
                Path.Combine(outputDirectory, "altazion-horizon-web.altztemplate"),
            },
            result.GeneratedArtifacts.ToArray());

        foreach (var artifactPath in result.GeneratedArtifacts)
            Assert.IsTrue(File.Exists(artifactPath), $"Expected generated artifact '{artifactPath}'.");

        using var archive = ZipFile.OpenRead(Path.Combine(outputDirectory, "altazion-horizon-web.altztemplate"));
        Assert.IsNotNull(archive.GetEntry("manifest.json"));
        Assert.IsNotNull(archive.GetEntry("snapshot.base.json"));
        Assert.IsNotNull(archive.GetEntry("menus.json"));
        Assert.IsNotNull(archive.GetEntry("pages/home/classic.json"));
        Assert.IsNotNull(archive.GetEntry("assets/theme.css"));
        Assert.IsNotNull(archive.GetEntry("assets/home-classic.css"));

        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifestReader = new StreamReader(manifestStream, Encoding.UTF8);
        using var manifestDocument = JsonDocument.Parse(manifestReader.ReadToEnd());

        Assert.AreEqual("theme-template", manifestDocument.RootElement.GetProperty("packageKind").GetString());
        Assert.AreEqual("Altazion Horizon for Web", manifestDocument.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(1, manifestDocument.RootElement.GetProperty("pages").GetArrayLength());
        Assert.AreEqual(1, manifestDocument.RootElement.GetProperty("presets").GetArrayLength());
    }

    [TestMethod]
    public void Pack_template_dry_run_validates_without_creating_archives()
    {
        using var template = TemporaryTemplate.Create();

        var outputDirectory = Path.Combine(template.RootDirectory, "dist");
        var result = ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = template.SourceDirectory,
            OutputFile = outputDirectory,
            IsDryRun = true,
        });

        Assert.AreEqual(PackSourceKind.Template, result.SourceKind);
        Assert.AreEqual(string.Empty, result.OutputPath);
        Assert.AreEqual(0L, result.Size);
        Assert.AreEqual(2, result.TemplateArtifacts.Count);
        Assert.IsFalse(Directory.Exists(outputDirectory));
    }

    [TestMethod]
    public void Pack_rejects_ambiguous_source_directory()
    {
        using var template = TemporaryTemplate.Create(includeRootThemeGeneral: true);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = template.SourceDirectory,
            OutputFile = Path.Combine(template.RootDirectory, "dist"),
        }));

        StringAssert.Contains(exception.Message, "Ambiguous source directory");
    }

    private sealed class TemporaryTemplate : IDisposable
    {
        public TemporaryTemplate(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            SourceDirectory = Path.Combine(rootDirectory, "src");
        }

        public string RootDirectory { get; }

        public string SourceDirectory { get; }

        public static TemporaryTemplate Create(bool includeRootThemeGeneral = false)
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), "altazion-template-packager-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectory);

            var template = new TemporaryTemplate(rootDirectory);
            Directory.CreateDirectory(template.SourceDirectory);

            WriteFile(template.SourceDirectory, "template.json", TemplateJson);

            if (includeRootThemeGeneral)
                WriteFile(template.SourceDirectory, "theme.general.json", "{\n  \"theme\": { \"id\": \"10000000-0000-0000-0000-000000000001\", \"name\": \"Ambiguous\" }\n}");

            WriteWebProfile(template.SourceDirectory);
            WriteSignageProfile(template.SourceDirectory);
            return template;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }

        private static void WriteWebProfile(string sourceDirectory)
        {
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "profile.json"), WebProfileJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "base", "theme.general.json"), WebThemeGeneralJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "base", "theme.shared.json"), WebThemeSharedJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "base", "menus", "main.json"), WebMenuJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "page-variants", "home", "classic.json"), WebClassicPageJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "assets", "theme.css"), "body { color: #111; }");
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "assets", "home-classic.css"), ".home { color: #222; }");
            WriteFile(sourceDirectory, Path.Combine("profiles", "web", "assets", "logo-web.svg"), "<svg xmlns='http://www.w3.org/2000/svg'></svg>");
        }

        private static void WriteSignageProfile(string sourceDirectory)
        {
            WriteFile(sourceDirectory, Path.Combine("profiles", "signage", "profile.json"), SignageProfileJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "signage", "base", "theme.general.json"), SignageThemeGeneralJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "signage", "base", "theme.shared.json"), SignageThemeSharedJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "signage", "page-variants", "home", "default.json"), SignageDefaultPageJson);
            WriteFile(sourceDirectory, Path.Combine("profiles", "signage", "assets", "theme.css"), "body { background: #000; }");
            WriteFile(sourceDirectory, Path.Combine("profiles", "signage", "assets", "home-default.css"), ".screen { color: #fff; }");
            WriteFile(sourceDirectory, Path.Combine("profiles", "signage", "assets", "logo-signage.svg"), "<svg xmlns='http://www.w3.org/2000/svg'></svg>");
        }

        private static void WriteFile(string sourceDirectory, string relativePath, string content)
        {
            var fullPath = Path.Combine(sourceDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, Encoding.UTF8);
        }

        private const string TemplateJson = """
        {
          "template": {
            "code": "altazion-horizon",
            "name": "Altazion Horizon"
          },
          "profiles": [
            {
              "code": "web",
              "name": "Altazion Horizon for Web",
              "source": "profiles/web"
            },
            {
              "code": "signage",
              "name": "Altazion Horizon for Signage",
              "source": "profiles/signage"
            }
          ]
        }
        """;

        private const string WebProfileJson = """
        {
          "profile": {
            "code": "web",
            "name": "Altazion Horizon for Web",
            "basePath": "base",
            "assetsPath": "assets",
            "commonAssets": [
              {
                "usage": "shared",
                "path": "assets/theme.css",
                "fileName": "theme.css",
                "mimeType": "text/css"
              },
              {
                "usage": "shared",
                "path": "assets/logo-web.svg",
                "fileName": "logo-web.svg",
                "mimeType": "image/svg+xml"
              }
            ],
            "optionGroups": [
              {
                "code": "branding",
                "name": "Branding",
                "options": [
                  {
                    "code": "primaryColor",
                    "name": "Couleur principale",
                    "type": "Color",
                    "defaultValue": "#123456"
                  }
                ]
              }
            ],
            "pages": [
              {
                "pageKey": "home",
                "name": "Accueil",
                "defaultVariantCode": "classic",
                "variants": [
                  {
                    "code": "classic",
                    "name": "Classique",
                    "documentPath": "page-variants/home/classic.json"
                  }
                ]
              }
            ],
            "presets": [
              {
                "code": "default-home",
                "name": "Default home",
                "pageVariantSelections": {
                  "home": "classic"
                }
              }
            ]
          }
        }
        """;

        private const string WebThemeGeneralJson = """
        {
          "theme": {
            "id": "81000000-0000-0000-0000-000000000001",
            "tenantId": 0,
            "name": "Altazion Horizon Web Base",
            "isActive": true
          }
        }
        """;

        private const string WebThemeSharedJson = """
        {
          "reusableComponents": [
            {
              "id": "81100000-0000-0000-0000-000000000001",
              "themeId": "81000000-0000-0000-0000-000000000001",
              "tenantId": 0,
              "name": "Header",
              "componentType": "HtmlContent",
              "isActive": true,
              "requestedRenderMode": "sharedFragment",
              "config": {
                "template": "<header>Header</header>"
              }
            }
          ]
        }
        """;

        private const string WebMenuJson = """
        {
          "code": "main",
          "name": "Main",
          "isActive": true,
          "nodes": [
            {
              "code": "home",
              "label": "Accueil",
              "targetUrl": "/",
              "order": 10,
              "isActive": true
            }
          ]
        }
        """;

        private const string WebClassicPageJson = """
        {
          "pageDefinition": {
            "id": "81200000-0000-0000-0000-000000000001",
            "themeId": "81000000-0000-0000-0000-000000000001",
            "tenantId": 0,
            "pageType": "Home",
            "name": "Home classic",
            "isActive": true,
            "nodes": [],
            "composition": []
          },
          "routes": [
            {
              "id": "81230000-0000-0000-0000-000000000001",
              "themeId": "81000000-0000-0000-0000-000000000001",
              "tenantId": 0,
              "isActive": true,
              "match": {
                "path": "/",
                "matchMode": "exact"
              },
              "target": {
                "definitionId": "81200000-0000-0000-0000-000000000001"
              }
            }
          ],
          "reusableComponents": [],
          "menus": [],
          "assets": [
            {
              "usage": "home",
              "path": "assets/home-classic.css",
              "fileName": "home-classic.css",
              "mimeType": "text/css"
            }
          ]
        }
        """;

        private const string SignageProfileJson = """
        {
          "profile": {
            "code": "signage",
            "name": "Altazion Horizon for Signage",
            "basePath": "base",
            "assetsPath": "assets",
            "commonAssets": [
              {
                "usage": "shared",
                "path": "assets/theme.css",
                "fileName": "theme.css",
                "mimeType": "text/css"
              },
              {
                "usage": "shared",
                "path": "assets/logo-signage.svg",
                "fileName": "logo-signage.svg",
                "mimeType": "image/svg+xml"
              }
            ],
            "pages": [
              {
                "pageKey": "home",
                "name": "Accueil",
                "defaultVariantCode": "default",
                "variants": [
                  {
                    "code": "default",
                    "name": "Default",
                    "documentPath": "page-variants/home/default.json"
                  }
                ]
              }
            ]
          }
        }
        """;

        private const string SignageThemeGeneralJson = """
        {
          "theme": {
            "id": "82000000-0000-0000-0000-000000000001",
            "tenantId": 0,
            "name": "Altazion Horizon Signage Base",
            "isActive": true
          }
        }
        """;

        private const string SignageThemeSharedJson = """
        {
          "reusableComponents": []
        }
        """;

        private const string SignageDefaultPageJson = """
        {
          "pageDefinition": {
            "id": "82200000-0000-0000-0000-000000000001",
            "themeId": "82000000-0000-0000-0000-000000000001",
            "tenantId": 0,
            "pageType": "Home",
            "name": "Home signage",
            "isActive": true,
            "nodes": [],
            "composition": []
          },
          "routes": [],
          "reusableComponents": [],
          "menus": [],
          "assets": [
            {
              "usage": "home",
              "path": "assets/home-default.css",
              "fileName": "home-default.css",
              "mimeType": "text/css"
            }
          ]
        }
        """;
    }
}