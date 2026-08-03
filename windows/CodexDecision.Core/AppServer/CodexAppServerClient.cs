using System.Text.Json;
using CodexDecision.Core.Routing;

namespace CodexDecision.Core.AppServer;

public sealed record AppServerSkillReference(string Name, string Path);

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly IAppServerTransport transport;
    private readonly AppServerConnection connection;

    public CodexAppServerClient(IAppServerTransport? transport = null)
    {
        this.transport = transport ?? new ProcessAppServerTransport();
        connection = new AppServerConnection(this.transport);
        connection.NotificationReceived += (_, notification) =>
            NotificationReceived?.Invoke(this, notification);
        connection.ServerRequestReceived += (_, request) =>
            ApprovalRequested?.Invoke(this, request);
        connection.ProtocolError += (_, error) => ProtocolError?.Invoke(this, error);
    }

    public event EventHandler<AppServerEvent>? NotificationReceived;

    public event EventHandler<AppServerRequest>? ApprovalRequested;

    public event EventHandler<Exception>? ProtocolError;

    public string? LastDiagnostic => (transport as ProcessAppServerTransport)?.LastDiagnostic;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return connection.StartAsync(cancellationToken);
    }

    public Task<ModelListResponse> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        return connection.RequestAsync<ModelListResponse>(
            "model/list",
            new { cursor = (string?)null, limit = 100, includeHidden = false },
            cancellationToken);
    }

    public Task<PermissionProfileListResponse> ListPermissionProfilesAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        return connection.RequestAsync<PermissionProfileListResponse>(
            "permissionProfile/list",
            new { cursor = (string?)null, limit = 100, cwd = workingDirectory },
            cancellationToken);
    }

    public Task<JsonElement> ListSkillsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        bool forceReload = false,
        CancellationToken cancellationToken = default) =>
        connection.RequestAsync<JsonElement>(
            "skills/list",
            new { cwds = workingDirectories ?? [], forceReload },
            cancellationToken);

    public Task<JsonElement> ListPluginsAsync(
        IReadOnlyList<string>? workingDirectories = null,
        CancellationToken cancellationToken = default) =>
        connection.RequestAsync<JsonElement>(
            "plugin/list",
            new { cwds = workingDirectories },
            cancellationToken);

    public Task<JsonElement> SetSkillEnabledAsync(
        string path,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return connection.RequestAsync<JsonElement>(
            "skills/config/write",
            new { path, enabled },
            cancellationToken);
    }

    public Task<JsonElement> InstallPluginAsync(
        string pluginName,
        string? marketplacePath = null,
        string? remoteMarketplaceName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        return connection.RequestAsync<JsonElement>(
            "plugin/install",
            new { pluginName, marketplacePath, remoteMarketplaceName },
            cancellationToken);
    }

    public Task<JsonElement> UninstallPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return connection.RequestAsync<JsonElement>(
            "plugin/uninstall",
            new { pluginId },
            cancellationToken);
    }

    public Task<JsonElement> ListMcpServersAsync(CancellationToken cancellationToken = default) =>
        connection.RequestAsync<JsonElement>(
            "mcpServerStatus/list",
            new { cursor = (string?)null, limit = 100, detail = "full", threadId = (string?)null },
            cancellationToken);

    public Task<JsonElement> ReloadMcpServersAsync(CancellationToken cancellationToken = default) =>
        connection.RequestAsync<JsonElement>("config/mcpServer/reload", null, cancellationToken);

    public Task<ThreadListResponse> ListThreadsAsync(
        string? workingDirectory = null,
        bool archived = false,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Thread page size must be between 1 and 100.");
        }

        return connection.RequestAsync<ThreadListResponse>(
            "thread/list",
            new
            {
                cursor,
                limit,
                sortKey = "updated_at",
                sortDirection = "desc",
                archived,
                cwd = workingDirectory,
                useStateDbOnly = true,
            },
            cancellationToken);
    }

    public Task<ThreadOperationResponse> StartThreadAsync(
        string workingDirectory,
        RoutingDecision route,
        bool modelIsLocked,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null)
    {
        return connection.RequestAsync<ThreadOperationResponse>(
            "thread/start",
            new
            {
                model = route.ModelId,
                allowProviderModelFallback = !modelIsLocked,
                cwd = workingDirectory,
                runtimeWorkspaceRoots = new[] { workingDirectory },
                approvalPolicy = "on-request",
                sandbox = SandboxMode(route.Permission),
                ephemeral = false,
                config = BuildThreadConfig(configuration),
            },
            cancellationToken);
    }

    public Task<ThreadOperationResponse> ResumeThreadAsync(
        string threadId,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null)
    {
        return connection.RequestAsync<ThreadOperationResponse>(
            "thread/resume",
            new
            {
                threadId,
                cwd = workingDirectory,
                runtimeWorkspaceRoots = new[] { workingDirectory },
                excludeTurns = false,
                config = BuildThreadConfig(configuration),
            },
            cancellationToken);
    }

    public Task<ThreadOperationResponse> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        return connection.RequestAsync<ThreadOperationResponse>(
            "thread/read",
            new { threadId, includeTurns = true },
            cancellationToken);
    }

    public Task<TurnOperationResponse> StartTurnAsync(
        string threadId,
        string prompt,
        string workingDirectory,
        RoutingDecision route,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null,
        bool unattended = false)
    {
        return connection.RequestAsync<TurnOperationResponse>(
            "turn/start",
            new
            {
                threadId,
                clientUserMessageId = Guid.NewGuid().ToString(),
                input = BuildUserInput(prompt, skills, attachments),
                cwd = workingDirectory,
                runtimeWorkspaceRoots = new[] { workingDirectory },
                approvalPolicy = unattended ? "never" : "on-request",
                sandboxPolicy = SandboxPolicy(route.Permission, workingDirectory),
                model = route.ModelId,
                effort = route.Effort,
                responsesapiClientMetadata = new Dictionary<string, string>
                {
                    ["route_source"] = route.ModelSource.ToString(),
                    ["permission_source"] = route.PermissionSource.ToString(),
                },
            },
            cancellationToken);
    }

    public Task<TurnSteerResponse> SteerTurnAsync(
        string threadId,
        string turnId,
        string prompt,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AppServerSkillReference>? skills = null,
        IReadOnlyList<AppServerAttachment>? attachments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return connection.RequestAsync<TurnSteerResponse>(
            "turn/steer",
            new
            {
                threadId,
                clientUserMessageId = Guid.NewGuid().ToString(),
                input = BuildUserInput(prompt, skills, attachments),
                expectedTurnId = turnId,
            },
            cancellationToken);
    }

    public Task<JsonElement> InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        return connection.RequestAsync<JsonElement>(
            "turn/interrupt",
            new { threadId, turnId },
            cancellationToken);
    }

    public Task<JsonElement> ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        return connection.RequestAsync<JsonElement>(
            "thread/archive",
            new { threadId },
            cancellationToken);
    }

    public Task<JsonElement> SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default)
    {
        return connection.RequestAsync<JsonElement>(
            "thread/name/set",
            new { threadId, name },
            cancellationToken);
    }

    public Task<ThreadOperationResponse> ForkThreadAsync(
        string threadId,
        string workingDirectory,
        string? lastTurnId = null,
        CancellationToken cancellationToken = default,
        AppServerThreadConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return connection.RequestAsync<ThreadOperationResponse>(
            "thread/fork",
            new
            {
                threadId,
                lastTurnId,
                cwd = workingDirectory,
                runtimeWorkspaceRoots = new[] { workingDirectory },
                excludeTurns = false,
                ephemeral = false,
                config = BuildThreadConfig(configuration),
            },
            cancellationToken);
    }

    public Task<JsonElement> StartCompactionAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return connection.RequestAsync<JsonElement>(
            "thread/compact/start",
            new { threadId },
            cancellationToken);
    }

    public Task RespondToApprovalAsync(
        AppServerRequest request,
        object response,
        CancellationToken cancellationToken = default)
    {
        return connection.RespondAsync(request.Id, response, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return connection.DisposeAsync();
    }

    private static IReadOnlyList<object> BuildUserInput(
        string prompt,
        IReadOnlyList<AppServerSkillReference>? skills,
        IReadOnlyList<AppServerAttachment>? attachments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var input = new List<object>
        {
            new { type = "text", text = prompt.Trim(), text_elements = Array.Empty<object>() },
        };
        foreach (var skill in skills ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(skill.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(skill.Path);
            if (!System.IO.Path.IsPathFullyQualified(skill.Path))
            {
                throw new ArgumentException("A special skill path must be absolute.", nameof(skills));
            }

            input.Add(new { type = "skill", name = skill.Name, path = skill.Path });
        }

        foreach (var attachment in attachments ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attachment.Path);
            var path = System.IO.Path.GetFullPath(attachment.Path);
            if (attachment.IsImage)
            {
                input.Add(new { type = "localImage", path, detail = attachment.Detail });
            }
            else
            {
                input.Add(new
                {
                    type = "mention",
                    name = string.IsNullOrWhiteSpace(attachment.DisplayName)
                        ? System.IO.Path.GetFileName(path)
                        : attachment.DisplayName,
                    path,
                });
            }
        }

        return input;
    }

    private static object? BuildThreadConfig(AppServerThreadConfiguration? configuration)
    {
        if (configuration is null || configuration.Agents.Count == 0)
        {
            return null;
        }

        var agents = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var agent in configuration.Agents)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agent.RoleName);
            ArgumentException.ThrowIfNullOrWhiteSpace(agent.Description);
            ArgumentException.ThrowIfNullOrWhiteSpace(agent.ConfigPath);
            if (!Path.IsPathFullyQualified(agent.ConfigPath))
            {
                throw new ArgumentException("An adaptive agent config path must be absolute.", nameof(configuration));
            }

            if (!agents.TryAdd(
                    agent.RoleName,
                    new
                    {
                        description = agent.Description,
                        config_file = agent.ConfigPath,
                        nickname_candidates = agent.NicknameCandidates,
                    }))
            {
                throw new ArgumentException(
                    $"The adaptive agent role '{agent.RoleName}' is registered more than once.",
                    nameof(configuration));
            }
        }

        return new
        {
            features = new Dictionary<string, bool>
            {
                ["multi_agent"] = true,
            },
            agents,
        };
    }

    private static string SandboxMode(PermissionLevel permission)
    {
        return permission switch
        {
            PermissionLevel.ReadOnly => "read-only",
            PermissionLevel.WorkspaceWrite => "workspace-write",
            PermissionLevel.FullAccess => "danger-full-access",
            _ => throw new InvalidOperationException("AutoSafe must be resolved before starting a thread."),
        };
    }

    private static object SandboxPolicy(PermissionLevel permission, string workingDirectory)
    {
        return permission switch
        {
            PermissionLevel.ReadOnly => new { type = "readOnly", networkAccess = false },
            PermissionLevel.WorkspaceWrite => new
            {
                type = "workspaceWrite",
                writableRoots = new[] { workingDirectory },
                networkAccess = false,
                excludeTmpdirEnvVar = false,
                excludeSlashTmp = false,
            },
            PermissionLevel.FullAccess => new { type = "dangerFullAccess" },
            _ => throw new InvalidOperationException("AutoSafe must be resolved before starting a turn."),
        };
    }
}
