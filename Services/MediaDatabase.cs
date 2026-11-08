using Microsoft.Data.Sqlite;
using System;
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
            "PhotoTagger.db");

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

        if (version < 1) CreateDB(conn);
    }

    private static void CreateDB(SqliteConnection conn)
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
                added_utc    INTEGER NOT NULL,
                isFolder     BIT     NOT NULL DEFAULT 0,
                folder_id INTEGER REFERENCES folders(id)
            );
            CREATE INDEX idx_media_fingerprint ON media(fingerprint, size_bytes);
            CREATE INDEX idx_media_kind ON media(kind);
            CREATE INDEX idx_media_folder ON media(folder_id);

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

            PRAGMA user_version = 1;
        ";

        create.ExecuteNonQuery();
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

        string file = Path.Combine(dir, $"PhotoTagger-{DateTime.Now:yyyy-MM-dd}.db");
        if (File.Exists(file))
            return null;

        Backup(file);

        foreach (var old in Directory.GetFiles(dir, "PhotoTagger-*.db")
                                     .OrderByDescending(f => f, StringComparer.Ordinal)
                                     .Skip(keep))
        {
            try { File.Delete(old); } catch { /* not worth failing startup over */ }
        }

        return file;
    }
}