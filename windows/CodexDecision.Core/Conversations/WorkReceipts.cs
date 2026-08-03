using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexDecision.Core.Git;
using CodexDecision.Core.Projects;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.Conversations;

public enum VerificationGateStatus
{
    NotConfigured,
    Skipped,
    Passed,
    Failed,
    Error,
}

public enum VerificationStepStatus
{
    Passed,
    Failed,
    TimedOut,
    Error,
}

public sealed record VerificationStepDefinition(
    Guid Id,
    string Name,
    string Command,
    int TimeoutSeconds = 300)
{
    public VerificationStepDefinition Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Command);
        if (TimeoutSeconds is < 5 or > 1800)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TimeoutSeconds),
                "Verification timeouts must be between 5 seconds and 30 minutes.");
        }

        return this with
        {
            Name = Name.Trim(),
            Command = Command.Trim(),
        };
    }
}

public sealed record VerificationPolicy(
    bool IsEnabled,
    IReadOnlyList<VerificationStepDefinition> Steps,
    bool PauseQueueOnFailure = true,
    bool RequirePassingReceiptBeforeGit = true)
{
    public static VerificationPolicy Disabled { get; } = new(false, []);

    public VerificationPolicy Normalize()
    {
        if (Steps.Count > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(Steps), "A project can have at most 12 verification steps.");
        }

        var normalized = Steps.Select(step => step.Normalize()).ToArray();
        if (IsEnabled && normalized.Length == 0)
        {
            throw new ArgumentException("An enabled verification gate requires at least one command.", nameof(Steps));
        }

        return this with { Steps = normalized };
    }
}

public sealed record VerificationStepResult(
    Guid StepId,
    string Name,
    string Command,
    VerificationStepStatus Status,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Output)
{
    public TimeSpan Duration => CompletedAt - StartedAt;
}

public sealed record WorkReceipt(
    Guid Id,
    Guid ProjectId,
    Guid TaskId,
    string? ThreadId,
    string? TurnId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string TurnStatus,
    string? ModelId,
    string? ReasoningEffort,
    PermissionLevel? Permission,
    long ContextTokensAtCompletion,
    string? RepositoryRoot,
    string? BranchName,
    string? HeadCommit,
    string? WorkingTreeFingerprint,
    int ChangedFileCount,
    int Additions,
    int Deletions,
    VerificationGateStatus GateStatus,
    IReadOnlyList<VerificationStepResult> Verification,
    IReadOnlyList<string> RiskFlags,
    bool IsAutomated = false)
{
    public bool Passed => GateStatus is VerificationGateStatus.Passed;

    public bool BlocksContinuation(VerificationPolicy policy) =>
        policy.IsEnabled && policy.PauseQueueOnFailure &&
        GateStatus is VerificationGateStatus.Failed or VerificationGateStatus.Error;

    public bool Matches(GitEnvironmentSnapshot environment) =>
        string.Equals(WorkingTreeFingerprint, WorkReceiptFingerprint.Create(environment), StringComparison.Ordinal);
}

public sealed record WorkReceiptState(IReadOnlyList<WorkReceipt> Receipts)
{
    public static WorkReceiptState Empty { get; } = new([]);
}

public static class WorkReceiptFingerprint
{
    public static string Create(GitEnvironmentSnapshot environment)
    {
        var value = new StringBuilder(environment.HeadCommit)
            .Append('|').Append(environment.BranchName);
        foreach (var file in environment.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            value.Append('\n')
                .Append(file.Path.Replace('\\', '/'))
                .Append('|').Append(file.StatusCode)
                .Append('|').Append(file.Additions)
                .Append('|').Append(file.Deletions);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }
}

public sealed class WorkReceiptStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ISecretProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WorkReceiptStore(ISecretProtector protector, string? path = null)
    {
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        FilePath = path ?? DefaultPath();
    }

    public string FilePath { get; }

    public async Task<WorkReceiptState> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath))
            {
                return WorkReceiptState.Empty;
            }

            var envelope = JsonSerializer.Deserialize<ReceiptEnvelope>(
                await File.ReadAllTextAsync(FilePath, cancellationToken),
                JsonOptions);
            if (envelope is null)
            {
                return WorkReceiptState.Empty;
            }

            if (envelope.Version != CurrentVersion)
            {
                throw new InvalidDataException($"Work receipt store version {envelope.Version} is not supported.");
            }

            var plaintext = protector.Unprotect(Convert.FromBase64String(envelope.Payload));
            return JsonSerializer.Deserialize<WorkReceiptState>(plaintext, JsonOptions)
                   ?? WorkReceiptState.Empty;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveStateAsync(WorkReceiptState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            var envelope = new ReceiptEnvelope(
                CurrentVersion,
                Convert.ToBase64String(protector.Protect(plaintext)));
            var directory = Path.GetDirectoryName(FilePath)
                            ?? throw new InvalidOperationException("The work receipt path has no directory.");
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
            local = AppContext.BaseDirectory;
        }

        return Path.Combine(local, "CodexDecision", "work-receipts.json");
    }

    private sealed record ReceiptEnvelope(int Version, string Payload);
}

public sealed record VerificationCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false);

public interface IVerificationCommandRunner
{
    Task<VerificationCommandResult> RunAsync(
        ProjectExecutionProfile profile,
        string workingDirectory,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class SystemVerificationCommandRunner : IVerificationCommandRunner
{
    private const int MaxOutputLength = 16_000;

    public async Task<VerificationCommandResult> RunAsync(
        ProjectExecutionProfile profile,
        string workingDirectory,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var info = CreateStartInfo(profile, workingDirectory, command);
        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"Unable to start verification command '{command}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new VerificationCommandResult(
                -1,
                Limit(await stdoutTask),
                Limit(await stderrTask),
                TimedOut: true);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new VerificationCommandResult(
            process.ExitCode,
            Limit(await stdoutTask),
            Limit(await stderrTask));
    }

    private static ProcessStartInfo CreateStartInfo(
        ProjectExecutionProfile profile,
        string workingDirectory,
        string command)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        ProcessStartInfo info;
        if (profile.Kind is ExecutionProfileKind.Wsl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.WslDistribution);
            info = NewStartInfo("wsl.exe");
            info.ArgumentList.Add("--distribution");
            info.ArgumentList.Add(profile.WslDistribution);
            info.ArgumentList.Add("--cd");
            info.ArgumentList.Add(ExecutionProfileService.ToWslPath(cwd));
            info.ArgumentList.Add("--exec");
            info.ArgumentList.Add("sh");
            info.ArgumentList.Add("-lc");
            info.ArgumentList.Add(command);
            return info;
        }

        var shell = string.IsNullOrWhiteSpace(profile.Shell) ? "cmd.exe" : profile.Shell.Trim();
        info = NewStartInfo(shell);
        info.WorkingDirectory = cwd;
        if (Path.GetFileNameWithoutExtension(shell).Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(shell).Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add("-NoLogo");
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(command);
        }
        else
        {
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(command);
        }

        return info;
    }

    private static ProcessStartInfo NewStartInfo(string fileName) => new(fileName)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the state check and Kill.
        }
    }

    private static string Limit(string value) => value.Length <= MaxOutputLength
        ? value
        : $"{value[..MaxOutputLength]}\n… output truncated";
}

public sealed class VerificationGateService(IVerificationCommandRunner? runner = null)
{
    private readonly IVerificationCommandRunner runner = runner ?? new SystemVerificationCommandRunner();

    public async Task<(VerificationGateStatus Status, IReadOnlyList<VerificationStepResult> Results)> RunAsync(
        VerificationPolicy policy,
        ProjectExecutionProfile profile,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var normalized = policy.Normalize();
        if (!normalized.IsEnabled)
        {
            return (VerificationGateStatus.NotConfigured, []);
        }

        var results = new List<VerificationStepResult>();
        foreach (var step in normalized.Steps)
        {
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                var result = await runner.RunAsync(
                    profile,
                    workingDirectory,
                    step.Command,
                    TimeSpan.FromSeconds(step.TimeoutSeconds),
                    cancellationToken);
                var status = result.TimedOut
                    ? VerificationStepStatus.TimedOut
                    : result.ExitCode == 0
                        ? VerificationStepStatus.Passed
                        : VerificationStepStatus.Failed;
                results.Add(new VerificationStepResult(
                    step.Id,
                    step.Name,
                    step.Command,
                    status,
                    result.ExitCode,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    JoinOutput(result.StandardOutput, result.StandardError)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                results.Add(new VerificationStepResult(
                    step.Id,
                    step.Name,
                    step.Command,
                    VerificationStepStatus.Error,
                    -1,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    error.Message));
            }
        }

        var gateStatus = results.Any(result => result.Status is VerificationStepStatus.Error)
            ? VerificationGateStatus.Error
            : results.All(result => result.Status is VerificationStepStatus.Passed)
                ? VerificationGateStatus.Passed
                : VerificationGateStatus.Failed;
        return (gateStatus, results);
    }

    private static string JoinOutput(string standardOutput, string standardError)
    {
        var output = standardOutput.TrimEnd();
        var error = standardError.TrimEnd();
        if (string.IsNullOrWhiteSpace(output))
        {
            return error;
        }

        return string.IsNullOrWhiteSpace(error) ? output : $"{output}{Environment.NewLine}{error}";
    }
}
