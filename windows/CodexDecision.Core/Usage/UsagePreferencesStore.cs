using System.Text.Json;

namespace CodexDecision.Core.Usage;

public sealed class UsagePreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public UsagePreferencesStore(string? path = null)
    {
        FilePath = path ?? DefaultPath();
    }

    public string FilePath { get; }

    public async Task<UsagePreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8 * 1024,
                useAsync: true);
            var value = await JsonSerializer.DeserializeAsync<UsagePreferences>(stream, JsonOptions, cancellationToken);
            return Normalize(value);
        }
        catch (FileNotFoundException)
        {
            return UsagePreferences.Default;
        }
        catch (DirectoryNotFoundException)
        {
            return UsagePreferences.Default;
        }
        catch (JsonException)
        {
            return UsagePreferences.Default;
        }
    }

    public async Task SaveAsync(UsagePreferences value, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(value);
        var directory = Path.GetDirectoryName(FilePath)
                        ?? throw new InvalidOperationException("The usage preference path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 8 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static UsagePreferences Normalize(UsagePreferences? value)
    {
        return new UsagePreferences(
            UsageCycles.NormalizeBillingDay(value?.BillingDay),
            UsagePricing.NormalizeBasis(value?.PricingBasis));
    }

    private static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "CodexDecision", "usage-preferences-v1.json");
    }
}
