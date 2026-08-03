using System.Diagnostics;
using CodexDecision.Core.Git;

namespace CodexDecision.Core.Projects;

public sealed class ExecutionProfileService(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new SystemProcessRunner();

    public async Task ValidateAsync(
        ProjectExecutionProfile profile,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"The working directory '{workingDirectory}' does not exist.");
        }

        if (profile.Kind is ExecutionProfileKind.NativeWindows)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profile.WslDistribution);
        var result = await runner.RunAsync("wsl.exe", ["--list", "--quiet"], cancellationToken);
        var distributions = result.StandardOutput.Replace("\0", string.Empty, StringComparison.Ordinal);
        if (!result.Succeeded || !distributions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(name => name.Trim().Equals(profile.WslDistribution, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"WSL distribution '{profile.WslDistribution}' is unavailable.");
        }
    }

    public ProcessStartInfo CreateTerminalStartInfo(
        ProjectExecutionProfile profile,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var cwd = Path.GetFullPath(workingDirectory);
        if (profile.Kind is ExecutionProfileKind.Wsl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.WslDistribution);
            var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = true };
            info.ArgumentList.Add("--distribution");
            info.ArgumentList.Add(profile.WslDistribution);
            info.ArgumentList.Add("--cd");
            info.ArgumentList.Add(ToWslPath(cwd));
            return info;
        }

        var shell = string.IsNullOrWhiteSpace(profile.Shell) ? "powershell.exe" : profile.Shell;
        return new ProcessStartInfo(shell)
        {
            UseShellExecute = true,
            WorkingDirectory = cwd,
        };
    }

    public ProcessStartInfo CreateEditorStartInfo(
        ProjectExecutionProfile profile,
        string workingDirectory)
    {
        var editor = string.IsNullOrWhiteSpace(profile.Editor) ? "code" : profile.Editor;
        var info = new ProcessStartInfo(editor) { UseShellExecute = true };
        info.ArgumentList.Add(Path.GetFullPath(workingDirectory));
        return info;
    }

    public static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            throw new ArgumentException("Only local drive paths can be mapped to WSL.", nameof(windowsPath));
        }

        var drive = char.ToLowerInvariant(root[0]);
        var remainder = full[root.Length..].Replace('\\', '/');
        return $"/mnt/{drive}/{remainder}".TrimEnd('/');
    }
}
