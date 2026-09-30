using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwiftBatchApp.Core;

/// <summary>
/// Outlook desktop via late-bound COM (no interop assemblies). Must be created and used on ONE STA thread.
/// Sends from <c>SenderAccount</c> when it is configured in the profile (SendUsingAccount), otherwise
/// on behalf of it (SentOnBehalfOfName); auto-replies are looked for in that account's inbox.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OutlookMailer : IMailer
{
    private const int OlMailItem = 0;
    private const int OlFolderInbox = 6;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _senderAccount;
    private dynamic? _app;
    private dynamic? _ns;
    private dynamic? _account;
    private bool _accountResolved;

    public string LastError { get; private set; } = "";

    public OutlookMailer(string senderAccount) => _senderAccount = (senderAccount ?? "").Trim();

    public static bool IsAvailable() => Type.GetTypeFromProgID("Outlook.Application") is not null;

    // ---------------------------------------------------------------- IMailer

    public bool SendFile(string to, string subject, string body, string? attachmentPath)
    {
        try
        {
            dynamic mail = App.CreateItem(OlMailItem);
            mail.To = to;
            mail.Subject = subject;
            mail.Body = body;
            if (!string.IsNullOrEmpty(attachmentPath)) mail.Attachments.Add(attachmentPath);

            object? account = Account;
            if (account is not null) SetSendUsingAccount(mail, account);
            else if (_senderAccount.Length > 0) mail.SentOnBehalfOfName = _senderAccount;

            mail.Send();
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool IsOutOfOffice(string token, int waitSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, waitSeconds));
        while (true)
        {
            try
            {
                if (ScanInboxFor(token)) return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            Thread.Sleep(left < PollInterval ? left : PollInterval);
        }
    }

    public void Dispose()
    {
        Release(_account);
        Release(_ns);
        Release(_app);      // never Quit(): the user's own Outlook session stays open
        _account = _ns = _app = null;
    }

    // ---------------------------------------------------------------- internals

    private dynamic App
    {
        get
        {
            if (_app is null)
            {
                Type type = Type.GetTypeFromProgID("Outlook.Application")
                            ?? throw new InvalidOperationException("Microsoft Outlook is not installed on this PC.");
                _app = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Outlook could not be started.");
                _ns = _app.GetNamespace("MAPI");
            }
            return _app;
        }
    }

    private object? Account
    {
        get
        {
            if (_accountResolved) return _account;
            _accountResolved = true;
            if (_senderAccount.Length == 0) return null;
            _ = App;
            dynamic accounts = _ns!.Accounts;
            int count = accounts.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic acc = accounts.Item(i);
                if (Matches(() => (string)acc.SmtpAddress) || Matches(() => (string)acc.DisplayName))
                {
                    _account = acc;
                    break;
                }
            }
            return _account;
        }
    }

    private bool Matches(Func<string> read)
    {
        try { return string.Equals(read()?.Trim(), _senderAccount, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private bool ScanInboxFor(string token)
    {
        _ = App;
        dynamic inbox = Inbox();
        dynamic items = inbox.Items.Restrict("[Unread] = true");
        int count = items.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic item = items.Item(i);
            string subject = Read(() => (string)item.Subject);
            if (!subject.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
            if (!AutoReplyDetector.IsAutoReply(subject, Read(() => (string)item.MessageClass))) continue;
            try
            {
                item.UnRead = false;
                item.Save();
            }
            catch
            {
                // best effort
            }
            return true;
        }
        return false;
    }

    private dynamic Inbox()
    {
        if (Account is not null)
        {
            try { return ((dynamic)Account).DeliveryStore.GetDefaultFolder(OlFolderInbox); }
            catch { /* fall back to the default store */ }
        }
        return _ns!.GetDefaultFolder(OlFolderInbox);
    }

    private static void SetSendUsingAccount(object mail, object account)
    {
        try
        {
            mail.GetType().InvokeMember("SendUsingAccount", BindingFlags.PutRefDispProperty, null, mail, new[] { account });
        }
        catch
        {
            ((dynamic)mail).SendUsingAccount = (dynamic)account;
        }
    }

    private static string Read(Func<string> read)
    {
        try { return read() ?? ""; }
        catch { return ""; }
    }

    private static void Release(object? com)
    {
        try
        {
            if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch
        {
            // best effort
        }
    }
}
