using Convers.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Core.Tests;

/// <summary>
/// The v1→v2 migration (adding the append-only <c>chatlog</c>) must be idempotent and, crucially,
/// <b>preserve the existing v1 data</b> — the persisted profiles and topics. These tests hand-build a
/// genuine v1 database (the exact v1 DDL, stamped <c>schema_version=1</c>, with rows), then open it
/// with the current (v2) <see cref="ConversStore"/> and assert nothing was lost and the new table works.
/// </summary>
public sealed class ConversStoreMigrationV1ToV2Tests : IDisposable
{
    private readonly DirectoryInfo _dir;
    private readonly string _dbPath;
    private readonly FakeTimeProvider _time;

    public ConversStoreMigrationV1ToV2Tests()
    {
        _dir = Directory.CreateTempSubdirectory("convers-migrate-test-");
        _dbPath = Path.Combine(_dir.FullName, "convers.db");
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void OpenV1Database_MigratesToV2_PreservingProfilesAndTopics()
    {
        long setUtc = _time.GetUtcNow().ToUnixTimeSeconds();
        SeedV1Database(setUtc);

        // Open with the current build: this must run the v1→v2 migration in place.
        using ConversStore store = ConversStore.Open(_dbPath, _time);
        Assert.Equal(2, store.SchemaVersion);

        // The pre-existing v1 profile survived intact.
        UserProfile? profile = store.GetProfile("M0LTE");
        Assert.NotNull(profile);
        Assert.Equal("M0LTE", profile.Callsign);
        Assert.Equal("Tom in Bath", profile.Personal);
        Assert.Equal("tom", profile.Nickname);
        Assert.Equal("pw:hash", profile.PasswordHash);

        // The pre-existing v1 topic survived intact.
        StoredTopic? topic = store.GetTopic(3333);
        Assert.NotNull(topic);
        Assert.Equal("Welcome", topic.Topic);
        Assert.Equal("M0LTE", topic.SetBy);
        Assert.Equal(setUtc, topic.SetAt.ToUnixTimeSeconds());

        // The new v2 chatlog exists, is empty, and is writable.
        Assert.Empty(store.QueryChatLog());
        store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            At = _time.GetUtcNow(),
            Channel = 3333,
            FromCall = "M0LTE",
            Origin = ChatLogOrigin.Local,
            Text = "post-migration message",
        });
        Assert.Equal("post-migration message", store.QueryChatLog().Single().Text);
    }

    [Fact]
    public void MigrationIsIdempotent_ReopeningV2IsANoOpAndKeepsEverything()
    {
        SeedV1Database(_time.GetUtcNow().ToUnixTimeSeconds());

        using (ConversStore first = ConversStore.Open(_dbPath, _time))
        {
            Assert.Equal(2, first.SchemaVersion);
            first.AppendChatLog(new ChatLogEntry
            {
                Kind = ChatLogKind.Presence,
                At = _time.GetUtcNow(),
                Channel = 100,
                FromCall = "G4ABC",
                Origin = ChatLogOrigin.Network,
                Text = "joined",
            });
        }

        // Re-open: already at v2, migration is a no-op; all data (v1 + the appended chatlog row) remains.
        using ConversStore reopened = ConversStore.Open(_dbPath, _time);
        Assert.Equal(2, reopened.SchemaVersion);
        Assert.NotNull(reopened.GetProfile("M0LTE"));
        Assert.NotNull(reopened.GetTopic(3333));
        Assert.Equal("joined", reopened.QueryChatLog().Single().Text);
    }

    /// <summary>
    /// Writes the exact v1 schema and stamps <c>schema_version=1</c>, with one profile and one topic —
    /// a faithful pre-v2 database the current build must migrate without data loss. The DDL here is a
    /// verbatim copy of the historical v1 schema (profiles + topics), deliberately not the live one, so
    /// the test pins the migration against what v1 actually wrote.
    /// </summary>
    private void SeedV1Database(long setUtc)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();

        Exec(connection, "PRAGMA journal_mode=WAL;");
        Exec(connection,
            "CREATE TABLE meta(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;");
        Exec(connection, """
            CREATE TABLE profiles(
                callsign      TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                personal      TEXT NOT NULL DEFAULT '',
                nickname      TEXT NOT NULL DEFAULT '',
                password_hash TEXT,
                updated_utc   INTEGER NOT NULL DEFAULT 0
            ) WITHOUT ROWID;
            """);
        Exec(connection, """
            CREATE TABLE topics(
                channel  INTEGER NOT NULL PRIMARY KEY,
                topic    TEXT NOT NULL DEFAULT '',
                set_by   TEXT NOT NULL DEFAULT '',
                set_utc  INTEGER NOT NULL DEFAULT 0
            ) WITHOUT ROWID;
            """);
        Exec(connection, "INSERT INTO meta(key,value) VALUES('schema_version','1');");

        using (var ins = connection.CreateCommand())
        {
            ins.CommandText =
                "INSERT INTO profiles(callsign,personal,nickname,password_hash,updated_utc) " +
                "VALUES('M0LTE','Tom in Bath','tom','pw:hash',$u);";
            ins.Parameters.AddWithValue("$u", setUtc);
            ins.ExecuteNonQuery();
        }

        using (var ins = connection.CreateCommand())
        {
            ins.CommandText =
                "INSERT INTO topics(channel,topic,set_by,set_utc) VALUES(3333,'Welcome','M0LTE',$u);";
            ins.Parameters.AddWithValue("$u", setUtc);
            ins.ExecuteNonQuery();
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
