using System.Text.Json;
using CodexDecision.Core.Usage;

namespace CodexDecision.Core.Tests;

public sealed class CodexUsageStoreTests
{
    [Fact]
    public async Task AggregatesExactCumulativeDeltasModelsDaysAndCurrentLimitsWithoutPromptData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-usage-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var sessionDirectory = Path.Combine(codexHome, "sessions", "2026", "07", "14");
        var cachePath = Path.Combine(root, "cache", "usage.json");
        Directory.CreateDirectory(sessionDirectory);
        var sessionPath = Path.Combine(sessionDirectory, "rollout-test.jsonl");
        try
        {
            var first = Tokens(100, 40, 20, 5, 120);
            var second = Tokens(250, 100, 50, 15, 300);
            await File.WriteAllLinesAsync(sessionPath,
            [
                SessionMeta("2026-07-14T08:00:00.000Z", "session-1"),
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-07-14T08:00:01.000Z",
                    type = "event_msg",
                    payload = new { type = "user_message", message = "secret prompt" },
                }),
                TurnContext("2026-07-14T08:00:30.000Z", "gpt-5.4"),
                TokenEvent("2026-07-14T08:01:00.000Z", first, first, 20),
                TokenEvent("2026-07-14T08:01:01.000Z", first, first, 21),
                TokenEvent("2026-07-14T08:02:00.000Z", second, Tokens(150, 60, 30, 10, 180), 22),
            ]);

            var store = Store(codexHome, cachePath);
            var snapshot = await store.RefreshAsync();

            Assert.Equal(300, snapshot.Activity.AllTime.TotalTokens);
            Assert.Equal(250, snapshot.Activity.AllTime.InputTokens);
            Assert.Equal(100, snapshot.Activity.AllTime.CachedInputTokens);
            Assert.Equal(50, snapshot.Activity.AllTime.OutputTokens);
            Assert.Equal(2, snapshot.Activity.AllTime.Requests);
            var day = Assert.Single(snapshot.Activity.Daily);
            Assert.Equal(300, day.Totals.TotalTokens);
            Assert.Equal(300, day.Totals.Models["gpt-5.4"].TotalTokens);
            Assert.Equal(2, snapshot.Limits.Count);
            Assert.Equal(22, snapshot.Limits[0].UsedPercent);
            Assert.Equal(55, snapshot.Limits[1].UsedPercent);
            Assert.Equal("pro", snapshot.Account.Plan);
            Assert.DoesNotContain("secret prompt", JsonSerializer.Serialize(snapshot));
            Assert.DoesNotContain("secret prompt", await File.ReadAllTextAsync(cachePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadsTheCacheAndAddsOnlyAppendedCumulativeUsage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-usage-cache-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var sessionDirectory = Path.Combine(codexHome, "sessions");
        var cachePath = Path.Combine(root, "cache", "usage.json");
        Directory.CreateDirectory(sessionDirectory);
        var sessionPath = Path.Combine(sessionDirectory, "rollout-test.jsonl");
        try
        {
            var first = Tokens(80, 20, 20, 4, 100);
            await File.WriteAllLinesAsync(sessionPath,
            [
                SessionMeta("2026-07-14T08:00:00.000Z", "session-2"),
                TurnContext("2026-07-14T08:00:30.000Z", "gpt-5.4"),
                TokenEvent("2026-07-14T08:01:00.000Z", first, first, 20),
            ]);

            Assert.Equal(100, (await Store(codexHome, cachePath).RefreshAsync()).Activity.AllTime.TotalTokens);

            var second = Tokens(150, 40, 50, 10, 200);
            await File.AppendAllLinesAsync(sessionPath,
            [
                TurnContext("2026-07-14T08:01:30.000Z", "gpt-5.6-sol"),
                TokenEvent("2026-07-14T08:02:00.000Z", second, Tokens(70, 20, 30, 6, 100), 35),
            ]);

            var snapshot = await Store(codexHome, cachePath).RefreshAsync();
            Assert.Equal(200, snapshot.Activity.AllTime.TotalTokens);
            Assert.Equal(2, snapshot.Activity.AllTime.Requests);
            Assert.Equal(100, snapshot.Activity.AllTime.Models["gpt-5.4"].TotalTokens);
            Assert.Equal(100, snapshot.Activity.AllTime.Models["gpt-5.6-sol"].TotalTokens);
            Assert.Equal(35, snapshot.Limits[0].UsedPercent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreferencesRoundTripWithNormalizedValues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-usage-preferences-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "preferences.json");
        try
        {
            var store = new UsagePreferencesStore(path);
            await store.SaveAsync(new UsagePreferences(23, "gpt-5.4"));
            Assert.Equal(new UsagePreferences(23, "gpt-5.4"), await store.LoadAsync());

            await store.SaveAsync(new UsagePreferences(32, "future-model"));
            Assert.Equal(UsagePreferences.Default, await store.LoadAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NullAndNonObjectTelemetryValuesAreIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-usage-null-values-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var sessionDirectory = Path.Combine(codexHome, "sessions", "2026", "07", "14");
        var sessionPath = Path.Combine(sessionDirectory, "rollout-null-values.jsonl");
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllLinesAsync(sessionPath,
            [
                "null",
                "[]",
                SessionMeta("2026-07-14T08:00:00.000Z", "null-values"),
                JsonSerializer.Serialize(new { timestamp = "2026-07-14T08:00:30.000Z", type = "event_msg", payload = new { type = "token_count", rate_limits = (object?)null, info = (object?)null } }),
                TokenEvent("2026-07-14T08:01:00.000Z", Tokens(70, 20, 30, 6, 100), Tokens(70, 20, 30, 6, 100), 25),
            ]);

            var snapshot = await Store(codexHome, Path.Combine(root, "cache.json")).RefreshAsync();

            Assert.Equal(UsageScanPhase.Ready, snapshot.Source.Scan.Phase);
            Assert.Equal(100, snapshot.Activity.AllTime.TotalTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RealLocalTelemetryCanBeIndexedWhenIntegrationIsEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_USAGE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"codex-usage-integration-{Guid.NewGuid():N}");
        try
        {
            var store = new CodexUsageStore(new CodexUsageStoreOptions(
                CachePath: Path.Combine(root, "cache.json"),
                Now: () => DateTimeOffset.Now));

            var snapshot = await store.RefreshAsync();

            Assert.True(
                snapshot.Source.Scan.Phase is UsageScanPhase.Ready,
                snapshot.Source.Scan.Error ?? $"Unexpected scan phase: {snapshot.Source.Scan.Phase}");
            Assert.True(snapshot.Source.FilesIndexed > 0);
            Assert.True(snapshot.Activity.AllTime.TotalTokens > 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CodexUsageStore Store(string codexHome, string cachePath)
    {
        return new CodexUsageStore(new CodexUsageStoreOptions(
            codexHome,
            cachePath,
            () => DateTimeOffset.Parse("2026-07-14T12:00:00.000Z"),
            "test"));
    }

    private static string SessionMeta(string timestamp, string sessionId)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp,
            type = "session_meta",
            payload = new { type = "session_meta", session_id = sessionId, timestamp },
        });
    }

    private static string TurnContext(string timestamp, string model)
    {
        return JsonSerializer.Serialize(new { timestamp, type = "turn_context", payload = new { model } });
    }

    private static string TokenEvent(string timestamp, object total, object last, double usedPercent)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new { total_token_usage = total, last_token_usage = last, model_context_window = 200_000 },
                rate_limits = new
                {
                    limit_id = "codex",
                    primary = new { used_percent = usedPercent, window_minutes = 300, resets_at = 1_800_000_000 },
                    secondary = new { used_percent = 55, window_minutes = 10_080, resets_at = 1_800_100_000 },
                    plan_type = "pro",
                    credits = (object?)null,
                },
            },
        });
    }

    private static object Tokens(long input, long cached, long output, long reasoning, long total)
    {
        return new
        {
            input_tokens = input,
            cached_input_tokens = cached,
            output_tokens = output,
            reasoning_output_tokens = reasoning,
            total_tokens = total,
        };
    }
}
