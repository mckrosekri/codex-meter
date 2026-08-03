using System.Text;
using System.Text.Json;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.AppServer;

public sealed class AdaptiveAgentConfigurationStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public AdaptiveAgentConfigurationStore(string? directory = null)
    {
        DirectoryPath = directory ?? DefaultDirectory();
    }

    public string DirectoryPath { get; }

    public async Task<AppServerThreadConfiguration> EnsureAsync(
        IReadOnlyList<AdaptiveAgentProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var definitions = new List<AppServerAgentDefinition>(profiles.Count);
            foreach (var profile in profiles)
            {
                Validate(profile);
                var path = Path.GetFullPath(Path.Combine(DirectoryPath, $"{profile.RoleName}.toml"));
                var content = BuildToml(profile);
                if (!File.Exists(path) ||
                    !string.Equals(await File.ReadAllTextAsync(path, cancellationToken), content, StringComparison.Ordinal))
                {
                    await WriteAtomicallyAsync(path, content, cancellationToken);
                }

                definitions.Add(new AppServerAgentDefinition(
                    profile.RoleName,
                    profile.Description,
                    path,
                    profile.NicknameCandidates));
            }

            return new AppServerThreadConfiguration(definitions);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string BuildToml(AdaptiveAgentProfile profile)
    {
        return string.Join(
            Environment.NewLine,
            $"name = {TomlString(profile.RoleName)}",
            $"description = {TomlString(profile.Description)}",
            $"developer_instructions = {TomlString(profile.DeveloperInstructions)}",
            $"model = {TomlString(profile.ModelId)}",
            $"model_reasoning_effort = {TomlString(profile.Effort)}",
            string.Empty);
    }

    private static string TomlString(string value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static void Validate(AdaptiveAgentProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.RoleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Effort);
        if (profile.RoleName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("An adaptive agent role contains unsupported characters.", nameof(profile));
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "CodexDecision", "adaptive-agents");
    }
}
