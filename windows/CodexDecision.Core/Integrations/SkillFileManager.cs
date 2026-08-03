using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexDecision.Core.Integrations;

public sealed record SkillDraft(string Name, string Description, string Instructions);

public sealed class SkillFileManager
{
    private static readonly Regex SkillNamePattern = new(
        "^[a-z0-9][a-z0-9-]{0,63}$",
        RegexOptions.CultureInvariant);
    private readonly string userSkillsRoot;
    private readonly string legacySkillsRoot;
    private readonly string trashRoot;

    public SkillFileManager(
        string? userSkillsRoot = null,
        string? legacySkillsRoot = null,
        string? trashRoot = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(profile, ".codex");
        }

        this.userSkillsRoot = Path.GetFullPath(userSkillsRoot ?? Path.Combine(profile, ".agents", "skills"));
        this.legacySkillsRoot = Path.GetFullPath(legacySkillsRoot ?? Path.Combine(codexHome, "skills"));
        this.trashRoot = Path.GetFullPath(trashRoot ?? Path.Combine(codexHome, "trash", "skills"));
    }

    public bool CanEdit(string skillPath)
    {
        if (string.IsNullOrWhiteSpace(skillPath) || !Path.IsPathFullyQualified(skillPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(skillPath);
        var directory = Path.GetDirectoryName(fullPath);
        return string.Equals(Path.GetFileName(fullPath), "SKILL.md", StringComparison.OrdinalIgnoreCase) &&
               directory is not null &&
               (IsEditablePersonalDirectory(directory, userSkillsRoot) ||
                IsEditablePersonalDirectory(directory, legacySkillsRoot));
    }

    public async Task<string> SaveAsync(
        SkillDraft draft,
        string? existingPath = null,
        CancellationToken cancellationToken = default)
    {
        Validate(draft);
        string targetPath;
        if (string.IsNullOrWhiteSpace(existingPath))
        {
            targetPath = Path.Combine(userSkillsRoot, draft.Name.Trim(), "SKILL.md");
            if (File.Exists(targetPath))
            {
                throw new InvalidOperationException($"A personal skill named '{draft.Name.Trim()}' already exists.");
            }
        }
        else
        {
            targetPath = Path.GetFullPath(existingPath);
            if (!CanEdit(targetPath))
            {
                throw new InvalidOperationException("Only personal skill files can be edited here.");
            }
        }

        var directory = Path.GetDirectoryName(targetPath)
                        ?? throw new InvalidOperationException("The skill folder could not be resolved.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".SKILL.{Guid.NewGuid():N}.tmp");
        var content = $"---{Environment.NewLine}" +
                      $"name: {JsonSerializer.Serialize(draft.Name.Trim())}{Environment.NewLine}" +
                      $"description: {JsonSerializer.Serialize(draft.Description.Trim())}{Environment.NewLine}" +
                      $"---{Environment.NewLine}{Environment.NewLine}" +
                      $"{draft.Instructions.Trim()}{Environment.NewLine}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return targetPath;
    }

    public async Task<string> ReadInstructionsAsync(
        string skillPath,
        CancellationToken cancellationToken = default)
    {
        if (!CanEdit(skillPath) || !File.Exists(skillPath))
        {
            throw new InvalidOperationException("The personal skill file is unavailable.");
        }

        var content = await File.ReadAllTextAsync(skillPath, cancellationToken);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            return content.Trim();
        }

        for (var index = 1; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].Trim(), "---", StringComparison.Ordinal))
            {
                return string.Join(Environment.NewLine, lines.Skip(index + 1)).Trim();
            }
        }

        return content.Trim();
    }

    public Task<string> MoveToTrashAsync(
        string skillPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanEdit(skillPath) || !File.Exists(skillPath))
        {
            throw new InvalidOperationException("Only an existing personal skill can be removed.");
        }

        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(skillPath))
                              ?? throw new InvalidOperationException("The skill folder could not be resolved.");
        Directory.CreateDirectory(trashRoot);
        var destination = Path.Combine(
            trashRoot,
            $"{Path.GetFileName(sourceDirectory)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.Move(sourceDirectory, destination);
        return Task.FromResult(destination);
    }

    public static void Validate(SkillDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Name) || !SkillNamePattern.IsMatch(draft.Name.Trim()))
        {
            throw new ArgumentException(
                "Skill names must use lowercase letters, numbers, or hyphens and be at most 64 characters.",
                nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.Description))
        {
            throw new ArgumentException("Describe when Codex should use this skill.", nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.Instructions))
        {
            throw new ArgumentException("Add at least one instruction for the skill.", nameof(draft));
        }
    }

    private static bool IsStrictlyBelow(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, ".", StringComparison.Ordinal) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsEditablePersonalDirectory(string candidate, string root)
    {
        if (!IsStrictlyBelow(candidate, root))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, candidate);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return !firstSegment.StartsWith(".", StringComparison.Ordinal);
    }
}
