# Changelog

## 0.4.4 - 2026-08-29

- Fixed target discovery so locally registered printer records take precedence over unrelated or stale enabled machine presets.
- Added a clearly labeled enabled-preset fallback for setups without recognizable local device records.
- Added continuous Bambu Studio process monitoring, immediate workspace locking, and automatic refresh after Studio closes.
- Fixed active-catalog gap detection so Program Files-only mirrors no longer hide missing Device/AMS profiles.
- Added a warning when bundled manufacturer profiles remain only in an inactive install mirror after a catalog refresh.
- Added regression coverage for Bambu system-preset refreshes removing active manufacturer profiles while leaving inactive mirrors behind.

## 0.4.3 - 2026-08-29

- Fixed SUNLU Marble PLA imports that could leave Bambu's unselected printer profiles pointing at a renamed base preset.
- Added complete proposed-catalog validation, dependent inheritance migration, post-write validation, and automatic rollback.
- Corrected the SUNLU Marble preset name to match Bambu Studio's existing system catalog while retaining its established file path.
- Added a Check for updates button with confirmed download, in-place installation, and automatic restart from GitHub Releases.
- Clarified that machine targets come from local Bambu Studio preset files and are not connected printers or account devices.
- Added regression coverage for fresh alias collisions and repair of catalogs affected by 0.4.2.

## 0.4.2 - 2026-08-24

- Fixed AMS-to-project synchronization for manufacturer IDs longer than the printer's eight-character limit.
- Added globally unique AMS ID generation and package normalization.
- Added a backed-up Repair AMS IDs action for roaming and Program Files catalog copies.
- Added regression coverage for P1S synchronization and PA6 ID collisions.

## 0.4.1 - 2026-08-24

- Moved package selection into the Import Package tab and renamed it to Import Filament Package.
- Added visible version and By Zataralee attribution.
- Added public repository documentation, issue templates, and release automation.

## 0.4.0 - 2026-08-24

- Added printer-neutral manufacturer packages and dynamic printer discovery.
- Added nine manufacturer libraries with 243 filament product families.
- Added compatible-printer-aware duplicate and gap detection.
- Added persistent dark mode.
- Added full-library backup and restore.
- Hardened edit and removal integrity checks across catalog subfolders.
