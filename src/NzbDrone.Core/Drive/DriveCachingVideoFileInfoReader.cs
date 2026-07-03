using System;
using System.IO;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.MediaFiles.MediaInfo;

namespace NzbDrone.Core.Drive
{
    // Decorates IVideoFileInfoReader so the arrs NEVER touch the rclone FUSE mount for a
    // Drive-backed file. Mediainfo is:
    //   1. served from the index probe_cache if already probed (probe-once, immutable branch);
    //   2. otherwise probed by pulling the file HEADER straight from the Drive API (by file
    //      ID, no FUSE) into a temp file and ffprobing that — full-download fallback for
    //      containers whose metadata isn't in the first chunk (e.g. mp4 moov-at-end);
    //   3. cached by Drive file ID forever (schema-revision invalidated).
    // Runtime comes free from Drive videoMediaMetadata when present. Local-branch files pass
    // straight through (ffprobe on real local disk is fine and never involves FUSE).
    public class DriveCachingVideoFileInfoReader : IVideoFileInfoReader
    {
        private const long HeaderBytes = 64L * 1024 * 1024;   // 64 MiB covers virtually all real headers

        private readonly IVideoFileInfoReader _inner;
        private readonly IDriveIndex _index;
        private readonly IDriveClient _driveClient;
        private readonly IDriveConfigService _configService;
        private readonly Logger _logger;

        private readonly bool _enabled;
        private readonly string _cloudRoot;
        private readonly string _localBranch;

        public DriveCachingVideoFileInfoReader(IVideoFileInfoReader inner, IDriveIndex index, IDriveClient driveClient, IDriveConfigService configService, Logger logger)
        {
            _inner = inner;
            _index = index;
            _driveClient = driveClient;
            _configService = configService;
            _logger = logger;

            var config = configService.Config;
            _enabled = config.Enabled && !string.IsNullOrWhiteSpace(config.CloudRoot);
            _cloudRoot = config.CloudRoot?.TrimEnd('/');
            _localBranch = config.LocalBranch?.TrimEnd('/');
        }

        public MediaInfoModel GetMediaInfo(string filename)
        {
            var entry = ResolveDriveEntry(filename);
            return entry == null ? _inner.GetMediaInfo(filename) : GetCachedOrProbe(entry);
        }

        public TimeSpan? GetRunTime(string filename)
        {
            var entry = ResolveDriveEntry(filename);
            if (entry == null)
            {
                return _inner.GetRunTime(filename);
            }

            // Runtime straight from Drive metadata — no bytes read at all.
            if (entry.VideoDurationMs is > 0)
            {
                return TimeSpan.FromMilliseconds(entry.VideoDurationMs.Value);
            }

            var model = GetCachedOrProbe(entry);
            return model != null && model.RunTime > TimeSpan.Zero ? model.RunTime : (TimeSpan?)null;
        }

        private MediaInfoModel GetCachedOrProbe(DriveFileEntry entry)
        {
            var cached = _index.GetProbe(entry.FileId);
            if (cached != null)
            {
                try
                {
                    var model = JsonConvert.DeserializeObject<MediaInfoModel>(cached);
                    if (model != null && model.SchemaRevision >= VideoFileInfoReader.CURRENT_MEDIA_INFO_SCHEMA_REVISION)
                    {
                        return model;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Corrupt probe cache for {0}; re-probing", entry.Path);
                }
            }

            var info = ProbeViaDrive(entry);
            if (info != null)
            {
                try
                {
                    _index.SetProbe(entry.FileId, JsonConvert.SerializeObject(info));
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to cache mediainfo for {0}", entry.Path);
                }
            }

            return info;
        }

        // FUSE-free probe: pull only the file HEADER via the Drive API to a temp file and
        // ffprobe that. Header-only by design — never download whole multi-GB files (a moov-
        // at-end mp4 or a broken/zero-content file just yields no mediainfo, which is fine and
        // bounded). Returns null on failure (no FUSE fallback — the arrs must never block on
        // the mount).
        private MediaInfoModel ProbeViaDrive(DriveFileEntry entry)
        {
            var temp = Path.Combine(Path.GetTempPath(), "gdrive-probe-" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
            try
            {
                using (var fs = File.Create(temp))
                {
                    _driveClient.DownloadPrefix(entry.FileId, HeaderBytes, fs);
                }

                return _inner.GetMediaInfo(temp);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Drive-API mediainfo probe failed for {0}", entry.Path);
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                    // best-effort temp cleanup
                }
            }
        }

        // A Drive-backed entry iff the path is under CloudRoot, is NOT present on the local
        // union branch, and exists in the index.
        private DriveFileEntry ResolveDriveEntry(string filename)
        {
            if (!_enabled || filename == null || _cloudRoot == null)
            {
                return null;
            }

            if (!filename.StartsWith(_cloudRoot + "/", StringComparison.Ordinal))
            {
                return null;
            }

            var relative = filename.Substring(_cloudRoot.Length + 1).TrimEnd('/');
            if (relative.Length == 0)
            {
                return null;
            }

            if (_localBranch != null && File.Exists(_localBranch + "/" + relative))
            {
                return null;
            }

            var entry = _index.FindEntry(relative);
            return entry != null && !entry.IsDirectory ? entry : null;
        }
    }
}
