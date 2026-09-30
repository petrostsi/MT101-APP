using System.Globalization;
using System.Text.Json;

namespace SwiftBatchApp.Core;

/// <summary>JSON {header → value} for rows parked in the PendingRows queue. Numbers stay numbers.</summary>
public static class RowCodec
{
    public static string Serialize(IReadOnlyDictionary<string, object?> row) => JsonSerializer.Serialize(row);

    public static Dictionary<string, object?> Deserialize(string json)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            result[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.Number when p.Value.TryGetInt64(out long l) && !p.Value.GetRawText().Contains('.') => l,
                JsonValueKind.Number => p.Value.GetDecimal(),
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.True => "TRUE",
                JsonValueKind.False => "FALSE",
                JsonValueKind.Null => null,
                _ => p.Value.GetRawText(),
            };
        }
        return result;
    }

    /// <summary>Text of a value as the Excel reader would return it (for de-duplication keys).</summary>
    public static string AsText(object? value) => value switch
    {
        null => "",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
