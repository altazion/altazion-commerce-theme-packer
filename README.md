# altazion-commerce-theme-packer

Dotnet tool for creating Altazion Commerce .altztheme archives from a source theme folder.

## Usage

```powershell
altazion-theme-pack pack --source ./src --output ./dist/theme.altztheme
```

To run the full validation suite without writing a .altztheme file:

```powershell
altazion-theme-pack pack --source ./src --dry-run
```

The source folder must contain theme.general.json. Optional files such as theme.shared.json, theme.seo.json, theme.marketing.json, pages/*.json, menus/*.json and binary assets are included automatically.

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