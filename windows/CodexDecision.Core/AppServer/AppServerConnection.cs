using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexDecision.Core.AppServer;

public sealed record AppServerEvent(string Method, JsonElement Parameters);

public sealed record AppServerRequest(JsonElement Id, string Method, JsonElement Parameters);

public sealed class AppServerRpcException(int code, string message)
    : InvalidOperationException($"Codex app-server error {code}: {message}")
{
    public int Code { get; } = code;
}

public sealed class AppServerConnection(IAppServerTransport transport) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private long nextRequestId;
    private Task? readPump;
    private bool started;

    public event EventHandler<AppServerEvent>? NotificationReceived;

    public event EventHandler<AppServerRequest>? ServerRequestReceived;

    public event EventHandler<Exception>? ProtocolError;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            return;
        }

        await transport.StartAsync(cancellationToken);
        readPump = Task.Run(() => ReadPumpAsync(lifetime.Token), CancellationToken.None);
        try
        {
            await RequestAsync<JsonElement>(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "codex_decision_windows",
                        title = "Codex Decision for Windows",
                        version = "0.5.0",
                    },
                    capabilities = new { experimentalApi = true },
                },
                cancellationToken);
            await NotifyAsync("initialized", cancellationToken);
            started = true;
        }
        catch
        {
            lifetime.Cancel();
            throw;
        }
    }

    public async Task<T> RequestAsync<T>(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Unable to register the app-server request.");
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["method"] = method,
                ["id"] = id,
                ["params"] = parameters,
            };
            await transport.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
            using var registration = cancellationToken.Register(() =>
            {
                if (pending.TryRemove(id, out var active))
                {
                    active.TrySetCanceled(cancellationToken);
                }
            });
            var result = await completion.Task;
            if (typeof(T) == typeof(JsonElement))
            {
                return (T)(object)result;
            }

            return result.Deserialize<T>(JsonOptions)
                   ?? throw new JsonException($"The '{method}' response was empty.");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, CancellationToken cancellationToken = default)
    {
        return transport.WriteLineAsync(
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["method"] = method }, JsonOptions),
            cancellationToken);
    }

    public Task RespondAsync(
        JsonElement id,
        object? result,
        CancellationToken cancellationToken = default)
    {
        return transport.WriteLineAsync(
            JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["id"] = id.Clone(), ["result"] = result },
                JsonOptions),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (readPump is not null)
        {
            try
            {
                await readPump;
            }
            catch (OperationCanceledException)
            {
            }
        }

        FailPending(new ObjectDisposedException(nameof(AppServerConnection)));
        await transport.DisposeAsync();
        lifetime.Dispose();
    }

    private async Task ReadPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in transport.ReadLinesAsync(cancellationToken))
            {
                try
                {
                    Dispatch(line);
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                {
                    ProtocolError?.Invoke(this, error);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                var error = new EndOfStreamException("Codex app-server closed its output stream.");
                FailPending(error);
                ProtocolError?.Invoke(this, error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            FailPending(error);
            ProtocolError?.Invoke(this, error);
        }
    }

    private void Dispatch(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var hasMethod = root.TryGetProperty("method", out var methodElement);
        var hasId = root.TryGetProperty("id", out var idElement);

        if (hasMethod)
        {
            var method = methodElement.GetString()
                         ?? throw new JsonException("An app-server method was not a string.");
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : EmptyObject();
            if (hasId)
            {
                ServerRequestReceived?.Invoke(
                    this,
                    new AppServerRequest(idElement.Clone(), method, parameters));
            }
            else
            {
                NotificationReceived?.Invoke(this, new AppServerEvent(method, parameters));
            }

            return;
        }

        if (!hasId || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id))
        {
            throw new JsonException("An app-server response did not contain a numeric request id.");
        }

        if (!pending.TryRemove(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("error", out var errorElement))
        {
            var code = errorElement.TryGetProperty("code", out var codeElement)
                ? codeElement.GetInt32()
                : -1;
            var message = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "Unknown error"
                : "Unknown error";
            completion.TrySetException(new AppServerRpcException(code, message));
            return;
        }

        var result = root.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : JsonDocument.Parse("null").RootElement.Clone();
        completion.TrySetResult(result);
    }

    private void FailPending(Exception error)
    {
        foreach (var request in pending.ToArray())
        {
            if (pending.TryRemove(request.Key, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private static JsonElement EmptyObject()
    {
        return JsonDocument.Parse("{}").RootElement.Clone();
    }
}
