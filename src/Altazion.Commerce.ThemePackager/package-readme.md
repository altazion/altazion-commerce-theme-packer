# Altazion Commerce Theme Packager

Altazion Commerce Theme Packager is a dotnet tool that creates theme, template and compiled signage packages.

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
- manifest.json with `schemaVersion: "signage-1"` produces one .altzsignage archive

Multiple source kinds in the same folder are rejected. A non-signage manifest
does not change detection of a theme or template, whose existing validation
still applies. If the manifest is the only
source marker, it is validated as signage and invalid versions are reported.
For theme sources, optional shared, SEO, marketing, pages, menus and asset files are detected automatically and embedded in the generated package.
Development-only files under `dev/` and `/dev/theme/...` runtime resource references are ignored and are not embedded in the package.

For template sources, each profile must provide profile.json, a base folder with theme.general.json, and the assets referenced by the profile and its page variants.

Use --dry-run to validate any format without writing archives.

## Pack compiled signage

```powershell
altazion-theme-pack pack --source .\compiled --output .\dist\signage.altzsignage
altazion-theme-pack pack --source .\compiled --dry-run
```

Every compiled file is included unchanged. The source must have a root
`manifest.json` and the manifest must identify an exact-case HTML entry point.
By default the `.altzsignage` ZIP is placed beside the source directory. Output inside the
source is refused; existing output is replaced only after successful archive
creation. Dry run builds the ZIP in memory without writing it. `pack-signage`
remains available as an explicit command; `--output` may specify another extension.

Validation covers UTF-8, JSON, the `signage-1` manifest, option defaults and
constraints, paths, links, collisions and package size/count limits. The checks
are maintained separately from Core-Business and cannot detect database
collation collisions. Final validation with the published Core importer is
still required.