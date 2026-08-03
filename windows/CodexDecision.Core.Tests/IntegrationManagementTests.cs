using System.Text.Json;
using CodexDecision.Core.Integrations;

namespace CodexDecision.Core.Tests;

public sealed class IntegrationManagementTests
{
    [Fact]
    public void ParsesTypedSkillAndPluginInventories()
    {
        using var skillsDocument = JsonDocument.Parse("""
            {
              "data": [{
                "cwd": "C:\\code",
                "skills": [{
                  "name": "weekly-status",
                  "description": "Prepare a weekly update.",
                  "enabled": true,
                  "path": "C:\\Users\\me\\.agents\\skills\\weekly-status\\SKILL.md",
                  "scope": "user",
                  "interface": { "displayName": "Weekly Status" }
                }],
                "errors": []
              }]
            }
            """);
        var skills = IntegrationInventoryParser.ParseSkills(skillsDocument.RootElement);

        var skill = Assert.Single(skills);
        Assert.Equal("weekly-status", skill.Name);
        Assert.Equal("Weekly Status", skill.DisplayName);
        Assert.True(skill.Enabled);

        using var pluginsDocument = JsonDocument.Parse("""
            {
              "marketplaces": [{
                "name": "curated",
                "path": "C:\\marketplace",
                "interface": { "displayName": "Curated" },
                "plugins": [{
                  "id": "calendar@curated",
                  "name": "calendar",
                  "installed": false,
                  "enabled": false,
                  "installPolicy": "AVAILABLE",
                  "availability": "AVAILABLE",
                  "version": "1.2.3",
                  "source": { "type": "local", "path": "C:\\marketplace\\calendar" },
                  "interface": { "displayName": "Calendar", "shortDescription": "Work with events." }
                }]
              }]
            }
            """);
        var plugins = IntegrationInventoryParser.ParsePlugins(pluginsDocument.RootElement);

        var plugin = Assert.Single(plugins);
        Assert.Equal("calendar@curated", plugin.Id);
        Assert.Equal("Calendar", plugin.DisplayName);
        Assert.False(plugin.Installed);
        Assert.Equal("Curated", plugin.MarketplaceDisplayName);
    }

    [Fact]
    public async Task McpManagerBuildsNativeCliArgumentsAndDetectsProtectedSettings()
    {
        var runner = new FakeCodexCommandRunner("""
            [
              {
                "name": "docs",
                "enabled": true,
                "disabled_reason": null,
                "transport": {
                  "type": "streamable_http",
                  "url": "https://example.com/mcp",
                  "bearer_token_env_var": "DOCS_TOKEN",
                  "http_headers": null,
                  "env_http_headers": null
                },
                "startup_timeout_sec": null,
                "tool_timeout_sec": null,
                "auth_status": "bearer_token"
              },
              {
                "name": "local",
                "enabled": true,
                "disabled_reason": null,
                "transport": {
                  "type": "stdio",
                  "command": "npx",
                  "args": ["-y", "server"],
                  "env": { "MODE": "test" },
                  "env_vars": [],
                  "cwd": null
                },
                "startup_timeout_sec": 30,
                "tool_timeout_sec": null,
                "auth_status": "unsupported"
              }
            ]
            """);
        var service = new CodexCliIntegrationService(runner);

        var servers = await service.ListMcpServersAsync();
        Assert.Equal(2, servers.Count);
        Assert.False(servers.Single(server => server.Name == "docs").HasAdvancedSettings);
        Assert.True(servers.Single(server => server.Name == "local").HasAdvancedSettings);

        await service.AddOrUpdateMcpServerAsync(new McpServerDraft(
            "new-docs",
            McpTransportType.StreamableHttp,
            "https://docs.example/mcp",
            null,
            [],
            "DOCS_TOKEN"));
        Assert.Equal(
            ["mcp", "add", "new-docs", "--url", "https://docs.example/mcp", "--bearer-token-env-var", "DOCS_TOKEN"],
            runner.Calls[^1]);

        await service.AddOrUpdateMcpServerAsync(new McpServerDraft(
            "stdio-server",
            McpTransportType.Stdio,
            null,
            "npx",
            ["-y", "package"],
            null));
        Assert.Equal(
            ["mcp", "add", "stdio-server", "--", "npx", "-y", "package"],
            runner.Calls[^1]);

        await service.RemoveMcpServerAsync("stdio-server");
        Assert.Equal(["mcp", "remove", "stdio-server"], runner.Calls[^1]);
    }

    [Fact]
    public async Task PersonalSkillSaveIsAtomicAndRemovalMovesWholeFolderToTrash()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"codex-skill-manager-{Guid.NewGuid():N}");
        var personalRoot = Path.Combine(testRoot, "personal");
        var legacyRoot = Path.Combine(testRoot, "legacy");
        var trashRoot = Path.Combine(testRoot, "trash");
        try
        {
            var manager = new SkillFileManager(personalRoot, legacyRoot, trashRoot);
            var path = await manager.SaveAsync(new SkillDraft(
                "weekly-status",
                "Use for weekly project updates.",
                "1. Read the notes.\n2. Summarize progress and risks."));

            Assert.True(File.Exists(path));
            Assert.True(manager.CanEdit(path));
            Assert.Contains("Summarize progress", await manager.ReadInstructionsAsync(path));
            Assert.False(manager.CanEdit(Path.Combine(personalRoot, "SKILL.md")));
            Assert.False(manager.CanEdit(Path.Combine(legacyRoot, ".system", "built-in", "SKILL.md")));

            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "template.md"), "template");
            var trashed = await manager.MoveToTrashAsync(path);
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
            Assert.True(File.Exists(Path.Combine(trashed, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(trashed, "template.md")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsUnsafeNamesAndInvalidHttpServers()
    {
        Assert.Throws<ArgumentException>(() => SkillFileManager.Validate(new SkillDraft(
            "../escape",
            "description",
            "instructions")));
        Assert.Throws<ArgumentException>(() => CodexCliIntegrationService.Validate(new McpServerDraft(
            "docs",
            McpTransportType.StreamableHttp,
            "file:///local/path",
            null,
            [],
            null)));
    }

    private sealed class FakeCodexCommandRunner(string listResponse) : ICodexCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<string> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(
                arguments.SequenceEqual(new[] { "mcp", "list", "--json" })
                    ? listResponse
                    : string.Empty);
        }
    }
}
