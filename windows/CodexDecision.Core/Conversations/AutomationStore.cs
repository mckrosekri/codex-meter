using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexDecision.Core.Conversations;

public enum ScheduleKind
{
    Once,
    Interval,
    Daily,
}

public enum ScheduledTaskPurpose
{
    FollowUp,
    Monitor,
}

public enum AutomationRunStatus
{
    Running,
    Completed,
    Failed,
}

public sealed record ScheduledTaskDefinition(
    Guid Id,
    Guid ProjectId,
    Guid TaskId,
    string Name,
    string Prompt,
    ScheduleKind Kind,
    int IntervalMinutes,
    TimeOnly? DailyAt,
    DateTimeOffset NextRunAt,
    bool IsEnabled = true,
    DateTimeOffset? LastRunAt = null,
    string? LastError = null,
    ScheduledTaskPurpose Purpose = ScheduledTaskPurpose.FollowUp)
{
    public ScheduledTaskDefinition Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Prompt);
        if (Kind is ScheduleKind.Interval && IntervalMinutes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(IntervalMinutes));
        }

        if (Kind is ScheduleKind.Daily && DailyAt is null)
        {
            throw new ArgumentException("A daily schedule requires a local time.", nameof(DailyAt));
        }

        return this with
        {
            Name = Name.Trim(),
            Prompt = Prompt.Trim(),
        };
    }

    public ScheduledTaskDefinition Advance(DateTimeOffset now, string? error = null)
    {
        var next = Kind switch
        {
            ScheduleKind.Once when error is not null => now.AddMinutes(5),
            ScheduleKind.Once => NextRunAt,
            ScheduleKind.Interval => now.AddMinutes(Math.Max(1, IntervalMinutes)),
            ScheduleKind.Daily => NextDaily(now, DailyAt!.Value),
            _ => now.AddDays(1),
        };
        return this with
        {
            IsEnabled = Kind is not ScheduleKind.Once || error is not null,
            LastRunAt = error is null ? now : LastRunAt,
            LastError = error,
            NextRunAt = next,
        };
    }

    private static DateTimeOffset NextDaily(DateTimeOffset now, TimeOnly time)
    {
        var local = now.ToLocalTime();
        var candidate = new DateTimeOffset(
            local.Year, local.Month, local.Day, time.Hour, time.Minute, time.Second, local.Offset);
        if (candidate <= local)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate.ToUniversalTime();
    }
}

public sealed record AutomationRunRecord(
    Guid Id,
    Guid AutomationId,
    Guid ProjectId,
    Guid TaskId,
    string AutomationName,
    ScheduledTaskPurpose Purpose,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    AutomationRunStatus Status,
    bool NeedsAttention,
    bool IsRead,
    string Summary,
    string? Error = null,
    string? ThreadId = null,
    string? TurnId = null);

public sealed record AutomationState(
    IReadOnlyList<ScheduledTaskDefinition> Definitions,
    IReadOnlyList<AutomationRunRecord> Runs)
{
    public static AutomationState Empty { get; } = new([], []);
}

public sealed record AutomationResultClassification(
    bool NeedsAttention,
    string Summary);

public static class AutomationResultClassifier
{
    public const string AttentionMarker = "MONITOR_STATUS: ATTENTION";
    public const string NoChangeMarker = "MONITOR_STATUS: NO_CHANGE";

    public static AutomationResultClassification Classify(
        ScheduledTaskPurpose purpose,
        string? response,
        string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new AutomationResultClassification(true, error.Trim());
        }

        var text = string.IsNullOrWhiteSpace(response)
            ? "Run completed without a response summary."
            : response.Trim();
        var noChange = purpose is ScheduledTaskPurpose.Monitor &&
                       text.Contains(NoChangeMarker, StringComparison.OrdinalIgnoreCase);
        var summary = string.Join(
                Environment.NewLine,
                text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !line.Trim().Equals(AttentionMarker, StringComparison.OrdinalIgnoreCase) &&
                                   !line.Trim().Equals(NoChangeMarker, StringComparison.OrdinalIgnoreCase)))
            .Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = noChange ? "No change detected." : "The run needs review.";
        }

        const int maxSummaryLength = 1200;
        if (summary.Length > maxSummaryLength)
        {
            summary = $"{summary[..maxSummaryLength].TrimEnd()}\u2026";
        }

        return new AutomationResultClassification(
            purpose is ScheduledTaskPurpose.FollowUp || !noChange,
            summary);
    }
}

public sealed class AutomationStore
{
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ISecretProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);

    public AutomationStore(ISecretProtector protector, string? path = null)
    {
        this.protector = protector;
        FilePath = path ?? DefaultPath();
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<ScheduledTaskDefinition>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        return (await LoadStateAsync(cancellationToken)).Definitions;
    }

    public async Task<AutomationState> LoadStateAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath))
            {
                return AutomationState.Empty;
            }

            var envelope = JsonSerializer.Deserialize<AutomationEnvelope>(
                await File.ReadAllTextAsync(FilePath, cancellationToken),
                JsonOptions);
            if (envelope is null)
            {
                return AutomationState.Empty;
            }
            if (envelope.Version is < 1 or > CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Automation store version {envelope.Version} is not supported.");
            }

            var plaintext = protector.Unprotect(Convert.FromBase64String(envelope.Payload));
            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind is JsonValueKind.Array)
            {
                var definitions = JsonSerializer.Deserialize<IReadOnlyList<ScheduledTaskDefinition>>(
                                      plaintext,
                                      JsonOptions) ?? [];
                return new AutomationState(definitions, []);
            }

            return JsonSerializer.Deserialize<AutomationState>(plaintext, JsonOptions)
                   ?? AutomationState.Empty;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<ScheduledTaskDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        await SaveStateAsync(new AutomationState(definitions, []), cancellationToken);
    }

    public async Task SaveStateAsync(
        AutomationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            var envelope = new AutomationEnvelope(
                CurrentVersion,
                Convert.ToBase64String(protector.Protect(plaintext)));
            var directory = Path.GetDirectoryName(FilePath)
                            ?? throw new InvalidOperationException("The automation store path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(envelope, JsonOptions),
                    cancellationToken);
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
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

        return Path.Combine(local, "CodexDecision", "automations-v1.json");
    }

    private sealed record AutomationEnvelope(int Version, string Payload);
}
