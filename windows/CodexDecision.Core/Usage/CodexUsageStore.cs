using System.Text;
using System.Text.Json;

namespace CodexDecision.Core.Usage;

public sealed record CodexUsageStoreOptions(
    string? CodexHome = null,
    string? CachePath = null,
    Func<DateTimeOffset>? Now = null,
    string? SourceLabel = null);

public sealed class CodexUsageStore
{
    private const int CacheVersion = 2;
    private const int RecentFileCount = 16;
    private const int TailBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string codexHome;
    private readonly string cachePath;
    private readonly string sourceLabel;
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<string, UsageFileRecord> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RateEntry> quickRates = new(StringComparer.OrdinalIgnoreCase);
    private bool cacheLoaded;
    private UsageScanStatus scanStatus = new(
        UsageScanPhase.Starting,
        0,
        0,
        0,
        0,
        null,
        null,
        null);

    public CodexUsageStore(CodexUsageStoreOptions? options = null)
    {
        options ??= new CodexUsageStoreOptions();
        var configuredHome = options.CodexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(configuredHome);
        cachePath = options.CachePath ?? DefaultCachePath();
        sourceLabel = options.SourceLabel ?? (string.IsNullOrWhiteSpace(configuredHome) ? "~/.codex" : "Custom CODEX_HOME");
        now = options.Now ?? (() => DateTimeOffset.Now);
    }

    public async Task<UsageSnapshot> RefreshAsync(
        bool force = false,
        Action<UsageSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await LoadCacheAsync(cancellationToken);
            var discovered = DiscoverFiles();
            await PrimeRecentRatesAsync(discovered, cancellationToken);

            var existing = discovered.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cachedPath in files.Keys.Where(path => !existing.Contains(path)).ToArray())
            {
                files.Remove(cachedPath);
            }

            scanStatus = new UsageScanStatus(
                UsageScanPhase.Indexing,
                discovered.Count,
                0,
                discovered.Sum(entry => entry.Size),
                0,
                now(),
                null,
                null);
            progress?.Invoke(BuildSnapshot());

            try
            {
                for (var index = 0; index < discovered.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = discovered[index];
                    files.TryGetValue(entry.Path, out var cached);
                    var unchanged = !force &&
                                    cached is not null &&
                                    cached.Size == entry.Size &&
                                    cached.ModifiedUtcTicks == entry.ModifiedUtcTicks;
                    if (!unchanged)
                    {
                        files[entry.Path] = await ParseFileAsync(entry, cached, force, cancellationToken);
                    }

                    scanStatus = scanStatus with
                    {
                        FilesProcessed = index + 1,
                        BytesProcessed = scanStatus.BytesProcessed + entry.Size,
                    };
                    if ((index + 1) % 10 == 0 || index == discovered.Count - 1)
                    {
                        progress?.Invoke(BuildSnapshot());
                    }

                    if ((index + 1) % 25 == 0)
                    {
                        await SaveCacheAsync(cancellationToken);
                    }
                }

                await SaveCacheAsync(cancellationToken);
                scanStatus = scanStatus with
                {
                    Phase = UsageScanPhase.Ready,
                    CompletedAt = now(),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                scanStatus = scanStatus with
                {
                    Phase = UsageScanPhase.Error,
                    Error = error.Message,
                    CompletedAt = now(),
                };
            }

            var snapshot = BuildSnapshot();
            progress?.Invoke(snapshot);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private List<UsageFileEntry> DiscoverFiles()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootName in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(codexHome, rootName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    paths.Add(path);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A session directory can move while discovery is running.
            }
        }

        var output = new List<UsageFileEntry>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    output.Add(new UsageFileEntry(path, info.Length, info.LastWriteTimeUtc.Ticks));
                }
            }
            catch (IOException)
            {
                // A session can be archived between enumeration and metadata lookup.
            }
        }

        return output
            .OrderByDescending(entry => entry.ModifiedUtcTicks)
            .ToList();
    }

    private async Task PrimeRecentRatesAsync(
        IReadOnlyList<UsageFileEntry> discovered,
        CancellationToken cancellationToken)
    {
        quickRates.Clear();
        foreach (var entry in discovered.Take(RecentFileCount))
        {
            try
            {
                var latest = await ReadLatestRateFromTailAsync(entry.Path, cancellationToken);
                if (latest is null)
                {
                    continue;
                }

                var key = latest.Rates.LimitId;
                if (!quickRates.TryGetValue(key, out var existing) || latest.Timestamp > existing.Timestamp)
                {
                    quickRates[key] = latest;
                }
            }
            catch (IOException)
            {
                // The full index pass retries files that are still being written or moved.
            }
        }
    }

    private static async Task<UsageFileRecord> ParseFileAsync(
        UsageFileEntry entry,
        UsageFileRecord? cached,
        bool force,
        CancellationToken cancellationToken)
    {
        var canAppend = !force &&
                        cached is not null &&
                        cached.Offset > 0 &&
                        cached.Offset <= entry.Size &&
                        cached.Size < entry.Size;
        var record = canAppend ? cached! : UsageFileRecord.Create(entry.Path);
        var start = canAppend ? cached!.Offset : 0;

        if (start < entry.Size)
        {
            await using var stream = new FileStream(
                entry.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: start == 0);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                ProcessTelemetryLine(line, record);
            }
        }

        record.Path = entry.Path;
        record.Size = entry.Size;
        record.Offset = entry.Size;
        record.ModifiedUtcTicks = entry.ModifiedUtcTicks;
        record.SessionId ??= Path.GetFileNameWithoutExtension(entry.Path);
        return record;
    }

    private static void ProcessTelemetryLine(string line, UsageFileRecord record)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return;
            }

            var type = ReadString(root, "type");
            if (type == "session_meta")
            {
                if (root.TryGetProperty("payload", out var payload))
                {
                    record.SessionId ??= ReadString(payload, "session_id") ?? ReadString(payload, "id");
                    record.StartedAt ??= ReadTimestamp(payload, "timestamp") ?? ReadTimestamp(root, "timestamp");
                }

                return;
            }

            if (type == "turn_context")
            {
                if (root.TryGetProperty("payload", out var payload))
                {
                    record.CurrentModel = NormalizeModel(ReadString(payload, "model"));
                }

                return;
            }

            if (!root.TryGetProperty("payload", out var eventPayload) ||
                ReadString(eventPayload, "type") != "token_count")
            {
                return;
            }

            var timestamp = ReadTimestamp(root, "timestamp");
            if (timestamp is { } observed)
            {
                record.LastEventAt = observed;
            }

            if (timestamp is { } rateTimestamp &&
                eventPayload.TryGetProperty("rate_limits", out var rateLimits) &&
                TryReadRatePayload(rateLimits, out var parsedRate))
            {
                record.LatestRateLimits = parsedRate;
                record.LatestRateTimestamp = rateTimestamp;
            }

            if (!eventPayload.TryGetProperty("info", out var info))
            {
                return;
            }

            var cumulative = TryReadTokenUsage(info, "total_token_usage");
            var last = TryReadTokenUsage(info, "last_token_usage");
            if (cumulative is null && last is null)
            {
                return;
            }

            TokenUsage? delta;
            if (cumulative is not null && record.LastCumulative is not null)
            {
                delta = SubtractTokens(cumulative, record.LastCumulative);
                if (HasNegativeToken(delta))
                {
                    delta = last ?? cumulative;
                }
            }
            else
            {
                delta = cumulative ?? last;
            }

            if (cumulative is not null)
            {
                record.LastCumulative = cumulative;
            }

            if (delta is null || delta.TotalTokens <= 0)
            {
                return;
            }

            record.Totals = UsageCycles.AddTokens(record.Totals, delta);
            AddModelTokens(record.ByModel, record.CurrentModel, delta);
            record.Requests += 1;

            if (timestamp is { } eventTimestamp)
            {
                var date = DateOnly.FromDateTime(eventTimestamp.ToLocalTime().DateTime);
                var key = date.ToString("yyyy-MM-dd");
                if (!record.ByDay.TryGetValue(key, out var day))
                {
                    day = new UsageDayRecord();
                    record.ByDay[key] = day;
                }

                day.Totals = UsageCycles.AddTokens(day.Totals, delta);
                AddModelTokens(day.Models, record.CurrentModel, delta);
                day.Requests += 1;
            }
        }
        catch (JsonException)
        {
            // Ignore an incomplete or malformed telemetry line without reading prompt content.
        }
    }

    private static async Task<RateEntry?> ReadLatestRateFromTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        var length = (int)Math.Min(stream.Length, TailBytes);
        if (length <= 0)
        {
            return null;
        }

        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        var lines = Encoding.UTF8.GetString(buffer, 0, read)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            try
            {
                using var document = JsonDocument.Parse(lines[index]);
                var root = document.RootElement;
                if (root.ValueKind is not JsonValueKind.Object ||
                    !root.TryGetProperty("payload", out var payload) ||
                    ReadString(payload, "type") != "token_count" ||
                    !payload.TryGetProperty("rate_limits", out var rateElement) ||
                    !TryReadRatePayload(rateElement, out var rates) ||
                    ReadTimestamp(root, "timestamp") is not { } timestamp)
                {
                    continue;
                }

                return new RateEntry(rates, timestamp);
            }
            catch (JsonException)
            {
                // A tail can begin in the middle of a JSON line.
            }
        }

        return null;
    }

    private UsageSnapshot BuildSnapshot()
    {
        var generatedAt = now();
        var records = NewestBySession(files.Values);
        var allTime = new MutableAggregate();
        var days = new Dictionary<DateOnly, MutableAggregate>();
        var rateEntries = new Dictionary<string, RateEntry>(quickRates, StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? firstEventAt = null;

        foreach (var record in records)
        {
            allTime.Add(record.Totals, record.Requests, record.ByModel);
            foreach (var (dateKey, day) in record.ByDay)
            {
                if (!DateOnly.TryParseExact(dateKey, "yyyy-MM-dd", out var date))
                {
                    continue;
                }

                if (!days.TryGetValue(date, out var aggregate))
                {
                    aggregate = new MutableAggregate();
                    days[date] = aggregate;
                }

                aggregate.Add(day.Totals, day.Requests, day.Models);
            }

            var start = record.StartedAt ?? record.LastEventAt;
            if (start is not null && (firstEventAt is null || start < firstEventAt))
            {
                firstEventAt = start;
            }

            if (record.LatestRateLimits is not null && record.LatestRateTimestamp is { } timestamp)
            {
                var key = record.LatestRateLimits.LimitId;
                if (!rateEntries.TryGetValue(key, out var existing) || timestamp > existing.Timestamp)
                {
                    rateEntries[key] = new RateEntry(record.LatestRateLimits, timestamp);
                }
            }
        }

        var latestRate = rateEntries.Values.OrderByDescending(entry => entry.Timestamp).FirstOrDefault();
        var daily = days
            .OrderBy(entry => entry.Key)
            .Select(entry => new DailyUsage(entry.Key, entry.Value.ToImmutable()))
            .ToArray();
        return new UsageSnapshot(
            generatedAt,
            new UsageSource(sourceLabel, true, records.Count, firstEventAt, scanStatus),
            new UsageAccount(latestRate?.Rates.PlanType),
            FlattenLimits(rateEntries.Values, generatedAt),
            new UsageActivity(allTime.ToImmutable(), daily),
            "Token totals are exact values recorded in local Codex session telemetry. Plan quota percentages are reported by Codex and may apply model or service-tier weighting that is not exposed as an absolute token allowance.");
    }

    private static IReadOnlyList<UsageFileRecord> NewestBySession(IEnumerable<UsageFileRecord> source)
    {
        var sessions = new Dictionary<string, UsageFileRecord>(StringComparer.Ordinal);
        foreach (var record in source)
        {
            var key = string.IsNullOrWhiteSpace(record.SessionId) ? record.Path : record.SessionId;
            if (!sessions.TryGetValue(key, out var existing))
            {
                sessions[key] = record;
                continue;
            }

            var existingTime = existing.LastEventAt ?? existing.StartedAt ?? DateTimeOffset.MinValue;
            var recordTime = record.LastEventAt ?? record.StartedAt ?? DateTimeOffset.MinValue;
            if (record.Totals.TotalTokens > existing.Totals.TotalTokens || recordTime > existingTime)
            {
                sessions[key] = record;
            }
        }

        return sessions.Values.ToArray();
    }

    private static IReadOnlyList<UsageLimitSnapshot> FlattenLimits(
        IEnumerable<RateEntry> entries,
        DateTimeOffset now)
    {
        var limits = new List<UsageLimitSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderByDescending(value => value.Timestamp))
        {
            foreach (var (slot, window) in new[]
                     {
                         ("primary", entry.Rates.Primary),
                         ("secondary", entry.Rates.Secondary),
                     })
            {
                if (window is null)
                {
                    continue;
                }

                var dedupe = $"{entry.Rates.LimitId}:{window.WindowMinutes}";
                if (!seen.Add(dedupe) || window.ResetsAt is { } reset && reset <= now)
                {
                    continue;
                }

                var used = window.UsedPercent;
                limits.Add(new UsageLimitSnapshot(
                    $"{dedupe}:{slot}",
                    entry.Rates.LimitId,
                    string.IsNullOrWhiteSpace(entry.Rates.LimitName)
                        ? WindowName(window.WindowMinutes)
                        : entry.Rates.LimitName,
                    slot,
                    used,
                    Math.Max(0, 100 - used),
                    window.WindowMinutes,
                    window.ResetsAt,
                    entry.Timestamp,
                    used >= 90 ? UsageLimitState.Critical : used >= 70 ? UsageLimitState.Warning : UsageLimitState.Normal));
            }
        }

        return limits.OrderBy(limit => limit.WindowMinutes).ToArray();
    }

    private async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (cacheLoaded)
        {
            return;
        }

        cacheLoaded = true;
        try
        {
            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            var cache = await JsonSerializer.DeserializeAsync<UsageCache>(stream, JsonOptions, cancellationToken);
            if (cache?.Version != CacheVersion || cache.Files is null)
            {
                return;
            }

            foreach (var (path, record) in cache.Files)
            {
                files[path] = record;
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cachePath)
                        ?? throw new InvalidOperationException("The usage cache path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new UsageCache(CacheVersion, now(), new Dictionary<string, UsageFileRecord>(files)),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static TokenUsage? TryReadTokenUsage(JsonElement parent, string property)
    {
        if (parent.ValueKind is not JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value) ||
            value.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return new TokenUsage(
            ReadLong(value, "input_tokens"),
            ReadLong(value, "cached_input_tokens"),
            ReadLong(value, "output_tokens"),
            ReadLong(value, "reasoning_output_tokens"),
            ReadLong(value, "total_tokens"));
    }

    private static TokenUsage SubtractTokens(TokenUsage current, TokenUsage previous)
    {
        return new TokenUsage(
            current.InputTokens - previous.InputTokens,
            current.CachedInputTokens - previous.CachedInputTokens,
            current.OutputTokens - previous.OutputTokens,
            current.ReasoningOutputTokens - previous.ReasoningOutputTokens,
            current.TotalTokens - previous.TotalTokens);
    }

    private static bool HasNegativeToken(TokenUsage value)
    {
        return value.InputTokens < 0 ||
               value.CachedInputTokens < 0 ||
               value.OutputTokens < 0 ||
               value.ReasoningOutputTokens < 0 ||
               value.TotalTokens < 0;
    }

    private static void AddModelTokens(Dictionary<string, TokenUsage> target, string? model, TokenUsage tokens)
    {
        var key = NormalizeModel(model);
        target[key] = UsageCycles.AddTokens(target.GetValueOrDefault(key, TokenUsage.Empty), tokens);
    }

    private static string NormalizeModel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();
    }

    private static bool TryReadRatePayload(JsonElement element, out RateLimitPayload payload)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            payload = null!;
            return false;
        }

        var id = ReadString(element, "limit_id") ?? "codex";
        var primary = TryReadRateWindow(element, "primary");
        var secondary = TryReadRateWindow(element, "secondary");
        if (primary is null && secondary is null)
        {
            payload = null!;
            return false;
        }

        payload = new RateLimitPayload
        {
            LimitId = id,
            LimitName = ReadString(element, "limit_name"),
            PlanType = ReadString(element, "plan_type"),
            Primary = primary,
            Secondary = secondary,
        };
        return true;
    }

    private static RateWindow? TryReadRateWindow(JsonElement parent, string property)
    {
        if (parent.ValueKind is not JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value) ||
            value.ValueKind is not JsonValueKind.Object ||
            !TryReadDouble(value, "used_percent", out var used))
        {
            return null;
        }

        var minutes = TryReadDouble(value, "window_minutes", out var window) ? (int)window : 0;
        DateTimeOffset? reset = null;
        if (TryReadDouble(value, "resets_at", out var rawReset))
        {
            reset = rawReset < 1_000_000_000_000d
                ? DateTimeOffset.FromUnixTimeSeconds((long)rawReset)
                : DateTimeOffset.FromUnixTimeMilliseconds((long)rawReset);
        }

        return new RateWindow
        {
            UsedPercent = used,
            WindowMinutes = minutes,
            ResetsAt = reset,
        };
    }

    private static string WindowName(int minutes)
    {
        if (minutes == 300) return "5-hour limit";
        if (minutes == 1_440) return "Daily limit";
        if (minutes == 10_080) return "Weekly limit";
        if (minutes > 0 && minutes % 1_440 == 0) return $"{minutes / 1_440}-day limit";
        if (minutes > 0 && minutes % 60 == 0) return $"{minutes / 60}-hour limit";
        return minutes > 0 ? $"{minutes}-minute limit" : "Usage limit";
    }

    private static string? ReadString(JsonElement parent, string property)
    {
        return parent.ValueKind is JsonValueKind.Object &&
               parent.TryGetProperty(property, out var value) &&
               value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement parent, string property)
    {
        return ReadString(parent, property) is { } value && DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : null;
    }

    private static long ReadLong(JsonElement parent, string property)
    {
        if (parent.ValueKind is not JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value))
        {
            return 0;
        }

        if (value.TryGetInt64(out var integer))
        {
            return integer;
        }

        return value.TryGetDouble(out var number) && double.IsFinite(number) ? (long)number : 0;
    }

    private static bool TryReadDouble(JsonElement parent, string property, out double value)
    {
        value = 0;
        return parent.ValueKind is JsonValueKind.Object &&
               parent.TryGetProperty(property, out var element) &&
               element.TryGetDouble(out value);
    }

    private static string DefaultCachePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "CodexDecision", "usage-cache-v2.json");
    }

    private sealed record UsageFileEntry(string Path, long Size, long ModifiedUtcTicks);

    private sealed record RateEntry(RateLimitPayload Rates, DateTimeOffset Timestamp);

    private sealed record UsageCache(
        int Version,
        DateTimeOffset SavedAt,
        Dictionary<string, UsageFileRecord> Files);

    private sealed class MutableAggregate
    {
        private TokenUsage tokens = TokenUsage.Empty;
        private long requests;
        private readonly Dictionary<string, TokenUsage> models = new(StringComparer.OrdinalIgnoreCase);

        public void Add(TokenUsage sourceTokens, long sourceRequests, IReadOnlyDictionary<string, TokenUsage> sourceModels)
        {
            tokens = UsageCycles.AddTokens(tokens, sourceTokens);
            requests += sourceRequests;
            foreach (var (model, value) in sourceModels)
            {
                models[model] = UsageCycles.AddTokens(models.GetValueOrDefault(model, TokenUsage.Empty), value);
            }
        }

        public UsageAggregate ToImmutable()
        {
            return new UsageAggregate(tokens, requests, new Dictionary<string, TokenUsage>(models));
        }
    }

    private sealed class UsageFileRecord
    {
        public string Path { get; set; } = string.Empty;

        public long Size { get; set; }

        public long ModifiedUtcTicks { get; set; }

        public long Offset { get; set; }

        public string? SessionId { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? LastEventAt { get; set; }

        public TokenUsage Totals { get; set; } = TokenUsage.Empty;

        public Dictionary<string, TokenUsage> ByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public long Requests { get; set; }

        public Dictionary<string, UsageDayRecord> ByDay { get; set; } = [];

        public string? CurrentModel { get; set; }

        public TokenUsage? LastCumulative { get; set; }

        public RateLimitPayload? LatestRateLimits { get; set; }

        public DateTimeOffset? LatestRateTimestamp { get; set; }

        public static UsageFileRecord Create(string path) => new() { Path = path };
    }

    private sealed class UsageDayRecord
    {
        public TokenUsage Totals { get; set; } = TokenUsage.Empty;

        public long Requests { get; set; }

        public Dictionary<string, TokenUsage> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RateLimitPayload
    {
        public string LimitId { get; set; } = "codex";

        public string? LimitName { get; set; }

        public string? PlanType { get; set; }

        public RateWindow? Primary { get; set; }

        public RateWindow? Secondary { get; set; }
    }

    private sealed class RateWindow
    {
        public double UsedPercent { get; set; }

        public int WindowMinutes { get; set; }

        public DateTimeOffset? ResetsAt { get; set; }
    }
}
