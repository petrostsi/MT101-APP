# SWIFT Batch Processor (Auto MT101)

A Windows desktop app (WPF, .NET 8) that automates the distribution of SWIFT **MT101** print files (`.prt`) to a team of payment processors, and extracts every payment order into Excel so the processors can work faster.

It replaces a manual routine: watch a shared folder → e-mail each file to one person → move it into a dated folder → type a row into an Excel registry.

- **No admin rights** needed, anywhere: build, install and run.
- **Microsoft-only dependencies**: Open XML SDK (Excel without Excel), Microsoft.Data.Sqlite, Outlook through late-bound COM.
- **One self-contained `.exe`**. Copy it to any folder you can write to.

---

## How it works

```
 \\share\Watch (*.prt) ──► Engine (manager PC only, one cycle every PollSeconds)
                            1 Flush queued Excel rows (workbooks that were locked last time)
                            2 Send deferred files that are due (after cut-off → next working day 07:00)
                            3 For each new file:
                                duplicate?            → ArchiveDay\Duplicates, no e-mail
                                after cut-off/holiday → next working day's folder, sent at 07:00
                                parse the MT101       → payments, OSN, 20/21/30/32B/50/57/59, MT103 vs MTF
                                customer check        → unknown customer → manager ("NO MATCH")
                                pick the fairest user → e-mail with the file attached + [token]
                                auto-reply (OOO)?     → mark OOO today, next user; nobody left → manager ("ESCALATED")
                                archive               → ArchiveRoot\YYYY\MM\DD\
                                Excel                 → Registry.xlsx (1 row/file) + PaymentOrders_YYMMDD.xlsx (1 row/payment)
                            4 Registry Status follows the payment statuses
 UI (every PC) reads the Excel workbooks (lock-free, refreshed every 60 s):
   Dashboard · Processing · Payments · Registry · Clients · Statistics · Users · Settings · About
```

### Roles

| Role | Who | Sees | Engine |
|---|---|---|---|
| **Manager** | the Windows account recorded in the team file (the first PC that finds no team file claims it) | everything | runs **only** here |
| **User** | mapped by Windows account in the team file, or picks themselves once on first start | own files and payments | never runs |

Roles come from the Windows logon only — there is no PIN (by design). **Switch account** (bottom-left) restarts the app to pick another identity or to claim manager rights on this PC.

The team file `AppUsers.json` lives next to `Registry.xlsx`. The manager PC writes it (users, manager, archive location); user PCs only read it, so they need nothing but the registry path.

### Where things are stored

Everything the team depends on is in the **shared folder** on the server (the folder of `Registry.xlsx`) — nothing business-related is kept on a PC:

```
\\server\share\                   ← the registry's folder = the team's shared folder
   Registry.xlsx                  one row per file
   AppUsers.json                  team list, manager, archive location
   SwiftBatch.db                  the engine's database: settings, users/OOO, files already sent,
                                  deferred queue, queued Excel rows
   logs\engine_yyyyMMdd.log       engine log
   (archive) YYYY\MM\DD\          archived .prt files + PaymentOrders_YYMMDD.xlsx

Each PC (folder of the exe)
   SwiftBatch.local.json          only where the shared folder is + who uses this PC
```

Only the **manager's app** opens `SwiftBatch.db` — a single writer, which is what keeps SQLite safe on a network share. Users' PCs read the Excel workbooks and the team file, never the database. If another PC claims manager, the old manager's engine notices before its next cycle and stops.

Upgrading from a version that kept `SwiftBatch.db` next to the exe: on the manager PC the file is moved to the shared folder automatically (history included); on every PC the old file is renamed `SwiftBatch.db.old`.

### Business rules

- **One file = one user**, even when it contains many payment orders. Exactly one final e-mail per file.
- **Fair and random**: the next file goes to the active user with the fewest payment orders today (min. 1 per file); ties are broken at random. ESCALATED / NO MATCH files do not count.
- **Out of office**: the e-mail subject carries a unique token `SWIFT File: <name> [yyMMddHHmmss-NNNN]`. An unread auto-reply (EN or GR, OOO or undeliverable) with that token within `OooWaitSeconds` means the user is away: they are marked OOO for the rest of the day and the next user gets the file. Exchange replies only once per sender per OOO period, so the flag is remembered; it can also be toggled by hand in **Users**.
- **Idempotent**: a file name that was already sent (or is waiting as deferred) is never e-mailed again — it is moved to `…\YYYY\MM\DD\Duplicates`.
- **Cut-off** (default 13:00): files arriving later, or on weekends/holidays, move to the next working day's folder and are sent at `DeferredSendTime` (default 07:00). Holidays are listed in Settings (Greek bank holidays and TARGET2 days can be added with one click).
- **Customer validation** (optional): the ordering customer must match one row of the master customer workbook on IBAN (field 50) **and** sender BIC (first 8) **and** name. Otherwise the file goes to the manager as **NO MATCH**; the manager reassigns it after checking.
- **MT103 vs MTF**: a payment is **MTF** when the creditor bank BIC (57A, else 59A) is exactly `PIRBGRAA` or `PIRBGRAAXXX`; everything else is MT103.
- **Payment-level status**: processors mark payments Completed; a file is Completed when all its payments are.
- **Failure policy**: if the e-mail cannot be sent, the file stays in the watch folder and is retried next cycle — nothing is logged. Once sent, the file is recorded first, so it can never be sent twice. If a workbook is open in Excel, its rows wait in a queue and are written at the next cycle.

### Excel outputs

**`Registry.xlsx`** — one row per file. The first 10 columns are the business layout, in this order:

`File Name | OSN from | OSN to | Orders | Execution Date | After cut-off / Real exec. date | Status | MT103 | MTF | Assigned User` then `Date | Logged At | Transaction Refs | Customer Match | Valeur`.

**`ArchiveRoot\YYYY\MM\DD\PaymentOrders_YYMMDD.xlsx`** — one row per payment, created with the day's first file. Column A is the assigned user (rewritten on OOO / reassignment):

`Assigned User | File Name | Sender Ref (20) | Receiver | Txn Ref (21) | Currency | Amount | Logged At | Beneficiary | Beneficiary Acct | Exec Date (30) | Ordering Customer | Status | Type | Ordering Acct | Creditor BIC`

Columns are always addressed by header text. Older workbooks are upgraded in place: missing columns are added at the end.

---

## Install / deploy (no admin rights)

1. Copy `SwiftBatchProcessorApp.exe` (+ `SwiftBatch.defaults.json`) to a folder you can write to (e.g. `C:\Users\<you>\SwiftBatch\`) — **not** Program Files. Only the small `SwiftBatch.local.json` is created there.
2. **Manager PC first.** Start the exe. The registry path from `SwiftBatch.defaults.json` locates the shared folder; the first start that reaches it without a registry or `AppUsers.json` makes this Windows account the manager and creates `SwiftBatch.db` there, seeded from the defaults file. Without a defaults file, use **Locate registry…**, then fill in **Settings** and **Users**.
3. In **Users**, enter each processor's Windows user name so their PCs recognise them automatically (otherwise they pick themselves once).
4. Copy the same exe to the users' PCs. If their registry path differs from the default, they use **Locate registry…** once.
5. Outlook desktop is only needed on the manager PC. Start the engine on the Dashboard (or tick *Start automatically*).

`SwiftBatch.defaults.example.json` shows the format of the optional defaults file.

### Settings

| Setting | Default | Meaning |
|---|---|---|
| Watch folder | – | where new `.prt` files arrive |
| Archive root | – | `…\YYYY\MM\DD\` folders and day workbooks |
| Registry path | – | `Registry.xlsx`; its folder is the shared folder (team file, database, logs). Stored per PC in `SwiftBatch.local.json`; changing it restarts the app |
| Master customer file | empty | `.xlsx` for customer validation; empty = off |
| File filter | `*.prt` | `;`-separated patterns |
| Send from account | default account | SMTP address of the Outlook account to send from |
| Manager e-mail / name | – | escalations and NO MATCH files |
| Out-of-office wait | 60 s | how long to wait for an auto-reply after each send (0 = no check) |
| Check every | 300 s | engine cycle interval |
| Cut-off / deferred send time | 13:00 / 07:00 | see business rules |
| Holidays | – | `yyyy-MM-dd` list |

---

## Build from source

**Windows, no admin:** run `setup-dotnet-sdk.bat` once (per-user .NET 8 SDK in `%LOCALAPPDATA%\Microsoft\dotnet`), then `publish.bat` → runs the tests and writes `dist\portable\SwiftBatchProcessorApp.exe`.

**Any OS with the .NET 8 SDK:**

```bash
dotnet build SwiftBatchProcessor.sln
dotnet test tests/SwiftBatchProcessor.Tests
```

The WPF project builds on Linux/macOS too (`EnableWindowsTargeting`), but only runs on Windows. All logic lives in `SwiftBatchProcessor.Core` (plain `net8.0`), so the tests run everywhere.

### Project layout

```
src/SwiftBatchProcessor.Core/     all non-UI logic (net8.0)
  Core/  SwiftParser, BusinessCalendar, CustomerMaster, FairAssigner, ProcessingEngine,
         ExcelReader, ExcelWriter, WorkbookLayout, OutlookMailer (+ IMailer), Session, RowCodec
  Data/  AppDb (SQLite), WorkbookStore (UI data), WorkbookOps (Excel write-backs), models
src/SwiftBatchProcessorApp/       WPF UI (net8.0-windows): App, MainWindow, Views/, Controls/BarChart
tests/SwiftBatchProcessor.Tests/  xUnit: parser, calendar, customer match, fairness, Excel, SQLite,
                                  session/roles, end-to-end engine cycles with a fake mailer
docs/MT101_FORMAT.md              notes on the .prt print format the parser relies on
```

---

## Troubleshooting

- **"Watch folder not reachable"** in the Processing log — the share is offline; the engine retries every cycle.
- **Rows "queued"** — someone has the workbook open in Excel. Nothing is lost; the rows are written at the next cycle after it is closed.
- **Everybody receives the OOO treatment / nobody does** — check `Out-of-office wait` and that replies arrive in the sending account's inbox. A user who switched OOO on *before* an earlier probe today may not reply again (Exchange replies once per sender): toggle OOO manually in Users.
- **Outlook asks for permission to send** — your Outlook security settings block programmatic access; ask IT for the usual "antivirus up to date" policy. Use *Send test e-mail* in Settings to check.
- **"The shared database could not be opened"** at start-up (manager PC) — the share is unreachable. The app offers Retry / Locate registry / Exit; it cannot run the engine without the shared folder.
- Logs: `logs\engine_yyyyMMdd.log` in the shared folder.
