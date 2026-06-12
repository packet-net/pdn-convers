using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Convers.Core;

/// <summary>
/// The SQLite-backed convers store: the saupp differentiators that must survive a restart —
/// per-user <b>personal text</b>, <b>nicknames</b> and <b>passwords</b>, and per-channel
/// <b>topics</b> (design decision 7). Live channel/presence state is deliberately NOT here; it is
/// in-memory in <see cref="ConversHub"/> and rebuilt from the uplink on reconnect.
///
/// <para>One database file (path supplied by the Host — the package state dir), WAL journal mode,
/// schema-versioned with an idempotent migration on open (the <see cref="BbsStore"/>-style
/// resilient-open ported from pdn-bbs). Instances are safe to use from multiple threads (a single
/// connection guarded by a lock); a second instance on the same path is a valid concurrent reader
/// under WAL.</para>
///
/// <para><see cref="TimeProvider"/> is injected; the store never reads the wall clock directly.</para>
/// </summary>
public sealed class ConversStore : IDisposable
{
    /// <summary>The schema version this build writes and expects.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private ConversStore(SqliteConnection connection, TimeProvider time, int schemaVersion)
    {
        _connection = connection;
        _time = time;
        SchemaVersion = schemaVersion;
    }

    /// <summary>The schema version found/created on open.</summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Opens (creating and/or migrating as needed) the store at <paramref name="path"/>. Migration
    /// is idempotent: re-opening an up-to-date database is a no-op. Throws when the database was
    /// written by a newer build (a forward-incompatible schema version).
    /// </summary>
    public static ConversStore Open(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            ExecuteRaw(connection, "PRAGMA journal_mode=WAL;");
            ExecuteRaw(connection, "PRAGMA synchronous=NORMAL;");
            int version = Migrate(connection);
            return new ConversStore(connection, timeProvider, version);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- user profiles

    /// <summary>Fetches a user's persisted profile by callsign (case-insensitive), or null when none exists.</summary>
    public UserProfile? GetProfile(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        string call = Callsigns.Normalize(callsign);

        lock (_gate)
        {
            using SqliteCommand cmd = Command(
                "SELECT callsign,personal,nickname,password_hash,updated_utc FROM profiles WHERE callsign=$c;");
            cmd.Parameters.AddWithValue("$c", call);
            using SqliteDataReader reader = cmd.ExecuteReader();
            return reader.Read() ? ReadProfile(reader) : null;
        }
    }

    /// <summary>All persisted profiles, ordered by callsign.</summary>
    public IReadOnlyList<UserProfile> ListProfiles()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(
                "SELECT callsign,personal,nickname,password_hash,updated_utc FROM profiles ORDER BY callsign;");
            using SqliteDataReader reader = cmd.ExecuteReader();
            var profiles = new List<UserProfile>();
            while (reader.Read())
            {
                profiles.Add(ReadProfile(reader));
            }

            return profiles;
        }
    }

    /// <summary>
    /// Inserts or updates a user's profile (personal text, nickname, password hash). The
    /// <see cref="UserProfile.UpdatedAt"/> on the supplied record is persisted; pass the current
    /// time. The callsign is normalised to its canonical form.
    /// </summary>
    public void UpsertProfile(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string call = Callsigns.Normalize(profile.Callsign);
        ArgumentException.ThrowIfNullOrWhiteSpace(call, nameof(profile));

        lock (_gate)
        {
            using SqliteCommand cmd = Command(
                "INSERT INTO profiles(callsign,personal,nickname,password_hash,updated_utc) " +
                "VALUES($c,$p,$n,$pw,$u) " +
                "ON CONFLICT(callsign) DO UPDATE SET personal=excluded.personal, nickname=excluded.nickname, " +
                "password_hash=excluded.password_hash, updated_utc=excluded.updated_utc;");
            cmd.Parameters.AddWithValue("$c", call);
            cmd.Parameters.AddWithValue("$p", profile.Personal);
            cmd.Parameters.AddWithValue("$n", profile.Nickname);
            cmd.Parameters.AddWithValue("$pw", (object?)profile.PasswordHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", ToUnix(profile.UpdatedAt));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Sets only the password hash for a callsign (null clears it), creating a bare profile row if
    /// the user is otherwise unknown. Personal text and nickname are left untouched.
    /// </summary>
    public void SetPasswordHash(string callsign, string? passwordHash)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        string call = Callsigns.Normalize(callsign);
        ArgumentException.ThrowIfNullOrWhiteSpace(call, nameof(callsign));
        long now = NowSeconds();

        lock (_gate)
        {
            using SqliteCommand cmd = Command(
                "INSERT INTO profiles(callsign,personal,nickname,password_hash,updated_utc) " +
                "VALUES($c,'','',$pw,$u) " +
                "ON CONFLICT(callsign) DO UPDATE SET password_hash=excluded.password_hash, updated_utc=excluded.updated_utc;");
            cmd.Parameters.AddWithValue("$c", call);
            cmd.Parameters.AddWithValue("$pw", (object?)passwordHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- channel topics

    /// <summary>Fetches a channel's persisted topic, or null when none has ever been stored.</summary>
    public StoredTopic? GetTopic(int channel)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(
                "SELECT channel,topic,set_by,set_utc FROM topics WHERE channel=$ch;");
            cmd.Parameters.AddWithValue("$ch", channel);
            using SqliteDataReader reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTopic(reader) : null;
        }
    }

    /// <summary>All persisted topics, ordered by channel number.</summary>
    public IReadOnlyList<StoredTopic> ListTopics()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command(
                "SELECT channel,topic,set_by,set_utc FROM topics ORDER BY channel;");
            using SqliteDataReader reader = cmd.ExecuteReader();
            var topics = new List<StoredTopic>();
            while (reader.Read())
            {
                topics.Add(ReadTopic(reader));
            }

            return topics;
        }
    }

    /// <summary>
    /// Inserts or updates a channel topic, honouring the SPECS <c>/..TOPI</c> "newer wins" rule: a
    /// stored topic with a <see cref="StoredTopic.SetAt"/> at or after the incoming one is left
    /// unchanged. Returns true when the write took effect, false when an equal-or-newer topic was
    /// already stored.
    /// </summary>
    public bool UpsertTopic(StoredTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        long setAt = ToUnix(topic.SetAt);

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();

            using (SqliteCommand existing = Command("SELECT set_utc FROM topics WHERE channel=$ch;", tx))
            {
                existing.Parameters.AddWithValue("$ch", topic.Channel);
                object? current = existing.ExecuteScalar();
                if (current is long currentSetAt && currentSetAt >= setAt)
                {
                    tx.Commit();
                    return false;
                }
            }

            using (SqliteCommand cmd = Command(
                "INSERT INTO topics(channel,topic,set_by,set_utc) VALUES($ch,$t,$by,$u) " +
                "ON CONFLICT(channel) DO UPDATE SET topic=excluded.topic, set_by=excluded.set_by, set_utc=excluded.set_utc;",
                tx))
            {
                cmd.Parameters.AddWithValue("$ch", topic.Channel);
                cmd.Parameters.AddWithValue("$t", topic.Topic);
                cmd.Parameters.AddWithValue("$by", Callsigns.Normalize(topic.SetBy));
                cmd.Parameters.AddWithValue("$u", setAt);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }
    }

    /// <summary>Removes a channel's persisted topic. Returns false when none was stored.</summary>
    public bool DeleteTopic(int channel)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Command("DELETE FROM topics WHERE channel=$ch;");
            cmd.Parameters.AddWithValue("$ch", channel);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---------------------------------------------------------------- plumbing

    internal long NowSeconds() => _time.GetUtcNow().ToUnixTimeSeconds();

    private static long ToUnix(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static void ExecuteRaw(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static UserProfile ReadProfile(SqliteDataReader reader) => new()
    {
        Callsign = reader.GetString(0),
        Personal = reader.GetString(1),
        Nickname = reader.GetString(2),
        PasswordHash = reader.IsDBNull(3) ? null : reader.GetString(3),
        UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
    };

    private static StoredTopic ReadTopic(SqliteDataReader reader) => new()
    {
        Channel = (int)reader.GetInt64(0),
        Topic = reader.GetString(1),
        SetBy = reader.GetString(2),
        SetAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
    };

    private static int Migrate(SqliteConnection connection)
    {
        ExecuteRaw(connection,
            "CREATE TABLE IF NOT EXISTS meta(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;");

        int version;
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            object? value = read.ExecuteScalar();
            version = value is null ? 0 : int.Parse((string)value, CultureInfo.InvariantCulture);
        }

        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this build supports ({CurrentSchemaVersion}).");
        }

        if (version < 1)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (SqliteCommand ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = SchemaV1;
                ddl.ExecuteNonQuery();
            }

            using (SqliteCommand stamp = connection.CreateCommand())
            {
                stamp.Transaction = tx;
                stamp.CommandText = "INSERT INTO meta(key,value) VALUES('schema_version','1');";
                stamp.ExecuteNonQuery();
            }

            tx.Commit();
            version = 1;
        }

        return version;
    }

    private const string SchemaV1 = """
        CREATE TABLE profiles(
            callsign      TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            personal      TEXT NOT NULL DEFAULT '',
            nickname      TEXT NOT NULL DEFAULT '',
            password_hash TEXT,
            updated_utc   INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;

        CREATE TABLE topics(
            channel  INTEGER NOT NULL PRIMARY KEY,
            topic    TEXT NOT NULL DEFAULT '',
            set_by   TEXT NOT NULL DEFAULT '',
            set_utc  INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;
        """;
}
