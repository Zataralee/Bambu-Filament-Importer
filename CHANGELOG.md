# Changelog

## 0.4.10 - 2026-08-29

- Made migration from older bundled manufacturer folders a true one-time operation so a deliberately removed managed package cannot return on a later startup.

## 0.4.9 - 2026-08-29

- Added silent startup checks with in-app notifications for BFI releases, installed-library updates, and newly published filament libraries.
- Replaced the all-or-nothing library updater with a searchable manufacturer chooser that distinguishes Available, Installed, New Library, and Updates Available states.
- Limited update notifications to manufacturer libraries already downloaded by the user while keeping every optional library visible in the chooser.
- Removed manufacturer packages from application release bundles; selected packages now download independently into the managed Local AppData library folder.
- Added one-time migration for package files left beside older portable BFI versions.

## 0.4.8 - 2026-08-29

- Moved active manufacturer packages to one managed Local AppData folder shared by every portable BFI copy.
- Seeded the managed folder once from bundled release packages and opened package selection there by default.
- Prevented application location, including Downloads, from creating competing manufacturer library locations.

## 0.4.7 - 2026-08-29

- Added a separate Update libraries action that checks manufacturer packages independently from the BFI application version.
- Added a lightweight GitHub catalog index with per-package versions, profile counts, and SHA-256 hashes.
- Added validated, atomic library downloads with rollback and clear separation between updating package files and importing them into Bambu Studio.
- Added isolated regression coverage for detecting, installing, and rechecking manufacturer library updates.

## 0.4.6 - 2026-08-29

- Made the inactive-mirror repair action select the Device/AMS destination before calculating active catalog gaps.

## 0.4.5 - 2026-08-29

- Fixed P1S AMS visibility by generating P1-family profiles for both P1S and P1P live-device identities while retaining the selected printer target.
- Eliminated blank filament menu entries by giving products that exactly matched their vendor group an explicit `Basic` product name and migrating matching child aliases.
- Normalized SUNLU vendor groups, including removal of a leaked `SUNLU ABS TEST` value from the packaged catalog.
- Changed the inactive-mirror warning into a repair action that loads the affected manufacturer package with active catalog gaps selected.
- Added package-wide visible-alias and P1 runtime compatibility regression coverage.

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
