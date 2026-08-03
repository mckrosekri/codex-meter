using System.Text.Json;
using System.Text.Json.Serialization;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Projects;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string? legacyPath;

    public WorkspaceStore(string? path = null, string? legacyPath = null)
    {
        FilePath = path ?? DefaultPath();
        this.legacyPath = legacyPath ?? (path is null ? DefaultLegacyPath() : null);
    }

    public string FilePath { get; }

    public async Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var sourcePath = File.Exists(FilePath)
                ? FilePath
                : legacyPath is not null && File.Exists(legacyPath)
                    ? legacyPath
                    : null;
            if (sourcePath is null)
            {
                return WorkspaceState.Empty;
            }

            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var loaded = await JsonSerializer.DeserializeAsync<WorkspaceState>(stream, JsonOptions, cancellationToken)
                         ?? WorkspaceState.Empty;
            var normalized = loaded with
            {
                Version = 2,
                EnabledSpecialSkills = SpecialSkillCatalog.Normalize(loaded.EnabledSpecialSkills),
            };
            if (!string.Equals(sourcePath, FilePath, StringComparison.OrdinalIgnoreCase))
            {
                await SaveCoreAsync(normalized, cancellationToken);
            }

            return normalized;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(state with { Version = 2 }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "CodexDecision", "workspace-v2.json");
    }

    private static string DefaultLegacyPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "CodexDecision", "workspace-v1.json");
    }

    private async Task SaveCoreAsync(WorkspaceState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)
                        ?? throw new InvalidOperationException("The workspace store path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
