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
    private const int MaxTagLength = 64;

    // A folder is "missing" when none of its files were found on the last scan.
    internal const string RecomputeFolderMissingSql =
        "UPDATE folders SET missing = CASE WHEN EXISTS " +
        "(SELECT 1 FROM media m WHERE m.folder_id = folders.id AND m.missing = 0) THEN 0 ELSE 1 END";

    private readonly MediaDatabase _db;

    public MediaRepository(MediaDatabase db) => _db = db;

    // ------------------------------------------------------------------
    // Paths
    // ------------------------------------------------------------------

    // "C:\Photos" -> "C:\Photos\" so prefix matching can't confuse C:\Photos with C:\Photos2.
    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";

    /// <summary>The directory a file lives in, without a trailing slash (this is how folders are keyed).</summary>
    internal static string FolderPathOf(string filePath) =>
        (Path.GetDirectoryName(filePath) ?? string.Empty).TrimEnd('\\', '/');

    internal static string FolderNameOf(string folderPath)
    {
        string name = Path.GetFileName(folderPath);
        return name.Length > 0 ? name : folderPath;   // drive roots have no name
    }

    // ------------------------------------------------------------------
    // Library folders (scan roots)
    // ------------------------------------------------------------------

    public List<string> GetRoots()
    {
        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, "SELECT path FROM library_roots ORDER BY path");
        using var r = cmd.ExecuteReader();

        var roots = new List<string>();
        while (r.Read()) roots.Add(r.GetString(0));
        return roots;
    }

    public void AddRoot(string path)
    {
        using var conn = _db.Open();
        Exec(conn, null, "INSERT OR IGNORE INTO library_roots(path) VALUES (@p)", ("@p", NormalizeRoot(path)));
    }

    // ------------------------------------------------------------------
    // Sync (scan results -> database)
    // ------------------------------------------------------------------

    private sealed record ExistingRow(long Id, long Size, long Modified, bool Missing);
    private sealed record RelinkCandidate(long MediaId, long? FolderId);

    public SyncResult SyncRoot(
        string root,
        IReadOnlyList<ScannedFile> files,
        IProgress<(int Done, int Total)>? progress = null)
    {
        root = NormalizeRoot(root);
        string rootDir = root.TrimEnd('\\');
        int added = 0, moved = 0, updated = 0, missing = 0, unreadable = 0;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // 1a. What files do we already know under this root?
        var existing = new Dictionary<string, ExistingRow>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Cmd(conn, tx,
            "SELECT id, path, size_bytes, modified_utc, missing FROM media " +
            "WHERE substr(path, 1, @len) = @root COLLATE NOCASE",
            ("@len", root.Length), ("@root", root)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                existing[r.GetString(1)] = new ExistingRow(r.GetInt64(0), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0);
        }

        // 1b. ...and what folders?
        var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Cmd(conn, tx,
            "SELECT id, path FROM folders WHERE path = @rootDir OR substr(path, 1, @len) = @root COLLATE NOCASE",
            ("@rootDir", rootDir), ("@len", root.Length), ("@root", root)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                folderIds[r.GetString(1)] = r.GetInt64(0);
        }

        // Finds (or creates) the folder row for a file's directory.
        // previousFolderId: set when the file was re-linked from somewhere else. If that old folder
        // no longer exists on disk, the whole folder was most likely renamed or moved, so we re-use
        // its row (and therefore its tags) instead of creating a new, untagged one.
        long ResolveFolder(string filePath, long? previousFolderId)
        {
            string dir = FolderPathOf(filePath);

            if (folderIds.TryGetValue(dir, out long known))
                return known;

            // Safety net: a row for this path might exist outside what we loaded.
            using (var find = Cmd(conn, tx, "SELECT id FROM folders WHERE path = @p", ("@p", dir)))
            {
                object? found = find.ExecuteScalar();
                if (found != null)
                {
                    long foundId = Convert.ToInt64(found);
                    folderIds[dir] = foundId;
                    return foundId;
                }
            }

            if (previousFolderId is long oldId)
            {
                string? oldPath;
                using (var cmd = Cmd(conn, tx, "SELECT path FROM folders WHERE id = @id", ("@id", oldId)))
                    oldPath = cmd.ExecuteScalar() as string;

                if (oldPath != null && !Directory.Exists(oldPath))
                {
                    Exec(conn, tx, "UPDATE folders SET path = @p, name = @n WHERE id = @id",
                        ("@p", dir), ("@n", FolderNameOf(dir)), ("@id", oldId));

                    folderIds.Remove(oldPath);
                    folderIds[dir] = oldId;
                    return oldId;
                }
            }

            Exec(conn, tx, "INSERT INTO folders(path, name, missing) VALUES (@p, @n, 0)",
                ("@p", dir), ("@n", FolderNameOf(dir)));

            long newId;
            using (var cmd = Cmd(conn, tx, "SELECT last_insert_rowid()"))
                newId = Convert.ToInt64(cmd.ExecuteScalar());

            folderIds[dir] = newId;
            return newId;
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
                    if (row.Size == file.SizeBytes && row.Modified == file.ModifiedUtcTicks)
                    {
                        if (row.Missing)
                            Exec(conn, tx, "UPDATE media SET missing = 0 WHERE id = @id", ("@id", row.Id));
                    }
                    else
                    {
                        // Same path, different content (edited photo). Keep the row, so tags stay.
                        string fp = MediaScanner.ComputeFingerprint(file.Path);
                        Exec(conn, tx,
                            "UPDATE media SET size_bytes=@s, modified_utc=@m, fingerprint=@fp, missing=0 WHERE id=@id",
                            ("@s", file.SizeBytes), ("@m", file.ModifiedUtcTicks), ("@fp", fp), ("@id", row.Id));
                        updated++;
                    }
                }
                else
                {
                    string fp = MediaScanner.ComputeFingerprint(file.Path);
                    RelinkCandidate? candidate = FindRelinkCandidate(conn, tx, fp, file.SizeBytes, file.Path);

                    if (candidate != null)
                    {
                        // Same content as a row whose file is gone: it was moved/renamed. Keep its tags.
                        long folderId = ResolveFolder(file.Path, candidate.FolderId);

                        Exec(conn, tx,
                            "UPDATE media SET path=@p, file_name=@n, modified_utc=@m, folder_id=@f, missing=0 WHERE id=@id",
                            ("@p", file.Path), ("@n", Path.GetFileName(file.Path)),
                            ("@m", file.ModifiedUtcTicks), ("@f", folderId), ("@id", candidate.MediaId));

                        claimedIds.Add(candidate.MediaId);
                        moved++;
                    }
                    else
                    {
                        long folderId = ResolveFolder(file.Path, null);

                        Exec(conn, tx,
                            "INSERT INTO media (path, file_name, kind, size_bytes, modified_utc, fingerprint, folder_id, added_utc) " +
                            "VALUES (@p, @n, @k, @s, @m, @fp, @f, @a)",
                            ("@p", file.Path), ("@n", Path.GetFileName(file.Path)), ("@k", (int)file.Kind),
                            ("@s", file.SizeBytes), ("@m", file.ModifiedUtcTicks), ("@fp", fp),
                            ("@f", folderId), ("@a", now));
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

        // 4. Folders follow their files: no files found = folder flagged missing (tags kept).
        Exec(conn, tx, RecomputeFolderMissingSql);

        tx.Commit();
        return new SyncResult(added, moved, updated, missing, unreadable);
    }

    // A row with identical content whose file no longer exists at its recorded path.
    // (A row whose file still exists is a genuine copy and gets its own, separately tagged row.)
    private static RelinkCandidate? FindRelinkCandidate(
        SqliteConnection conn, SqliteTransaction tx, string fingerprint, long size, string newPath)
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
            sql.Append(" AND m.file_name LIKE @text ESCAPE '\\'");
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

    // ------------------------------------------------------------------
    // Filtering: folders
    // ------------------------------------------------------------------

    /// <summary>
    /// Folders that directly contain photos/videos. Text matches anywhere in the folder path,
    /// so typing a parent folder's name lists its subfolders too. The cover is the folder's first
    /// item in Explorer order.
    /// </summary>
    public List<FolderItem> QueryFolders(MediaFilter filter)
    {
        var sql = new StringBuilder(
            "SELECT f.id, f.path, f.name, " +
            "(SELECT COUNT(*) FROM media m WHERE m.folder_id = f.id AND m.missing = 0), " +
            "(SELECT m.path FROM media m WHERE m.folder_id = f.id AND m.missing = 0 ORDER BY m.file_name COLLATE EXPLORER_ORDER LIMIT 1), " +
            "(SELECT m.kind FROM media m WHERE m.folder_id = f.id AND m.missing = 0 ORDER BY m.file_name COLLATE EXPLORER_ORDER LIMIT 1) " +
            "FROM folders f WHERE f.missing = 0");
        var ps = new List<(string, object?)>();

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            sql.Append(" AND f.path LIKE @text ESCAPE '\\'");
            ps.Add(("@text", "%" + EscapeLike(filter.Text.Trim()) + "%"));
        }

        AppendTagFilter(sql, ps, filter, "f.id", "folder_tags", "folder_id");

        sql.Append(" ORDER BY f.path COLLATE EXPLORER_ORDER");

        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, sql.ToString(), ps.ToArray());
        using var r = cmd.ExecuteReader();

        var folders = new List<FolderItem>();
        while (r.Read())
        {
            folders.Add(new FolderItem(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? MediaKind.Image : (MediaKind)r.GetInt32(5)));
        }

        return folders;
    }

    /// <summary>The files directly inside a folder, in Explorer order. Used for prev/next browsing.</summary>
    public List<MediaItem> GetFolderMedia(long folderId)
    {
        using var conn = _db.Open();
        using var cmd = Cmd(conn, null,
            "SELECT m.id, m.path, m.kind FROM media m " +
            "WHERE m.folder_id = @f AND m.missing = 0 ORDER BY m.file_name COLLATE EXPLORER_ORDER",
            ("@f", folderId));
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

    private static (string Table, string Column, string Parent) Link(TagTarget target) =>
        target == TagTarget.Folder
            ? ("folder_tags", "folder_id", "folders")
            : ("media_tags", "media_id", "media");

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
    public List<TagInfo> GetTagsWithCounts(TagTarget target)
    {
        var (table, column, parent) = Link(target);

        using var conn = _db.Open();
        using var cmd = Cmd(conn, null,
            $"SELECT t.id, t.name, COUNT(*) FROM tags t " +
            $"JOIN {table} l ON l.tag_id = t.id " +
            $"JOIN {parent} x ON x.id = l.{column} AND x.missing = 0 " +
            $"GROUP BY t.id ORDER BY t.name COLLATE NOCASE");
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

    // ------------------------------------------------------------------
    // Small ADO.NET helpers
    // ------------------------------------------------------------------

    private static SqliteCommand Cmd(
        SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
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
}