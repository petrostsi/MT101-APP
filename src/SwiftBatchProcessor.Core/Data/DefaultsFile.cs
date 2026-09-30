using System.Text.Json;

namespace SwiftBatchApp.Data;

/// <summary>
/// Optional organisation defaults read from <c>SwiftBatch.defaults.json</c> next to the exe.
/// They seed a NEW SwiftBatch.db (settings that are still missing, and the user list when empty),
/// so real share paths and team e-mails never have to be compiled into the (public) source.
/// See SwiftBatch.defaults.example.json.
/// </summary>
public sealed class DefaultsFile
{
    public const string FileName = "SwiftBatch.defaults.json";

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DefaultUser> Users { get; set; } = new();

    public sealed class DefaultUser
    {
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string WindowsUser { get; set; } = "";
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Returns null when the file does not exist; throws on malformed JSON.</summary>
    public static DefaultsFile? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var file = JsonSerializer.Deserialize<DefaultsFile>(File.ReadAllText(path), options) ?? new DefaultsFile();
        file.Settings = new Dictionary<string, string>(file.Settings, StringComparer.OrdinalIgnoreCase);
        return file;
    }
}
