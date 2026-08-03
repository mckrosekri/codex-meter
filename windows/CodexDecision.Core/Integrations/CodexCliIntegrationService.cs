using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexDecision.Core.Integrations;

public interface ICodexCommandRunner
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class CodexCliIntegrationService
{
    private static readonly Regex IntegrationNamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant);
    private readonly ICodexCommandRunner runner;

    public CodexCliIntegrationService(ICodexCommandRunner? runner = null)
    {
        this.runner = runner ?? new CodexCommandRunner();
    }

    public async Task<IReadOnlyList<McpServerIntegration>> ListMcpServersAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await runner.RunAsync(["mcp", "list", "--json"], cancellationToken);
        return ParseMcpServers(json);
    }

    public Task AddOrUpdateMcpServerAsync(
        McpServerDraft draft,
        CancellationToken cancellationToken = default)
    {
        Validate(draft);
        var arguments = new List<string> { "mcp", "add", draft.Name.Trim() };
        if (draft.Transport == McpTransportType.StreamableHttp)
        {
            arguments.Add("--url");
            arguments.Add(draft.Url!.Trim());
            if (!string.IsNullOrWhiteSpace(draft.BearerTokenEnvironmentVariable))
            {
                arguments.Add("--bearer-token-env-var");
                arguments.Add(draft.BearerTokenEnvironmentVariable.Trim());
            }
        }
        else
        {
            arguments.Add("--");
            arguments.Add(draft.Command!.Trim());
            arguments.AddRange(draft.Arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)));
        }

        return RunIgnoringOutputAsync(arguments, cancellationToken);
    }

    public Task RemoveMcpServerAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        return RunIgnoringOutputAsync(["mcp", "remove", name.Trim()], cancellationToken);
    }

    public static IReadOnlyList<McpServerIntegration> ParseMcpServers(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var servers = new List<McpServerIntegration>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(name) ||
                !item.TryGetProperty("transport", out var transport) ||
                transport.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ReadString(transport, "type");
            var isHttp = string.Equals(type, "streamable_http", StringComparison.OrdinalIgnoreCase);
            var arguments = ReadStringArray(transport, "args");
            var hasEnvironment = HasObjectEntries(transport, "env") || ReadStringArray(transport, "env_vars").Count > 0;
            var hasHeaders = HasObjectEntries(transport, "http_headers") || HasObjectEntries(transport, "env_http_headers");
            var hasAdvancedSettings = hasEnvironment ||
                                      hasHeaders ||
                                      !string.IsNullOrWhiteSpace(ReadString(transport, "cwd")) ||
                                      HasValue(item, "startup_timeout_sec") ||
                                      HasValue(item, "tool_timeout_sec") ||
                                      HasArrayEntries(item, "enabled_tools") ||
                                      HasArrayEntries(item, "disabled_tools");
            servers.Add(new McpServerIntegration(
                name,
                ReadBoolean(item, "enabled", defaultValue: true),
                ReadString(item, "disabled_reason"),
                isHttp ? McpTransportType.StreamableHttp : McpTransportType.Stdio,
                ReadString(transport, "url"),
                ReadString(transport, "command"),
                arguments,
                ReadString(transport, "bearer_token_env_var"),
                ReadString(item, "auth_status") ?? "unknown",
                hasAdvancedSettings));
        }

        return servers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void Validate(McpServerDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateName(draft.Name);
        if (draft.Transport == McpTransportType.StreamableHttp)
        {
            if (!Uri.TryCreate(draft.Url?.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("Enter a valid HTTP or HTTPS server URL.", nameof(draft));
            }

            if (!string.IsNullOrWhiteSpace(draft.BearerTokenEnvironmentVariable) &&
                !IntegrationNamePattern.IsMatch(draft.BearerTokenEnvironmentVariable.Trim()))
            {
                throw new ArgumentException("The token environment variable contains unsupported characters.", nameof(draft));
            }
        }
        else if (string.IsNullOrWhiteSpace(draft.Command))
        {
            throw new ArgumentException("Enter the command that starts the MCP server.", nameof(draft));
        }
    }

    private async Task RunIgnoringOutputAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        _ = await runner.RunAsync(arguments, cancellationToken);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !IntegrationNamePattern.IsMatch(name.Trim()))
        {
            throw new ArgumentException(
                "Names must start with a letter or number and use only letters, numbers, underscores, or hyphens.",
                nameof(name));
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    private static bool HasObjectEntries(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Any();

    private static bool HasArrayEntries(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() > 0;

    private static bool HasValue(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName, bool defaultValue = false) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;
}

public sealed class CodexCommandRunner : ICodexCommandRunner
{
    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(arguments);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Unable to start the Codex CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? $"Codex CLI exited with code {process.ExitCode}."
                    : message.Trim());
        }

        return output;
    }

    internal static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var binary = ResolveCodexBinary();
        ProcessStartInfo startInfo;
        if (binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            binary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildCmdCommand(binary, arguments));
        }
        else
        {
            startInfo = new ProcessStartInfo(binary);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        return startInfo;
    }

    private static string ResolveCodexBinary()
    {
        foreach (var variable in new[] { "CODEX_BIN", "CODEX_CLI_PATH" })
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var appBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI",
                "Codex",
                "bin");
            if (Directory.Exists(appBin))
            {
                var native = Directory.EnumerateFiles(appBin, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(native))
                {
                    return native;
                }
            }

            return "codex.cmd";
        }

        return "codex";
    }

    private static string BuildCmdCommand(string binary, IReadOnlyList<string> arguments)
    {
        var values = new[] { binary }.Concat(arguments).Select(QuoteForCmd);
        return string.Join(" ", values);
    }

    private static string QuoteForCmd(string value)
    {
        var escaped = value
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
