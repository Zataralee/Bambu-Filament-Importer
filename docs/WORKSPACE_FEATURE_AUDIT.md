# Workspace feature audit — 0.6.2

The audit follows every action exposed by `MainWindow.xaml` and its handlers. The production entry point uses `WorkspaceWindow`; no workspace command launches the old window. File pickers, confirmations, the download picker, and diagnostic-report dialogs remain ordinary task dialogs. Administrator commands use a headless helper and Windows approval, not another editor window.

| Previous action | Current location and behavior |
| --- | --- |
| Refresh Library | My Library → Refresh library |
| Browse current manufacturers/products/profiles | My Library → System or Custom → brand → type → filament; explicit printer-profile selection |
| Search library | My Library search; maintains the hierarchy |
| Read/edit effective settings | My Library settings table; search, category filter, origin, save/discard, reset inherited value |
| Save profile name/manufacturer | My Library → Advanced library tools → Profile identity |
| Rename manufacturer | My Library → Advanced library tools → Change brand for the selected filament/type/brand; supports both libraries |
| Remove profile | My Library → Delete this profile; also available in Advanced library tools |
| Remove filament/manufacturer | My Library → Delete selected; additionally supports material-type groups; filtered selections explicitly show their count and library |
| Remove matching installed copies | My Library → Advanced library tools → separate administrator action |
| Repair AMS IDs | My Library → Advanced library tools → Library repairs; separate administrator action when installed copies are affected |
| Detect catalog drift / Load Repair Package | My Library → Advanced library tools → Find missing catalog profiles → select package → review in Add Filaments |
| Repair missing printer coverage | My Library → Advanced library tools → Review missing coverage; fills Add Filaments for review, including products present only in installed copies |
| Open manufacturer package | Add Filaments → Open package; printer-neutral libraries join the browser |
| Open legacy printer-specific package | Add Filaments → Open package routes to the inline Advanced import page |
| Edit package profile name/manufacturer | Add Filaments → Advanced import → editable package table; edits are frozen at review |
| Select all / deselect package filaments | Add Filaments → Select visible/Clear selection; Advanced import → Select all profiles/Clear selection |
| Skip duplicates | Normal import retains existing settings by default; Advanced import has an explicit Skip duplicates action |
| Select/clear printers | Add Filaments, including Advanced import; nozzle selection remains available |
| Device/AMS, Project Library, Both | Normal flow chooses System or Custom; Advanced import exposes System only, Custom, and System + project-dropdown registration |
| Install selected profiles | Reviewed install in either Add Filaments flow; existing-profile replacement is explicit in advanced review |
| Mirror to Program Files | Add Filaments → Advanced import → separate administrator install/mirror button after review |
| Back Up Library / Import Backup | Maintenance → Backups & restore; automatic backups also protect writes |
| Application update check/install | Header Check for updates and application-update notice; notice can be dismissed for the session |
| Download/update filament libraries | Add Filaments → Download libraries and the library-update notice; notice can be dismissed for the session |
| Report Bug / logs | Header Report a problem; Maintenance → Diagnostics and Activity & technical details |
| Theme and Studio process status | Header; writes remain paused while Studio is open |
| Detected paths | Maintenance → Activity & technical details |

Advanced library tools is an in-place page under My Library and contains library editing and repair only. Advanced import is a separate in-place page under Add Filaments. Maintenance contains backup/recovery and diagnostics, not a second library editor. The removed test/live switch stays removed.

Validation covers deletion scope and stale review rejection, system/user dependencies, profile renaming and brand isolation, package edits and legacy review, installed-catalog rollback, same-window navigation, visible deletion without choosing a profile, both themes, and update notifications. Tests write only to isolated fixtures; administrator UAC acceptance and physical-printer behavior are not automated.
