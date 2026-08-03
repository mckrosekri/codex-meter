using System.Threading.Channels;
using CodexDecision.Core.AppServer;

namespace CodexDecision.Core.Tests;

internal sealed class FakeAppServerTransport : IAppServerTransport
{
    private readonly Channel<string> inbound = Channel.CreateUnbounded<string>();
    private readonly Channel<string> outbound = Channel.CreateUnbounded<string>();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default)
    {
        return inbound.Reader.ReadAllAsync(cancellationToken);
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        return outbound.Writer.WriteAsync(line, cancellationToken).AsTask();
    }

    public ValueTask<string> ReadOutboundAsync(CancellationToken cancellationToken = default)
    {
        return outbound.Reader.ReadAsync(cancellationToken);
    }

    public ValueTask SendInboundAsync(string line, CancellationToken cancellationToken = default)
    {
        return inbound.Writer.WriteAsync(line, cancellationToken);
    }

    public void CompleteInbound(Exception? error = null)
    {
        inbound.Writer.TryComplete(error);
    }

    public ValueTask DisposeAsync()
    {
        inbound.Writer.TryComplete();
        outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
