using System.Text.Json;

namespace CodexDecision.Core.Integrations;

public sealed record SkillIntegration(
    string Name,
    string DisplayName,
    string Description,
    string Path,
    string Scope,
    bool Enabled);

public sealed record PluginIntegration(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    string MarketplaceName,
    string MarketplaceDisplayName,
    string? MarketplacePath,
    string? Version,
    bool Installed,
    bool Enabled,
    string InstallPolicy,
    string Availability,
    string? Source);

public enum McpTransportType
{
    Stdio,
    StreamableHttp,
}

public sealed record McpServerIntegration(
    string Name,
    bool Enabled,
    string? DisabledReason,
    McpTransportType Transport,
    string? Url,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? BearerTokenEnvironmentVariable,
    string AuthStatus,
    bool HasAdvancedSettings)
{
    public string Endpoint => Transport == McpTransportType.StreamableHttp
        ? Url ?? string.Empty
        : string.Join(" ", new[] { Command }.Concat(Arguments).Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record McpServerDraft(
    string Name,
    McpTransportType Transport,
    string? Url,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? BearerTokenEnvironmentVariable);

public static class IntegrationInventoryParser
{
    public static IReadOnlyList<SkillIntegration> ParseSkills(JsonElement root)
    {
        var skills = new Dictionary<string, SkillIntegration>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetArray(root, "data", out var entries))
        {
            return [];
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryGetArray(entry, "skills", out var inventory))
            {
                continue;
            }

            foreach (var item in inventory.EnumerateArray())
            {
                var path = ReadString(item, "path");
                var name = ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var interfaceElement = ReadObject(item, "interface");
                var displayName = interfaceElement is { } skillInterface
                    ? ReadString(skillInterface, "displayName")
                    : null;
                skills[path] = new SkillIntegration(
                    name,
                    string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                    ReadString(item, "description") ?? string.Empty,
                    path,
                    ReadString(item, "scope") ?? "unknown",
                    ReadBoolean(item, "enabled", defaultValue: true));
            }
        }

        return skills.Values
            .OrderByDescending(skill => skill.Enabled)
            .ThenBy(skill => skill.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<PluginIntegration> ParsePlugins(JsonElement root)
    {
        if (!TryGetArray(root, "marketplaces", out var marketplaces))
        {
            return [];
        }

        var plugins = new List<PluginIntegration>();
        foreach (var marketplace in marketplaces.EnumerateArray())
        {
            var marketplaceName = ReadString(marketplace, "name") ?? "unknown";
            var marketplacePath = ReadString(marketplace, "path");
            var marketplaceInterface = ReadObject(marketplace, "interface");
            var marketplaceDisplayName = marketplaceInterface is { } interfaceValue
                ? ReadString(interfaceValue, "displayName")
                : null;
            if (!TryGetArray(marketplace, "plugins", out var marketplacePlugins))
            {
                continue;
            }

            foreach (var item in marketplacePlugins.EnumerateArray())
            {
                var id = ReadString(item, "id");
                var name = ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var pluginInterface = ReadObject(item, "interface");
                var displayName = pluginInterface is { } metadata
                    ? ReadString(metadata, "displayName")
                    : null;
                var description = pluginInterface is { } descriptionMetadata
                    ? ReadString(descriptionMetadata, "shortDescription")
                      ?? ReadString(descriptionMetadata, "longDescription")
                    : null;
                var source = ReadObject(item, "source");
                plugins.Add(new PluginIntegration(
                    id,
                    name,
                    string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                    description ?? string.Empty,
                    marketplaceName,
                    string.IsNullOrWhiteSpace(marketplaceDisplayName) ? marketplaceName : marketplaceDisplayName,
                    marketplacePath,
                    ReadString(item, "localVersion") ?? ReadString(item, "version"),
                    ReadBoolean(item, "installed"),
                    ReadBoolean(item, "enabled"),
                    ReadString(item, "installPolicy") ?? "NOT_AVAILABLE",
                    ReadString(item, "availability") ?? "AVAILABLE",
                    source is { } sourceValue
                        ? ReadString(sourceValue, "path")
                          ?? ReadString(sourceValue, "url")
                          ?? ReadString(sourceValue, "package")
                        : null));
            }
        }

        return plugins
            .GroupBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(plugin => plugin.Installed).First())
            .OrderByDescending(plugin => plugin.Installed)
            .ThenBy(plugin => plugin.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static JsonElement? ReadObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

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
