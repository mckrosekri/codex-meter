using System.Text.Json;
using CodexDecision.Core.AppServer;

namespace CodexDecision.Core.Tests;

public sealed class AppServerConnectionTests
{
    [Fact]
    public async Task PerformsRequiredInitializeHandshake()
    {
        var transport = new FakeAppServerTransport();
        await using var connection = new AppServerConnection(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var start = connection.StartAsync(timeout.Token);
        using var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));
        Assert.Equal("initialize", initialize.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "codex_decision_windows",
            initialize.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.True(
            initialize.RootElement.GetProperty("params").GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean());

        var id = initialize.RootElement.GetProperty("id").GetInt64();
        await transport.SendInboundAsync(
            JsonSerializer.Serialize(new { id, result = new { userAgent = "test" } }),
            timeout.Token);
        await start;

        using var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());
        Assert.False(initialized.RootElement.TryGetProperty("params", out _));
    }

    [Fact]
    public async Task DispatchesNotificationsAndServerRequests()
    {
        var transport = new FakeAppServerTransport();
        await using var connection = new AppServerConnection(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await CompleteHandshakeAsync(connection, transport, timeout.Token);

        var notificationReceived = new TaskCompletionSource<AppServerEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestReceived = new TaskCompletionSource<AppServerRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (_, value) => notificationReceived.TrySetResult(value);
        connection.ServerRequestReceived += (_, value) => requestReceived.TrySetResult(value);

        await transport.SendInboundAsync(
            """{"method":"item/agentMessage/delta","params":{"delta":"hello"}}""",
            timeout.Token);
        await transport.SendInboundAsync(
            """{"method":"item/commandExecution/requestApproval","id":"approval-1","params":{"command":"npm test"}}""",
            timeout.Token);

        var notification = await notificationReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal("item/agentMessage/delta", notification.Method);
        Assert.Equal("hello", notification.Parameters.GetProperty("delta").GetString());

        var request = await requestReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal("item/commandExecution/requestApproval", request.Method);
        Assert.Equal("approval-1", request.Id.GetString());
        await connection.RespondAsync(request.Id, new { decision = "decline" }, timeout.Token);

        using var response = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));
        Assert.Equal("approval-1", response.RootElement.GetProperty("id").GetString());
        Assert.Equal("decline", response.RootElement.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task SurfacesStructuredRpcErrors()
    {
        var transport = new FakeAppServerTransport();
        await using var connection = new AppServerConnection(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await CompleteHandshakeAsync(connection, transport, timeout.Token);

        var request = connection.RequestAsync<JsonElement>("model/list", new { }, timeout.Token);
        using var outbound = JsonDocument.Parse(await transport.ReadOutboundAsync(timeout.Token));
        var id = outbound.RootElement.GetProperty("id").GetInt64();
        await transport.SendInboundAsync(
            JsonSerializer.Serialize(new
            {
                id,
                error = new { code = -32001, message = "Server overloaded; retry later." },
            }),
            timeout.Token);

        var error = await Assert.ThrowsAsync<AppServerRpcException>(() => request);
        Assert.Equal(-32001, error.Code);
        Assert.Contains("overloaded", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task CompleteHandshakeAsync(
        AppServerConnection connection,
        FakeAppServerTransport transport,
        CancellationToken cancellationToken)
    {
        var start = connection.StartAsync(cancellationToken);
        using var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(cancellationToken));
        var id = initialize.RootElement.GetProperty("id").GetInt64();
        await transport.SendInboundAsync(
            JsonSerializer.Serialize(new { id, result = new { } }),
            cancellationToken);
        await start;
        using var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(cancellationToken));
    }
}
