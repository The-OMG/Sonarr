using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using NLog;

namespace NzbDrone.Core.Drive
{
    public interface IDriveClient
    {
        IEnumerable<DriveRawNode> EnumerateDrive(string driveId);

        // Download the first `bytes` of a file (or the whole file if bytes<=0) straight from
        // the Drive API by file ID — NO FUSE. Used for FUSE-free mediainfo probing.
        void DownloadPrefix(string fileId, long bytes, Stream destination);
    }

    public class DriveClient : IDriveClient
    {
        private const string Fields =
            "nextPageToken,files(id,name,parents,mimeType,size,md5Checksum,modifiedTime,videoMediaMetadata,shortcutDetails)";

        private readonly IDriveConfigService _configService;
        private readonly Logger _logger;

        private DriveService _service;

        public DriveClient(IDriveConfigService configService, Logger logger)
        {
            _configService = configService;
            _logger = logger;
        }

        public IEnumerable<DriveRawNode> EnumerateDrive(string driveId)
        {
            var service = GetService();
            string pageToken = null;

            do
            {
                var request = service.Files.List();
                request.Corpora = "drive";
                request.DriveId = driveId;
                request.IncludeItemsFromAllDrives = true;
                request.SupportsAllDrives = true;
                request.PageSize = 1000;
                request.Q = "trashed=false";
                request.Fields = Fields;
                request.PageToken = pageToken;

                var response = request.Execute();

                foreach (var file in response.Files)
                {
                    yield return Map(file);
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }

        public void DownloadPrefix(string fileId, long bytes, Stream destination)
        {
            var service = GetService();
            var request = service.Files.Get(fileId);
            request.SupportsAllDrives = true;

            if (bytes > 0)
            {
                request.DownloadRange(destination, new RangeHeaderValue(0, bytes - 1));
            }
            else
            {
                request.Download(destination);
            }
        }

        private DriveService GetService()
        {
            if (_service != null)
            {
                return _service;
            }

            var saFile = _configService.Config.ServiceAccountFile;

            if (string.IsNullOrWhiteSpace(saFile) || !File.Exists(saFile))
            {
                throw new InvalidOperationException($"Drive service account file not found: {saFile}");
            }

            GoogleCredential credential;
            using (var stream = File.OpenRead(saFile))
            {
                credential = GoogleCredential.FromStream(stream).CreateScoped(DriveService.ScopeConstants.DriveReadonly);
            }

            _service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "sonarr-gdrive"
            });

            return _service;
        }

        private static DriveRawNode Map(Google.Apis.Drive.v3.Data.File file)
        {
            var video = file.VideoMediaMetadata;

            return new DriveRawNode
            {
                Id = file.Id,
                Name = file.Name ?? string.Empty,
                ParentId = file.Parents != null && file.Parents.Count > 0 ? file.Parents[0] : null,
                MimeType = file.MimeType ?? string.Empty,
                Size = file.Size ?? 0,
                Md5 = file.Md5Checksum,
                ModifiedRaw = file.ModifiedTimeRaw,
                VideoWidth = (int?)video?.Width,
                VideoHeight = (int?)video?.Height,
                VideoDurationMs = video?.DurationMillis,
                ShortcutTargetId = file.ShortcutDetails?.TargetId
            };
        }
    }
}
