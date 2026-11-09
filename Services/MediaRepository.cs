using Microsoft.Data.Sqlite;
using PhotoTagger.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoTagger.Services;

public sealed record SyncResult(int Added, int Moved, int Updated, int MarkedMissing, int Unreadable);

/// <summary>
/// All reads/writes of the library. Methods are synchronous; call them from Task.Run.
/// Rules that protect your tags (they only exist in this database):
///  - Rows are NEVER deleted because a file disappeared; they are flagged missing and hidden.
///  - A moved/renamed file is re-linked by fingerprint so it keeps its tags.
///  - A renamed folder is detected through its re-linked files, so it keeps its tags too.
/// </summary>
public sealed class MediaRepository
{
    #region Variables

    private const int MaxTagLength = 64;

    private readonly MediaDatabase _db;

    public MediaRepository(MediaDatabase db) => _db = db;

    #endregion

    // ------------------------------------------------------------------
    // Paths
    // ------------------------------------------------------------------

    // "C:\Photos" -> "C:\Photos\" so prefix matching can't confuse C:\Photos with C:\Photos2.
    private static string NormalizeRoot(string path) => Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";

    /// <summary>The directory a file lives in, without a trailing slash (this is how folders are keyed).</summary>
    internal static string FolderPathOf(string filePath) => (Path.GetDirectoryName(filePath) ?? string.Empty).TrimEnd('\\', '/');

    internal static string FolderNameOf(string folderPath)
    {
        string name = Path.GetFileName(folderPath);
        return name.Length > 0 ? name : folderPath;   // drive roots have no name
    }

    #region SQL Commands

    private static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;

        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        return cmd;
    }

    private static void Exec(
        SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Cmd(conn, tx, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    #endregion

    #region Root Folder

    public List<string> GetRoots()
    {
        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, "SELECT path FROM library_roots ORDER BY path");
        using var r = cmd.ExecuteReader();

        var roots = new List<string>();
        while (r.Read())
            roots.Add(r.GetString(0));

        return roots;
    }

    public void AddRoot(string path)
    {
        using var conn = _db.Open();
        Exec(conn, null, "INSERT OR IGNORE INTO library_roots(path) VALUES (@path)", ("@path", NormalizeRoot(path)));
    }

    #endregion

    // ------------------------------------------------------------------
    // Sync (scan results -> database)
    // ------------------------------------------------------------------

    private sealed record ExistingRow(long Id, MediaKind Kind, long Size, long Modified, bool Missing);
    private sealed record RelinkCandidate(long MediaId, long? FolderId);

    public SyncResult SyncRoot(string root, IReadOnlyList<ScannedFile> files, IProgress<(int Done, int Total)>? progress = null)
    {
        root = NormalizeRoot(root);
        string rootDir = root.TrimEnd('\\');
        int added = 0, moved = 0, updated = 0, missing = 0, unreadable = 0;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Get existing files
        var existing = new Dictionary<string, ExistingRow>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Cmd(conn, tx,
            @"SELECT id, path, kind, size_bytes, modified_utc, missing
            FROM media
            WHERE substr(path, 1, @len) = @root COLLATE NOCASE",
            ("@len", root.Length), ("@root", root)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                existing[r.GetString(1)] = new ExistingRow(r.GetInt64(0), (MediaKind)r.GetInt32(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5) != 0);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedIds = new HashSet<long>();   // media rows re-linked to a new path during this scan
        long now = DateTime.UtcNow.Ticks;
        int done = 0;

        // 2. Walk the files found on disk.
        foreach (var file in files)
        {
            seen.Add(file.Path);

            try
            {
                if (existing.TryGetValue(file.Path, out var row))
                {
                    if ((row.Kind != MediaKind.Folder && row.Size == file.SizeBytes) && row.Modified == file.ModifiedUtcTicks)
                    {
                        if (row.Missing)
                            Exec(conn, tx, "UPDATE media SET missing = 0 WHERE id = @id", ("@id", row.Id));
                    }
                    else
                    {
                        // Same path, different content (edited photo). Keep the row, so tags stay.
                        string fp = MediaScanner.ComputeFingerprint(file.Path);
                        Exec(conn, tx, @"
                            UPDATE media
                            SET size_bytes = @size_bytes, modified_utc = @modified_utc, fingerprint = @fingerprint, missing = 0
                            WHERE id = @id",
                            ("@size_bytes", file.SizeBytes), ("@modified_utc", file.ModifiedUtcTicks), ("@fingerprint", fp), ("@id", row.Id));
                        updated++;
                    }
                }
                else
                {
                    // TODO - Review from here ---------------------------------------------------------------------------------------------------------------------------------
                    string fp = MediaScanner.ComputeFingerprint(file.Path);
                    RelinkCandidate? candidate = FindRelinkCandidate(conn, tx, fp, file.SizeBytes, file.Path);

                    if (candidate != null)
                    {
                        Exec(conn, tx, @"
                            UPDATE media 
                            SET path=@p, name=@n, modified_utc=@m, missing=0 
                            WHERE id=@id",
                            ("@p", file.Path), ("@n", Path.GetFileName(file.Path)),
                            ("@m", file.ModifiedUtcTicks), ("@id", candidate.MediaId));

                        claimedIds.Add(candidate.MediaId);
                        moved++;
                    }
                    else
                    {
                        Exec(conn, tx,
                            "INSERT INTO media (path, name, kind, size_bytes, modified_utc, fingerprint, added_utc) " +
                            "VALUES (@path, @name, @kind, @size_bytes, @modified_utc, @fingerprint, @added_utc)",
                            ("@path", file.Path), ("@name", Path.GetFileName(file.Path)), ("@kind", (int)file.Kind),
                            ("@size_bytes", file.SizeBytes), ("@modified_utc", file.ModifiedUtcTicks), ("@fingerprint", fp), ("@added_utc", now));
                        added++;
                    }
                }
            }
            catch (IOException) { unreadable++; }                  // locked or vanished mid-scan
            catch (UnauthorizedAccessException) { unreadable++; }

            progress?.Report((++done, files.Count));
        }

        // 3. Files under this root that weren't found: flag them, never delete them.
        foreach (var (path, row) in existing)
        {
            if (seen.Contains(path) || row.Missing || claimedIds.Contains(row.Id))
                continue;

            Exec(conn, tx, "UPDATE media SET missing = 1 WHERE id = @id", ("@id", row.Id));
            missing++;
        }

        tx.Commit();
        return new SyncResult(added, moved, updated, missing, unreadable);
    }

    // A row with identical content whose file no longer exists at its recorded path.
    // (A row whose file still exists is a genuine copy and gets its own, separately tagged row.)
    private static RelinkCandidate? FindRelinkCandidate(SqliteConnection conn, SqliteTransaction tx, string fingerprint, long size, string newPath)
    {
        using var cmd = Cmd(conn, tx,
            "SELECT id, path, missing, folder_id FROM media WHERE fingerprint = @fp AND size_bytes = @s AND path <> @p",
            ("@fp", fingerprint), ("@s", size), ("@p", newPath));
        using var r = cmd.ExecuteReader();

        while (r.Read())
        {
            bool isMissing = r.GetInt64(2) != 0;
            if (isMissing || !File.Exists(r.GetString(1)))
                return new RelinkCandidate(r.GetInt64(0), r.IsDBNull(3) ? null : r.GetInt64(3));
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Filtering: files
    // ------------------------------------------------------------------

    public List<MediaItem> Query(MediaFilter filter)
    {
        var sql = new StringBuilder("SELECT m.id, m.path, m.kind FROM media m WHERE m.missing = 0");
        var ps = new List<(string, object?)>();

        if (filter.Kind is MediaKind kind)
        {
            sql.Append(" AND m.kind = @kind");
            ps.Add(("@kind", (int)kind));
        }

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            sql.Append(" AND m.name LIKE @text ESCAPE '\\'");
            ps.Add(("@text", "%" + EscapeLike(filter.Text.Trim()) + "%"));
        }

        AppendTagFilter(sql, ps, filter, "m.id", "media_tags", "media_id");

        sql.Append(" ORDER BY m.path COLLATE NOCASE");

        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, sql.ToString(), ps.ToArray());
        using var r = cmd.ExecuteReader();

        var items = new List<MediaItem>();
        while (r.Read())
            items.Add(new MediaItem(r.GetInt64(0), r.GetString(1), (MediaKind)r.GetInt32(2)));

        return items;
    }

    // Shared by both queries. idExpression is the row id being filtered (m.id or f.id).
    // The table/column names are fixed strings from this file, never user input.
    private static void AppendTagFilter(
        StringBuilder sql, List<(string, object?)> ps, MediaFilter filter,
        string idExpression, string linkTable, string linkColumn)
    {
        if (filter.UntaggedOnly)
        {
            sql.Append($" AND NOT EXISTS (SELECT 1 FROM {linkTable} x WHERE x.{linkColumn} = {idExpression})");
            return;
        }

        if (filter.Tags.Count == 0)
            return;

        // Tag names are NOCASE in the table, so "Beach" and "beach" are the same tag.
        var names = filter.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var placeholders = new List<string>();

        for (int i = 0; i < names.Count; i++)
        {
            placeholders.Add("@t" + i);
            ps.Add(("@t" + i, names[i]));
        }

        sql.Append($" AND {idExpression} IN (SELECT l.{linkColumn} FROM {linkTable} l JOIN tags t ON t.id = l.tag_id ")
           .Append("WHERE t.name IN (").Append(string.Join(",", placeholders)).Append($") GROUP BY l.{linkColumn}");

        if (filter.MatchAllTags)
        {
            sql.Append(" HAVING COUNT(*) = @n");
            ps.Add(("@n", names.Count));
        }

        sql.Append(")");
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ------------------------------------------------------------------
    // Tags (shared by files and folders)
    // ------------------------------------------------------------------

    private static (string Table, string Column, string Parent) Link(TagTarget target) => ("media_tags", "media_id", "media");

    /// <summary>Trims, collapses inner whitespace, caps the length. Commas and symbols are fine.</summary>
    public static string NormalizeTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string tag = Regex.Replace(raw.Trim(), @"\s+", " ");
        return tag.Length > MaxTagLength ? tag.Substring(0, MaxTagLength).TrimEnd() : tag;
    }

    public List<string> GetTags(TagTarget target, long id)
    {
        var (table, column, _) = Link(target);

        using var conn = _db.Open();
        using var cmd = Cmd(conn, null,
            $"SELECT t.name FROM {table} l JOIN tags t ON t.id = l.tag_id " +
            $"WHERE l.{column} = @id ORDER BY t.name COLLATE NOCASE",
            ("@id", id));
        using var r = cmd.ExecuteReader();

        var tags = new List<string>();
        while (r.Read()) tags.Add(r.GetString(0));
        return tags;
    }

    /// <summary>Tags in use by files (or by folders), with counts. Unused tags are not listed.</summary>
    public List<TagInfo> GetTagsWithCounts(TagTarget target, string tagNameFilter = "")
    {
        var (table, column, parent) = Link(target);

        (string Name, object? Value)[] parameters = [($"@{nameof(tagNameFilter)}", "%" + tagNameFilter + "%")];

        StringBuilder sql = new(@$"
            SELECT t.id, t.name, COUNT(*)
            FROM tags t
            JOIN {table} l ON l.tag_id = t.id
            JOIN {parent} x ON x.id = l.{column} AND x.missing = 0");

        if (tagNameFilter != "")
            sql.Append($" WHERE t.name LIKE @{nameof(tagNameFilter)} ");

        sql.Append($@"
            GROUP BY t.id 
            ORDER BY t.name COLLATE NOCASE");

        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, sql.ToString(), parameters);
        using var r = cmd.ExecuteReader();

        var tags = new List<TagInfo>();
        while (r.Read())
            tags.Add(new TagInfo(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        return tags;
    }

    /// <summary>Every tag name, for autocomplete (a tag used on folders can be reused on files and vice versa).</summary>
    public List<string> GetAllTagNames()
    {
        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, "SELECT name FROM tags ORDER BY name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();

        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>Adds a tag to one or many files/folders in a single transaction. Re-adding is a no-op.</summary>
    public void AddTag(TagTarget target, IReadOnlyCollection<long> ids, string tag)
    {
        tag = NormalizeTag(tag);
        if (tag.Length == 0 || ids.Count == 0) return;

        var (table, column, _) = Link(target);

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, "INSERT OR IGNORE INTO tags(name) VALUES (@n)", ("@n", tag));

        long tagId;
        using (var cmd = Cmd(conn, tx, "SELECT id FROM tags WHERE name = @n", ("@n", tag)))
            tagId = Convert.ToInt64(cmd.ExecuteScalar());

        foreach (long id in ids)
        {
            Exec(conn, tx, $"INSERT OR IGNORE INTO {table}({column}, tag_id) VALUES (@i, @t)",
                ("@i", id), ("@t", tagId));
        }

        tx.Commit();
    }

    /// <summary>
    /// Removes a tag from one file/folder. If neither files nor folders use the tag any more,
    /// the tag itself is deleted.
    /// </summary>
    public void RemoveTag(TagTarget target, long id, string tag)
    {
        var (table, column, _) = Link(target);

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx,
            $"DELETE FROM {table} WHERE {column} = @i AND tag_id = (SELECT id FROM tags WHERE name = @n)",
            ("@i", id), ("@n", tag));

        Exec(conn, tx,
            "DELETE FROM tags WHERE name = @n " +
            "AND NOT EXISTS (SELECT 1 FROM media_tags WHERE tag_id = tags.id) " +
            "AND NOT EXISTS (SELECT 1 FROM folder_tags WHERE tag_id = tags.id)",
            ("@n", tag));

        tx.Commit();
    }
}