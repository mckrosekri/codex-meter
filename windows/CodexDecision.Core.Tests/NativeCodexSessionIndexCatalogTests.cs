using CodexDecision.Core.Projects;

namespace CodexDecision.Core.Tests;

public sealed class NativeCodexSessionIndexCatalogTests
{
    [Fact]
    public async Task ReadsLatestValidEntryPerThreadAndSkipsPartialLines()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-session-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var indexPath = Path.Combine(root, "session_index.jsonl");
        try
        {
            await File.WriteAllLinesAsync(indexPath,
            [
                "{\"id\":\"thread-1\",\"thread_name\":\"Earlier\",\"updated_at\":\"2026-08-01T10:00:00Z\"}",
                "{partial",
                "{\"id\":\"thread-2\",\"thread_name\":null,\"updated_at\":\"2026-08-02T10:00:00Z\"}",
                "{\"id\":\"thread-1\",\"thread_name\":\"Latest\",\"updated_at\":\"2026-08-03T10:00:00Z\"}",
            ]);

            var entries = await new NativeCodexSessionIndexCatalog(indexPath).LoadAsync();

            Assert.Equal(2, entries.Count);
            Assert.Equal("thread-1", entries[0].ThreadId);
            Assert.Equal("Latest", entries[0].Title);
            Assert.Equal("Untitled task", entries[1].Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingIndexReturnsEmptyInventory()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-session-index-{Guid.NewGuid():N}.jsonl");
        var entries = await new NativeCodexSessionIndexCatalog(missing).LoadAsync();
        Assert.Empty(entries);
    }
}
