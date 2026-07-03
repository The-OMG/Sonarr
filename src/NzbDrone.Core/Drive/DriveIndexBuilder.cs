using System.Diagnostics;
using System.Linq;
using NLog;

namespace NzbDrone.Core.Drive
{
    public interface IDriveIndexBuilder
    {
        void BuildAll();
    }

    public class DriveIndexBuilder : IDriveIndexBuilder
    {
        private readonly IDriveConfigService _configService;
        private readonly IDriveClient _client;
        private readonly IDriveIndex _index;
        private readonly Logger _logger;

        public DriveIndexBuilder(IDriveConfigService configService, IDriveClient client, IDriveIndex index, Logger logger)
        {
            _configService = configService;
            _client = client;
            _index = index;
            _logger = logger;
        }

        public void BuildAll()
        {
            var config = _configService.Config;

            if (!config.Enabled)
            {
                _logger.Debug("Native Drive backend disabled; skipping index build");
                return;
            }

            _index.EnsureCreated();

            foreach (var drive in config.Drives.OrderBy(d => d.Rank))
            {
                var stopwatch = Stopwatch.StartNew();
                _logger.Info("Indexing Drive {0} ({1}) rank {2}", drive.Name, drive.DriveId, drive.Rank);

                // EnumerateDrive yields lazily, so StageDrive streams it to disk (bounded memory).
                _index.StageDrive(drive.DriveId, _client.EnumerateDrive(drive.DriveId));
                _index.MaterializeDrive(drive.Rank, drive.DriveId);

                _logger.Info("Indexed Drive {0} in {1:0}s", drive.Name, stopwatch.Elapsed.TotalSeconds);
            }
        }
    }
}
