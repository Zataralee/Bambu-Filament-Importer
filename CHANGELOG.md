# Changelog

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
