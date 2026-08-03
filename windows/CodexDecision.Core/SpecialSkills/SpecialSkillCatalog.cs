namespace CodexDecision.Core.SpecialSkills;

public enum SpecialSkillId
{
    PromptMasterCodex,
}

public sealed record SpecialSkillDefinition(
    SpecialSkillId Id,
    string Slug,
    string DisplayName,
    string Description,
    string RelativeSkillPath,
    string SourceUrl);

public static class SpecialSkillCatalog
{
    private static readonly IReadOnlyList<SpecialSkillDefinition> Definitions =
    [
        new(
            SpecialSkillId.PromptMasterCodex,
            "prompt-master-codex",
            "Prompt Master",
            "Reinterprets informal requests as precise principal-engineering briefs for OpenAI Codex.",
            Path.Combine("SpecialSkills", "PromptMasterCodex", "SKILL.md"),
            "https://github.com/nidhinjs/prompt-master"),
    ];

    public static IReadOnlyList<SpecialSkillDefinition> All => Definitions;

    public static SpecialSkillDefinition Get(SpecialSkillId id)
    {
        return Definitions.FirstOrDefault(definition => definition.Id == id)
               ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown special skill.");
    }

    public static IReadOnlyList<SpecialSkillId> Normalize(IEnumerable<SpecialSkillId>? values)
    {
        if (values is null)
        {
            return [];
        }

        var known = Definitions.Select(definition => definition.Id).ToHashSet();
        return values
            .Where(known.Contains)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
    }
}
