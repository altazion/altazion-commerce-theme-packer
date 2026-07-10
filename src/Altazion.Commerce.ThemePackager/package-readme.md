# Altazion Commerce Theme Packager

Altazion Commerce Theme Packager is a dotnet tool that creates .altztheme archives from a source theme folder.

## Install

```powershell
dotnet tool install --global Altazion.Commerce.ThemePackager
```

## Pack a theme

```powershell
altazion-theme-pack pack --source ./src --output ./dist/theme.altztheme
```

The source directory must contain theme.general.json. Optional shared, SEO, marketing, pages, menus and asset files are detected automatically and embedded in the generated package.