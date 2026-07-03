using System;
using System.IO;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.MediaFiles.MediaInfo;

namespace NzbDrone.Core.Drive
{
    // Decorates IVideoFileInfoReader. For a Drive-backed file (immutable R/O branch) the full
    // ffprobe result is cached in the index keyed by the Drive file ID, so a given file is
    // probed at most once ever — even across app-DB resets. GetRunTime is served for free from
    // Drive's videoMediaMetadata duration when present. Local-branch files pass straight
    // through (ffprobe on real local disk is already fast).
    public class DriveCachingVideoFileInfoReader : IVideoFileInfoReader
    {
        private readonly IVideoFileInfoReader _inner;
        private readonly IDriveIndex _index;
        private readonly IDriveConfigService _configService;
        private readonly Logger _logger;

        private readonly bool _enabled;
        private readonly string _cloudRoot;
        private readonly string _localBranch;

        public DriveCachingVideoFileInfoReader(IVideoFileInfoReader inner, IDriveIndex index, IDriveConfigService configService, Logger logger)
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

        public MediaInfoModel GetMediaInfo(string filename)
        {
            var entry = ResolveDriveEntry(filename);
            if (entry == null)
            {
                return _inner.GetMediaInfo(filename);
            }

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

            var info = _inner.GetMediaInfo(filename);
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

        public TimeSpan? GetRunTime(string filename)
        {
            var entry = ResolveDriveEntry(filename);
            if (entry == null)
            {
                return _inner.GetRunTime(filename);
            }

            // Runtime straight from Drive metadata — no byte reads at all.
            if (entry.VideoDurationMs is > 0)
            {
                return TimeSpan.FromMilliseconds(entry.VideoDurationMs.Value);
            }

            // Else fall back to a cached probe if we have one.
            var cached = _index.GetProbe(entry.FileId);
            if (cached != null)
            {
                try
                {
                    var model = JsonConvert.DeserializeObject<MediaInfoModel>(cached);
                    if (model != null && model.RunTime > TimeSpan.Zero)
                    {
                        return model.RunTime;
                    }
                }
                catch
                {
                    // fall through to a live probe
                }
            }

            return _inner.GetRunTime(filename);
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
