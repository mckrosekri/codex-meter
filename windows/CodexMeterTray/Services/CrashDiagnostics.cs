using System.Text;

namespace CodexMeterTray.Services;

internal static class CrashDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexDecision",
        "logs",
        "app-errors.log");

    public static void Record(string source, Exception? exception = null, string? details = null)
    {
        try
        {
            var builder = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {source}");
            if (!string.IsNullOrWhiteSpace(details))
            {
                builder.AppendLine(details);
            }
            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }
            builder.AppendLine();

            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Gate)
            {
                File.AppendAllText(path, builder.ToString());
            }
        }
        catch
        {
            // Diagnostics must never become another application failure.
        }
    }
}
