using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhotoTagger.Services;

/// <summary>
/// Owns the SQLite file. The database is the ONLY place tags live, so it takes a dated
/// backup copy once per day, and a separate safety copy before any schema upgrade.
/// </summary>
public sealed class MediaDatabase
{
    private const int SchemaVersion = 2;

    private readonly string _connectionString;

    public string DbPath { get; }

    public MediaDatabase(string? dbPath = null)
    {
        DbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoTagger",
            "library.db");

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            ForeignKeys = true
        }.ToString();

        Initialize();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Explorer-style ordering (IMG_2 before IMG_10). Registered per connection and used only
        // in ORDER BY. Never use it in an index or table definition, or other tools
        // (DB Browser, an Android app) couldn't read the file.
        connection.CreateCollation("EXPLORER_ORDER", NaturalSort.Compare);

        return connection;
    }

    // ------------------------------------------------------------------
    // Schema and upgrades
    // ------------------------------------------------------------------

    private void Initialize()
    {
        using var conn = Open();

        // WAL = safer against crashes and lets reads happen during a write. Persists in the file.
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteScalar();
        }

        long version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(cmd.ExecuteScalar());
        }

        if (version >= SchemaVersion)
            return;

        // A database that already holds tags gets a safety copy before it is changed.
        if (version >= 1)
            BackupBeforeUpgrade(version);

        if (version < 1) CreateV1(conn);
        if (version < 2) UpgradeToV2(conn);
    }

    private static void CreateV1(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var create = conn.CreateCommand();
        create.Transaction = tx;
        create.CommandText = @"
            CREATE TABLE media (
                id           INTEGER PRIMARY KEY,
                path         TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                file_name    TEXT    NOT NULL,
                kind         INTEGER NOT NULL,            -- 0 = image, 1 = video
                size_bytes   INTEGER NOT NULL,
                modified_utc INTEGER NOT NULL,            -- DateTime ticks (UTC)
                fingerprint  TEXT    NOT NULL,            -- size + head/tail hash; survives moves/renames
                missing      INTEGER NOT NULL DEFAULT 0,  -- 1 = not found on last scan (tags are kept)
                added_utc    INTEGER NOT NULL
            );
            CREATE INDEX idx_media_fingerprint ON media(fingerprint, size_bytes);
            CREATE INDEX idx_media_kind ON media(kind);

            CREATE TABLE tags (
                id   INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );

            CREATE TABLE media_tags (
                media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
                tag_id   INTEGER NOT NULL REFERENCES tags(id)  ON DELETE CASCADE,
                PRIMARY KEY (media_id, tag_id)
            ) WITHOUT ROWID;
            CREATE INDEX idx_media_tags_tag ON media_tags(tag_id, media_id);

            CREATE TABLE library_roots (
                path TEXT PRIMARY KEY COLLATE NOCASE
            );

            PRAGMA user_version = 1;
            ";
        create.ExecuteNonQuery();
        tx.Commit();
    }

    // v2: folders become first-class (they can carry tags), and every file points at its folder.
    private static void UpgradeToV2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        using (var ddl = conn.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = @"
                CREATE TABLE folders (
                    id      INTEGER PRIMARY KEY,
                    path    TEXT    NOT NULL UNIQUE COLLATE NOCASE,   -- full directory path, no trailing slash
                    name    TEXT    NOT NULL,
                    missing INTEGER NOT NULL DEFAULT 0                -- 1 = no photos/videos found in it on last scan (tags kept)
                );

                CREATE TABLE folder_tags (
                    folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                    tag_id    INTEGER NOT NULL REFERENCES tags(id)    ON DELETE CASCADE,
                    PRIMARY KEY (folder_id, tag_id)
                ) WITHOUT ROWID;
                CREATE INDEX idx_folder_tags_tag ON folder_tags(tag_id, folder_id);

                ALTER TABLE media ADD COLUMN folder_id INTEGER REFERENCES folders(id);
                CREATE INDEX idx_media_folder ON media(folder_id);
                ";
            ddl.ExecuteNonQuery();
        }

        // Backfill: work out each existing file's folder from its path.
        var rows = new List<(long Id, string Dir)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT id, path FROM media";
            using var r = read.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), MediaRepository.FolderPathOf(r.GetString(1))));
        }

        var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO folders(path, name, missing) VALUES (@p, @n, 0)";
            var pPath = insert.Parameters.Add("@p", SqliteType.Text);
            var pName = insert.Parameters.Add("@n", SqliteType.Text);

            using var lastId = conn.CreateCommand();
            lastId.Transaction = tx;
            lastId.CommandText = "SELECT last_insert_rowid()";

            foreach (string dir in rows.Select(x => x.Dir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                pPath.Value = dir;
                pName.Value = MediaRepository.FolderNameOf(dir);
                insert.ExecuteNonQuery();
                folderIds[dir] = Convert.ToInt64(lastId.ExecuteScalar());
            }
        }

        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE media SET folder_id = @f WHERE id = @id";
            var pFolder = update.Parameters.Add("@f", SqliteType.Integer);
            var pId = update.Parameters.Add("@id", SqliteType.Integer);

            foreach (var (id, dir) in rows)
            {
                pFolder.Value = folderIds[dir];
                pId.Value = id;
                update.ExecuteNonQuery();
            }
        }

        using (var missing = conn.CreateCommand())
        {
            missing.Transaction = tx;
            missing.CommandText = MediaRepository.RecomputeFolderMissingSql;
            missing.ExecuteNonQuery();
        }

        using (var version = conn.CreateCommand())
        {
            version.Transaction = tx;
            version.CommandText = "PRAGMA user_version = 2;";
            version.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ------------------------------------------------------------------
    // Backups
    // ------------------------------------------------------------------

    private void BackupBeforeUpgrade(long fromVersion)
    {
        string name = $"premigration-from-v{fromVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.db";

        try
        {
            string dir = BackupDirectory();
            Backup(Path.Combine(dir, name));
        }
        catch
        {
            // Documents unavailable: keep the safety copy next to the database instead.
            // If even that fails the exception stops the upgrade, which is the safe outcome.
            Backup(Path.Combine(Path.GetDirectoryName(DbPath)!, name));
        }
    }

    private static string BackupDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PhotoTagger",
            "Backups");

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Copies the live database to <paramref name="destination"/> (safe while the app is using it).</summary>
    public void Backup(string destination)
    {
        string temp = destination + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);

        using (var source = Open())
        using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temp,
            Pooling = false     // so the file handle is released and we can rename it
        }.ToString()))
        {
            source.BackupDatabase(dest);
        }

        File.Move(temp, destination, overwrite: true);
    }

    /// <summary>
    /// Makes one backup per day in Documents\PhotoTagger\Backups and keeps the newest <paramref name="keep"/>.
    /// Returns the new backup path, or null if today's backup already exists.
    /// </summary>
    public string? BackupIfDue(int keep = 14)
    {
        string dir = BackupDirectory();

        string file = Path.Combine(dir, $"library-{DateTime.Now:yyyy-MM-dd}.db");
        if (File.Exists(file))
            return null;

        Backup(file);

        foreach (var old in Directory.GetFiles(dir, "library-*.db")
                                     .OrderByDescending(f => f, StringComparer.Ordinal)
                                     .Skip(keep))
        {
            try { File.Delete(old); } catch { /* not worth failing startup over */ }
        }

        return file;
    }
}