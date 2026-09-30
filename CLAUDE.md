# CLAUDE.md — SWIFT Batch Processor (Auto MT101)

WPF (.NET 8) desktop app that distributes SWIFT MT101 `.prt` files from a shared folder to payment processors by Outlook e-mail (fair/random, out-of-office aware), archives them by date and extracts every payment into Excel. See README.md for the user-facing description and business rules.

This is a clean rebuild (v2.4.0) of the v2.3.1 app from a project handoff. The owner prefers **decisive, finished deliverables with a short summary**.

## Commands

```bash
dotnet build SwiftBatchProcessor.sln            # everything (WPF builds on Linux thanks to EnableWindowsTargeting)
dotnet test tests/SwiftBatchProcessor.Tests     # ~110 tests, a few seconds
# Windows exe (what publish.bat does):
dotnet publish src/SwiftBatchProcessorApp/SwiftBatchProcessorApp.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false -o dist/portable
```

Note the property is `IncludeNativeLibrariesForSelfExtract` (the v2.3.1 script used a misspelt `…Extraction`, which silently left `e_sqlite3.dll` and the WPF native DLLs outside the exe).

**Linux containers:** Ubuntu's `dotnet-sdk-8.0` apt package lacks `Microsoft.NET.Sdk.WindowsDesktop`. Copy it from Microsoft's SDK package (packages.microsoft.com → `dotnet-sdk-8.0_<same version>-1_amd64.deb`, folder `usr/share/dotnet/sdk/<ver>/Sdks/Microsoft.NET.Sdk.WindowsDesktop`) into `/usr/lib/dotnet/sdk/<ver>/Sdks/`. The app cannot be *run* on Linux; XAML only gets compile-checked, so review XAML changes for runtime-only problems (StaticResource order, binding paths, ElementName inside non-visual elements).

## Layout

```
src/SwiftBatchProcessor.Core/   net8.0 — ALL logic, testable anywhere
  Core/SwiftParser.cs           .prt → ParsedFile (line-based state machine; see docs/MT101_FORMAT.md)
  Core/ProcessingEngine.cs      cycle = FlushPendingRows → ProcessDueDeferred → ScanWatchFolder → SyncRegistry
  Core/FairAssigner.cs          PickFairest: lowest load today, random tie-break
  Core/BusinessCalendar.cs      Route (cut-off / weekends / holidays), Greek + TARGET2 holiday suggestions
  Core/CustomerMaster.cs        master xlsx BY POSITION (A ASC, B CRS, C customer, F instructing, G BIC, H IBANs, J valeur)
  Core/ExcelReader|Writer.cs    Open XML; lock-free reads; header-mapped writes; retries → IOException
  Core/WorkbookLayout.cs        registry / daily headers (business contract), folder layout, FileStatus
  Core/OutlookMailer.cs         late-bound COM (dynamic), STA only; IMailer for tests
  Core/Session.cs               roles from Windows logon + AppUsers.json team file next to Registry.xlsx
  Data/AppDb.cs                 SQLite SwiftBatch.db next to the exe (settings, users/OOO, Files, Deferred, PendingRows)
  Data/WorkbookStore.cs         UI data from Excel (cached by mtime); WorkbookOps.cs = write-backs
src/SwiftBatchProcessorApp/     net8.0-windows WPF — App.xaml (all styles), MainWindow, Views/*, Controls/BarChart
tests/SwiftBatchProcessor.Tests xUnit; synthetic .prt fixtures; engine tests use temp dirs + FakeMailer + fake clock
```

## Hard rules (from the owner — binding)

- **Zero admin rights.** Never propose installers, services, registry writes, Program Files, elevated scheduled tasks or machine-wide SDKs. Per-user SDK (`setup-dotnet-sdk.bat`), self-contained single exe, runs from any writable folder.
- **Microsoft-only runtime dependencies**: `DocumentFormat.OpenXml`, `Microsoft.Data.Sqlite`, `Microsoft.CSharp`. No Excel interop, no chart libraries (pure-WPF `BarChart`), no third-party UI kits. (xUnit is test-only.)
- **English UI.** The user manual is Greek (`SWIFT_Batch_Processor_Odigos_Xrisis_*.docx`, kept outside the repo) — mention when a change needs a manual update. Outlook may be EN or GR: OOO detection handles both.
- **Palette**: yellow `#FFB81C`, charcoal `#1E1E24`. No copyrighted/brand assets (the icon is generated).
- **No lock/PIN on admin screens** (declined). Roles come from the Windows account only.
- **The engine runs only on the manager's PC.** User PCs must never process (would duplicate e-mails).
- **Excel is the source of truth for the UI** so user PCs work without the manager's SQLite.
- **A user holding a workbook open must never crash the engine or lose a row** (retries → PendingRows queue).
- One file = one user; exactly one final e-mail per file; assignment fair + random, never a hierarchy.
- Verify Microsoft behaviour (Exchange OOO, Outlook object model) against Microsoft Learn before claiming it.
- Validate parser changes against the real sample `00663459.prt` (put it in `tests/.../Fixtures/real/`, git-ignored — `Real_sample_00663459_when_available` then runs) plus the synthetic multi-payment fixtures.
- **This repository is public.** Never commit real share paths, e-mail addresses, customer data or real `.prt` files. Organisation values go in the git-ignored `SwiftBatch.defaults.json` (see the `.example`).
- **Version lives in one place**: `Directory.Build.props` `<Version>`. The status bar and About read it from the assembly. Add a CHANGELOG.md entry per release.

## Conventions

- File-scoped namespaces (`SwiftBatchApp.Core`, `SwiftBatchApp.Data`, `SwiftBatchApp.Views`), nullable on, `required`/`init` for context objects.
- Static service classes (`AppDb`, `WorkbookStore`, `ExcelWriter`, `ExcelReader`, `CustomerMaster`, `Session`, `BusinessCalendar`, `SwiftParser`). Tests swap their inputs through `internal` hooks (`Session.WindowsUserProvider`, `WorkbookStore.*Provider`, `ExcelWriter.RetryCount/RetryDelayMs`) — tests run serially for this reason.
- Compiled static `Regex` fields (`RegexOptions.Compiled`).
- Engine logs via `Emit("[TAG] message")` with tags `[CYCLE] [QUEUE] [DEFER] [PARSE] [MATCH] [ASSIGN] [MAIL] [ARCHIVE] [DUP] [SYNC] [ERROR]`; the UI colours lines by tag.
- Best-effort try/catch around non-critical steps; a critical failure before the e-mail leaves the file in the watch folder for the next cycle.
- Section dividers: `// ---------------------------------------------------------------- name`.
- Excel columns are always addressed by header text (`WorkbookLayout` constants), never by position — except the master customer file, which is positional by business rule.
- Views are code-behind `UserControl`s implementing `IRefreshable`; heavy reads go through `Task.Run`. Grids use the `GridBase` style + `CellText`/`CellNumber`/`CellMono` element styles; status dots via `StatusBrush`/`MatchBrush` converters.
- Timestamps in Excel: `yyyy-MM-dd HH:mm:ss`; registry `Date` is `yyyy-MM-dd`; displayed dates `dd/MM/yyyy`; amounts displayed Greek-style `1.234,56` (`DisplayFormats`).

## Key decisions (why things are the way they are)

| Decision | Instead of | Why |
|---|---|---|
| Token OOO probe `SWIFT File: <name> [yyMMddHHmmss-NNNN]`, unread replies only | matching any mail with the file name | stale auto-replies cascaded through all users (PAD v3.0 bug) |
| Persist OOO per day (`Users.OooDate`) | re-probing | Exchange auto-replies once per sender per OOO period |
| All users OOO → e-mail the manager (ESCALATED) | log without sending | the file must always be e-mailed |
| Send failure → leave the file, no rows | log and move | automatic retry, no ghost rows |
| Record in SQLite right after sending, then archive, then Excel | Excel first | never send twice; interrupted archive moves are completed next cycle |
| Idempotency: Files row OR open Deferred row → duplicate to `Duplicates` | re-send to the original user | duplicates must never generate e-mails |
| Cut-off defers to the next working day's folder + Deferred row | in-memory timer | survives restarts |
| PendingRows write-behind queue flushed each cycle, de-duplicated by key | failing the cycle | users keep workbooks open |
| Header-mapped appends + `EnsureHeaders` | positional columns | old workbooks keep working when columns are added |
| File status derived from payments (Completed iff all payments Completed) | manual file status | one source of truth |
| Team file `AppUsers.json` next to the registry, written by the manager | per-PC config | user PCs need only the registry path; carries the archive root too |
| Engine on a dedicated STA thread, `SemaphoreSlim` against overlapping cycles, single-instance mutex | timers | Outlook COM needs STA; no double processing |
| Core logic in a `net8.0` library | everything in the WPF project | tests run on any OS / CI |
| No trimming | `PublishTrimmed` | WPF does not support trimming |

## Known limitations / ideas

- The OOO probe waits `OooWaitSeconds` **per file, serially**: a burst of N files takes ≥ N minutes. Improvement: send all files of a cycle first, then probe all tokens in one window.
- Registry status sync covers the last 7 days each cycle (`ProcessingEngine.StatusSyncDays`); the UI derives status for the last 45 days (`WorkbookStore.DeriveWindowDays`) and for anything still open.
- Holidays are suggestions (Greek bank + TARGET2) that the manager must review and save.
- The synthetic fixtures approximate the print layout described in docs/MT101_FORMAT.md; confirm against real files when the parser changes.
