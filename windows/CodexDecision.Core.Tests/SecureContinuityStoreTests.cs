using CodexDecision.Core.Conversations;

namespace CodexDecision.Core.Tests;

public sealed class SecureContinuityStoreTests
{
    [Fact]
    public async Task RoundTripsQueueAndGoalWithoutPlaintextOnDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"continuity-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "secure.json");
        try
        {
            const string sentinel = "private prompt sentinel";
            var queue = new FollowUpQueue();
            queue.Enqueue(sentinel);
            var store = new SecureContinuityStore(new XorProtector(), path);
            await store.SaveTaskAsync(
                Guid.Empty,
                new TaskContinuity(
                    queue.Snapshot(),
                    new GoalDefinition("Ship recovery", "No plaintext", "Tests pass")));

            var serialized = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(sentinel, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("Ship recovery", serialized, StringComparison.Ordinal);

            var loaded = await store.LoadTaskAsync(Guid.Empty);
            Assert.Equal(sentinel, Assert.Single(loaded.Queue).Prompt);
            Assert.Equal("Ship recovery", loaded.Goal?.Outcome);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task QuarantinesPayloadThatCannotBeDecrypted()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"continuity-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "secure.json");
        try
        {
            var writer = new SecureContinuityStore(new XorProtector(), path);
            await writer.SaveTaskAsync(Guid.Empty, new TaskContinuity([], new GoalDefinition("Goal", "", "")));

            var reader = new SecureContinuityStore(new ThrowingProtector(), path);
            var loaded = await reader.LoadTaskAsync(Guid.Empty);

            Assert.Empty(loaded.Queue);
            Assert.Single(reader.Warnings);
            Assert.Contains("\"quarantined\": true", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BatchLoadAndSaveRoundTripsEveryTaskInOneDocument()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"continuity-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "secure.json");
        try
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var removed = Guid.NewGuid();
            var store = new SecureContinuityStore(new XorProtector(), path);
            await store.SaveTaskAsync(removed, new TaskContinuity([], new GoalDefinition("Removed", "", "")));
            await store.SaveAllAsync(new Dictionary<Guid, TaskContinuity>
            {
                [first] = new([], new GoalDefinition("First", "", "")),
                [second] = new([], new GoalDefinition("Second", "", "")),
            });

            var loaded = await store.LoadAllAsync();

            Assert.Equal(2, loaded.Count);
            Assert.Equal("First", loaded[first].Goal?.Outcome);
            Assert.Equal("Second", loaded[second].Goal?.Outcome);
            Assert.False(loaded.ContainsKey(removed));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext);

        private static byte[] Transform(ReadOnlySpan<byte> value) => value.ToArray().Select(item => (byte)(item ^ 0xA5)).ToArray();
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) =>
            throw new System.Security.Cryptography.CryptographicException("Wrong user");
    }
}
