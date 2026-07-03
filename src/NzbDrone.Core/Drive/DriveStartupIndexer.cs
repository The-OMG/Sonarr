using System;
using System.Threading;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Drive
{
    // On startup, ensure the Drive index exists and build it (in the background) if empty.
    // A full build is API-bound and long, so it never blocks boot; rescans read the index.
    public class DriveStartupIndexer : IHandle<ApplicationStartedEvent>
    {
        private readonly IDriveConfigService _configService;
        private readonly IDriveIndex _index;
        private readonly IDriveIndexBuilder _builder;
        private readonly Logger _logger;

        public DriveStartupIndexer(IDriveConfigService configService, IDriveIndex index, IDriveIndexBuilder builder, Logger logger)
        {
            _configService = configService;
            _index = index;
            _builder = builder;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            if (!_configService.Config.Enabled)
            {
                return;
            }

            _index.EnsureCreated();

            if (_index.HasData())
            {
                _logger.Debug("Drive index already populated; skipping startup build");
                return;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    _logger.Info("Building Drive index for the first time (background)");
                    _builder.BuildAll();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Drive index build failed");
                }
            })
            {
                IsBackground = true,
                Name = "gdrive-index-build"
            };

            thread.Start();
        }
    }
}
