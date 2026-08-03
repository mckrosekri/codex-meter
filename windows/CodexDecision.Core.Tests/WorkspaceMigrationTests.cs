using CodexDecision.Core.Projects;

namespace CodexDecision.Core.Tests;

public sealed class WorkspaceMigrationTests
{
    [Fact]
    public async Task MigratesLegacyFileToV2AndLeavesLegacyRollbackFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"workspace-migration-{Guid.NewGuid():N}");
        var legacy = Path.Combine(directory, "workspace-v1.json");
        var current = Path.Combine(directory, "workspace-v2.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(legacy, """{"version":1,"projects":[],"followUpBehavior":"queue"}""");
            var state = await new WorkspaceStore(current, legacy).LoadAsync();

            Assert.Equal(2, state.Version);
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(legacy));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
