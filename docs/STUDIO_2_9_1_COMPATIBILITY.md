# Bambu Studio 2.9.1 compatibility fix

Verified locally on November 17, 2026 against installed Studio 02.09.01.70.

## Failure and evidence

BFI 0.4.15 logs showed `ArgumentException: An item with the same key has already been added` for `filament_dev_ams_drying_time`. Import failed in `BambuCatalogIntegrity.ValidateProfileRoot`; the AMS audit and removal also failed. The unmodified smoke suite reproduced the exception while validating the live catalog.

Both the roaming and Program Files catalogs contain repeated drying-time properties in `BBL/filament/fdm_filament_pc.json`. The bundled PVA profile also contains them. Earlier arrays have four values; the later arrays have eight. These are upstream settings, unrelated to the SUNLU material being imported. Other machine/process profiles also contain duplicate properties.

Studio uses nlohmann JSON for profile loading. Its bundled parser assigns repeated object properties to the same slot, retaining the last value:

- [Studio 2.9.1 profile loader](https://github.com/bambulab/BambuStudio/blob/v02.09.01.70/src/libslic3r/Config.cpp)
- [Bundled JSON parser](https://github.com/bambulab/BambuStudio/blob/v02.09.01.70/src/nlohmann/detail/input/json_sax.hpp)
- [Studio release](https://github.com/bambulab/BambuStudio/releases/tag/v02.09.01.70)

## Change

`BambuJson.Parse` parses through `JsonDocument` and recursively constructs editable nodes with last-property-wins semantics. Bambu JSON consumers share this reader, including scanning, settings inheritance, install validation, removal, backup/restore, and AMS ID auditing/repair. It handles nested objects and arrays, preserves case-sensitive keys, and still rejects malformed JSON.

Catalog checks for duplicate profile identities, missing files, mismatched names, unsafe paths, and missing inherited parents remain in place. Read operations do not rewrite upstream files. Existing backup and import rollback behavior is retained. The compatibility fix was verified before starting the UI redesign; both are included in v0.6.0.

## Verification

- Release build: zero warnings, zero errors.
- Smoke suite: all 121 checks passed, including 17 manufacturer packages / 423 product families and the existing light/dark window renders.
- Read-only validation of both live catalogs and live AMS ID audit passed.
- An isolated copy of the updated roaming catalog successfully imported the reported SUNLU High Speed Matte PETG for P1S, with backups and expected printer coverage.
- A persistent duplicate-key fixture verified scanning, settings resolution, rename and removal. The native PC profile remained byte identical through the test.
- Malformed JSON and duplicate manifest identities are still rejected.
- Existing Project Library import, full backup/restore, inheritance repair and process-lock checks passed.

Write tests ran only in temporary catalogs; no live filament/config writes or physical-printer tests were performed. This fixes the observed JSON compatibility failure, not a guarantee about all future Studio changes.

## Build

Standalone local build: `dist/studio-2.9.1-fix/BambuFilamentImporter.exe` (development version 0.5.0).

Test output: `Tests/artifacts/studio-2.9.1-results.txt` (local artifact, ignored by Git).

```powershell
dotnet build -c Release
dotnet run --project Tests/SmokeTests.csproj -c Release
dotnet publish BambuFilamentImporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/studio-2.9.1-fix
```
