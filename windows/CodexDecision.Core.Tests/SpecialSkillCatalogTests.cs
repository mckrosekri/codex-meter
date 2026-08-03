using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Tests;

public sealed class SpecialSkillCatalogTests
{
    [Fact]
    public void PromptMasterHasStableCodexDefinitionAndNormalization()
    {
        var definition = SpecialSkillCatalog.Get(SpecialSkillId.PromptMasterCodex);

        Assert.Equal("prompt-master-codex", definition.Slug);
        Assert.Equal("Prompt Master", definition.DisplayName);
        Assert.EndsWith(Path.Combine("PromptMasterCodex", "SKILL.md"), definition.RelativeSkillPath);
        Assert.Equal(
            [SpecialSkillId.PromptMasterCodex],
            SpecialSkillCatalog.Normalize(
                [SpecialSkillId.PromptMasterCodex, SpecialSkillId.PromptMasterCodex]));
    }
}
