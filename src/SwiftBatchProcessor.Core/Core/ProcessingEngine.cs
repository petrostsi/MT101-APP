using System.Globalization;
using System.Text;
using SwiftBatchApp.Data;
using static SwiftBatchApp.Core.WorkbookLayout;

namespace SwiftBatchApp.Core;

public enum EngineState { Stopped, Idle, Working }

/// <summary>Counters of one engine cycle.</summary>
public sealed class CycleSummary
{
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; set; }
    public int Processed { get; set; }
    public int Deferred { get; set; }
    public int Duplicates { get; set; }
    public int Failed { get; set; }
    public int Flushed { get; set; }
    public int StillQueued { get; set; }

    public override string ToString() =>
        $"{Processed} processed, {Deferred} deferred, {Duplicates} duplicate(s), {Failed} failed/retry" +
        (Flushed + StillQueued > 0 ? $", queue: {Flushed} written / {StillQueued} waiting" : "");
}

/// <summary>
/// The distribution engine. Runs ONLY on the manager's PC, on a dedicated STA thread (Outlook COM).
///
/// Each cycle (settings reloaded every time, cycles never overlap):
///   1. FlushPendingRows    — retry Excel rows parked while a workbook was locked
///   2. ProcessDueDeferred  — after-cut-off files whose send time has come
///   3. ScanWatchFolder     — normal intake: duplicate check → cut-off routing → parse → customer check
///                            → fair assignment + OOO probe → record → archive → Excel rows
///   4. SyncRegistry        — registry Status column follows payment statuses (best effort)
///
/// Failure policy: if e-mailing fails the file stays in the watch folder and nothing is recorded, so the next
/// cycle retries it. Once a file is e-mailed it is recorded in SQLite FIRST, so it can never be e-mailed twice.
/// </summary>
public sealed class ProcessingEngine : IDisposable
{
    private readonly Func<EngineSettings, IMailer> _mailerFactory;
    private readonly Func<DateTime> _clock;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private Thread? _loopThread;
    private volatile bool _stopRequested;

    public ProcessingEngine(Func<EngineSettings, IMailer> mailerFactory, Func<DateTime>? clock = null)
    {
        _mailerFactory = mailerFactory;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>Raised on the engine thread with a timestamped line ("HH:mm:ss [TAG] message").</summary>
    public event Action<string>? Log;
    public event Action? StateChanged;
    public event Action<CycleSummary>? CycleCompleted;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public bool IsLoopRunning => _loopThread is { IsAlive: true };
    public DateTime? LastCycleAt { get; private set; }
    public DateTime? NextCycleAt { get; private set; }
    public CycleSummary? LastSummary { get; private set; }

    /// <summary>Folder for daily log files (engine_yyyyMMdd.log); null disables file logging.</summary>
    public string? LogDirectory { get; set; }

    /// <summary>Days (including today) whose registry statuses are re-synced every cycle.</summary>
    public int StatusSyncDays { get; set; } = 7;

    /// <summary>
    /// Checked before every cycle; false stops the engine (e.g. another PC has claimed manager — two engines
    /// would e-mail files twice and write to the same shared database).
    /// </summary>
    public Func<bool>? MayRun { get; set; }

    // ---------------------------------------------------------------- lifecycle

    public void Start()
    {
        lock (_gate)
        {
            if (IsLoopRunning) return;
            _stopRequested = false;
            _loopThread = CreateStaThread(Loop, "SwiftBatch engine loop");
            _loopThread.Start();
        }
    }

    /// <summary>Stops after the current file/cycle. Returns false if the cycle is still busy after <paramref name="wait"/>.</summary>
    public bool Stop(TimeSpan? wait = null)
    {
        Thread? t;
        lock (_gate)
        {
            t = _loopThread;
            _stopRequested = true;
            _wake.Set();
        }
        return t is null || t.Join(wait ?? TimeSpan.FromSeconds(5));
    }

    /// <summary>"Check now": wakes the loop, or runs a single cycle on its own STA thread when the loop is off.</summary>
    public void TriggerNow()
    {
        if (IsLoopRunning)
        {
            _wake.Set();
            return;
        }
        CreateStaThread(() => RunCycle(), "SwiftBatch engine one-shot").Start();
    }

    public void Dispose()
    {
        // A cycle that is still busy (e.g. waiting for an OOO reply) keeps its handles; the process is exiting anyway.
        if (Stop(TimeSpan.FromSeconds(2)))
        {
            _wake.Dispose();
            _cycleLock.Dispose();
        }
    }

    private static Thread CreateStaThread(Action body, string name)
    {
        var t = new Thread(() => body()) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
        return t;
    }

    private void Loop()
    {
        Emit("[CYCLE] Engine started");
        SetState(EngineState.Idle);
        while (!_stopRequested)
        {
            RunCycle();
            if (_stopRequested) break;
            int poll = 300;
            try { poll = AppDb.LoadSettings().PollSeconds; } catch { /* keep default */ }
            poll = Math.Max(10, poll);
            NextCycleAt = _clock().AddSeconds(poll);
            SetState(EngineState.Idle);
            _wake.WaitOne(TimeSpan.FromSeconds(poll));
        }
        NextCycleAt = null;
        SetState(EngineState.Stopped);
        Emit("[CYCLE] Engine stopped");
    }

    // ---------------------------------------------------------------- cycle

    /// <summary>Runs one full cycle synchronously. Returns null if another cycle is already running.</summary>
    public CycleSummary? RunCycle()
    {
        if (!_cycleLock.Wait(0))
        {
            Emit("[CYCLE] A cycle is already running — request ignored");
            return null;
        }
        var summary = new CycleSummary { StartedAt = _clock() };
        try
        {
            if (MayRun is not null && !MayRun())
            {
                _stopRequested = true;
                _wake.Set();
                Emit("[ERROR] This PC is no longer the manager (the team file names another Windows account). " +
                     "The engine has stopped so that no file is e-mailed twice.");
                return null;
            }
            SetState(EngineState.Working);
            EngineSettings cfg = AppDb.LoadSettings();
            Emit("[CYCLE] Cycle started");

            FlushPendingRows(cfg, summary);

            string? problem = ValidateSettings(cfg);
            if (problem is not null)
            {
                Emit("[ERROR] " + problem);
            }
            else
            {
                using var cx = new CycleContext(cfg, _clock(), AppDb.GetPaymentCounts(DateKey(_clock())), () => _mailerFactory(cfg));
                ProcessDueDeferred(cx, summary);
                ScanWatchFolder(cx, summary);
                SyncRegistry(cfg);
            }

            summary.FinishedAt = _clock();
            LastCycleAt = summary.FinishedAt;
            LastSummary = summary;
            Emit($"[CYCLE] Cycle finished: {summary}");
            CycleCompleted?.Invoke(summary);
            return summary;
        }
        catch (Exception ex)
        {
            Emit($"[ERROR] Cycle failed: {ex.Message}");
            return summary;
        }
        finally
        {
            _cycleLock.Release();
            SetState(IsLoopRunning && !_stopRequested ? EngineState.Idle : EngineState.Stopped);
        }
    }

    private static string? ValidateSettings(EngineSettings cfg)
    {
        if (cfg.WatchFolder.Length == 0) return "Watch folder is not set (Settings).";
        if (cfg.ArchiveRoot.Length == 0) return "Archive root is not set (Settings).";
        if (cfg.RegistryPath.Length == 0) return "Registry path is not set (Settings).";
        if (!Directory.Exists(cfg.WatchFolder)) return $"Watch folder not reachable: {cfg.WatchFolder}";
        return null;
    }

    // ---------------------------------------------------------------- 1. write-behind queue

    private void FlushPendingRows(EngineSettings cfg, CycleSummary summary)
    {
        List<PendingRow> pending;
        try { pending = AppDb.GetPendingRows(); }
        catch (Exception ex) { Emit($"[QUEUE] Cannot read the queue: {ex.Message}"); return; }
        if (pending.Count == 0) return;

        Emit($"[QUEUE] Flushing {pending.Count} queued Excel row(s)");
        foreach (var group in pending.GroupBy(p => (p.Path, p.Kind)))
        {
            var ids = group.Select(p => p.Id).ToList();
            string file = Path.GetFileName(group.Key.Path);
            try
            {
                var rows = group.Select(p => (IReadOnlyDictionary<string, object?>)RowCodec.Deserialize(p.RowJson)).ToList();
                int written = WriteRows(group.Key.Path, group.Key.Kind, rows);
                AppDb.DeletePendingRows(ids);
                summary.Flushed += ids.Count;
                Emit($"[QUEUE] {file}: {written} row(s) written" + (written < ids.Count ? $", {ids.Count - written} already present" : ""));
            }
            catch (Exception ex)
            {
                AppDb.MarkPendingRowsFailed(ids, ex.Message);
                summary.StillQueued += ids.Count;
                Emit($"[QUEUE] {file} still unavailable ({ex.Message}) — {ids.Count} row(s) stay queued");
            }
        }
    }

    // ---------------------------------------------------------------- 2. deferred files

    private void ProcessDueDeferred(CycleContext cx, CycleSummary summary)
    {
        foreach (DeferredItem d in AppDb.GetDueDeferred(cx.Now))
        {
            if (_stopRequested) return;
            try
            {
                if (AppDb.GetFileRecord(d.FileName) is not null)
                {
                    AppDb.MarkDeferredDone(d.Id);       // already sent (crash between send and bookkeeping)
                    continue;
                }
                if (!File.Exists(d.FilePath))
                {
                    Emit($"[DEFER] {d.FileName}: no longer at {d.FilePath} — removed from the queue");
                    AppDb.MarkDeferredDone(d.Id);
                    continue;
                }
                Emit($"[DEFER] {d.FileName} is due (scheduled {d.ScheduledFor})");
                DateTime day = d.TargetDay ?? cx.Now.Date;
                if (ProcessFile(cx, d.FilePath, day, moveToArchive: false, deferred: d))
                {
                    AppDb.MarkDeferredDone(d.Id);
                    summary.Processed++;
                }
                else summary.Failed++;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                Emit($"[ERROR] {d.FileName}: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- 3. intake

    private void ScanWatchFolder(CycleContext cx, CycleSummary summary)
    {
        EngineSettings cfg = cx.Settings;
        var options = new EnumerationOptions
        {
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
        };
        List<string> files = cfg.FilePatterns
            .SelectMany(p => Directory.EnumerateFiles(cfg.WatchFolder, p, options))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return;

        Emit($"[CYCLE] {files.Count} file(s) in the watch folder");
        foreach (string path in files)
        {
            if (_stopRequested) return;
            string name = Path.GetFileName(path);
            try
            {
                if (!IsReady(path))
                {
                    Emit($"[CYCLE] {name} is still being written — next cycle");
                    continue;
                }
                if (AppDb.IsFileProcessedOrQueued(name))
                {
                    HandleDuplicate(cx, path);
                    summary.Duplicates++;
                    continue;
                }
                DateTime now = _clock();
                var (defer, target) = BusinessCalendar.Route(now, cfg.Cutoff, cfg.Holidays);
                if (defer)
                {
                    DeferFile(cx, path, now, target);
                    summary.Deferred++;
                    continue;
                }
                if (ProcessFile(cx, path, now.Date, moveToArchive: true, deferred: null)) summary.Processed++;
                else summary.Failed++;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                Emit($"[ERROR] {name}: {ex.Message}");
            }
        }
    }

    private static bool IsReady(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void HandleDuplicate(CycleContext cx, string path)
    {
        string name = Path.GetFileName(path);
        FileRecord? rec = AppDb.GetFileRecord(name);
        if (rec is not null && rec.ArchivePath.Length > 0 && !File.Exists(rec.ArchivePath) && !SamePath(rec.ArchivePath, path))
        {
            MoveFile(path, rec.ArchivePath);
            Emit($"[ARCHIVE] {name} was already sent — its archive move is now complete");
            return;
        }
        string dest = UniquePath(DuplicatesFolder(cx.Settings.ArchiveRoot, cx.Now.Date), name);
        MoveFile(path, dest);
        string previous = rec is null ? "is waiting in the deferred queue" : $"was sent on {rec.ProcessedDate} to {rec.AssignedUser}";
        Emit($"[DUP] {name} {previous} — duplicate moved to {Path.GetDirectoryName(dest)}, no e-mail sent");
    }

    private void DeferFile(CycleContext cx, string path, DateTime now, DateTime target)
    {
        EngineSettings cfg = cx.Settings;
        string name = Path.GetFileName(path);
        string dest = UniquePath(DayFolder(cfg.ArchiveRoot, target), name);
        MoveFile(path, dest);
        DateTime sendAt = target.Date + cfg.DeferredSendTime;
        try
        {
            AppDb.AddDeferred(new DeferredItem
            {
                FileName = name,
                FilePath = dest,
                ReceivedAt = Timestamp(now),
                ScheduledFor = sendAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                TargetYmd = target.ToString("yyMMdd", CultureInfo.InvariantCulture),
            });
        }
        catch
        {
            MoveFile(dest, path);                 // undo: the file must not sit unqueued in a future folder
            throw;
        }
        string reason = BusinessCalendar.IsWorkingDay(now.Date, cfg.Holidays)
            ? $"after the {BusinessCalendar.FormatTime(cfg.Cutoff)} cut-off"
            : "on a non-working day";
        Emit($"[DEFER] {name} arrived {now:dd/MM HH:mm} {reason} → {target:ddd dd/MM/yyyy}, will be sent at {sendAt:HH:mm}");
    }

    /// <summary>Parse → validate → distribute → record → archive → Excel. False = retry next cycle.</summary>
    private bool ProcessFile(CycleContext cx, string path, DateTime day, bool moveToArchive, DeferredItem? deferred)
    {
        EngineSettings cfg = cx.Settings;
        string name = Path.GetFileName(path);

        ParsedFile parsed = SwiftParser.ParseFile(path);
        Emit(parsed.HasError
            ? $"[PARSE] {name}: {parsed.Error}"
            : $"[PARSE] {name}: {parsed.PaymentCount} order(s), OSN {parsed.OsnFrom}–{parsed.OsnTo}, " +
              $"MT103 {parsed.Mt103Count} / MTF {parsed.MtfCount}, {parsed.TotalsText}");

        MatchResult match;
        try
        {
            match = CustomerMaster.Match(parsed, cfg.MasterFilePath);
        }
        catch (Exception ex)
        {
            Emit($"[MATCH] Master customer file unavailable ({ex.Message}) — {name} waits for the next cycle");
            return false;
        }
        if (match.Outcome == MatchOutcome.Match) Emit($"[MATCH] {name}: {match.Customer!.Customer}");
        else if (match.IsNoMatch) Emit($"[MATCH] {name}: NO MATCH — {match.Reason}");

        var (user, status) = Distribute(cx, path, parsed, match, deferred);
        if (user.Length == 0) return false;

        DateTime now = _clock();
        string archivePath = moveToArchive ? UniquePath(DayFolder(cfg.ArchiveRoot, day), name) : path;
        var record = new FileRecord
        {
            FileName = name,
            AssignedUser = user,
            ProcessedDate = DateKey(day),
            PaymentCount = parsed.PaymentCount,
            TxnRefs = parsed.TxnRefs,
            Status = status == FileStatus.Pending ? FileStatus.DbOk : status,
            LoggedAt = Timestamp(now),
            ArchivePath = archivePath,
            CustomerMatch = match.RegistryText,
        };
        AppDb.RecordFile(record, parsed.Payments);          // idempotency point — before anything can fail

        if (moveToArchive)
        {
            try
            {
                MoveFile(path, archivePath);
                Emit($"[ARCHIVE] {name} → {Path.GetDirectoryName(archivePath)}");
            }
            catch (Exception ex)
            {
                Emit($"[ARCHIVE] Could not move {name} ({ex.Message}) — the move is completed next cycle");
            }
        }

        WriteExcel(cfg, parsed, match, user, status, day, now, deferred);
        return true;
    }

    // ---------------------------------------------------------------- distribution

    private (string User, string Status) Distribute(CycleContext cx, string path, ParsedFile parsed, MatchResult match, DeferredItem? deferred)
    {
        EngineSettings cfg = cx.Settings;
        string name = Path.GetFileName(path);
        string title = Path.GetFileNameWithoutExtension(path);
        string ymd = cx.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        string body = BuildBody(parsed, match, deferred);

        if (match.IsNoMatch)
        {
            string text = $"The ordering customer could not be validated against the master file: {match.Reason}.\r\n" +
                          "Please validate the file and reassign it in SWIFT Batch Processor.\r\n\r\n" + body;
            if (!Send(cx, cfg.ManagerEmail, $"NO CUSTOMER MATCH - SWIFT File: {title}", text, path)) return ("", "");
            Emit($"[ASSIGN] {name} → manager (no customer match)");
            return (cfg.ManagerEmail, FileStatus.NoMatch);
        }

        List<string> candidates = AppDb.GetAssignableUsers(ymd);
        if (candidates.Count == 0) Emit("[ASSIGN] No active processor is available today");
        while (candidates.Count > 0)
        {
            string user = FairAssigner.PickFairest(candidates, cx.Counts);
            string token = $"{_clock():yyMMddHHmmss}-{Random.Shared.Next(1000, 10000)}";
            Emit($"[ASSIGN] {name} → {user} (today's load: {cx.Counts.GetValueOrDefault(user)})");
            if (!Send(cx, user, $"SWIFT File: {title} [{token}]", body, path)) return ("", "");

            if (cfg.OooWaitSeconds > 0)
            {
                Emit($"[MAIL] Waiting up to {cfg.OooWaitSeconds}s for an automatic reply from {user}");
                if (cx.Mailer.IsOutOfOffice(token, cfg.OooWaitSeconds))
                {
                    Emit($"[ASSIGN] {user} is out of office — marked OOO for today, trying the next processor");
                    AppDb.SetOoo(user, ymd);
                    candidates.Remove(user);
                    continue;
                }
            }
            cx.Counts[user] = cx.Counts.GetValueOrDefault(user) + FairAssigner.LoadOf(parsed.PaymentCount);
            return (user, parsed.HasError ? FileStatus.ParseError : FileStatus.Pending);
        }

        string escalation = "No processor is available (all out of office or inactive). Please handle or reassign this file.\r\n\r\n" + body;
        if (!Send(cx, cfg.ManagerEmail, $"ESCALATION - no user available - SWIFT File: {title}", escalation, path)) return ("", "");
        Emit($"[ASSIGN] {name} escalated to the manager");
        return (cfg.ManagerEmail, FileStatus.Escalated);
    }

    private bool Send(CycleContext cx, string to, string subject, string body, string attachment)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            Emit($"[MAIL] Not sent: no recipient for \"{subject}\" (is the manager e-mail set?)");
            return false;
        }
        try
        {
            if (cx.Mailer.SendFile(to, subject, body, attachment))
            {
                Emit($"[MAIL] Sent \"{subject}\" to {to}");
                return true;
            }
            Emit($"[MAIL] Sending to {to} failed: {cx.Mailer.LastError} — the file stays in the watch folder");
        }
        catch (Exception ex)
        {
            Emit($"[MAIL] Mail system unavailable: {ex.Message} — the file stays in the watch folder");
        }
        return false;
    }

    internal static string BuildBody(ParsedFile parsed, MatchResult match, DeferredItem? deferred)
    {
        var sb = new StringBuilder();
        sb.Append("SWIFT MT101 file: ").Append(parsed.FileName).Append("\r\n");
        sb.Append($"Orders: {parsed.PaymentCount} (MT103: {parsed.Mt103Count}, MTF: {parsed.MtfCount})\r\n");
        if (parsed.OsnFrom.HasValue) sb.Append($"OSN: {parsed.OsnFrom} – {parsed.OsnTo}\r\n");
        if (parsed.ExecutionDate.HasValue) sb.Append($"Requested execution date (30): {DisplayDate(parsed.ExecutionDate)}\r\n");
        if (deferred is not null)
            sb.Append($"Received after the cut-off ({deferred.ReceivedAt}); processing day {DisplayDate(deferred.TargetDay)}\r\n");
        if (parsed.OrderingName.Length + parsed.OrderingAccount.Length > 0)
            sb.Append($"Ordering customer (50): {parsed.OrderingName} {parsed.OrderingAccount}".TrimEnd()).Append("\r\n");
        if (parsed.SenderBic.Length > 0) sb.Append($"Sender: {parsed.SenderBic}\r\n");
        sb.Append($"Customer check: {match.RegistryText}");
        if (match.Outcome == MatchOutcome.Match && match.Customer!.Valeur.Length > 0) sb.Append($" (valeur {match.Customer.Valeur})");
        sb.Append("\r\n");
        if (parsed.TotalsByCurrency.Count > 0)
            sb.Append("Totals: ").Append(string.Join("; ", parsed.TotalsByCurrency.OrderBy(k => k.Key)
                .Select(k => $"{k.Key} {DisplayFormats.Amount(k.Value)}"))).Append("\r\n");
        if (parsed.HasError) sb.Append($"WARNING: {parsed.Error}\r\n");

        if (parsed.Payments.Count > 0)
        {
            sb.Append("\r\n  #  Txn Ref (21)          Ccy            Amount  Type   Beneficiary\r\n");
            foreach (PaymentOrder p in parsed.Payments)
                sb.Append($"{p.Index,3}  {p.TxnRef,-20} {p.Currency,-3} {DisplayFormats.Amount(p.Amount),17}  {p.Type,-5}  {p.BeneficiaryName}\r\n");
        }
        sb.Append("\r\nThis message was sent automatically by SWIFT Batch Processor.\r\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Excel

    private void WriteExcel(EngineSettings cfg, ParsedFile parsed, MatchResult match, string user, string status,
        DateTime day, DateTime now, DeferredItem? deferred)
    {
        string loggedAt = Timestamp(now);
        var registryRow = new Dictionary<string, object?>
        {
            [RFileName] = parsed.FileName,
            [ROsnFrom] = parsed.OsnFrom,
            [ROsnTo] = parsed.OsnTo,
            [ROrders] = parsed.PaymentCount,
            [RExecDate] = DisplayDate(parsed.ExecutionDate),
            [RAfterCutoff] = deferred is null ? "" : DisplayDate(day),
            [RStatus] = status,
            [RMt103] = parsed.Mt103Count,
            [RMtf] = parsed.MtfCount,
            [RAssignedUser] = user,
            [RDate] = DateKey(day),
            [RLoggedAt] = loggedAt,
            [RTxnRefs] = parsed.TxnRefs,
            [RCustomerMatch] = match.RegistryText,
            [RValeur] = match.Outcome == MatchOutcome.Match ? match.Customer!.Valeur : "",
        };
        WriteRowsSafe(cfg.RegistryPath, PendingRowKind.Registry, new[] { registryRow });

        var dailyRows = parsed.Payments.Select(p => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            [DAssignedUser] = user,
            [DFileName] = parsed.FileName,
            [DSenderRef] = p.SenderRef,
            [DReceiver] = p.Receiver,
            [DTxnRef] = p.TxnRef,
            [DCurrency] = p.Currency,
            [DAmount] = p.Amount,
            [DLoggedAt] = loggedAt,
            [DBeneficiary] = p.BeneficiaryName,
            [DBeneficiaryAcct] = p.BeneficiaryAccount,
            [DExecDate] = DisplayDate(p.ExecutionDate),
            [DOrderingCustomer] = p.OrderingName,
            [DStatus] = PaymentStatus.Pending,
            [DType] = p.Type,
            [DOrderingAcct] = p.OrderingAccount,
            [DCreditorBic] = p.CreditorBic,
        }).ToList();
        if (dailyRows.Count > 0) WriteRowsSafe(DailyWorkbookPath(cfg.ArchiveRoot, day), PendingRowKind.Daily, dailyRows);
    }

    /// <summary>Writes rows, or parks them in the PendingRows queue when the workbook is locked/unavailable.</summary>
    private void WriteRowsSafe(string path, string kind, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        try
        {
            WriteRows(path, kind, rows);
        }
        catch (Exception ex)
        {
            foreach (var row in rows) AppDb.EnqueuePendingRow(path, RowCodec.Serialize(row), kind, ex.Message);
            Emit($"[QUEUE] {Path.GetFileName(path)} unavailable ({ex.Message}) — {rows.Count} row(s) queued for the next cycle");
        }
    }

    /// <summary>Creates the workbook if needed, skips rows already present, appends the rest. Returns rows written.</summary>
    private static int WriteRows(string path, string kind, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        bool registry = kind == PendingRowKind.Registry;
        bool created = ExcelWriter.EnsureWorkbook(path, registry ? RegistrySheet : DailySheet, registry ? RegistryHeaders : DailyHeaders);

        List<IReadOnlyDictionary<string, object?>> fresh = rows.ToList();
        if (!created)
        {
            HashSet<string> existing = ExistingKeys(path, registry);
            fresh = rows.Where(r => RowKey(r, registry) is not { Length: > 0 } k || !existing.Contains(k)).ToList();
        }
        ExcelWriter.AppendRows(path, fresh);
        return fresh.Count;
    }

    /// <summary>De-duplication keys: registry = File Name; day workbook = File Name|Txn Ref.</summary>
    private static HashSet<string> ExistingKeys(string path, bool registry)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ExcelReader.ReadTable(path).Rows)
        {
            string key = registry
                ? row.GetValueOrDefault(RFileName, "")
                : WorkbookOps.Key(row.GetValueOrDefault(DFileName, ""), row.GetValueOrDefault(DTxnRef, ""));
            if (key.Trim('|').Length > 0) keys.Add(key);
        }
        return keys;
    }

    private static string RowKey(IReadOnlyDictionary<string, object?> row, bool registry)
    {
        string Get(string h) => row.TryGetValue(h, out object? v) ? RowCodec.AsText(v) : "";
        if (registry) return Get(RFileName);
        string txn = Get(DTxnRef);
        return txn.Length == 0 ? "" : WorkbookOps.Key(Get(DFileName), txn);
    }

    private void SyncRegistry(EngineSettings cfg)
    {
        try
        {
            DateTime today = _clock().Date;
            var days = Enumerable.Range(0, Math.Max(1, StatusSyncDays)).Select(i => today.AddDays(-i));
            int changed = WorkbookOps.SyncRegistryStatuses(cfg.RegistryPath, cfg.ArchiveRoot, days);
            if (changed > 0) Emit($"[SYNC] Registry status updated for {changed} file(s)");
        }
        catch (Exception ex)
        {
            Emit($"[SYNC] Registry status sync skipped: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- file helpers

    private static void MoveFile(string from, string to)
    {
        string? dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Move(from, to);
    }

    /// <summary>dir\name, or dir\name_HHmmss(_n).ext when that name is taken.</summary>
    internal static string UniquePath(string dir, string name)
    {
        string candidate = Path.Combine(dir, name);
        if (!File.Exists(candidate)) return candidate;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        for (int n = 0; ; n++)
        {
            candidate = Path.Combine(dir, n == 0 ? $"{stem}_{stamp}{ext}" : $"{stem}_{stamp}_{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- logging / state

    private void Emit(string message)
    {
        DateTime now = DateTime.Now;
        string line = $"{now:HH:mm:ss} {message}";
        try { Log?.Invoke(line); } catch { /* UI handlers must not break the engine */ }
        if (LogDirectory is null) return;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, $"engine_{now:yyyyMMdd}.log"), $"{now:yyyy-MM-dd} {line}{Environment.NewLine}");
        }
        catch
        {
            // best effort
        }
    }

    private void SetState(EngineState state)
    {
        if (State == state) return;
        State = state;
        try { StateChanged?.Invoke(); } catch { /* ignore */ }
    }

    private sealed class CycleContext : IDisposable
    {
        private readonly Func<IMailer> _factory;
        private IMailer? _mailer;

        public CycleContext(EngineSettings settings, DateTime now, Dictionary<string, int> counts, Func<IMailer> factory)
        {
            Settings = settings;
            Now = now;
            Counts = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
            _factory = factory;
        }

        public EngineSettings Settings { get; }
        public DateTime Now { get; }
        public Dictionary<string, int> Counts { get; }

        /// <summary>Created on first use only, so idle cycles never start Outlook.</summary>
        public IMailer Mailer => _mailer ??= _factory();

        public void Dispose() => _mailer?.Dispose();
    }
}
