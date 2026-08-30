# Contributing

Thanks for helping improve Bambu Filament Importer.

## Bug Reports

Include:

- importer version
- Bambu Studio version
- Windows version
- selected destination and printer model
- the package name
- the exact error message
- relevant screenshots or `%LOCALAPPDATA%\BambuFilamentImporter\error.log`

Do not attach Bambu Studio configuration files without reviewing them for account, printer, network, or access-code information.

## Filament Requests and Corrections

Provide an official manufacturer product page, technical data sheet, printing guide, or safety data sheet. State which values are manufacturer recommendations and which values were calibrated experimentally.

Catalog definitions belong in `catalogs/manufacturers.json` or a sibling `catalogs/manufacturers.*.json` file. SUNLU's original curated base profiles are retained in `catalogs/source` for reproducible migration to the printer-neutral package format.

Regenerate packages after catalog changes:

```powershell
& .\tools\BuildManufacturerPackages.ps1
```

## Pull Requests

1. Keep changes focused.
2. Build with `dotnet build -c Release`.
3. Run `dotnet run --project .\Tests\SmokeTests.csproj -c Release` when a local Bambu Studio installation is available.
4. Validate every generated `.bflib` package.
5. Do not commit `dist`, `bin`, `obj`, recovery snapshots, backups, or user configuration files.

Changes that write to Bambu Studio catalogs must preserve the existing backup, process-check, and post-write integrity safeguards.
