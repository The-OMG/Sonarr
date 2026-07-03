using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Drive
{
    public class DriveSource
    {
        // Union search order (lower wins, mirrors rclone union search_policy=ff).
        public int Rank { get; set; }
        public string DriveId { get; set; }
        public string Name { get; set; }
    }

    public class DriveConfig
    {
        public bool Enabled { get; set; }
        public string ServiceAccountFile { get; set; }

        // Absolute mount root the union is presented at (e.g. /home/theomg/cloud).
        public string CloudRoot { get; set; }

        // Local RW branch of the union (e.g. /home/theomg/plexmedia). Reads that resolve
        // here are served by the real filesystem; only Drive-only paths hit the index.
        public string LocalBranch { get; set; }

        // Where the sqlite index lives; defaults to <AppData>/gdrive-index.sqlite.
        public string IndexPath { get; set; }

        public List<DriveSource> Drives { get; set; } = new ();
    }

    public interface IDriveConfigService
    {
        DriveConfig Config { get; }
    }

    public class DriveConfigService : IDriveConfigService
    {
        private readonly Logger _logger;
        private readonly DriveConfig _config;

        public DriveConfigService(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _logger = logger;
            _config = Load(appFolderInfo);
        }

        public DriveConfig Config => _config;

        private DriveConfig Load(IAppFolderInfo appFolderInfo)
        {
            var path = Path.Combine(appFolderInfo.AppDataFolder, "gdrive.json");

            if (!File.Exists(path))
            {
                _logger.Debug("No gdrive.json at {0}; native Drive backend disabled", path);
                return new DriveConfig { Enabled = false };
            }

            try
            {
                var config = JsonConvert.DeserializeObject<DriveConfig>(File.ReadAllText(path)) ?? new DriveConfig();

                if (string.IsNullOrWhiteSpace(config.IndexPath))
                {
                    config.IndexPath = Path.Combine(appFolderInfo.AppDataFolder, "gdrive-index.sqlite");
                }

                _logger.Info(
                    "Native Drive backend {0}: {1} drive(s), index {2}",
                    config.Enabled ? "enabled" : "present-but-disabled",
                    config.Drives.Count,
                    config.IndexPath);

                return config;
            }
            catch (System.Exception ex)
            {
                _logger.Error(ex, "Failed to parse gdrive.json at {0}; native Drive backend disabled", path);
                return new DriveConfig { Enabled = false };
            }
        }
    }
}
