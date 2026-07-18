# altazion-commerce-theme-packer

Dotnet tool for creating Altazion Commerce packages from either a theme source folder or a template source folder.

## Usage

The packer auto-detects the source kind:

- theme source: root folder contains theme.general.json
- template source: root folder contains template.json

If both files are present in the same source folder, the command fails because the source is ambiguous.

### Pack a theme

```powershell
altazion-theme-pack pack --source ./src --output ./dist/theme.altztheme
```

For a theme source, the generated package is a single .altztheme archive.

The source folder must contain theme.general.json. Optional files such as theme.shared.json, theme.seo.json, theme.marketing.json, pages/*.json, menus/*.json and binary assets are included automatically.

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

Dry run works for both source kinds.

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