using System.Text.Json;
using CodexDecision.Core.AgentBackends;
using CodexDecision.Core.AppServer;

namespace CodexDecision.Core.Tests;

public sealed class CodexConnectionSupervisorTests
{
    [Fact]
    public async Task ReconnectsAfterEofWithoutReplayingRequests()
    {
        var first = new FakeAppServerTransport();
        var second = new FakeAppServerTransport();
        var transports = new Queue<FakeAppServerTransport>([first, second]);
        await using var supervisor = new CodexConnectionSupervisor(
            () => new CodexAppServerClient(transports.Dequeue()),
            [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var start = supervisor.StartAsync(timeout.Token);
        await CompleteHandshakeAsync(first, timeout.Token);
        await start;
        Assert.Equal(1, supervisor.Snapshot.Generation);

        first.CompleteInbound();
        using var initialize = JsonDocument.Parse(await second.ReadOutboundAsync(timeout.Token));
        var id = initialize.RootElement.GetProperty("id").GetInt64();
        await second.SendInboundAsync(JsonSerializer.Serialize(new { id, result = new { } }), timeout.Token);
        using var initialized = JsonDocument.Parse(await second.ReadOutboundAsync(timeout.Token));

        while (supervisor.Snapshot.Generation < 2)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal(AgentBackendState.Ready, supervisor.Snapshot.State);
        Assert.Equal("openai-codex", supervisor.Descriptor.Id);
        Assert.True(supervisor.Descriptor.Supports(AgentBackendCapabilities.PersistentThreads));
        Assert.Equal(2, supervisor.Snapshot.Generation);
    }

    private static async Task CompleteHandshakeAsync(
        FakeAppServerTransport transport,
        CancellationToken cancellationToken)
    {
        using var initialize = JsonDocument.Parse(await transport.ReadOutboundAsync(cancellationToken));
        var id = initialize.RootElement.GetProperty("id").GetInt64();
        await transport.SendInboundAsync(JsonSerializer.Serialize(new { id, result = new { } }), cancellationToken);
        using var initialized = JsonDocument.Parse(await transport.ReadOutboundAsync(cancellationToken));
    }
}
