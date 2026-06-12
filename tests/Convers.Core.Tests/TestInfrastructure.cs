using Convers.Core;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Core.Tests;

/// <summary>
/// One <see cref="ConversStore"/> in its own temp directory (no shared state) with a fake clock.
/// Dispose removes the directory. Mirrors the pdn-bbs <c>TestStore</c> idiom, but on the real
/// <see cref="FakeTimeProvider"/> from Microsoft.Extensions.TimeProvider.Testing.
/// </summary>
internal sealed class TestStore : IDisposable
{
    private readonly DirectoryInfo _dir;

    public TestStore()
    {
        _dir = Directory.CreateTempSubdirectory("convers-core-test-");
        DbPath = Path.Combine(_dir.FullName, "convers.db");
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero));
        Store = ConversStore.Open(DbPath, Time);
    }

    public string DbPath { get; }

    public FakeTimeProvider Time { get; }

    public ConversStore Store { get; private set; }

    /// <summary>Closes and reopens the store on the same file (migration idempotence, persistence).</summary>
    public ConversStore Reopen()
    {
        Store.Dispose();
        Store = ConversStore.Open(DbPath, Time);
        return Store;
    }

    /// <summary>Opens a second, independent store instance on the same file (WAL concurrent access).</summary>
    public ConversStore OpenSecond() => ConversStore.Open(DbPath, Time);

    public void Dispose()
    {
        Store.Dispose();
        _dir.Delete(recursive: true);
    }
}
