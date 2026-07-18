# Altazion Commerce Theme Packager

Altazion Commerce Theme Packager is a dotnet tool that creates Altazion Commerce packages from either a theme source or a template source.

## Install

```powershell
dotnet tool install --global Altazion.Commerce.ThemePackager
```

## Pack a source

```powershell
altazion-theme-pack pack --source ./src --output ./dist
```

The tool auto-detects the source kind from the root folder:

- theme.general.json produces a single .altztheme archive
- template.json produces one .altztemplate archive per declared profile

For theme sources, optional shared, SEO, marketing, pages, menus and asset files are detected automatically and embedded in the generated package.

For template sources, each profile must provide profile.json, a base folder with theme.general.json, and the assets referenced by the profile and its page variants.

Use --dry-run to validate either format without writing archives.