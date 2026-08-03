using System.Text.Json;

namespace CodexDecision.Core.Projects;

public sealed class NativeCodexWorkspaceCatalog(string? statePath = null)
{
    public string StatePath { get; } = statePath ?? DefaultStatePath();

    public async Task<NativeWorkspaceSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return Parse(document.RootElement);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return NativeWorkspaceSnapshot.Empty;
        }
    }

    private static NativeWorkspaceSnapshot Parse(JsonElement root)
    {
        var projects = new Dictionary<string, NativeProjectRecord>(StringComparer.OrdinalIgnoreCase);
        AddRootArray(root, "project-order", projects);
        AddRootArray(root, "electron-saved-workspace-roots", projects);
        AddRootArray(root, "active-workspace-roots", projects);

        if (root.TryGetProperty("local-projects", out var localProjects) &&
            localProjects.ValueKind is JsonValueKind.Object &&
            root.TryGetProperty("project-writable-roots", out var writableRoots) &&
            writableRoots.ValueKind is JsonValueKind.Object)
        {
            foreach (var localProject in localProjects.EnumerateObject())
            {
                var name = ReadString(localProject.Value, "name");
                if (!writableRoots.TryGetProperty(localProject.Name, out var roots) ||
                    roots.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var writableRoot in roots.EnumerateArray())
                {
                    AddProject(projects, ReadString(writableRoot, "path"), name);
                }
            }
        }

        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("thread-project-assignments", out var threadAssignments) &&
            threadAssignments.ValueKind is JsonValueKind.Object)
        {
            foreach (var assignment in threadAssignments.EnumerateObject())
            {
                var folder = ReadString(assignment.Value, "path")
                             ?? ReadString(assignment.Value, "projectId")
                             ?? ReadString(assignment.Value, "cwd");
                if (TryNormalize(folder, out var normalized))
                {
                    assignments[assignment.Name] = normalized;
                    AddProject(projects, normalized, null);
                }
            }
        }

        var rootHintLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("thread-workspace-root-hints", out var rootHints) &&
            rootHints.ValueKind is JsonValueKind.Object)
        {
            foreach (var hint in rootHints.EnumerateObject())
            {
                if (assignments.ContainsKey(hint.Name) ||
                    hint.Value.ValueKind is not JsonValueKind.String ||
                    !TryNormalize(hint.Value.GetString(), out var normalized))
                {
                    continue;
                }

                rootHintLookup[hint.Name] = normalized;
            }
        }

        return new NativeWorkspaceSnapshot(projects.Values.ToArray(), assignments, rootHintLookup);
    }

    private static void AddRootArray(
        JsonElement root,
        string propertyName,
        IDictionary<string, NativeProjectRecord> projects)
    {
        if (!root.TryGetProperty(propertyName, out var values))
        {
            return;
        }

        if (values.ValueKind is JsonValueKind.String)
        {
            AddProject(projects, values.GetString(), null);
            return;
        }

        if (values.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind is JsonValueKind.String)
            {
                AddProject(projects, value.GetString(), null);
            }
        }
    }

    private static void AddProject(
        IDictionary<string, NativeProjectRecord> projects,
        string? folder,
        string? name)
    {
        if (!TryNormalize(folder, out var normalized))
        {
            return;
        }

        var resolvedName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized))
            : name.Trim();
        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            resolvedName = "Codex project";
        }

        if (!projects.TryGetValue(normalized, out var existing) || !string.IsNullOrWhiteSpace(name))
        {
            projects[normalized] = new NativeProjectRecord(resolvedName, normalized);
        }
    }

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.ValueKind is JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string DefaultStatePath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.Combine(codexHome, ".codex-global-state.json");
    }
}
