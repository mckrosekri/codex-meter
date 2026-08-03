namespace CodexDecision.Core.AppServer;

public interface IAppServerTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default);

    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);
}
