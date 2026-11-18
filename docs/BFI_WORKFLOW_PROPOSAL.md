# BFI workflow and layout proposal

Original review proposal, November 17, 2026. Prepared after the Studio 2.9.1 compatibility fix passed all 121 smoke checks. On November 18, the owner selected and tested the Compact workspace and approved its promotion to the v0.6.0 stable release. The analysis below records the original alternatives.

## Recommendation

Use a guided, catalog-centered workspace with three main destinations: **Add filaments**, **My library**, and **Maintenance**. Keep the native Windows application and its existing light/dark styling. Reduce the number of storage concepts users must understand before completing an import.

The alternate **Compact workspace** uses a horizontal navigation bar and a selection panel beside the product list. It is faster for experienced users who regularly import many products. The guided layout is the stronger default because it gives each decision enough room and makes printer scope harder to overlook. Density can be a preference without maintaining two different install engines.

The accompanying interactive mockup includes both layouts and sample states for selection, printer choice, change review, completion, profile editing, recovery, library updates, and restore. Its data and temperature values are illustrative; it does not access or change Bambu files.

## Findings in the current application

| Observed behavior | Effect on the workflow | Proposed change |
| --- | --- | --- |
| The library catalog labels a locally downloaded package “Installed.” Its footer says a separate import is still required. | Users can reasonably expect the material to be available in Studio before it is. | Distinct states: Available to download, Downloaded, In Studio, Partial coverage, Update available. |
| Download happens in a separate modal, followed by opening a `.bflib` from a file picker in another tab. | Users have to reconnect two parts of the same task and recognize package filenames. | Select manufacturer/products directly; download missing packages as part of the reviewed operation. Retain “Open local package” as a secondary entry point. |
| Import options and the global Install Selected button remain visible while managing the current library. | The actions and selections belong to different contexts, increasing ambiguity. | Keep actions next to the content they affect. Show installation only in the add/review flow. |
| “Select all” controls are repeated, while package filaments and generated printer profiles use different counts. | It is hard to understand what will actually be written. | Select products once; summarize products, printer coverage, destinations, and exact file changes separately. |
| Selecting a product in `SelectProduct` prefers an X1C profile when available. | The editor may target a printer the user did not explicitly choose. | Always show printer, nozzle and edit scope before settings. Keep the last explicit choice, with a clear fallback prompt when unavailable. |
| Settings are a long table of direct and inherited values. | Users must understand inheritance and find relevant rows among many fields. | Group common settings, display units and origin, support deliberate overrides/reset, and retain a searchable advanced table. |
| BFI disables the entire workspace while Studio is running. | Users cannot browse or prepare an import during a print/session. | Allow browsing and staging. Disable writes only; refresh and revalidate the plan after Studio closes. Never apply automatically. |
| Backups, log details, updates and recovery are scattered among global buttons and warnings. | Recovery requires technical knowledge and competes with ordinary importing. | Maintenance owns health checks, missing-profile repairs, update previews, backups and activity. Errors show the next useful action, with expandable diagnostic details. |

These are findings from the current XAML, event handlers, README, library dialog and observed running window. They are design hypotheses, not results of a usability study.

## Proposed flows

**New import:** Add filaments → search/browse products → choose printers/nozzles and destination → review additions, retained profiles and conflicts → install → show where to find the materials in Studio.

The default destination can be “AMS and project dropdown,” with plain-language alternatives. Remember an explicit preference. Keep Program Files mirroring under advanced options, disabled by default. Describe it accurately: a mirror can aid recovery, but it does not prevent Studio from refreshing its active catalog.

Review is a real change plan, not a confirmation sentence. It must show new versus retained coverage, overwritten settings, shared-base effects, expected downloads, backup scope and any elevated permissions. Disable installation for zero selected printers, an empty/no-op selection, invalid packages, unresolved conflicts or an open Studio process. Recompute when the source catalog changes.

**Edit a material:** My library → product → explicit printer/nozzle → edit common settings → review/save changes. Show the inherited source beside each value and a clear unsaved-changes indicator. Warn before navigating away with unsaved work. Editing a base that affects several printers requires a visible scope preview; avoid silently broadening a printer-specific change.

**Manufacturer update:** show downloaded package version separately from applied version → compare new package values, previous package values and local tuning → keep overrides by default → review conflicts → apply selected changes with backup. The current package updater is not already capable of this three-way merge; this is new work requiring provenance data and conflict handling.

**After a Studio update:** read-only scan → identify which installed products lost active coverage → review missing assignments → restore only the reviewed gaps → validate. Match identities and actual compatible printers, not filenames or inactive Program Files mirrors alone. Do not offer a green compatibility status merely because the Studio process is closed.

**Removal and restore:** choose product or manufacturer → show affected printers, shared dependencies and custom settings → back up → apply → validate. Offer a specific recovery entry afterward. Restoration should preview merges and flag edits made since a backup; a generic “Undo everything” promise would be unsafe with the current file-level backup model.

## Layout

- Narrow persistent navigation: Add filaments, My library, Maintenance. Application updates, theme, support and diagnostic export belong in a secondary Settings/Help area.
- Main work area uses product names as the primary unit. Printer profiles and source paths appear on demand.
- The guided import uses three steps and one primary action at each step. The compact alternative keeps selection, destination and printer controls together before the same review page.
- My library uses a product list and a settings panel. A manufacturer selection shows a useful summary rather than an empty settings table.
- A small persistent Studio status explains what is available. Detailed logs are collapsed into activity/error details instead of consuming the bottom of every screen.
- Reflow for smaller windows, clear keyboard focus and text labels, accessible status text, and light/dark contrast. The prototype was checked at desktop, 736 px and 320 px widths; final native WPF work also needs DPI and keyboard checks.

## Implementation sequence after review

1. Clarify status terms and introduce an explicit selection/change-plan model. Separate package download state from installed coverage. Keep the tested catalog services and safeguards.
2. Build the unified product browser and guided review/completion flow, including local-package entry, offline behavior and multi-manufacturer selection.
3. Rework My library around explicit printer/nozzle scope, grouped settings and unsaved-change handling.
4. Add provenance-aware library updates and scoped repair/restore previews. This is the largest backend addition and should be developed separately from the initial layout work.

Validation scenarios: a first-time user installs one material without finding a package file; a returning user adds one printer without overwriting tuning; a user edits only the selected printer; an update preserves a local override; Studio opening mid-review blocks writes without losing selections; closing Studio refreshes the plan; failed writes roll back; removal never leaves dangling inheritance.

Approval should cover the layout direction and the flow above before production UI implementation starts. The compatibility fix is already implemented independently.
