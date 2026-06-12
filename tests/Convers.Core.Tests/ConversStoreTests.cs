using Convers.Core;
using Microsoft.Data.Sqlite;

namespace Convers.Core.Tests;

/// <summary>Store fundamentals: schema/migration, WAL, the saupp-persisted profile + topic round-trips.</summary>
public sealed class ConversStoreTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void Open_CreatesSchemaAtCurrentVersion()
    {
        Assert.Equal(ConversStore.CurrentSchemaVersion, _ts.Store.SchemaVersion);
    }

    [Fact]
    public void Open_UsesWalJournalMode()
    {
        using var connection = new SqliteConnection($"Data Source={_ts.DbPath};Pooling=False");
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Open_IsIdempotent_DataSurvivesReopen()
    {
        _ts.Store.UpsertProfile(new UserProfile
        {
            Callsign = "M0LTE",
            Personal = "Tom in Bath",
            Nickname = "tom",
            UpdatedAt = _ts.Time.GetUtcNow(),
        });

        ConversStore reopened = _ts.Reopen();

        Assert.Equal(ConversStore.CurrentSchemaVersion, reopened.SchemaVersion);
        UserProfile? loaded = reopened.GetProfile("m0lte");
        Assert.NotNull(loaded);
        Assert.Equal("Tom in Bath", loaded.Personal);
        Assert.Equal("tom", loaded.Nickname);
    }

    [Fact]
    public void Open_RejectsNewerSchemaVersion()
    {
        using (var connection = new SqliteConnection($"Data Source={_ts.DbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value='999' WHERE key='schema_version';";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => _ts.Reopen());
    }

    [Fact]
    public void Profile_RoundTripsAllFields()
    {
        DateTimeOffset now = _ts.Time.GetUtcNow();
        _ts.Store.UpsertProfile(new UserProfile
        {
            Callsign = "g4abc",
            Personal = "personal note",
            Nickname = "nick",
            PasswordHash = "hash:abc123",
            UpdatedAt = now,
        });

        UserProfile? loaded = _ts.Store.GetProfile("G4ABC");

        Assert.NotNull(loaded);
        Assert.Equal("G4ABC", loaded.Callsign);
        Assert.Equal("personal note", loaded.Personal);
        Assert.Equal("nick", loaded.Nickname);
        Assert.Equal("hash:abc123", loaded.PasswordHash);
        Assert.Equal(now.ToUnixTimeSeconds(), loaded.UpdatedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void UpsertProfile_UpdatesExistingRow()
    {
        _ts.Store.UpsertProfile(new UserProfile { Callsign = "M0LTE", Personal = "first", UpdatedAt = _ts.Time.GetUtcNow() });
        _ts.Store.UpsertProfile(new UserProfile { Callsign = "M0LTE", Personal = "second", UpdatedAt = _ts.Time.GetUtcNow() });

        Assert.Equal("second", _ts.Store.GetProfile("M0LTE")!.Personal);
        Assert.Single(_ts.Store.ListProfiles());
    }

    [Fact]
    public void GetProfile_ReturnsNullForUnknownCallsign()
    {
        Assert.Null(_ts.Store.GetProfile("NOBODY"));
    }

    [Fact]
    public void SetPasswordHash_CreatesBareProfileAndLeavesOtherFieldsUntouched()
    {
        _ts.Store.UpsertProfile(new UserProfile { Callsign = "M0LTE", Personal = "keep me", UpdatedAt = _ts.Time.GetUtcNow() });

        _ts.Store.SetPasswordHash("M0LTE", "pw:secret");

        UserProfile? loaded = _ts.Store.GetProfile("M0LTE");
        Assert.NotNull(loaded);
        Assert.Equal("pw:secret", loaded.PasswordHash);
        Assert.Equal("keep me", loaded.Personal);
    }

    [Fact]
    public void SetPasswordHash_NullClearsThePassword()
    {
        _ts.Store.SetPasswordHash("M0LTE", "pw:secret");
        _ts.Store.SetPasswordHash("M0LTE", null);

        Assert.Null(_ts.Store.GetProfile("M0LTE")!.PasswordHash);
    }

    [Fact]
    public void Topic_RoundTripsAndSurvivesReopen()
    {
        DateTimeOffset now = _ts.Time.GetUtcNow();
        bool wrote = _ts.Store.UpsertTopic(new StoredTopic
        {
            Channel = 3333,
            Topic = "Welcome to the test channel",
            SetBy = "M0LTE",
            SetAt = now,
        });
        Assert.True(wrote);

        ConversStore reopened = _ts.Reopen();
        StoredTopic? loaded = reopened.GetTopic(3333);

        Assert.NotNull(loaded);
        Assert.Equal(3333, loaded.Channel);
        Assert.Equal("Welcome to the test channel", loaded.Topic);
        Assert.Equal("M0LTE", loaded.SetBy);
        Assert.Equal(now.ToUnixTimeSeconds(), loaded.SetAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void UpsertTopic_NewerWins_OlderIsRejected()
    {
        DateTimeOffset t1 = _ts.Time.GetUtcNow();
        _ts.Store.UpsertTopic(new StoredTopic { Channel = 100, Topic = "newer", SetBy = "A", SetAt = t1 });

        // An older /..TOPI must not overwrite (SPECS: a held newer topic is not changed).
        bool wrote = _ts.Store.UpsertTopic(new StoredTopic
        {
            Channel = 100,
            Topic = "older",
            SetBy = "B",
            SetAt = t1 - TimeSpan.FromHours(1),
        });

        Assert.False(wrote);
        Assert.Equal("newer", _ts.Store.GetTopic(100)!.Topic);
    }

    [Fact]
    public void UpsertTopic_SameTimestampIsRejectedAsNotNewer()
    {
        DateTimeOffset t = _ts.Time.GetUtcNow();
        _ts.Store.UpsertTopic(new StoredTopic { Channel = 7, Topic = "first", SetBy = "A", SetAt = t });

        Assert.False(_ts.Store.UpsertTopic(new StoredTopic { Channel = 7, Topic = "second", SetBy = "B", SetAt = t }));
        Assert.Equal("first", _ts.Store.GetTopic(7)!.Topic);
    }

    [Fact]
    public void DeleteTopic_RemovesIt()
    {
        _ts.Store.UpsertTopic(new StoredTopic { Channel = 5, Topic = "x", SetBy = "A", SetAt = _ts.Time.GetUtcNow() });

        Assert.True(_ts.Store.DeleteTopic(5));
        Assert.Null(_ts.Store.GetTopic(5));
        Assert.False(_ts.Store.DeleteTopic(5));
    }

    [Fact]
    public void ListTopics_OrderedByChannel()
    {
        DateTimeOffset now = _ts.Time.GetUtcNow();
        _ts.Store.UpsertTopic(new StoredTopic { Channel = 300, Topic = "c", SetBy = "A", SetAt = now });
        _ts.Store.UpsertTopic(new StoredTopic { Channel = 100, Topic = "a", SetBy = "A", SetAt = now });
        _ts.Store.UpsertTopic(new StoredTopic { Channel = 200, Topic = "b", SetBy = "A", SetAt = now });

        int[] channels = _ts.Store.ListTopics().Select(t => t.Channel).ToArray();
        Assert.Equal([100, 200, 300], channels);
    }

    [Fact]
    public void Wal_AllowsConcurrentReaderInstance()
    {
        _ts.Store.UpsertProfile(new UserProfile { Callsign = "M0LTE", Personal = "seen", UpdatedAt = _ts.Time.GetUtcNow() });

        using ConversStore reader = _ts.OpenSecond();
        Assert.Equal("seen", reader.GetProfile("M0LTE")!.Personal);
    }
}
