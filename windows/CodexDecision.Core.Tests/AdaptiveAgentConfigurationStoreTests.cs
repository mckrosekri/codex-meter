using CodexDecision.Core.AppServer;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.Tests;

public sealed class AdaptiveAgentConfigurationStoreTests
{
    [Fact]
    public async Task WritesNamespacedAgentConfigsThatInheritParentPermissions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"adaptive-agents-{Guid.NewGuid():N}");
        try
        {
            var models = new[]
            {
                new ModelOption("gpt-5.6-sol", "Sol", true, "medium", ["low", "medium", "high"]),
                new ModelOption("gpt-5.6-terra", "Terra", false, "medium", ["low", "medium", "high"]),
                new ModelOption("gpt-5.6-luna", "Luna", false, "low", ["low", "medium"]),
            };
            var store = new AdaptiveAgentConfigurationStore(directory);

            var configuration = await store.EnsureAsync(AdaptiveAgentCatalog.Create(models));

            Assert.Equal(4, configuration.Agents.Count);
            Assert.All(configuration.Agents, agent =>
            {
                Assert.StartsWith(Path.GetFullPath(directory), agent.ConfigPath, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(agent.ConfigPath));
            });
            var explorer = Assert.Single(configuration.Agents, agent => agent.RoleName == "decision_explorer");
            var content = await File.ReadAllTextAsync(explorer.ConfigPath);
            Assert.Contains("model = \"gpt-5.6-luna\"", content, StringComparison.Ordinal);
            Assert.Contains("model_reasoning_effort = \"low\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("sandbox", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("approval", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
