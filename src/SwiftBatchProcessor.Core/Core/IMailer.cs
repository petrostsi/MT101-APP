using System.Globalization;
using System.Text;

namespace SwiftBatchApp.Core;

/// <summary>Mail transport used by the engine. Production: <see cref="OutlookMailer"/>; tests: a fake.</summary>
public interface IMailer : IDisposable
{
    /// <summary>Sends the file as an attachment. False (and <see cref="LastError"/>) on failure.</summary>
    bool SendFile(string to, string subject, string body, string? attachmentPath);

    /// <summary>
    /// Waits up to <paramref name="waitSeconds"/> for an unread auto-reply (OOO / undeliverable) whose
    /// subject carries <paramref name="token"/>. Matching replies are marked read.
    /// </summary>
    bool IsOutOfOffice(string token, int waitSeconds);

    string LastError { get; }
}

/// <summary>Recognises Exchange/Outlook automatic replies in English and Greek.</summary>
public static class AutoReplyDetector
{
    /// <summary>Subject markers, compared without case or accents.</summary>
    public static readonly string[] SubjectMarkers =
    {
        "automatic reply", "auto reply", "autoreply", "out of office",
        "αυτοματη απαντηση", "εκτος γραφειου",
        "undeliverable", "delivery has failed", "μη παραδοσιμο", "δεν ηταν δυνατη η παραδοση",
    };

    private static readonly string[] FoldedMarkers = SubjectMarkers.Select(Fold).ToArray();

    public static bool IsAutoReply(string? subject, string? messageClass)
    {
        string cls = messageClass ?? "";
        if (cls.StartsWith("IPM.Note.Rules.OofTemplate", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls.StartsWith("REPORT.", StringComparison.OrdinalIgnoreCase) && cls.Contains("NDR", StringComparison.OrdinalIgnoreCase)) return true;
        string s = Fold(subject);
        return FoldedMarkers.Any(m => s.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>Lower case, accents removed ("Αυτόματη απάντηση" → "αυτοματη απαντηση").</summary>
    internal static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).Replace('ς', 'σ');
    }
}
