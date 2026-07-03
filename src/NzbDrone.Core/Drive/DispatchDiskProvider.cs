using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.Drive
{
    // Decorates the real IDiskProvider. Reads/lists/stats that resolve to a Drive-only path
    // (under CloudRoot, not present on the local union branch) are served from the sqlite
    // index instead of the rclone FUSE mount. The local branch and ALL writes pass straight
    // through to the inner provider, so the union keeps routing creates to local unchanged.
    public class DispatchDiskProvider : IDiskProvider
    {
        private readonly IDiskProvider _inner;
        private readonly IDriveIndex _index;
        private readonly IDriveConfigService _configService;
        private readonly Logger _logger;

        private readonly string _cloudRoot;
        private readonly string _localBranch;
        private readonly bool _enabled;

        public DispatchDiskProvider(IDiskProvider inner, IDriveIndex index, IDriveConfigService configService, Logger logger)
        {
            _inner = inner;
            _index = index;
            _configService = configService;
            _logger = logger;

            var config = configService.Config;
            _enabled = config.Enabled && !string.IsNullOrWhiteSpace(config.CloudRoot);
            _cloudRoot = config.CloudRoot?.TrimEnd('/');
            _localBranch = config.LocalBranch?.TrimEnd('/');
        }

        // --- dispatch helpers ---------------------------------------------------------------

        private bool TryRelative(string path, out string relative)
        {
            relative = null;

            if (!_enabled || path == null || _cloudRoot == null)
            {
                return false;
            }

            if (!path.StartsWith(_cloudRoot + "/", StringComparison.Ordinal))
            {
                return false;
            }

            relative = path.Substring(_cloudRoot.Length + 1).TrimEnd('/');
            return relative.Length > 0;
        }

        private string LocalPath(string relative) => _localBranch + "/" + relative;

        private string ToCloud(string localPath) => string.Concat(_cloudRoot, localPath.AsSpan(_localBranch.Length));

        private bool OnLocalBranch(string relative) => _localBranch != null && _inner.FileExists(LocalPath(relative));

        private bool LocalFolder(string relative) => _localBranch != null && _inner.FolderExists(LocalPath(relative));

        // --- reads served from the index ----------------------------------------------------

        public bool FileExists(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.FileExists(path);
            }

            if (OnLocalBranch(relative))
            {
                return true;
            }

            var entry = _index.FindEntry(relative);
            return entry != null && !entry.IsDirectory;
        }

        public bool FileExists(string path, StringComparison stringComparison) => FileExists(path);

        public bool FolderExists(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.FolderExists(path);
            }

            if (LocalFolder(relative))
            {
                return true;
            }

            var entry = _index.FindEntry(relative);
            return entry != null && entry.IsDirectory;
        }

        public long GetFileSize(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.GetFileSize(path);
            }

            var localPath = LocalPath(relative);
            if (_inner.FileExists(localPath))
            {
                return _inner.GetFileSize(localPath);
            }

            var entry = _index.FindEntry(relative);
            return entry?.Size ?? _inner.GetFileSize(path);
        }

        public DateTime FileGetLastWrite(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.FileGetLastWrite(path);
            }

            var localPath = LocalPath(relative);
            if (_inner.FileExists(localPath))
            {
                return _inner.FileGetLastWrite(localPath);
            }

            var entry = _index.FindEntry(relative);
            return entry?.LastWriteUtc ?? _inner.FileGetLastWrite(path);
        }

        public DateTime FolderGetLastWrite(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.FolderGetLastWrite(path);
            }

            if (LocalFolder(relative))
            {
                return _inner.FolderGetLastWrite(LocalPath(relative));
            }

            var entry = _index.FindEntry(relative);
            return entry?.LastWriteUtc ?? _inner.FolderGetLastWrite(path);
        }

        public DateTime FolderGetCreationTime(string path) => FolderGetLastWrite(path);

        public IEnumerable<string> GetFiles(string path, bool recursive)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.GetFiles(path, recursive);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<string>();

            var localDir = LocalPath(relative);
            if (_inner.FolderExists(localDir))
            {
                foreach (var localFile in _inner.GetFiles(localDir, recursive))
                {
                    var cloud = ToCloud(localFile);
                    if (seen.Add(cloud))
                    {
                        results.Add(cloud);
                    }
                }
            }

            var indexFiles = recursive
                ? _index.ListDescendants(relative).Where(e => !e.IsDirectory)
                : _index.ListChildren(relative).Where(e => !e.IsDirectory);

            foreach (var entry in indexFiles)
            {
                var cloud = _cloudRoot + "/" + entry.Path;
                if (seen.Add(cloud))
                {
                    results.Add(cloud);
                }
            }

            return results;
        }

        public IEnumerable<string> GetDirectories(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.GetDirectories(path);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<string>();

            var localDir = LocalPath(relative);
            if (_inner.FolderExists(localDir))
            {
                foreach (var localSub in _inner.GetDirectories(localDir))
                {
                    var cloud = ToCloud(localSub);
                    if (seen.Add(cloud))
                    {
                        results.Add(cloud);
                    }
                }
            }

            foreach (var entry in _index.ListChildren(relative).Where(e => e.IsDirectory))
            {
                var cloud = _cloudRoot + "/" + entry.Path;
                if (seen.Add(cloud))
                {
                    results.Add(cloud);
                }
            }

            return results;
        }

        public long GetFolderSize(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.GetFolderSize(path);
            }

            long total = 0;

            var localDir = LocalPath(relative);
            if (_inner.FolderExists(localDir))
            {
                total += _inner.GetFolderSize(localDir);
            }

            total += _index.ListDescendants(relative).Where(e => !e.IsDirectory).Sum(e => e.Size);
            return total;
        }

        public bool FolderEmpty(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.FolderEmpty(path);
            }

            var localDir = LocalPath(relative);
            if (_inner.FolderExists(localDir) && !_inner.FolderEmpty(localDir))
            {
                return false;
            }

            return _index.ListChildren(relative).Count == 0;
        }

        public FileAttributes GetFileAttributes(string path)
        {
            if (!TryRelative(path, out var relative))
            {
                return _inner.GetFileAttributes(path);
            }

            var localPath = LocalPath(relative);
            if (_inner.FileExists(localPath) || _inner.FolderExists(localPath))
            {
                return _inner.GetFileAttributes(localPath);
            }

            var entry = _index.FindEntry(relative);
            if (entry == null)
            {
                return _inner.GetFileAttributes(path);
            }

            return entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        }

        public FileStream OpenReadStream(string path)
        {
            // Byte reads are rare (mediainfo is served from Drive metadata + the probe cache).
            // Prefer the real local file; otherwise fall back to the FUSE mount, which rclone
            // still serves correctly for Drive-only paths.
            if (TryRelative(path, out var relative) && _inner.FileExists(LocalPath(relative)))
            {
                return _inner.OpenReadStream(LocalPath(relative));
            }

            return _inner.OpenReadStream(path);
        }

        // --- everything else delegates unchanged (writes route through the union to local) ---

        public long? GetAvailableSpace(string path) => _inner.GetAvailableSpace(path);
        public long? GetTotalSize(string path) => _inner.GetTotalSize(path);
        public void InheritFolderPermissions(string filename) => _inner.InheritFolderPermissions(filename);
        public void SetEveryonePermissions(string filename) => _inner.SetEveryonePermissions(filename);
        public void SetFilePermissions(string path, string mask, string group) => _inner.SetFilePermissions(path, mask, group);
        public void SetPermissions(string path, string mask, string group) => _inner.SetPermissions(path, mask, group);
        public void CopyPermissions(string sourcePath, string targetPath) => _inner.CopyPermissions(sourcePath, targetPath);
        public void EnsureFolder(string path) => _inner.EnsureFolder(path);
        public bool FolderWritable(string path) => _inner.FolderWritable(path);
        public void CreateFolder(string path) => _inner.CreateFolder(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public void CloneFile(string source, string destination, bool overwrite = false) => _inner.CloneFile(source, destination, overwrite);
        public void CopyFile(string source, string destination, bool overwrite = false) => _inner.CopyFile(source, destination, overwrite);
        public void MoveFile(string source, string destination, bool overwrite = false) => _inner.MoveFile(source, destination, overwrite);
        public void MoveFolder(string source, string destination) => _inner.MoveFolder(source, destination);
        public bool TryRenameFile(string source, string destination) => _inner.TryRenameFile(source, destination);
        public bool TryCreateHardLink(string source, string destination) => _inner.TryCreateHardLink(source, destination);
        public bool TryCreateRefLink(string source, string destination) => _inner.TryCreateRefLink(source, destination);
        public void DeleteFolder(string path, bool recursive) => _inner.DeleteFolder(path, recursive);
        public string ReadAllText(string filePath) => _inner.ReadAllText(filePath);
        public void WriteAllText(string filename, string contents) => _inner.WriteAllText(filename, contents);
        public void FolderSetLastWriteTime(string path, DateTime dateTime) => _inner.FolderSetLastWriteTime(path, dateTime);
        public void FileSetLastWriteTime(string path, DateTime dateTime) => _inner.FileSetLastWriteTime(path, dateTime);
        public bool IsFileLocked(string path) => _inner.IsFileLocked(path);
        public string GetPathRoot(string path) => _inner.GetPathRoot(path);
        public string GetParentFolder(string path) => _inner.GetParentFolder(path);
        public void EmptyFolder(string path) => _inner.EmptyFolder(path);
        public string GetVolumeLabel(string path) => _inner.GetVolumeLabel(path);
        public FileStream OpenWriteStream(string path) => _inner.OpenWriteStream(path);
        public List<IMount> GetMounts() => _inner.GetMounts();
        public IMount GetMount(string path) => _inner.GetMount(path);
        public List<DirectoryInfo> GetDirectoryInfos(string path) => _inner.GetDirectoryInfos(path);
        public List<FileInfo> GetFileInfos(string path) => _inner.GetFileInfos(path);
        public void RemoveEmptySubfolders(string path) => _inner.RemoveEmptySubfolders(path);
        public void SaveStream(Stream stream, string path) => _inner.SaveStream(stream, path);
        public bool IsValidFolderPermissionMask(string mask) => _inner.IsValidFolderPermissionMask(mask);
    }
}
