# Changelog

## 2.5.0 — team database on the server (2026-09-30)

- `SwiftBatch.db` now lives in the **shared folder** next to `Registry.xlsx` (business rule: no data stored on a PC). Only the manager's app opens it; users' PCs never do.
- Engine logs are written to `…\logs\` in the shared folder.
- Each PC keeps only `SwiftBatch.local.json`: the registry path (= shared folder) and the chosen identity.
- Upgrade: a local `SwiftBatch.db` from an earlier version is moved to the shared folder by the manager's app (history kept) and renamed `SwiftBatch.db.old` on every PC.
- The engine stops itself when the team file names another manager, so two PCs never process (or write the database) at the same time.
- Manager start-up without the share: Retry / Locate registry / Exit instead of running without data.
- SQLite forced to the rollback journal (no WAL) for network-share safety.

## 2.4.0 — clean rebuild (2026-09-30)

Rebuilt from the v2.3.1 project handoff with new code, the same feature set and business rules, plus:

- **Solution split**: all logic in `SwiftBatchProcessor.Core` (net8.0), WPF UI in `SwiftBatchProcessorApp`, xUnit suite (parser, calendar, customer match, fairness, Excel, SQLite, roles, end-to-end engine cycles).
- **Single-file publish fixed**: `IncludeNativeLibrariesForSelfExtract` (was misspelt), so `e_sqlite3.dll` and the WPF native DLLs are inside the exe.
- **Parser**: line-based; keeps each message's 20/30/50 context in multi-message files, survives page-break headers, ignores 50C/50L (instructing party), accepts raw FIN lines, tolerates NUL padding and non-UTF-8 bytes.
- **Engine**: records a sent file in SQLite before archiving (never sends twice); completes interrupted archive moves; de-duplicates queued Excel rows; syncs the registry Status column from payment statuses; daily log files.
- **Customer check**: master file unreadable → the file waits (instead of being sent unchecked); Greek accents and punctuation ignored when comparing names.
- **OOO detection**: also recognises Exchange OOF message classes and NDRs.
- **Roles**: team file `AppUsers.json` also shares the archive root; "Locate registry…" for user PCs; "Switch account" can claim manager.
- **Settings**: Greek bank holiday and TARGET2 suggestions per year; test e-mail button.
- **Reassign** can e-mail the archived file to the new assignee.
- **Version** in one place (`Directory.Build.props`); organisation defaults in a git-ignored `SwiftBatch.defaults.json`.
- Removed the orphaned v2.2 helpers (`GetExistingAssignment`, `GetSummary`).
- SQLite schema upgrades v2.3 databases in place (additive columns).

## History before this repository

- **2.3.1** — UI polish: zebra grids, frozen columns, status dots, nav icons, clear-filter buttons, file payments grid.
- **2.3** — business rules: idempotency/duplicates, lock-safe Excel queue, registry de-duplication, cut-off/deferral with holidays, MT103/MTF, customer match, new registry layout, OSN, dashboard pending grid, Clients view, open file, mark file completed.
- **2.2 / 2.2b** — payment-level status, portable-only build; manager claim / switch account, pending metric.
- **2.1** — roles, Excel as the UI's source of truth.
- **2.0** — WPF GUI.
- **1.x** — console exe (first C# port).
- **PAD 3.0–3.2** — Power Automate Desktop flow + PowerShell extractor (superseded).
