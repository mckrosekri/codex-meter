using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexDecision.Core.Conversations;

public enum SecureContinuityMode
{
    Disabled,
    DpapiCurrentUser,
}

public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

public sealed record ContinuityWarning(Guid TaskId, string Message);

public sealed record TaskContinuity(
    IReadOnlyList<QueuedFollowUp> Queue,
    GoalDefinition? Goal,
    string? HandoffDraft = null);

public sealed class SecureContinuityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ISecretProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SecureContinuityStore(ISecretProtector protector, string? path = null)
    {
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        FilePath = path ?? DefaultPath();
    }

    public string FilePath { get; }

    public IReadOnlyList<ContinuityWarning> Warnings { get; private set; } = [];

    public async Task<IReadOnlyDictionary<Guid, TaskContinuity>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var loaded = new Dictionary<Guid, TaskContinuity>();
            var quarantined = new HashSet<Guid>();
            foreach (var record in document.Tasks.Where(record => !record.Quarantined))
            {
                try
                {
                    var ciphertext = Convert.FromBase64String(record.Payload);
                    var plaintext = protector.Unprotect(ciphertext);
                    loaded[record.TaskId] = JsonSerializer.Deserialize<TaskContinuity>(plaintext, JsonOptions)
                                            ?? new TaskContinuity([], null);
                }
                catch (Exception error) when (error is FormatException or JsonException or System.Security.Cryptography.CryptographicException)
                {
                    quarantined.Add(record.TaskId);
                    Warnings =
                    [
                        .. Warnings.Where(candidate => candidate.TaskId != record.TaskId),
                        new ContinuityWarning(
                            record.TaskId,
                            "Secure continuity data could not be decrypted and was quarantined."),
                    ];
                }
            }

            if (quarantined.Count > 0)
            {
                await SaveDocumentAsync(document with
                {
                    Tasks = document.Tasks.Select(record => quarantined.Contains(record.TaskId)
                        ? record with
                        {
                            Quarantined = true,
                            QuarantineReason = "DecryptionFailed",
                        }
                        : record).ToArray(),
                }, cancellationToken);
            }

            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TaskContinuity> LoadTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var record = document.Tasks.FirstOrDefault(candidate => candidate.TaskId == taskId);
            if (record is null || record.Quarantined)
            {
                return new TaskContinuity([], null);
            }

            try
            {
                var ciphertext = Convert.FromBase64String(record.Payload);
                var plaintext = protector.Unprotect(ciphertext);
                return JsonSerializer.Deserialize<TaskContinuity>(plaintext, JsonOptions)
                       ?? new TaskContinuity([], null);
            }
            catch (Exception error) when (error is FormatException or JsonException or System.Security.Cryptography.CryptographicException)
            {
                var warning = new ContinuityWarning(
                    taskId,
                    "Secure continuity data could not be decrypted and was quarantined.");
                Warnings = [.. Warnings.Where(candidate => candidate.TaskId != taskId), warning];
                var quarantined = record with { Quarantined = true, QuarantineReason = error.GetType().Name };
                await SaveDocumentAsync(document with
                {
                    Tasks = document.Tasks.Select(candidate =>
                        candidate.TaskId == taskId ? quarantined : candidate).ToArray(),
                }, cancellationToken);
                return new TaskContinuity([], null);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveTaskAsync(
        Guid taskId,
        TaskContinuity continuity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuity);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(continuity, JsonOptions);
            var ciphertext = protector.Protect(plaintext);
            var updated = new EncryptedTaskRecord(
                taskId,
                DateTimeOffset.UtcNow,
                Convert.ToBase64String(ciphertext),
                false,
                null);
            await SaveDocumentAsync(document with
            {
                Tasks = [updated, .. document.Tasks.Where(candidate => candidate.TaskId != taskId)],
            }, cancellationToken);
            Warnings = Warnings.Where(candidate => candidate.TaskId != taskId).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAllAsync(
        IReadOnlyDictionary<Guid, TaskContinuity> continuityByTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuityByTask);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var updated = continuityByTask.Select(pair =>
            {
                var plaintext = JsonSerializer.SerializeToUtf8Bytes(pair.Value, JsonOptions);
                var ciphertext = protector.Protect(plaintext);
                return new EncryptedTaskRecord(
                    pair.Key,
                    DateTimeOffset.UtcNow,
                    Convert.ToBase64String(ciphertext),
                    false,
                    null);
            }).ToArray();
            await SaveDocumentAsync(new ContinuityDocument(1, updated), cancellationToken);
            Warnings = [];
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            await SaveDocumentAsync(document with
            {
                Tasks = document.Tasks.Where(candidate => candidate.TaskId != taskId).ToArray(),
            }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ContinuityDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return ContinuityDocument.Empty;
        }

        await using var stream = new FileStream(
            FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
        return await JsonSerializer.DeserializeAsync<ContinuityDocument>(stream, JsonOptions, cancellationToken)
               ?? ContinuityDocument.Empty;
    }

    private async Task SaveDocumentAsync(ContinuityDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)
                        ?? throw new InvalidOperationException("The secure continuity path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
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

    private static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "CodexDecision", "secure-queue-v1.json");
    }

    private sealed record ContinuityDocument(int Version, IReadOnlyList<EncryptedTaskRecord> Tasks)
    {
        public static ContinuityDocument Empty { get; } = new(1, []);
    }

    private sealed record EncryptedTaskRecord(
        Guid TaskId,
        DateTimeOffset UpdatedAt,
        string Payload,
        bool Quarantined,
        string? QuarantineReason);
}
