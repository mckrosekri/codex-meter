using System.Text;
using System.Text.Json;
using CodexDecision.Core.AppServer;

namespace CodexDecision.Core.Conversations;

public static class ConversationExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ExportJson(CodexThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return JsonSerializer.Serialize(thread, JsonOptions);
    }

    public static string ExportMarkdown(CodexThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var builder = new StringBuilder();
        builder.AppendLine($"# {thread.Name ?? thread.Preview ?? "Codex task"}");
        builder.AppendLine();
        builder.AppendLine($"- Thread: `{thread.Id}`");
        builder.AppendLine($"- Working directory: `{thread.WorkingDirectory}`");
        foreach (var turn in thread.Turns)
        {
            builder.AppendLine();
            builder.AppendLine($"## Turn `{turn.Id}`");
            foreach (var item in turn.Items)
            {
                var type = ReadString(item, "type") ?? "item";
                var text = type switch
                {
                    "userMessage" => ReadUserMessage(item),
                    "agentMessage" => ReadString(item, "text") ?? ReadString(item, "message") ?? string.Empty,
                    _ => string.Empty,
                };
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                builder.AppendLine();
                builder.AppendLine(type == "userMessage" ? "### User" : "### Codex");
                builder.AppendLine();
                builder.AppendLine(text.Trim());
            }
        }

        return builder.ToString();
    }

    private static string ReadUserMessage(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, content.EnumerateArray()
            .Select(part => ReadString(part, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public static class AttachmentPolicy
{
    public static AppServerAttachment Validate(
        string attachmentPath,
        IEnumerable<string> projectRoots,
        bool allowOutsideProject)
    {
        var path = Path.GetFullPath(attachmentPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected attachment no longer exists.", path);
        }

        var inside = projectRoots.Any(root => IsWithin(path, root));
        if (!inside && !allowOutsideProject)
        {
            throw new InvalidOperationException("The attachment is outside this project and requires explicit confirmation.");
        }

        return new AppServerAttachment(path, Path.GetFileName(path));
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.StartsWith(
            $"{normalizedRoot}{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }
}
