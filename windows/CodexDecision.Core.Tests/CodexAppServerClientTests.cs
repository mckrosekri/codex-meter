using System.Text.Json;
using CodexDecision.Core.AppServer;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task LockedThreadDisablesProviderFallbackAndTurnCarriesResolvedPermission()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startClient = client.StartAsync(timeout.Token);
        using (var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            var id = initialize.RootElement.GetProperty("id").GetInt64();
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new { id, result = new { } }),
                timeout.Token);
        }
        await startClient;
        using (var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());
        }

        var route = new RoutingDecision(
            "gpt-5.6-terra",
            "medium",
            PermissionLevel.WorkspaceWrite,
            RoutingSource.TaskLock,
            RoutingSource.TaskLock,
            RoutingSource.Auto,
            TaskComplexity.Standard,
            "Test route");
        var adaptiveConfiguration = new AppServerThreadConfiguration(
        [
            new AppServerAgentDefinition(
                "decision_explorer",
                "Read-heavy exploration.",
                @"C:\skills\adaptive\decision_explorer.toml",
                ["Scout"]),
        ]);

        var startThread = client.StartThreadAsync(
            @"C:\code\sample",
            route,
            modelIsLocked: true,
            timeout.Token,
            adaptiveConfiguration);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("thread/start", request.RootElement.GetProperty("method").GetString());
            var parameters = request.RootElement.GetProperty("params");
            Assert.False(parameters.GetProperty("allowProviderModelFallback").GetBoolean());
            Assert.Equal("workspace-write", parameters.GetProperty("sandbox").GetString());
            AssertAdaptiveConfig(parameters);
            var id = request.RootElement.GetProperty("id").GetInt64();
            await transport.SendInboundAsync(ThreadResponse(id), timeout.Token);
        }
        var thread = await startThread;
        Assert.Equal("thread-1", thread.Thread.Id);

        var resumeThread = client.ResumeThreadAsync(
            "thread-1",
            @"C:\code\sample",
            timeout.Token,
            adaptiveConfiguration);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("thread/resume", request.RootElement.GetProperty("method").GetString());
            AssertAdaptiveConfig(request.RootElement.GetProperty("params"));
            var id = request.RootElement.GetProperty("id").GetInt64();
            await transport.SendInboundAsync(ThreadResponse(id), timeout.Token);
        }
        await resumeThread;

        var startTurn = client.StartTurnAsync(
            "thread-1",
            "Implement the settings page.",
            @"C:\code\sample",
            route,
            timeout.Token,
            [
                new AppServerSkillReference("prompt-master-codex", @"C:\skills\prompt-master-codex\SKILL.md"),
                new AppServerSkillReference("adaptive-routing-codex", @"C:\skills\adaptive-routing-codex\SKILL.md"),
            ]);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("turn/start", request.RootElement.GetProperty("method").GetString());
            var parameters = request.RootElement.GetProperty("params");
            Assert.Equal("gpt-5.6-terra", parameters.GetProperty("model").GetString());
            Assert.Equal("medium", parameters.GetProperty("effort").GetString());
            Assert.Equal(
                "workspaceWrite",
                parameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
            Assert.False(parameters.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
            Assert.Equal(
                "Implement the settings page.",
                parameters.GetProperty("input")[0].GetProperty("text").GetString());
            Assert.Equal(3, parameters.GetProperty("input").GetArrayLength());
            Assert.Equal("skill", parameters.GetProperty("input")[1].GetProperty("type").GetString());
            Assert.Equal("prompt-master-codex", parameters.GetProperty("input")[1].GetProperty("name").GetString());
            Assert.Equal(
                @"C:\skills\prompt-master-codex\SKILL.md",
                parameters.GetProperty("input")[1].GetProperty("path").GetString());
            Assert.Equal("skill", parameters.GetProperty("input")[2].GetProperty("type").GetString());
            Assert.Equal("adaptive-routing-codex", parameters.GetProperty("input")[2].GetProperty("name").GetString());
            var id = request.RootElement.GetProperty("id").GetInt64();
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id,
                    result = new
                    {
                        turn = new { id = "turn-1", items = Array.Empty<object>(), status = "inProgress" },
                    },
                }),
                timeout.Token);
        }
        var turn = await startTurn;
        Assert.Equal("turn-1", turn.Turn.Id);
    }

    [Fact]
    public async Task SteerTargetsTheExpectedActiveTurnWithoutStartingAnotherTurn()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startClient = client.StartAsync(timeout.Token);
        using (var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            var id = initialize.RootElement.GetProperty("id").GetInt64();
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new { id, result = new { } }),
                timeout.Token);
        }
        await startClient;
        using (var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());
        }

        var steer = client.SteerTurnAsync(
            "thread-1",
            "turn-active",
            "Keep the current API and add a regression test.",
            timeout.Token,
            [new AppServerSkillReference("prompt-master-codex", @"C:\skills\prompt-master-codex\SKILL.md")]);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("turn/steer", request.RootElement.GetProperty("method").GetString());
            var parameters = request.RootElement.GetProperty("params");
            Assert.Equal("thread-1", parameters.GetProperty("threadId").GetString());
            Assert.Equal("turn-active", parameters.GetProperty("expectedTurnId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(parameters.GetProperty("clientUserMessageId").GetString()));
            Assert.Equal(
                "Keep the current API and add a regression test.",
                parameters.GetProperty("input")[0].GetProperty("text").GetString());
            Assert.Equal("skill", parameters.GetProperty("input")[1].GetProperty("type").GetString());
            Assert.Equal("prompt-master-codex", parameters.GetProperty("input")[1].GetProperty("name").GetString());
            Assert.False(parameters.TryGetProperty("model", out _));
            Assert.False(parameters.TryGetProperty("sandboxPolicy", out _));
            var id = request.RootElement.GetProperty("id").GetInt64();
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new { id, result = new { turnId = "turn-active" } }),
                timeout.Token);
        }

        var response = await steer;
        Assert.Equal("turn-active", response.TurnId);
    }

    [Fact]
    public async Task UsesTypedAttachmentForkAndCompactionProtocolShapes()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startClient = client.StartAsync(timeout.Token);
        using (var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id = initialize.RootElement.GetProperty("id").GetInt64(),
                    result = new { },
                }),
                timeout.Token);
        }
        await startClient;
        using var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));

        var route = new RoutingDecision(
            "gpt-5.6-terra", "medium", PermissionLevel.ReadOnly,
            RoutingSource.Auto, RoutingSource.Auto, RoutingSource.Auto,
            TaskComplexity.Standard, "test");
        var startTurn = client.StartTurnAsync(
            "thread-1",
            "Review attachments",
            @"C:\code\sample",
            route,
            timeout.Token,
            attachments:
            [
                new AppServerAttachment(@"C:\code\sample\shot.png"),
                new AppServerAttachment(@"C:\code\sample\notes.txt", "notes.txt"),
            ]);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            var input = request.RootElement.GetProperty("params").GetProperty("input");
            Assert.Equal("localImage", input[1].GetProperty("type").GetString());
            Assert.Equal("mention", input[2].GetProperty("type").GetString());
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id = request.RootElement.GetProperty("id").GetInt64(),
                    result = new { turn = new { id = "turn-1", items = Array.Empty<object>(), status = "inProgress" } },
                }),
                timeout.Token);
        }
        await startTurn;

        var fork = client.ForkThreadAsync("thread-1", @"C:\code\sample", "turn-1", timeout.Token);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("thread/fork", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("turn-1", request.RootElement.GetProperty("params").GetProperty("lastTurnId").GetString());
            await transport.SendInboundAsync(
                ThreadResponse(request.RootElement.GetProperty("id").GetInt64()),
                timeout.Token);
        }
        await fork;

        var compact = client.StartCompactionAsync("thread-1", timeout.Token);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("thread/compact/start", request.RootElement.GetProperty("method").GetString());
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new { id = request.RootElement.GetProperty("id").GetInt64(), result = new { } }),
                timeout.Token);
        }
        await compact;
    }

    [Fact]
    public async Task ThreadListCarriesCursorArchiveAndStableSortOptions()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startClient = client.StartAsync(timeout.Token);
        using (var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id = initialize.RootElement.GetProperty("id").GetInt64(),
                    result = new { },
                }),
                timeout.Token);
        }
        await startClient;
        using var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));

        var list = client.ListThreadsAsync(
            @"C:\code\sample",
            archived: true,
            cursor: "page-2",
            limit: 40,
            cancellationToken: timeout.Token);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            Assert.Equal("thread/list", request.RootElement.GetProperty("method").GetString());
            var parameters = request.RootElement.GetProperty("params");
            Assert.Equal("page-2", parameters.GetProperty("cursor").GetString());
            Assert.Equal(40, parameters.GetProperty("limit").GetInt32());
            Assert.True(parameters.GetProperty("archived").GetBoolean());
            Assert.Equal(@"C:\code\sample", parameters.GetProperty("cwd").GetString());
            Assert.Equal("updated_at", parameters.GetProperty("sortKey").GetString());
            Assert.Equal("desc", parameters.GetProperty("sortDirection").GetString());
            Assert.True(parameters.GetProperty("useStateDbOnly").GetBoolean());
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id = request.RootElement.GetProperty("id").GetInt64(),
                    result = new { data = Array.Empty<object>(), nextCursor = (string?)null },
                }),
                timeout.Token);
        }

        Assert.Empty((await list).Data);
    }

    [Fact]
    public async Task IntegrationMutationsUseNativeAppServerMethods()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startClient = client.StartAsync(timeout.Token);
        using (var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id = initialize.RootElement.GetProperty("id").GetInt64(),
                    result = new { },
                }),
                timeout.Token);
        }
        await startClient;
        using var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));

        var skillWrite = client.SetSkillEnabledAsync(@"C:\skills\weekly\SKILL.md", false, timeout.Token);
        await AssertRequestAsync(
            transport,
            "skills/config/write",
            parameters =>
            {
                Assert.Equal(@"C:\skills\weekly\SKILL.md", parameters.GetProperty("path").GetString());
                Assert.False(parameters.GetProperty("enabled").GetBoolean());
            },
            timeout.Token);
        await skillWrite;

        var install = client.InstallPluginAsync("calendar", @"C:\marketplace", cancellationToken: timeout.Token);
        await AssertRequestAsync(
            transport,
            "plugin/install",
            parameters =>
            {
                Assert.Equal("calendar", parameters.GetProperty("pluginName").GetString());
                Assert.Equal(@"C:\marketplace", parameters.GetProperty("marketplacePath").GetString());
            },
            timeout.Token);
        await install;

        var uninstall = client.UninstallPluginAsync("calendar@curated", timeout.Token);
        await AssertRequestAsync(
            transport,
            "plugin/uninstall",
            parameters => Assert.Equal("calendar@curated", parameters.GetProperty("pluginId").GetString()),
            timeout.Token);
        await uninstall;
    }

    [Fact]
    public async Task UnattendedTurnUsesNeverApprovalPolicy()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startClient = client.StartAsync(timeout.Token);
        using (var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id = initialize.RootElement.GetProperty("id").GetInt64(),
                    result = new { },
                }),
                timeout.Token);
        }
        await startClient;
        using var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));

        var route = new RoutingDecision(
            "gpt-5.6-terra", "medium", PermissionLevel.ReadOnly,
            RoutingSource.Auto, RoutingSource.Auto, RoutingSource.Auto,
            TaskComplexity.Standard, "scheduled monitor");
        var startTurn = client.StartTurnAsync(
            "thread-1",
            "Check current status.",
            @"C:\code\sample",
            route,
            timeout.Token,
            unattended: true);
        using (var request = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token)))
        {
            var parameters = request.RootElement.GetProperty("params");
            Assert.Equal("never", parameters.GetProperty("approvalPolicy").GetString());
            await transport.SendInboundAsync(
                JsonSerializer.Serialize(new
                {
                    id = request.RootElement.GetProperty("id").GetInt64(),
                    result = new
                    {
                        turn = new { id = "turn-monitor", items = Array.Empty<object>(), status = "inProgress" },
                    },
                }),
                timeout.Token);
        }
        await startTurn;
    }

    [Fact]
    public async Task RealAppServerListsModelsWhenIntegrationIsEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_CODEX_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        await using var client = new CodexAppServerClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.StartAsync(timeout.Token);
        var response = await client.ListModelsAsync(timeout.Token);

        Assert.NotEmpty(response.Data);
        Assert.Contains(response.Data, model => model.IsDefault);
        Assert.All(response.Data.Where(model => !model.Hidden), model => Assert.NotEmpty(model.Model));
    }

    [Fact]
    public async Task RealAppServerAcceptsAdaptiveAgentConfigurationWhenIntegrationIsEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_CODEX_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"codex-adaptive-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var client = new CodexAppServerClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.StartAsync(timeout.Token);
            var models = (await client.ListModelsAsync(timeout.Token)).Data
                .Where(model => !model.Hidden)
                .ToArray();
            Assert.NotEmpty(models);
            var selected = models.FirstOrDefault(model => model.IsDefault) ?? models[0];
            var configuration = await new AdaptiveAgentConfigurationStore(Path.Combine(directory, "agents"))
                .EnsureAsync(AdaptiveAgentCatalog.Create(models.Select(model => model.ToRoutingOption()).ToArray()), timeout.Token);
            var route = new RoutingDecision(
                selected.Model,
                selected.DefaultReasoningEffort,
                PermissionLevel.ReadOnly,
                RoutingSource.Auto,
                RoutingSource.Auto,
                RoutingSource.Auto,
                TaskComplexity.Standard,
                "Integration route");

            var thread = await client.StartThreadAsync(
                Directory.GetCurrentDirectory(),
                route,
                modelIsLocked: false,
                timeout.Token,
                configuration);

            Assert.False(string.IsNullOrWhiteSpace(thread.Thread.Id));
            Assert.Equal(selected.Model, thread.Model);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string ThreadResponse(long id)
    {
        return JsonSerializer.Serialize(new
        {
            id,
            result = new
            {
                thread = new
                {
                    id = "thread-1",
                    preview = string.Empty,
                    name = (string?)null,
                    cwd = @"C:\code\sample",
                    createdAt = 1,
                    updatedAt = 1,
                    status = "idle",
                    turns = Array.Empty<object>(),
                },
                model = "gpt-5.6-terra",
                reasoningEffort = "medium",
            },
        });
    }

    private static async Task AssertRequestAsync(
        FakeAppServerTransport transport,
        string method,
        Action<JsonElement> assertParameters,
        CancellationToken cancellationToken)
    {
        using var request = JsonDocument.Parse(await transport.ReadOutboundAsync(cancellationToken));
        Assert.Equal(method, request.RootElement.GetProperty("method").GetString());
        assertParameters(request.RootElement.GetProperty("params"));
        await transport.SendInboundAsync(
            JsonSerializer.Serialize(new
            {
                id = request.RootElement.GetProperty("id").GetInt64(),
                result = new { },
            }),
            cancellationToken);
    }

    private static void AssertAdaptiveConfig(JsonElement parameters)
    {
        var config = parameters.GetProperty("config");
        Assert.True(config.GetProperty("features").GetProperty("multi_agent").GetBoolean());
        var explorer = config.GetProperty("agents").GetProperty("decision_explorer");
        Assert.Equal("Read-heavy exploration.", explorer.GetProperty("description").GetString());
        Assert.Equal(
            @"C:\skills\adaptive\decision_explorer.toml",
            explorer.GetProperty("config_file").GetString());
        Assert.Equal("Scout", explorer.GetProperty("nickname_candidates")[0].GetString());
    }
}
