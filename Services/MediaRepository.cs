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

public sealed class MediaRepository
{
    #region Variables

    private const int MaxTagLength = 64;

    private readonly MediaDatabase _db;

    public MediaRepository(MediaDatabase db) => _db = db;

    #endregion

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

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Cmd(conn, tx, sql, parameters);

        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new Exception($"SQL error: {ex.Message}\n{sql}", ex);
        }
    }

    #endregion

    #region Root Folder

    private static string NormalizeRoot(string path) => Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";

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

    #region Sync

    private sealed record ExistingRow(long Id, MediaKind MediaKind, long SizeBytes, long ModifiedUtc, bool IsMissing);
    private sealed record RelinkCandidate(long MediaId, bool IsFolder);


    public SyncResult SyncRoot(string root, IReadOnlyList<ScannedFile> files, IProgress<(int Done, int Total)>? progress = null)
    {
        root = NormalizeRoot(root);
        string rootDir = root.TrimEnd('\\');
        int added = 0, moved = 0, updated = 0, missing = 0, unreadable = 0;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Get existing files
        var existing = new Dictionary<string, ExistingRow>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Cmd(conn, tx, @"
            SELECT id, path, media_kind, size_bytes, modified_utc, is_missing
            FROM media
            WHERE substr(path, 1, @len) = @root COLLATE NOCASE",
            ("@len", root.Length), ("@root", root)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                existing[r.GetString(1)] = new ExistingRow(r.GetInt64(0), (MediaKind)r.GetInt32(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5) != 0);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedIds = new HashSet<long>();
        long now = DateTime.UtcNow.Ticks;
        int done = 0;

        foreach (var file in files)
        {
            seen.Add(file.Path);

            try
            {
                if (existing.TryGetValue(file.Path, out var row))
                {
                    if (row.SizeBytes == file.SizeBytes && row.ModifiedUtc == file.ModifiedUtc && row.IsMissing)
                    {
                        Exec(conn, tx, "UPDATE media SET is_missing = 0 WHERE id = @id", ("@id", row.Id));
                    }
                    else
                    {
                        // Same path, different content (edited photo). Keep the row, so tags stay.
                        string? fingerprint = MediaScanner.ComputeFingerprint(file.Path, file.MediaKind);
                        if (fingerprint == null)
                        {
                            unreadable++;
                            continue;
                        }

                        Exec(conn, tx, @"
                            UPDATE media
                            SET size_bytes = @size_bytes, modified_utc = @modified_utc, fingerprint = @fingerprint, is_missing = 0, 
                                folder_path = @folder_path, image_height = @image_height, image_width = @image_width
                            WHERE id = @id",
                            ("@size_bytes", file.SizeBytes), ("@modified_utc", file.ModifiedUtc),
                            ("@fingerprint", fingerprint), ("@id", row.Id), ("@folder_path", file.FolderPath),
                            ("@image_height", file.ImageHeight), ("@image_width", file.ImageHeight));
                        updated++;
                    }
                }
                else
                {
                    string? fingerprint = MediaScanner.ComputeFingerprint(file.Path, file.MediaKind);
                    if (fingerprint == null)
                    {
                        unreadable++;
                        continue;
                    }

                    RelinkCandidate? candidate = FindRelinkCandidate(conn, tx, fingerprint, file.SizeBytes, file.Path, file.MediaKind);

                    if (candidate != null)
                    {
                        Exec(conn, tx, @"
                            UPDATE media 
                            SET path = @path, name = @name, modified_utc = @modified_utc, is_missing = 0, folder_path = @folder_path, image_height = @image_height, image_width = @image_width
                            WHERE id = @id",
                            ("@path", file.Path), ("@name", CleanFileName(file.Path, file.Extension)),
                            ("@modified_utc", file.ModifiedUtc), ("@id", candidate.MediaId), ("@folder_path", file.FolderPath),
                            ("@image_height", file.ImageHeight), ("@image_width", file.ImageHeight));

                        claimedIds.Add(candidate.MediaId);
                        moved++;
                    }
                    else
                    {
                        Exec(conn, tx, @"
                            INSERT INTO media (path, name, media_kind, size_bytes, modified_utc, fingerprint, added_utc, is_folder, folder_path, image_height, image_width)
                            VALUES (@path, @name, @media_kind, @size_bytes, @modified_utc, @fingerprint, @added_utc, @is_folder, @folder_path, @image_height, @image_width)",
                            ("@path", file.Path), ("@name", CleanFileName(file.Path, file.Extension)),
                            ("@media_kind", (int)file.MediaKind), ("@size_bytes", file.SizeBytes),
                            ("@modified_utc", file.ModifiedUtc), ("@fingerprint", fingerprint),
                            ("@added_utc", now), ("@is_folder", file.MediaKind == MediaKind.Folder ? 1 : 0),
                            ("@folder_path", file.FolderPath), ("@image_height", file.ImageHeight), ("@image_width", file.ImageWidth)); // TODO - Test folder path - moving out of folder

                        added++;
                    }
                }
            }
            catch (IOException) { unreadable++; }                  // locked or vanished mid-scan
            catch (UnauthorizedAccessException) { unreadable++; }

            progress?.Report((++done, files.Count));
        }

        // Files under this root that weren't found flag them as missing.
        foreach (var (path, row) in existing)
        {
            if (seen.Contains(path) || row.IsMissing || claimedIds.Contains(row.Id))
                continue;

            Exec(conn, tx, "UPDATE media SET is_missing = 1 WHERE id = @id", ("@id", row.Id));
            missing++;
        }

        tx.Commit();
        return new SyncResult(added, moved, updated, missing, unreadable);
    }

    private static string CleanFileName(string path, string extension)
    {
        string filePath = Path.GetFileName(path);
        if (extension is not null)
            filePath = filePath.Replace(extension, "");

        return Regex.Replace(filePath.Replace("_", " "), @"(^\w)|(\s\w)", m => m.Value.ToUpper());
    }

    // A row with identical content whose file no longer exists at its recorded path (A row whose file still exists is a genuine copy and gets its own, separately tagged row.)
    private static RelinkCandidate? FindRelinkCandidate(SqliteConnection conn, SqliteTransaction tx, string fingerprint, long size, string newPath, MediaKind mediaKind)
    {
        using var cmd = Cmd(conn, tx, @"
            SELECT id, path, is_missing, is_folder 
            FROM media 
            WHERE fingerprint = @fingerprint AND size_bytes = @size_bytes AND path <> @path AND media_kind = @media_kind",
            ("@fingerprint", fingerprint), ("@size_bytes", size), ("@path", newPath), ("@media_kind", mediaKind));
        using var r = cmd.ExecuteReader();

        while (r.Read())
        {
            bool isMissing = r.GetInt64(2) != 0;
            if (isMissing || !File.Exists(r.GetString(1)))
                return new RelinkCandidate(r.GetInt64(0), r.GetBoolean(3));
        }

        return null;
    }

    #endregion

    #region Media

    #region Queries

    public List<MediaItem> QueryMedia(MediaFilter filter)
    {
        StringBuilder sql = new(@"
            SELECT m.id, m.path, m.name, m.media_kind, IFNULL(m.folder_thumbnail_path, ''), IFNULL(m.image_height, 0), IFNULL(m.image_width, 0)
            FROM media m 
            WHERE m.is_missing = 0 ");

        List<(string, object?)> ps = new();

        if (filter.FolderPath is not null && filter.FolderPath.Count() != 0)
        {
            sql.Append(" AND (");

            for (int x = 0; x < filter.FolderPath.Count(); x++)
            {
                sql.Append($" m.folder_path LIKE @folder_path{x} ESCAPE '\\' ");
                ps.Add(($"@folder_path{x}", "%" + EscapeLike(filter.FolderPath[x])));

                if (x < filter.FolderPath.Count() - 1)
                {
                    sql.Append(" OR ");
                }
            }

            sql.Append(") ");
        }
        else
        {
            return new List<MediaItem>();
        }

        if (filter.MediaKind is MediaKind mediaKind)
        {
            sql.Append(" AND m.media_kind = @media_kind ");
            ps.Add(("@media_kind", (int)mediaKind));
        }

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            sql.Append(" AND m.name LIKE @text ESCAPE '\\' ");
            ps.Add(("@text", "%" + EscapeLike(filter.Text.Trim()) + "%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Path))
        {
            sql.Append(" AND m.path LIKE @path ESCAPE '\\' ");
            ps.Add(("@path", "%" + filter.Path + "%"));
        }

        if (filter.IsFolder is bool isFolder)
        {
            sql.Append(" AND m.is_folder = @is_folder ");
            ps.Add(("@is_folder", isFolder ? 1 : 0));
        }

        if (filter.UntaggedOnly is not null && filter.UntaggedOnly == true)
        {
            sql.Append($" AND NOT EXISTS (SELECT 1 FROM media_tags x WHERE x.media_id = m.id)");
        }
        else if (filter.Tags is not null && filter.Tags.Count > 0)
        {
            var names = filter.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var placeholders = new List<string>();

            for (int i = 0; i < names.Count; i++)
            {
                placeholders.Add("@t" + i);
                ps.Add(("@t" + i, names[i]));
            }

            sql.Append($@" AND m.id IN (SELECT l.media_id FROM media_tags l JOIN tags t ON t.id = l.tag_id
                        WHERE t.name IN (").Append(string.Join(",", placeholders)).Append($") GROUP BY l.media_id");

            if (filter.MatchAllTags is not null && filter.MatchAllTags == true)
            {
                sql.Append(" HAVING COUNT(*) = @name_count");
                ps.Add(("@name_count", names.Count));
            }

            sql.Append(")");
        }

        sql.Append(" ORDER BY m.path COLLATE NOCASE ");

        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, sql.ToString(), ps.ToArray());
        using var r = cmd.ExecuteReader();

        var items = new List<MediaItem>();
        int index = 0;
        while (r.Read())
            items.Add(new MediaItem(index++, r.GetInt64(0), r.GetString(1), r.GetString(2), (MediaKind)r.GetInt32(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6)));

        return items;
    }

    #endregion

    #region Updates

    public void UpdateFolderThumbnail(long id, string newPath)
    {
        StringBuilder sql = new("UPDATE media SET folder_thumbnail_path = @folder_thumbnail_path WHERE id = @id");

        List<(string, object?)> ps = new();
        ps.Add(("@folder_thumbnail_path", newPath));
        ps.Add(("@id", id));

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, sql.ToString(), ps.ToArray());
        tx.Commit();
    }

    #endregion

    #endregion

    #region Tags

    #region Queries

    public List<string> GetTags(long id)
    {
        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, @$"
            SELECT t.name 
            FROM media_tags l 
            JOIN tags t ON t.id = l.tag_id
            WHERE l.media_id = @id ORDER BY t.name COLLATE NOCASE",
            ("@id", id));
        using var r = cmd.ExecuteReader();

        var tags = new List<string>();
        while (r.Read()) tags.Add(r.GetString(0));
        return tags;
    }

    /// <summary>Tags in use by files (or by folders), with counts. Unused tags are not listed.</summary>
    public List<TagInfo> GetTagsWithCounts(string tagNameFilter = "")
    {
        (string Name, object? Value)[] parameters = [($"@{nameof(tagNameFilter)}", "%" + tagNameFilter + "%")];

        StringBuilder sql = new(@$"
            SELECT t.id, t.name, COUNT(*)
            FROM tags t
            JOIN media_tags l ON l.tag_id = t.id
            JOIN media x ON x.id = l.media_id AND x.is_missing = 0");

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

    #endregion

    #region Updates

    /// <summary>Adds a tag to one or many files/folders in a single transaction. Re-adding is a no-op.</summary>
    public void AddTag(IReadOnlyCollection<long> ids, string tag)
    {
        tag = NormalizeTag(tag);
        if (tag.Length == 0 || ids.Count == 0) return;


        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, "INSERT OR IGNORE INTO tags(name) VALUES (@name)", ("@name", tag));

        long tagId;
        using (var cmd = Cmd(conn, tx, "SELECT id FROM tags WHERE name = @name", ("@name", tag)))
            tagId = Convert.ToInt64(cmd.ExecuteScalar());

        foreach (long id in ids)
        {
            Exec(conn, tx, $"INSERT OR IGNORE INTO media_tags(media_id, tag_id) VALUES (@id, @tagId)", ("@id", id), ("@tagId", tagId));
        }

        tx.Commit();
    }

    /// <summary>
    /// Removes a tag from one file/folder. If neither files nor folders use the tag any more,
    /// the tag itself is deleted.
    /// </summary>
    public void RemoveTag(long id, string tag)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx,
            $"DELETE FROM media_tags WHERE media_id = @id AND tag_id = (SELECT id FROM tags WHERE name = @name)",
            ("@id", id), ("@name", tag));

        Exec(conn, tx, @"
            DELETE FROM tags 
            WHERE name = @name
            AND NOT EXISTS (SELECT 1 FROM media_tags WHERE tag_id = tags.id)
            AND NOT EXISTS (SELECT 1 FROM folder_tags WHERE tag_id = tags.id)",
            ("@name", tag));

        tx.Commit();
    }

    #endregion

    #region Helpers

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Trims, collapses inner whitespace, caps the length. Commas and symbols are fine.</summary>
    public static string NormalizeTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string tag = Regex.Replace(raw.Trim(), @"\s+", " ");
        return tag.Length > MaxTagLength ? tag.Substring(0, MaxTagLength).TrimEnd() : tag;
    }

    #endregion

    #endregion

    #region Folders

    #region Queries

    /// <summary>Tags in use by files (or by folders), with counts. Unused tags are not listed.</summary>
    public List<FolderInfo> GetFolders(string folderNameFilter = "")
    {
        (string Name, object? Value)[] parameters = [($"@{nameof(folderNameFilter)}", "%" + folderNameFilter + "%")];

        StringBuilder sql = new(@$"
            SELECT m.folder_path, COUNT(1)
            FROM media m
            WHERE m.folder_path IS NOT NULL");

        if (folderNameFilter != "")
            sql.Append($" AND m.folder_path LIKE @{nameof(folderNameFilter)} ");

        sql.Append($@"
            GROUP BY m.folder_path 
            ORDER BY m.folder_path COLLATE NOCASE");

        using var conn = _db.Open();
        using var cmd = Cmd(conn, null, sql.ToString(), parameters);
        using var r = cmd.ExecuteReader();

        List<FolderInfo> folders = new();
        while (r.Read())
            if (r.GetString(0) is not null)
                folders.Add(new FolderInfo(r.GetInt64(1), r.GetString(0).Substring(r.GetString(0).Substring(0, r.GetString(0).LastIndexOf("\\")).LastIndexOf("\\") + 1)));

        return folders;
    }

    #endregion

    #endregion
}