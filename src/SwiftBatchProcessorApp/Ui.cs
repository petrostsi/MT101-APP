using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SwiftBatchApp.Core;

namespace SwiftBatchApp;

/// <summary>Small UI helpers shared by the views.</summary>
public static class Ui
{
    public const string Title = "SWIFT Batch Processor";

    public static void Info(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Error(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(string message) =>
        Show(message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image)
    {
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow;
        return owner is { IsVisible: true }
            ? MessageBox.Show(owner, message, Title, buttons, image)
            : MessageBox.Show(message, Title, buttons, image);
    }

    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Warn($"The folder is not reachable:\n{path}");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenInNotepad(string path)
    {
        if (!File.Exists(path))
        {
            Warn($"The file was not found:\n{path}");
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenWithShell(string path)
    {
        if (!File.Exists(path))
        {
            Warn($"The file was not found:\n{path}");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Case-insensitive "contains" over several fields.</summary>
    public static bool Matches(string search, params string?[] fields) =>
        search.Length == 0 || fields.Any(f => f is not null && f.Contains(search, StringComparison.OrdinalIgnoreCase));

    public static string Plural(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";
}

/// <summary>Status text → colored dot brush.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = (value?.ToString() ?? "").Trim();
        string resource = key.ToUpperInvariant() switch
        {
            "COMPLETED" => "SuccessBrush",
            "PENDING" or "OK" => "WarningBrush",
            "ESCALATED" or "PARSE ERROR" => "DangerBrush",
            "NO MATCH" => "PurpleBrush",
            "MT103" => "InfoBrush",
            "MTF" => "AccentBrush",
            "WORKING" => "AccentBrush",
            "IDLE" => "SuccessBrush",
            "STOPPED" => "NeutralBrush",
            _ => "NeutralBrush",
        };
        return Application.Current.TryFindResource(resource) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Customer check outcome (enum or registry text) → brush.</summary>
public sealed class MatchBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        MatchOutcome outcome = value switch
        {
            MatchOutcome m => m,
            string s when s.StartsWith("NO MATCH", StringComparison.OrdinalIgnoreCase) => MatchOutcome.NoMatch,
            string s when s.StartsWith("MATCH", StringComparison.OrdinalIgnoreCase) => MatchOutcome.Match,
            _ => MatchOutcome.NotChecked,
        };
        string resource = outcome switch
        {
            MatchOutcome.Match => "SuccessBrush",
            MatchOutcome.NoMatch => "PurpleBrush",
            _ => "NeutralBrush",
        };
        return Application.Current.TryFindResource(resource) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>One bar of a <see cref="Controls.BarChart"/>.</summary>
public sealed class BarItem
{
    public BarItem(string label, double value, double max, string? display = null, Brush? brush = null)
    {
        Label = label;
        Value = value;
        Display = display ?? value.ToString("N0", CultureInfo.CurrentCulture);
        double ratio = max <= 0 ? 0 : Math.Clamp(value / max, 0, 1);
        BarWidth = new GridLength(Math.Max(ratio, 0.0001), GridUnitType.Star);
        RestWidth = new GridLength(Math.Max(1 - ratio, 0.0001), GridUnitType.Star);
        Brush = brush ?? (Application.Current.TryFindResource("AccentBrush") as Brush ?? Brushes.Orange);
    }

    public string Label { get; }
    public double Value { get; }
    public string Display { get; }
    public GridLength BarWidth { get; }
    public GridLength RestWidth { get; }
    public Brush Brush { get; }

    public static List<BarItem> From(IEnumerable<(string Label, double Value)> data, Func<double, string>? format = null)
    {
        var list = data.ToList();
        double max = list.Count == 0 ? 0 : list.Max(d => d.Value);
        return list.Select(d => new BarItem(d.Label, d.Value, max, format?.Invoke(d.Value))).ToList();
    }
}

/// <summary>Engine log line → color by tag.</summary>
public sealed class LogBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string line = value as string ?? "";
        string resource =
            line.Contains("[ERROR]") ? "DangerBrush" :
            line.Contains("[MAIL]") && (line.Contains("failed") || line.Contains("Not sent") || line.Contains("unavailable")) ? "DangerBrush" :
            line.Contains("[QUEUE]") ? "WarningBrush" :
            line.Contains("[DEFER]") ? "PurpleBrush" :
            line.Contains("[ASSIGN]") ? "SuccessBrush" :
            line.Contains("[MAIL]") ? "InfoBrush" :
            line.Contains("[MATCH]") && line.Contains("NO MATCH") ? "PurpleBrush" :
            line.Contains("[DUP]") ? "MutedBrush" :
            "TextBrush";
        return Application.Current.TryFindResource(resource) as Brush ?? Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
