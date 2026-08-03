using CodexDecision.Core.Projects;

namespace CodexDecision.Core.Tests;

public sealed class NativeCodexWorkspaceCatalogTests
{
    [Fact]
    public async Task ReadsSavedRootsNamedProjectsAssignmentsAndProjectlessHints()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var saved = Path.Combine(root, "saved");
        var named = Path.Combine(root, "named");
        var assigned = Path.Combine(root, "assigned");
        var projectless = Path.Combine(root, "projectless");
        var statePath = Path.Combine(root, "global-state.json");
        try
        {
            var json = $$"""
            {
              "electron-saved-workspace-roots": ["{{Escape(saved)}}"],
              "local-projects": {
                "local-1": { "name": "Named project" }
              },
              "project-writable-roots": {
                "local-1": [{ "kind": "local", "path": "{{Escape(named)}}" }]
              },
              "thread-project-assignments": {
                "assigned-thread": { "path": "{{Escape(assigned)}}" }
              },
              "thread-workspace-root-hints": {
                "projectless-thread": "{{Escape(projectless)}}"
              }
            }
            """;
            await File.WriteAllTextAsync(statePath, json);

            var snapshot = await new NativeCodexWorkspaceCatalog(statePath).LoadAsync();

            Assert.Contains(snapshot.Projects, project => project.Folder == Path.GetFullPath(saved));
            Assert.Contains(snapshot.Projects, project =>
                project.Name == "Named project" && project.Folder == Path.GetFullPath(named));
            Assert.Equal(Path.GetFullPath(assigned), snapshot.ResolveProjectDirectory("assigned-thread", saved));
            Assert.Equal(
                Path.GetFullPath(projectless),
                snapshot.ResolveProjectDirectory("projectless-thread", Path.Combine(root, "output")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);
}
