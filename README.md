# altazion-commerce-theme-packer

Dotnet tool for creating Altazion Commerce theme, template and compiled signage packages.

## Usage

The packer auto-detects the source kind:

- theme source: root folder contains theme.general.json
- template source: root folder contains template.json
- signage source: root folder contains a manifest.json with `schemaVersion: "signage-1"`

The command fails if multiple source kinds are present. A non-signage
`manifest.json` does not change detection of an existing theme or template;
the existing source validator still applies to that file.
When `manifest.json` is the only source marker, it is validated as a signage
manifest, so malformed or unsupported versions produce a diagnostic.

### Pack a theme

```powershell
altazion-theme-pack pack --source ./src --output ./dist/theme.altztheme
```

For a theme source, the generated package is a single .altztheme archive.

The source folder must contain theme.general.json. Optional files such as theme.shared.json, theme.seo.json, theme.marketing.json, pages/*.json, menus/*.json and binary assets are included automatically.
Development-only files under `dev/` and `/dev/theme/...` runtime resource references are ignored and are not included in the package.

### Pack a template

```powershell
altazion-theme-pack pack --source ./src --output ./dist
```

For a template source, the generated output is one .altztemplate archive per profile declared in template.json.

The source folder must contain template.json. Each profile must point to a folder containing at least:

- profile.json
- base/theme.general.json
- an assets folder

The generated archives follow the template package structure consumed by the Core-Business template installer.

### Dry run

To run validation without writing archives:

```powershell
altazion-theme-pack pack --source ./src --dry-run
```

Dry run works for all three source kinds.

### Pack compiled signage

```powershell
altazion-theme-pack pack --source .\compiled --output .\dist\signage.altzsignage
altazion-theme-pack pack --source .\compiled --dry-run
```

The auto-detected signage packager includes every file in the compiled directory unchanged, including
`manifest.json` at the root and its exact-case HTML `entryPoint`. It does not
compile the source or apply the theme/template exclusions. Without `--output`,
the `.altzsignage` ZIP is named after the source directory and placed alongside it. Output
must be outside the source; an existing ZIP is replaced only after successful
validation and archive creation. Dry run creates the ZIP in memory to check the
same compressed-size limit, but writes nothing.

The explicit `pack-signage` command remains available. With `--output`, the
caller can specify a different filename or extension.

For example, `compiled\manifest.json` can declare:

```json
{
  "schemaVersion": "signage-1",
  "template": { "code": "demo", "name": "Demo", "version": "1.0.0" },
  "entryPoint": "index.html",
  "optionGroups": [],
  "animationZones": [
    { "code": "HOME", "label": "Home", "renderMode": "html" }
  ]
}
```

In this example, `compiled\index.html` must exist alongside the manifest.

The packer checks UTF-8/JSON, the `signage-1` manifest, option defaults and
constraints, the entry point, paths, links, collisions and the Core limits
(5,000 files, 1 MiB manifest, 250,000,000 uncompressed and 100,000,000 ZIP
bytes). Database collation collisions are outside its scope. These checks are
maintained separately from Core-Business: a successful pack or dry run does not
guarantee import, and validation by the published Core importer remains necessary.

## Build

```powershell
dotnet build .\src\Altazion.Commerce.ThemePackager\Altazion.Commerce.ThemePackager.csproj -c Release
dotnet pack .\src\Altazion.Commerce.ThemePackager\Altazion.Commerce.ThemePackager.csproj -c Release
```

## Publish

The package is intended to be published as a public dotnet tool on nuget.org and consumed by GitHub Actions, Azure DevOps tasks and local theme developers.

## GitHub Actions And Trusted Publishing

If you want to use nuget.org Trusted Publishing, the publication workflow must run on GitHub Actions. In practice, that means this repository is better hosted on GitHub, or mirrored there for release automation.

The workflow template is available in [.github/workflows/build-and-publish.yml](.github/workflows/build-and-publish.yml). It builds the tool on pushes and pull requests, then publishes tagged releases such as v1.0.0 or v1.0.0-preview.1 to nuget.org using OIDC trusted publishing.