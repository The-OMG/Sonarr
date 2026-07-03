using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using Dapper;
using NLog;

namespace NzbDrone.Core.Drive
{
    public interface IDriveIndex
    {
        void EnsureCreated();
        void StageDrive(string driveId, IEnumerable<DriveRawNode> nodes);
        void MaterializeDrive(int rank, string driveId);

        bool HasData();
        DriveFileEntry FindEntry(string relativePath);
        bool Exists(string relativePath);
        List<DriveFileEntry> ListChildren(string relativeDir);
        List<DriveFileEntry> ListDescendants(string relativeDir);

        string GetProbe(string fileId);
        void SetProbe(string fileId, string json);
    }

    public class DriveIndex : IDriveIndex
    {
        private const string SelectColumns =
            "file_id AS FileId, drive_id AS DriveId, drive_rank AS DriveRank, parent_id AS ParentId, " +
            "name AS Name, path AS Path, is_dir AS IsDirectory, size AS Size, md5 AS Md5, " +
            "modified AS ModifiedRaw, vmm_w AS VideoWidth, vmm_h AS VideoHeight, vmm_dur AS VideoDurationMs, " +
            "shortcut_target AS ShortcutTargetId";

        private readonly IDriveConfigService _configService;
        private readonly Logger _logger;

        public DriveIndex(IDriveConfigService configService, Logger logger)
        {
            _configService = configService;
            _logger = logger;
        }

        private string ConnectionString => $"Data Source={_configService.Config.IndexPath};Version=3;Pooling=True;";

        private SQLiteConnection Connect()
        {
            var connection = new SQLiteConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        public void EnsureCreated()
        {
            using var con = Connect();
            con.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            con.Execute(@"CREATE TABLE IF NOT EXISTS raw(
                drive_id TEXT, id TEXT, name TEXT, parent TEXT, mime TEXT, size INTEGER,
                md5 TEXT, modified TEXT, vmm_w INTEGER, vmm_h INTEGER, vmm_dur INTEGER, shortcut TEXT,
                PRIMARY KEY(drive_id, id));");
            con.Execute(@"CREATE TABLE IF NOT EXISTS entries(
                file_id TEXT PRIMARY KEY, drive_id TEXT, drive_rank INTEGER, parent_id TEXT, name TEXT,
                path TEXT, is_dir INTEGER, size INTEGER, md5 TEXT, modified TEXT,
                vmm_w INTEGER, vmm_h INTEGER, vmm_dur INTEGER, shortcut_target TEXT);");
            con.Execute("CREATE INDEX IF NOT EXISTS idx_entries_path ON entries(path);");
            con.Execute("CREATE INDEX IF NOT EXISTS idx_entries_parent ON entries(parent_id);");
            con.Execute(@"CREATE TABLE IF NOT EXISTS probe_cache(file_id TEXT PRIMARY KEY, json TEXT);");
        }

        // Stream a drive's raw nodes to disk one page-batch at a time (bounded memory).
        public void StageDrive(string driveId, IEnumerable<DriveRawNode> nodes)
        {
            using var con = Connect();
            con.Execute("DELETE FROM raw WHERE drive_id=@d", new { d = driveId });

            using var tx = con.BeginTransaction();
            using var cmd = new SQLiteCommand(
                "INSERT OR IGNORE INTO raw VALUES(@drive,@id,@name,@parent,@mime,@size,@md5,@mod,@w,@h,@dur,@sc)", con, tx);
            var pDrive = cmd.Parameters.Add("@drive", System.Data.DbType.String);
            var pId = cmd.Parameters.Add("@id", System.Data.DbType.String);
            var pName = cmd.Parameters.Add("@name", System.Data.DbType.String);
            var pParent = cmd.Parameters.Add("@parent", System.Data.DbType.String);
            var pMime = cmd.Parameters.Add("@mime", System.Data.DbType.String);
            var pSize = cmd.Parameters.Add("@size", System.Data.DbType.Int64);
            var pMd5 = cmd.Parameters.Add("@md5", System.Data.DbType.String);
            var pMod = cmd.Parameters.Add("@mod", System.Data.DbType.String);
            var pW = cmd.Parameters.Add("@w", System.Data.DbType.Int32);
            var pH = cmd.Parameters.Add("@h", System.Data.DbType.Int32);
            var pDur = cmd.Parameters.Add("@dur", System.Data.DbType.Int64);
            var pSc = cmd.Parameters.Add("@sc", System.Data.DbType.String);

            var count = 0;
            pDrive.Value = driveId;
            foreach (var n in nodes)
            {
                pId.Value = n.Id;
                pName.Value = n.Name;
                pParent.Value = (object)n.ParentId ?? System.DBNull.Value;
                pMime.Value = n.MimeType;
                pSize.Value = n.Size;
                pMd5.Value = (object)n.Md5 ?? System.DBNull.Value;
                pMod.Value = (object)n.ModifiedRaw ?? System.DBNull.Value;
                pW.Value = (object)n.VideoWidth ?? System.DBNull.Value;
                pH.Value = (object)n.VideoHeight ?? System.DBNull.Value;
                pDur.Value = (object)n.VideoDurationMs ?? System.DBNull.Value;
                pSc.Value = (object)n.ShortcutTargetId ?? System.DBNull.Value;
                cmd.ExecuteNonQuery();
                count++;
            }

            tx.Commit();
            _logger.Debug("Staged {0} raw nodes for drive {1}", count, driveId);
        }

        // Reconstruct cloud-root-relative paths from parent pointers and fill `entries`.
        // Peak RAM = id->name/parent maps for THIS drive only.
        public void MaterializeDrive(int rank, string driveId)
        {
            var name = new Dictionary<string, string>();
            var parent = new Dictionary<string, string>();

            using (var readCon = Connect())
            {
                foreach (var row in readCon.Query("SELECT id, name, parent FROM raw WHERE drive_id=@d", new { d = driveId }))
                {
                    string id = row.id;
                    name[id] = (string)row.name ?? string.Empty;
                    parent[id] = row.parent;
                }
            }

            var cache = new Dictionary<string, string>();

            string Resolve(string id)
            {
                if (id == driveId)
                {
                    return string.Empty;
                }

                if (cache.TryGetValue(id, out var cached))
                {
                    return cached;
                }

                if (!name.ContainsKey(id))
                {
                    return null;
                }

                var parentId = parent[id];
                string full;
                if (parentId == null)
                {
                    full = name[id];
                }
                else
                {
                    var parentPath = Resolve(parentId);
                    full = parentPath == null ? null : (parentPath.Length == 0 ? name[id] : parentPath + "/" + name[id]);
                }

                cache[id] = full;
                return full;
            }

            using var writeCon = Connect();
            writeCon.Execute("DELETE FROM entries WHERE drive_id=@d", new { d = driveId });

            using var readRaw = Connect();
            using var tx = writeCon.BeginTransaction();
            using var ins = new SQLiteCommand(
                "INSERT OR IGNORE INTO entries (file_id,drive_id,drive_rank,parent_id,name,path,is_dir,size,md5,modified,vmm_w,vmm_h,vmm_dur,shortcut_target) " +
                "VALUES(@fid,@did,@rk,@pid,@nm,@pt,@isdir,@sz,@md5,@mod,@w,@h,@dur,@st)",
                writeCon,
                tx);
            var pFid = ins.Parameters.Add("@fid", System.Data.DbType.String);
            var pDid = ins.Parameters.Add("@did", System.Data.DbType.String);
            var pRk = ins.Parameters.Add("@rk", System.Data.DbType.Int32);
            var pPid = ins.Parameters.Add("@pid", System.Data.DbType.String);
            var pNm = ins.Parameters.Add("@nm", System.Data.DbType.String);
            var pPt = ins.Parameters.Add("@pt", System.Data.DbType.String);
            var pIsDir = ins.Parameters.Add("@isdir", System.Data.DbType.Int32);
            var pSz = ins.Parameters.Add("@sz", System.Data.DbType.Int64);
            var pMd5 = ins.Parameters.Add("@md5", System.Data.DbType.String);
            var pMod = ins.Parameters.Add("@mod", System.Data.DbType.String);
            var pW = ins.Parameters.Add("@w", System.Data.DbType.Int32);
            var pH = ins.Parameters.Add("@h", System.Data.DbType.Int32);
            var pDur = ins.Parameters.Add("@dur", System.Data.DbType.Int64);
            var pSt = ins.Parameters.Add("@st", System.Data.DbType.String);

            pDid.Value = driveId;
            pRk.Value = rank;

            using (var cmd = new SQLiteCommand(
                "SELECT id, parent, name, mime, size, md5, modified, vmm_w, vmm_h, vmm_dur, shortcut FROM raw WHERE drive_id=@d", readRaw))
            {
                cmd.Parameters.AddWithValue("@d", driveId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var mime = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);

                    if (mime == DriveMime.Shortcut)
                    {
                        continue;
                    }

                    var path = Resolve(id);
                    if (path == null)
                    {
                        continue;
                    }

                    pFid.Value = id;
                    pPid.Value = reader.IsDBNull(1) ? (object)System.DBNull.Value : reader.GetString(1);
                    pNm.Value = reader.GetString(2);
                    pPt.Value = path;
                    pIsDir.Value = mime == DriveMime.Folder ? 1 : 0;
                    pSz.Value = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
                    pMd5.Value = reader.IsDBNull(5) ? (object)System.DBNull.Value : reader.GetString(5);
                    pMod.Value = reader.IsDBNull(6) ? (object)System.DBNull.Value : reader.GetString(6);
                    pW.Value = reader.IsDBNull(7) ? (object)System.DBNull.Value : reader.GetInt32(7);
                    pH.Value = reader.IsDBNull(8) ? (object)System.DBNull.Value : reader.GetInt32(8);
                    pDur.Value = reader.IsDBNull(9) ? (object)System.DBNull.Value : reader.GetInt64(9);
                    pSt.Value = reader.IsDBNull(10) ? (object)System.DBNull.Value : reader.GetString(10);
                    ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
            writeCon.Execute("DELETE FROM raw WHERE drive_id=@d", new { d = driveId });
            _logger.Info(
                "Materialized drive {0} (rank {1}): {2} entries",
                driveId,
                rank,
                name.Count(kvp => cache.TryGetValue(kvp.Key, out var p) && p != null));
        }

        public bool HasData()
        {
            using var con = Connect();
            var name = con.ExecuteScalar<string>(
                "SELECT name FROM sqlite_master WHERE type='table' AND name='entries'");
            if (name == null)
            {
                return false;
            }

            return con.ExecuteScalar<long>("SELECT COUNT(1) FROM entries") > 0;
        }

        public DriveFileEntry FindEntry(string relativePath)
        {
            using var con = Connect();
            return con.QueryFirstOrDefault<DriveFileEntry>(
                $"SELECT {SelectColumns} FROM entries WHERE path=@p ORDER BY drive_rank LIMIT 1",
                new { p = relativePath });
        }

        public bool Exists(string relativePath)
        {
            using var con = Connect();
            return con.ExecuteScalar<long>("SELECT COUNT(1) FROM entries WHERE path=@p", new { p = relativePath }) > 0;
        }

        public List<DriveFileEntry> ListChildren(string relativeDir)
        {
            using var con = Connect();

            // Children = entries whose parent is the folder(s) at relativeDir. The same folder
            // path can exist on multiple drives (union), so match all of them by parent_id.
            // Union merge: when the same child name exists on multiple drives, the lowest
            // drive_rank wins (rclone union search_policy=ff).
            return con.Query<DriveFileEntry>(
                    $"SELECT {SelectColumns} FROM entries WHERE parent_id IN " +
                    "(SELECT file_id FROM entries WHERE path=@d AND is_dir=1) ORDER BY drive_rank",
                    new { d = relativeDir })
                .GroupBy(e => e.Name)
                .Select(g => g.First())
                .ToList();
        }

        // All entries (files and folders) beneath relativeDir, recursively. Union merge:
        // same relative path on multiple drives collapses to the lowest drive_rank.
        public List<DriveFileEntry> ListDescendants(string relativeDir)
        {
            using var con = Connect();

            var pattern = EscapeLike(relativeDir) + "/%";

            return con.Query<DriveFileEntry>(
                    $"SELECT {SelectColumns} FROM entries WHERE path LIKE @p ESCAPE '\\' ORDER BY drive_rank",
                    new { p = pattern })
                .GroupBy(e => e.Path)
                .Select(g => g.First())
                .ToList();
        }

        private static string EscapeLike(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
        }

        public string GetProbe(string fileId)
        {
            using var con = Connect();
            return con.QueryFirstOrDefault<string>("SELECT json FROM probe_cache WHERE file_id=@f", new { f = fileId });
        }

        public void SetProbe(string fileId, string json)
        {
            using var con = Connect();
            con.Execute("INSERT OR REPLACE INTO probe_cache(file_id, json) VALUES(@f, @j)", new { f = fileId, j = json });
        }
    }
}
