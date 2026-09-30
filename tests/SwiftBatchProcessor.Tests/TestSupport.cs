using System.Text.RegularExpressions;
using SwiftBatchApp.Core;

// AppDb, Session and WorkbookStore are static: run test classes one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SwiftBatchApp.Tests;

/// <summary>A throw-away folder under the system temp directory.</summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "swiftbatch-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Root);

    public string this[string relative] => Path.Combine(Root, relative);

    public string Dir(string relative)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public const string SingleMt103 = "synthetic_single_mt103.prt";
    public const string MultiMtf = "synthetic_multi_mtf.prt";
    public const string MultiMessage = "synthetic_multi_message.prt";
    public const string NotMt101 = "not_mt101.prt";

    /// <summary>Copies a fixture into <paramref name="folder"/>, optionally under another name.</summary>
    public static string CopyTo(string fixture, string folder, string? asName = null)
    {
        Directory.CreateDirectory(folder);
        string dest = System.IO.Path.Combine(folder, asName ?? fixture);
        File.Copy(Path(fixture), dest, overwrite: true);
        return dest;
    }
}

/// <summary>Records sends; users in <see cref="OutOfOffice"/> "auto-reply"; users in <see cref="FailFor"/> fail.</summary>
public sealed class FakeMailer : IMailer
{
    private static readonly Regex TokenRx = new(@"\[([^\]]+)\]\s*$");
    private readonly Dictionary<string, string> _tokenToRecipient = new();

    public List<(string To, string Subject, string Body, string? Attachment)> Sent { get; } = new();
    public HashSet<string> OutOfOffice { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailAll { get; set; }
    public string LastError { get; private set; } = "";

    public bool SendFile(string to, string subject, string body, string? attachmentPath)
    {
        if (FailAll || FailFor.Contains(to))
        {
            LastError = "simulated failure";
            return false;
        }
        Assert.True(attachmentPath is null || File.Exists(attachmentPath), $"attachment missing: {attachmentPath}");
        Sent.Add((to, subject, body, attachmentPath));
        Match m = TokenRx.Match(subject);
        if (m.Success) _tokenToRecipient[m.Groups[1].Value] = to;
        return true;
    }

    public bool IsOutOfOffice(string token, int waitSeconds) =>
        _tokenToRecipient.TryGetValue(token, out string? to) && OutOfOffice.Contains(to);

    public void Dispose() { }
}
