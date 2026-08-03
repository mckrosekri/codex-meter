using System.Text.Json;

namespace CodexDecision.Core.Projects;

public sealed record NativeSessionIndexEntry(
    string ThreadId,
    string Title,
    DateTimeOffset UpdatedAt);

public sealed class NativeCodexSessionIndexCatalog(string? indexPath = null)
{
    public string IndexPath { get; } = indexPath ?? DefaultIndexPath();

    public async Task<IReadOnlyList<NativeSessionIndexEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, NativeSessionIndexEntry>(StringComparer.Ordinal);
        try
        {
            await using var stream = new FileStream(
                IndexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var threadId = ReadString(root, "id");
                    if (string.IsNullOrWhiteSpace(threadId))
                    {
                        continue;
                    }

                    var title = ReadString(root, "thread_name");
                    var updatedAt = DateTimeOffset.TryParse(ReadString(root, "updated_at"), out var parsed)
                        ? parsed
                        : DateTimeOffset.MinValue;
                    var entry = new NativeSessionIndexEntry(
                        threadId,
                        string.IsNullOrWhiteSpace(title) ? "Untitled task" : title.Trim(),
                        updatedAt);
                    if (!entries.TryGetValue(threadId, out var current) || entry.UpdatedAt >= current.UpdatedAt)
                    {
                        entries[threadId] = entry;
                    }
                }
                catch (JsonException)
                {
                    // A partially written line must not prevent the rest of the index loading.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return entries.Values.OrderByDescending(entry => entry.UpdatedAt).ToArray();
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DefaultIndexPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.Combine(codexHome, "session_index.jsonl");
    }
}
