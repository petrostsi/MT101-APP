using System.Globalization;

namespace SwiftBatchApp.Core;

/// <summary>Greek-style number formatting (1.234,56) without depending on OS culture data.</summary>
public static class DisplayFormats
{
    private static readonly NumberFormatInfo Greek = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = new[] { 3 },
    };

    public static string Amount(decimal? value) => value?.ToString("N2", Greek) ?? "";

    public static string Integer(long? value) => value?.ToString("N0", Greek) ?? "";
}
