using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altazion.Commerce.ThemePackager.Tests;

[TestClass]
public sealed class ThemePackagerValidationTests
{
  private const string SharedComponentId = "20000000-0000-0000-0000-000000000001";

    [TestMethod]
    public void Pack_accepts_a_valid_theme_source()
    {
        using var theme = TemporaryTheme.Create();

        var outputFile = Path.Combine(theme.RootDirectory, "valid.altztheme");
        var result = ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = outputFile,
        });

        Assert.AreEqual(outputFile, result.OutputPath);
        Assert.IsTrue(File.Exists(outputFile));
    }

    [TestMethod]
    public void Pack_dry_run_validates_without_creating_archive()
    {
        using var theme = TemporaryTheme.Create();

        var outputFile = Path.Combine(theme.RootDirectory, "dry-run.altztheme");
        var result = ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = outputFile,
            IsDryRun = true,
        });

        Assert.AreEqual(string.Empty, result.OutputPath);
        Assert.AreEqual(0L, result.Size);
        Assert.IsFalse(File.Exists(outputFile));
    }

    [TestMethod]
    public void Pack_rejects_duplicate_node_guids()
    {
        using var theme = TemporaryTheme.Create(pageJson: DuplicateNodePageJson);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = Path.Combine(theme.RootDirectory, "duplicate-node.altztheme"),
        }));

        StringAssert.Contains(exception.Message, "Duplicate composition node GUID");
    }

    [TestMethod]
    public void Pack_rejects_unknown_shared_component_reference()
    {
        using var theme = TemporaryTheme.Create(pageJson: UnknownSharedComponentPageJson);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = Path.Combine(theme.RootDirectory, "unknown-shared.altztheme"),
        }));

        StringAssert.Contains(exception.Message, "points to unknown shared component");
    }

      [TestMethod]
      public void Pack_accepts_recursive_shared_components_within_depth_limit()
      {
        using var theme = TemporaryTheme.Create(sharedJson: RecursiveSharedComponentsJson);

        var outputFile = Path.Combine(theme.RootDirectory, "recursive-shared.altztheme");
        var result = ThemePackager.Pack(new PackCommandOptions
        {
          SourceDirectory = theme.SourceDirectory,
          OutputFile = outputFile,
        });

        Assert.AreEqual(outputFile, result.OutputPath);
        Assert.IsTrue(File.Exists(outputFile));
      }

      [TestMethod]
      public void Pack_rejects_cyclic_recursive_shared_components()
      {
        using var theme = TemporaryTheme.Create(sharedJson: CyclicSharedComponentsJson);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
        {
          SourceDirectory = theme.SourceDirectory,
          OutputFile = Path.Combine(theme.RootDirectory, "cyclic-shared.altztheme"),
        }));

        StringAssert.Contains(exception.Message, "cyclic reference between shared components");
      }

      [TestMethod]
      public void Pack_rejects_recursive_shared_components_beyond_depth_limit()
      {
        using var theme = TemporaryTheme.Create(sharedJson: TooDeepSharedComponentsJson);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
        {
          SourceDirectory = theme.SourceDirectory,
          OutputFile = Path.Combine(theme.RootDirectory, "too-deep-shared.altztheme"),
        }));

        StringAssert.Contains(exception.Message, "exceeds maximum depth of 3");
      }

    [TestMethod]
    public void Pack_rejects_unknown_route_target_definition()
    {
        using var theme = TemporaryTheme.Create(pageJson: UnknownRouteTargetPageJson);

        var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
            OutputFile = Path.Combine(theme.RootDirectory, "unknown-route-target.altztheme"),
        }));

        StringAssert.Contains(exception.Message, "target.definitionId points to unknown page definition");
    }

    [TestMethod]
    public void Pack_ignores_missing_local_dev_theme_resource()
    {
        using var theme = TemporaryTheme.Create();
        File.Delete(Path.Combine(theme.SourceDirectory, "logo.svg"));

      var outputFile = Path.Combine(theme.RootDirectory, "missing-resource.altztheme");
      var result = ThemePackager.Pack(new PackCommandOptions
        {
            SourceDirectory = theme.SourceDirectory,
        OutputFile = outputFile,
      });

      Assert.AreEqual(outputFile, result.OutputPath);
      Assert.IsTrue(File.Exists(outputFile));
    }

    [TestMethod]
    public void Pack_does_not_include_dev_files_in_archive()
    {
      using var theme = TemporaryTheme.Create();
      var devDirectory = Path.Combine(theme.SourceDirectory, "dev", "theme");
      Directory.CreateDirectory(devDirectory);
      File.WriteAllText(Path.Combine(devDirectory, "sdk.js"), "development bundle");

      var outputFile = Path.Combine(theme.RootDirectory, "without-dev-files.altztheme");
      ThemePackager.Pack(new PackCommandOptions
      {
        SourceDirectory = theme.SourceDirectory,
        OutputFile = outputFile,
      });

      using var archive = System.IO.Compression.ZipFile.OpenRead(outputFile);
      Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.Contains("dev/", StringComparison.OrdinalIgnoreCase)));
    }

  [TestMethod]
  public void Pack_rejects_duplicate_route_signatures()
  {
    using var theme = TemporaryTheme.Create(
      additionalPages: new Dictionary<string, string>
      {
        ["other.json"] = CreatePageJson(
          definitionId: "30000000-0000-0000-0000-000000000002",
          routeId: "32000000-0000-0000-0000-000000000002",
          routeTargetDefinitionId: "30000000-0000-0000-0000-000000000002",
          routePath: "/",
          matchMode: "exact",
          nodesJson: CreateReferencedNodeJson("31000000-0000-0000-0000-000000000010"),
          compositionJson: CreateCompositionEntryJson("31000000-0000-0000-0000-000000000010", "body", 10),
          pageName: "Other"),
      });

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "duplicate-route-signature.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "Duplicate route signature");
  }

  [TestMethod]
  public void Pack_rejects_orphan_nodes()
  {
    using var theme = TemporaryTheme.Create(pageJson: CreatePageJson(
      definitionId: "30000000-0000-0000-0000-000000000001",
      routeId: "32000000-0000-0000-0000-000000000001",
      routeTargetDefinitionId: "30000000-0000-0000-0000-000000000001",
      routePath: "/",
      matchMode: "exact",
      nodesJson: string.Join(",\n", new[]
      {
        CreateReferencedNodeJson("31000000-0000-0000-0000-000000000001"),
        CreateInlineNodeJson("31000000-0000-0000-0000-000000000002", "<main>Orphan</main>"),
      }),
      compositionJson: CreateCompositionEntryJson("31000000-0000-0000-0000-000000000001", "body", 10)));

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "orphan-node.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "is never referenced from pageDefinition.composition");
  }

  [TestMethod]
  public void Pack_rejects_duplicate_node_references_in_composition()
  {
    using var theme = TemporaryTheme.Create(pageJson: CreatePageJson(
      definitionId: "30000000-0000-0000-0000-000000000001",
      routeId: "32000000-0000-0000-0000-000000000001",
      routeTargetDefinitionId: "30000000-0000-0000-0000-000000000001",
      routePath: "/",
      matchMode: "exact",
      nodesJson: CreateReferencedNodeJson("31000000-0000-0000-0000-000000000001"),
      compositionJson: string.Join(",\n", new[]
      {
        CreateCompositionEntryJson("31000000-0000-0000-0000-000000000001", "body", 10),
        CreateCompositionEntryJson("31000000-0000-0000-0000-000000000001", "body", 20),
      })));

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "duplicate-composition-node.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "Duplicate composition node reference");
  }

  [TestMethod]
  public void Pack_rejects_duplicate_orders_in_same_slot()
  {
    using var theme = TemporaryTheme.Create(pageJson: CreatePageJson(
      definitionId: "30000000-0000-0000-0000-000000000001",
      routeId: "32000000-0000-0000-0000-000000000001",
      routeTargetDefinitionId: "30000000-0000-0000-0000-000000000001",
      routePath: "/",
      matchMode: "exact",
      nodesJson: string.Join(",\n", new[]
      {
        CreateReferencedNodeJson("31000000-0000-0000-0000-000000000001"),
        CreateInlineNodeJson("31000000-0000-0000-0000-000000000002", "<main>Two</main>"),
      }),
      compositionJson: string.Join(",\n", new[]
      {
        CreateCompositionEntryJson("31000000-0000-0000-0000-000000000001", "body", 10),
        CreateCompositionEntryJson("31000000-0000-0000-0000-000000000002", "body", 10),
      })));

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "duplicate-order.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "Duplicate composition order for slot 'body'");
  }

  [TestMethod]
  public void Pack_rejects_duplicate_page_import_keys()
  {
    using var theme = TemporaryTheme.Create(
      pageJson: CreatePageJson(
        definitionId: "30000000-0000-0000-0000-000000000001",
        routeId: "32000000-0000-0000-0000-000000000001",
        routeTargetDefinitionId: "30000000-0000-0000-0000-000000000001",
        routePath: "/",
        matchMode: "exact",
        nodesJson: CreateReferencedNodeJson("31000000-0000-0000-0000-000000000001"),
        compositionJson: CreateCompositionEntryJson("31000000-0000-0000-0000-000000000001", "body", 10),
        pageImportKey: "home"),
      additionalPages: new Dictionary<string, string>
      {
        ["other.json"] = CreatePageJson(
          definitionId: "30000000-0000-0000-0000-000000000002",
          routeId: "32000000-0000-0000-0000-000000000002",
          routeTargetDefinitionId: "30000000-0000-0000-0000-000000000002",
          routePath: "/other",
          matchMode: "exact",
          nodesJson: CreateReferencedNodeJson("31000000-0000-0000-0000-000000000002"),
          compositionJson: CreateCompositionEntryJson("31000000-0000-0000-0000-000000000002", "body", 10),
          pageImportKey: "home",
          pageName: "Other"),
      });

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "duplicate-import-key.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "Duplicate page importKey");
  }

  [TestMethod]
  public void Pack_rejects_requested_render_mode_mismatches()
  {
    using var theme = TemporaryTheme.Create(pageJson: CreatePageJson(
      definitionId: "30000000-0000-0000-0000-000000000001",
      routeId: "32000000-0000-0000-0000-000000000001",
      routeTargetDefinitionId: "30000000-0000-0000-0000-000000000001",
      routePath: "/",
      matchMode: "exact",
      nodesJson: CreateReferencedNodeJson("31000000-0000-0000-0000-000000000001", "inlineHtml"),
      compositionJson: CreateCompositionEntryJson("31000000-0000-0000-0000-000000000001", "body", 10)));

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "render-mode-mismatch.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "does not match shared component render mode");
  }

  [TestMethod]
  public void Pack_rejects_invalid_menus()
  {
    using var theme = TemporaryTheme.Create(menuFiles: new Dictionary<string, string>
    {
      ["header.json"] = CreateMenuJson(
        menuCode: "header",
        nodesJson: string.Join(",\n", new[]
        {
          CreateMenuNodeJson("home", "/", 10),
          CreateMenuNodeJson("home", "/contact", 10),
        }))
    });

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "invalid-menu.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "Duplicate menu node code");
  }

  [TestMethod]
  public void Pack_rejects_unexpected_json_files()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: new Dictionary<string, string>
    {
      ["drafts/unused.json"] = "{}",
    });

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "unexpected-json.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "drafts/unused.json is not part of the supported theme pack structure");
  }

  [TestMethod]
  public void Pack_accepts_content_definition_files()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: ContentDefinitionFiles);

    var outputFile = Path.Combine(theme.RootDirectory, "content-definitions.altztheme");
    ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = outputFile,
    });

    using var archive = ZipFile.OpenRead(outputFile);
    Assert.IsNotNull(archive.GetEntry("theme.content.json"));
    Assert.IsNotNull(archive.GetEntry("content-types/blog-article.json"));
    Assert.IsNotNull(archive.GetEntry("dam/blog.json"));
    Assert.IsNull(archive.GetEntry("assets/content-types/blog-article.json"));
    Assert.IsNull(archive.GetEntry("assets/dam/blog.json"));
  }

  [TestMethod]
  public void Pack_dry_run_accepts_content_definition_files()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: ContentDefinitionFiles);

    var outputFile = Path.Combine(theme.RootDirectory, "content-definitions-dry-run.altztheme");
    var result = ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = outputFile,
      IsDryRun = true,
    });

    Assert.AreEqual(string.Empty, result.OutputPath);
    Assert.IsFalse(File.Exists(outputFile));
  }

  [TestMethod]
  public void Pack_lists_content_definition_files_in_manifest()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: ContentDefinitionFiles);

    var outputFile = Path.Combine(theme.RootDirectory, "content-manifest.altztheme");
    ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = outputFile,
    });

    using var archive = ZipFile.OpenRead(outputFile);
    using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
    using var manifest = JsonDocument.Parse(manifestReader.ReadToEnd());
    var root = manifest.RootElement;
    var files = root.GetProperty("files");

    Assert.AreEqual(2, root.GetProperty("schemaVersion").GetInt32());
    Assert.AreEqual("theme.content.json", files.GetProperty("content").GetString());
    Assert.AreEqual("content-types/blog-article.json", files.GetProperty("contentTypes")[0].GetString());
    Assert.AreEqual("dam/blog.json", files.GetProperty("damCollections")[0].GetString());
  }

  [TestMethod]
  public void Pack_manifest_has_no_content_definition_entries_when_absent()
  {
    using var theme = TemporaryTheme.Create();

    var outputFile = Path.Combine(theme.RootDirectory, "no-content-manifest.altztheme");
    ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = outputFile,
    });

    using var archive = ZipFile.OpenRead(outputFile);
    using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
    using var manifest = JsonDocument.Parse(manifestReader.ReadToEnd());
    var files = manifest.RootElement.GetProperty("files");

    Assert.AreEqual(JsonValueKind.Null, files.GetProperty("content").ValueKind);
    Assert.AreEqual(0, files.GetProperty("contentTypes").GetArrayLength());
    Assert.AreEqual(0, files.GetProperty("damCollections").GetArrayLength());
  }

  [TestMethod]
  public void Pack_rejects_nested_json_in_content_definition_folders()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: new Dictionary<string, string>
    {
      ["content-types/sub/blog.json"] = "{}",
    });

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "nested-content-definition.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "content-types/sub/blog.json is not part of the supported theme pack structure");
  }

  [TestMethod]
  public void Pack_rejects_invalid_json_in_content_definition_files()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: new Dictionary<string, string>
    {
      ["dam/broken.json"] = "{",
    });

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "broken-content-definition.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "dam/broken.json is not valid JSON");
  }

  [TestMethod]
  public void Pack_does_not_package_non_json_files_of_content_definition_folders_as_assets()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: new Dictionary<string, string>
    {
      ["content-types/notes.txt"] = "notes",
    });

    var outputFile = Path.Combine(theme.RootDirectory, "content-notes.altztheme");
    ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = outputFile,
    });

    using var archive = ZipFile.OpenRead(outputFile);
    Assert.IsNull(archive.GetEntry("assets/content-types/notes.txt"));
  }

  [TestMethod]
  public void Pack_rejects_content_type_without_id()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "name": "A", "columns": [ { "code": "title", "dataType": "Text" } ] }"""),
      "is missing required property 'id'");

  [TestMethod]
  public void Pack_rejects_content_type_with_invalid_id()
    => AssertPackRejected(
      ContentType("Blog.json", """{ "id": "Blog", "name": "A", "columns": [ { "code": "title", "dataType": "Text" } ] }"""),
      "Blog.json.id 'Blog' is invalid");

  [TestMethod]
  public void Pack_rejects_content_type_file_name_that_differs_from_its_id()
    => AssertPackRejected(
      ContentType("article.json", ValidBlogArticleJson),
      "must be named 'blog-article.json'");

  [TestMethod]
  public void Pack_rejects_content_type_without_columns()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "id": "blog-article", "name": "A" }"""),
      "columns must be an array with at least one column");

  [TestMethod]
  public void Pack_rejects_duplicate_column_codes_ignoring_case()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "id": "blog-article", "name": "A", "columns": [ { "code": "title", "dataType": "Text" }, { "code": "Title", "dataType": "Text" } ] }"""),
      "Duplicate column code");

  [TestMethod]
  public void Pack_rejects_unsupported_column_data_type()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "id": "blog-article", "name": "A", "columns": [ { "code": "title", "dataType": "Number" } ] }"""),
      "dataType 'Number' is not supported");

  [TestMethod]
  public void Pack_rejects_unknown_title_column_code()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "id": "blog-article", "name": "A", "titleColumnCode": "missing", "columns": [ { "code": "title", "dataType": "Text" } ] }"""),
      "titleColumnCode 'missing' does not match any declared column");

  [TestMethod]
  public void Pack_rejects_group_referencing_unknown_column()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "id": "blog-article", "name": "A", "columns": [ { "code": "title", "dataType": "Text" } ], "groups": [ { "name": "G", "columns": [ "missing" ] } ] }"""),
      "does not match any declared column");

  [TestMethod]
  public void Pack_rejects_extending_type_that_changes_more_than_columns()
    => AssertPackRejected(
      ContentType("products.json", """{ "id": "products", "extends": true, "name": "Produits", "columns": [ { "code": "pitch", "dataType": "Text" } ] }"""),
      "is not allowed when 'extends' is true");

  [TestMethod]
  public void Pack_accepts_extending_type_that_only_adds_columns()
    => AssertPackAccepted(
      ContentType("products.json", """{ "id": "products", "extends": true, "columns": [ { "code": "pitch", "dataType": "Text" } ] }"""));

  [TestMethod]
  public void Pack_rejects_owner_declared_in_content_type_source()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "id": "blog-article", "name": "A", "definitionOwnerCode": "x", "columns": [ { "code": "title", "dataType": "Text" } ] }"""),
      "definitionOwnerCode is set by the import");

  [TestMethod]
  public void Pack_rejects_external_app_code_declared_on_a_column()
    => AssertPackRejected(
      ContentType("blog-article.json", """{ "id": "blog-article", "name": "A", "columns": [ { "code": "title", "dataType": "Text", "externAppCode": "Altazion.Pim" } ] }"""),
      "externAppCode is set by the import");

  [TestMethod]
  public void Pack_rejects_dam_collection_without_code()
    => AssertPackRejected(
      DamCollection("blog.json", """{ "name": "Blog" }"""),
      "is missing required property 'code'");

  [TestMethod]
  public void Pack_rejects_dam_collection_file_name_that_differs_from_its_code()
    => AssertPackRejected(
      DamCollection("images.json", ValidBlogDamJson),
      "must be named 'blog.json'");

  [TestMethod]
  public void Pack_rejects_unsupported_dam_kind()
    => AssertPackRejected(
      DamCollection("blog.json", """{ "code": "blog", "name": "Blog", "kind": "Videos" }"""),
      "kind must be one of");

  [TestMethod]
  public void Pack_rejects_duplicate_dam_group_codes()
    => AssertPackRejected(
      DamCollection("blog.json", """{ "code": "blog", "name": "Blog", "groups": [ { "code": "cover" }, { "code": "Cover" } ] }"""),
      "Duplicate DAM group code");

  [TestMethod]
  public void Pack_rejects_invalid_dam_group_mime_types()
    => AssertPackRejected(
      DamCollection("blog.json", """{ "code": "blog", "name": "Blog", "groups": [ { "code": "cover", "allowedMimeTypes": [ 12 ] } ] }"""),
      "allowedMimeTypes must be an array of non-empty strings");

  [TestMethod]
  public void Pack_rejects_content_skin_pointing_to_unknown_content_type()
    => AssertPackRejected(
      new Dictionary<string, string>(),
      "points to unknown content type 'blog-article'",
      sharedJson: SharedJsonWithSkin("blog-article"));

  [TestMethod]
  public void Pack_accepts_content_skin_pointing_to_declared_content_type()
    => AssertPackAccepted(
      ContentType("blog-article.json", ValidBlogArticleJson),
      sharedJson: SharedJsonWithSkin("blog-article"));

  [TestMethod]
  public void Pack_rejects_duplicate_content_skins()
    => AssertPackRejected(
      ContentType("blog-article.json", ValidBlogArticleJson),
      "Duplicate content skin 'blog-article/full'",
      sharedJson: SharedJsonWithSkin("blog-article", "full", secondSkinCode: "full"));

  [TestMethod]
  public void Pack_rejects_page_pointing_to_unknown_content_type()
    => AssertPackRejected(
      new Dictionary<string, string>(),
      "pageDefinition.config.contentTypeId points to unknown content type 'blog-article'",
      pageJson: PageJsonWithContentType("blog-article"));

  [TestMethod]
  public void Pack_accepts_page_pointing_to_declared_content_type()
    => AssertPackAccepted(
      ContentType("blog-article.json", ValidBlogArticleJson),
      pageJson: PageJsonWithContentType("blog-article"));

  [TestMethod]
  public void Pack_rejects_unwanted_asset_files()
  {
    using var theme = TemporaryTheme.Create(additionalFiles: new Dictionary<string, string>
    {
      ["Thumbs.db"] = "junk",
    });

    var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
    {
      SourceDirectory = theme.SourceDirectory,
      OutputFile = Path.Combine(theme.RootDirectory, "unwanted-asset.altztheme"),
    }));

    StringAssert.Contains(exception.Message, "Thumbs.db should not be packaged as an asset");
  }

    private const string DuplicateNodePageJson = """
    {
      "pageDefinition": {
        "id": "30000000-0000-0000-0000-000000000001",
        "themeId": "10000000-0000-0000-0000-000000000001",
        "tenantId": 0,
        "pageType": "Home",
        "name": "Home",
        "isActive": true,
        "revision": 1,
        "nodes": [
          {
            "id": "31000000-0000-0000-0000-000000000001",
            "reference": {
              "reusableComponentId": "20000000-0000-0000-0000-000000000001"
            },
            "requestedRenderMode": "sharedFragment",
            "isActive": true
          },
          {
            "id": "31000000-0000-0000-0000-000000000001",
            "componentType": "HtmlContent",
            "requestedRenderMode": "inlineHtml",
            "isActive": true,
            "config": {
              "template": "<main>Home</main>"
            }
          }
        ],
        "composition": [
          {
            "nodeId": "31000000-0000-0000-0000-000000000001",
            "slot": "body",
            "order": 10
          }
        ]
      },
      "routes": [
        {
          "id": "32000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "isActive": true,
          "priority": 1000,
          "match": {
            "path": "/",
            "matchMode": "exact"
          },
          "target": {
            "definitionId": "30000000-0000-0000-0000-000000000001"
          }
        }
      ]
    }
    """;

    private const string UnknownSharedComponentPageJson = """
    {
      "pageDefinition": {
        "id": "30000000-0000-0000-0000-000000000001",
        "themeId": "10000000-0000-0000-0000-000000000001",
        "tenantId": 0,
        "pageType": "Home",
        "name": "Home",
        "isActive": true,
        "revision": 1,
        "nodes": [
          {
            "id": "31000000-0000-0000-0000-000000000001",
            "reference": {
              "reusableComponentId": "20000000-0000-0000-0000-000000000099"
            },
            "requestedRenderMode": "sharedFragment",
            "isActive": true
          }
        ],
        "composition": [
          {
            "nodeId": "31000000-0000-0000-0000-000000000001",
            "slot": "body",
            "order": 10
          }
        ]
      },
      "routes": [
        {
          "id": "32000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "isActive": true,
          "priority": 1000,
          "match": {
            "path": "/",
            "matchMode": "exact"
          },
          "target": {
            "definitionId": "30000000-0000-0000-0000-000000000001"
          }
        }
      ]
    }
    """;

    private const string UnknownRouteTargetPageJson = """
    {
      "pageDefinition": {
        "id": "30000000-0000-0000-0000-000000000001",
        "themeId": "10000000-0000-0000-0000-000000000001",
        "tenantId": 0,
        "pageType": "Home",
        "name": "Home",
        "isActive": true,
        "revision": 1,
        "nodes": [
          {
            "id": "31000000-0000-0000-0000-000000000001",
            "reference": {
              "reusableComponentId": "20000000-0000-0000-0000-000000000001"
            },
            "requestedRenderMode": "sharedFragment",
            "isActive": true
          }
        ],
        "composition": [
          {
            "nodeId": "31000000-0000-0000-0000-000000000001",
            "slot": "body",
            "order": 10
          }
        ]
      },
      "routes": [
        {
          "id": "32000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "isActive": true,
          "priority": 1000,
          "match": {
            "path": "/",
            "matchMode": "exact"
          },
          "target": {
            "definitionId": "30000000-0000-0000-0000-000000000099"
          }
        }
      ]
    }
    """;

    private static readonly IReadOnlyDictionary<string, string> ContentDefinitionFiles = new Dictionary<string, string>
    {
      ["theme.content.json"] = "{}",
      ["content-types/blog-article.json"] = ValidBlogArticleJson,
      ["dam/blog.json"] = ValidBlogDamJson,
    };

    internal const string ValidBlogArticleJson = """
    {
      "id": "blog-article",
      "name": "Article de blog",
      "titleColumnCode": "title",
      "columns": [
        { "code": "title", "dataType": "Text" },
        { "code": "publishedAt", "dataType": "Date" }
      ],
      "groups": [
        { "name": "Contenu", "columns": [ "title", "publishedAt" ] }
      ]
    }
    """;

    private const string ValidBlogDamJson = """
    {
      "code": "blog",
      "name": "Blog",
      "kind": "BlogArticle",
      "groups": [
        { "code": "cover", "allowedMimeTypes": [ "image/jpeg", "image/png" ] }
      ],
      "properties": [ { "code": "credit" } ],
      "folders": [ { "code": "covers" } ]
    }
    """;

    internal static string SharedJsonWithSkin(string contentTypeId, string skinCode = "full", string secondSkinCode = "")
    {
      var secondSkin = secondSkinCode.Length == 0
        ? string.Empty
        : $$"""
          ,
            {
              "id": "20000000-0000-0000-0000-000000000003",
              "themeId": "10000000-0000-0000-0000-000000000001",
              "tenantId": 0,
              "name": "Skin 2",
              "componentType": "ContentSkin",
              "isActive": true,
              "revision": 1,
              "requestedRenderMode": "sharedFragment",
              "config": { "contentTypeId": "{{contentTypeId}}", "skinCode": "{{secondSkinCode}}" }
            }
          """;

      return $$"""
      {
        "reusableComponents": [
          {
            "id": "20000000-0000-0000-0000-000000000001",
            "themeId": "10000000-0000-0000-0000-000000000001",
            "tenantId": 0,
            "name": "Head assets",
            "componentType": "AltazionSdkHead",
            "isActive": true,
            "revision": 1,
            "requestedRenderMode": "sharedFragment",
            "config": {}
          },
          {
            "id": "20000000-0000-0000-0000-000000000002",
            "themeId": "10000000-0000-0000-0000-000000000001",
            "tenantId": 0,
            "name": "Skin",
            "componentType": "ContentSkin",
            "isActive": true,
            "revision": 1,
            "requestedRenderMode": "sharedFragment",
            "config": { "contentTypeId": "{{contentTypeId}}", "skinCode": "{{skinCode}}" }
          }{{secondSkin}}
        ]
      }
      """;
    }

    private static string PageJsonWithContentType(string contentTypeId)
      => ValidPageJson.Replace(
        "\"pageType\": \"Home\",",
        $"\"pageType\": \"Home\", \"config\": {{ \"contentTypeId\": \"{contentTypeId}\" }},");

    private static void AssertPackRejected(
      IReadOnlyDictionary<string, string> files,
      string expectedMessage,
      string? sharedJson = null,
      string? pageJson = null)
    {
      using var theme = TemporaryTheme.Create(pageJson: pageJson, sharedJson: sharedJson, additionalFiles: files);

      var exception = Assert.ThrowsException<ThemePackagerException>(() => ThemePackager.Pack(new PackCommandOptions
      {
        SourceDirectory = theme.SourceDirectory,
        OutputFile = Path.Combine(theme.RootDirectory, "rejected.altztheme"),
      }));

      StringAssert.Contains(exception.Message, expectedMessage);
    }

    private static void AssertPackAccepted(
      IReadOnlyDictionary<string, string> files,
      string? sharedJson = null,
      string? pageJson = null)
    {
      using var theme = TemporaryTheme.Create(pageJson: pageJson, sharedJson: sharedJson, additionalFiles: files);

      var outputFile = Path.Combine(theme.RootDirectory, "accepted.altztheme");
      ThemePackager.Pack(new PackCommandOptions
      {
        SourceDirectory = theme.SourceDirectory,
        OutputFile = outputFile,
      });

      Assert.IsTrue(File.Exists(outputFile));
    }

    private static IReadOnlyDictionary<string, string> ContentType(string fileName, string json)
      => new Dictionary<string, string> { [$"content-types/{fileName}"] = json };

    private static IReadOnlyDictionary<string, string> DamCollection(string fileName, string json)
      => new Dictionary<string, string> { [$"dam/{fileName}"] = json };

    internal sealed class TemporaryTheme : IDisposable
    {
      private TemporaryTheme(string rootDirectory)
      {
        RootDirectory = rootDirectory;
        SourceDirectory = Path.Combine(rootDirectory, "src");
      }

      public string RootDirectory { get; }

      public string SourceDirectory { get; }

      public static TemporaryTheme Create(
        string? pageJson = null,
        IReadOnlyDictionary<string, string>? additionalPages = null,
        IReadOnlyDictionary<string, string>? menuFiles = null,
        IReadOnlyDictionary<string, string>? additionalFiles = null,
        string? sharedJson = null)
      {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "altazion-theme-packager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        var theme = new TemporaryTheme(rootDirectory);
        Directory.CreateDirectory(theme.SourceDirectory);
        Directory.CreateDirectory(Path.Combine(theme.SourceDirectory, "pages"));

        File.WriteAllText(Path.Combine(theme.SourceDirectory, "theme.general.json"), ThemeGeneralJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(theme.SourceDirectory, "theme.shared.json"), sharedJson ?? ThemeSharedJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(theme.SourceDirectory, "pages", "home.json"), pageJson ?? ValidPageJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(theme.SourceDirectory, "theme.css"), "body { color: #111; }", Encoding.UTF8);
        File.WriteAllText(Path.Combine(theme.SourceDirectory, "logo.svg"), "<svg xmlns='http://www.w3.org/2000/svg'></svg>", Encoding.UTF8);

        if (additionalPages is not null)
        {
          foreach (var page in additionalPages)
            WriteFile(theme.SourceDirectory, Path.Combine("pages", page.Key), page.Value);
        }

        if (menuFiles is not null)
        {
          foreach (var menuFile in menuFiles)
            WriteFile(theme.SourceDirectory, Path.Combine("menus", menuFile.Key), menuFile.Value);
        }

        if (additionalFiles is not null)
        {
          foreach (var additionalFile in additionalFiles)
            WriteFile(theme.SourceDirectory, additionalFile.Key, additionalFile.Value);
        }

        return theme;
      }

      public void Dispose()
      {
        if (Directory.Exists(RootDirectory))
          Directory.Delete(RootDirectory, recursive: true);
      }

      private static void WriteFile(string sourceDirectory, string relativePath, string content)
      {
        var fullPath = Path.Combine(sourceDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Encoding.UTF8);
      }
    }

    private const string ThemeGeneralJson = """
    {
      "theme": {
        "id": "10000000-0000-0000-0000-000000000001",
        "tenantId": 0,
        "name": "Starter Theme",
        "isActive": true,
        "revision": 1
      }
    }
    """;

    private const string ThemeSharedJson = """
    {
      "reusableComponents": [
        {
          "id": "20000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Head assets",
          "componentType": "AltazionSdkHead",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "config": {
            "extraHtml": "<link rel='stylesheet' href='/dev/theme/theme.css' /><img src='/dev/theme/logo.svg' alt='Logo' />"
          }
        }
      ]
    }
    """;

    private const string RecursiveSharedComponentsJson = """
    {
      "reusableComponents": [
        {
          "id": "20000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Root composite",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "nodes": [
            {
              "id": "21000000-0000-0000-0000-000000000001",
              "reference": {
                "reusableComponentId": "20000000-0000-0000-0000-000000000002"
              },
              "requestedRenderMode": "sharedFragment",
              "isActive": true
            }
          ],
          "composition": [
            {
              "nodeId": "21000000-0000-0000-0000-000000000001",
              "slot": "head",
              "order": 10
            }
          ]
        },
        {
          "id": "20000000-0000-0000-0000-000000000002",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Nested composite",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "nodes": [
            {
              "id": "21000000-0000-0000-0000-000000000002",
              "reference": {
                "reusableComponentId": "20000000-0000-0000-0000-000000000003"
              },
              "requestedRenderMode": "sharedFragment",
              "isActive": true
            }
          ],
          "composition": [
            {
              "nodeId": "21000000-0000-0000-0000-000000000002",
              "slot": "head",
              "order": 10
            }
          ]
        },
        {
          "id": "20000000-0000-0000-0000-000000000003",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Leaf assets",
          "componentType": "AltazionSdkHead",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "config": {
            "extraHtml": "<link rel='stylesheet' href='/dev/theme/theme.css' /><img src='/dev/theme/logo.svg' alt='Logo' />"
          }
        }
      ]
    }
    """;

    private const string CyclicSharedComponentsJson = """
    {
      "reusableComponents": [
        {
          "id": "20000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Cycle A",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "nodes": [
            {
              "id": "21000000-0000-0000-0000-000000000011",
              "reference": {
                "reusableComponentId": "20000000-0000-0000-0000-000000000002"
              },
              "requestedRenderMode": "sharedFragment",
              "isActive": true
            }
          ],
          "composition": [
            {
              "nodeId": "21000000-0000-0000-0000-000000000011",
              "slot": "head",
              "order": 10
            }
          ]
        },
        {
          "id": "20000000-0000-0000-0000-000000000002",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Cycle B",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "nodes": [
            {
              "id": "21000000-0000-0000-0000-000000000012",
              "reference": {
                "reusableComponentId": "20000000-0000-0000-0000-000000000001"
              },
              "requestedRenderMode": "sharedFragment",
              "isActive": true
            }
          ],
          "composition": [
            {
              "nodeId": "21000000-0000-0000-0000-000000000012",
              "slot": "head",
              "order": 10
            }
          ]
        }
      ]
    }
    """;

    private const string TooDeepSharedComponentsJson = """
    {
      "reusableComponents": [
        {
          "id": "20000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Level 1",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "nodes": [
            {
              "id": "21000000-0000-0000-0000-000000000021",
              "reference": {
                "reusableComponentId": "20000000-0000-0000-0000-000000000002"
              },
              "requestedRenderMode": "sharedFragment",
              "isActive": true
            }
          ],
          "composition": [
            {
              "nodeId": "21000000-0000-0000-0000-000000000021",
              "slot": "head",
              "order": 10
            }
          ]
        },
        {
          "id": "20000000-0000-0000-0000-000000000002",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Level 2",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "nodes": [
            {
              "id": "21000000-0000-0000-0000-000000000022",
              "reference": {
                "reusableComponentId": "20000000-0000-0000-0000-000000000003"
              },
              "requestedRenderMode": "sharedFragment",
              "isActive": true
            }
          ],
          "composition": [
            {
              "nodeId": "21000000-0000-0000-0000-000000000022",
              "slot": "head",
              "order": 10
            }
          ]
        },
        {
          "id": "20000000-0000-0000-0000-000000000003",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Level 3",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "nodes": [
            {
              "id": "21000000-0000-0000-0000-000000000023",
              "reference": {
                "reusableComponentId": "20000000-0000-0000-0000-000000000004"
              },
              "requestedRenderMode": "sharedFragment",
              "isActive": true
            }
          ],
          "composition": [
            {
              "nodeId": "21000000-0000-0000-0000-000000000023",
              "slot": "head",
              "order": 10
            }
          ]
        },
        {
          "id": "20000000-0000-0000-0000-000000000004",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "name": "Level 4",
          "componentType": "AltazionSdkHead",
          "isActive": true,
          "revision": 1,
          "requestedRenderMode": "sharedFragment",
          "config": {
            "extraHtml": "<link rel='stylesheet' href='/dev/theme/theme.css' />"
          }
        }
      ]
    }
    """;

    private const string ValidPageJson = """
    {
      "pageDefinition": {
        "id": "30000000-0000-0000-0000-000000000001",
        "themeId": "10000000-0000-0000-0000-000000000001",
        "tenantId": 0,
        "pageType": "Home",
        "name": "Home",
        "isActive": true,
        "revision": 1,
        "nodes": [
          {
            "id": "31000000-0000-0000-0000-000000000001",
            "reference": {
              "reusableComponentId": "20000000-0000-0000-0000-000000000001"
            },
            "requestedRenderMode": "sharedFragment",
            "isActive": true
          },
          {
            "id": "31000000-0000-0000-0000-000000000002",
            "componentType": "HtmlContent",
            "requestedRenderMode": "inlineHtml",
            "isActive": true,
            "config": {
              "template": "<main><a href='/'>Home</a></main>"
            }
          }
        ],
        "composition": [
          {
            "nodeId": "31000000-0000-0000-0000-000000000001",
            "slot": "head",
            "order": 10
          },
          {
            "nodeId": "31000000-0000-0000-0000-000000000002",
            "slot": "body",
            "order": 20
          }
        ]
      },
      "routes": [
        {
          "id": "32000000-0000-0000-0000-000000000001",
          "themeId": "10000000-0000-0000-0000-000000000001",
          "tenantId": 0,
          "isActive": true,
          "priority": 1000,
          "match": {
            "path": "/",
            "matchMode": "exact"
          },
          "target": {
            "definitionId": "30000000-0000-0000-0000-000000000001"
          }
        }
      ]
    }
    """;

    private static string CreateReferencedNodeJson(string nodeId, string requestedRenderMode = "sharedFragment")
        => $$"""
          {
            "id": "{{nodeId}}",
            "reference": {
              "reusableComponentId": "{{SharedComponentId}}"
            },
            "requestedRenderMode": "{{requestedRenderMode}}",
            "isActive": true
          }
        """;

    private static string CreateInlineNodeJson(string nodeId, string template, string requestedRenderMode = "inlineHtml")
        => $$"""
          {
            "id": "{{nodeId}}",
            "componentType": "HtmlContent",
            "requestedRenderMode": "{{requestedRenderMode}}",
            "isActive": true,
            "config": {
              "template": "{{template}}"
            }
          }
        """;

    private static string CreateCompositionEntryJson(string nodeId, string slot, int order)
        => $$"""
          {
            "nodeId": "{{nodeId}}",
            "slot": "{{slot}}",
            "order": {{order}}
          }
        """;

    private static string CreatePageJson(
        string definitionId,
        string routeId,
        string routeTargetDefinitionId,
        string routePath,
        string matchMode,
        string nodesJson,
        string compositionJson,
        string? pageImportKey = null,
        string pageType = "Home",
        string pageName = "Home")
    {
        var metadataBlock = pageImportKey is null
            ? string.Empty
            : $$"""
                "metadata": {
                  "extensions": {
                    "importKey": "{{pageImportKey}}"
                  }
                },
        """;

        return $$"""
        {
          "pageDefinition": {
            "id": "{{definitionId}}",
            "themeId": "10000000-0000-0000-0000-000000000001",
            "tenantId": 0,
            "pageType": "{{pageType}}",
            "name": "{{pageName}}",
            "isActive": true,
            "revision": 1,
        {{metadataBlock}}
            "nodes": [
        {{nodesJson}}
            ],
            "composition": [
        {{compositionJson}}
            ]
          },
          "routes": [
            {
              "id": "{{routeId}}",
              "themeId": "10000000-0000-0000-0000-000000000001",
              "tenantId": 0,
              "isActive": true,
              "priority": 1000,
              "match": {
                "path": "{{routePath}}",
                "matchMode": "{{matchMode}}"
              },
              "target": {
                "definitionId": "{{routeTargetDefinitionId}}"
              }
            }
          ]
        }
        """;
    }

    private static string CreateMenuJson(string menuCode, string nodesJson, string menuName = "Menu")
        => $$"""
        {
          "code": "{{menuCode}}",
          "name": "{{menuName}}",
          "isActive": true,
          "nodes": [
        {{nodesJson}}
          ]
        }
        """;

    private static string CreateMenuNodeJson(string code, string targetUrl, int order)
        => $$"""
          {
            "code": "{{code}}",
            "label": "{{code}}",
            "targetUrl": "{{targetUrl}}",
            "order": {{order}},
            "isActive": true
          }
        """;
}