using System;

namespace NzbDrone.Core.Drive
{
    // A raw node as returned by the Drive API (pre path-reconstruction).
    public class DriveRawNode
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ParentId { get; set; }
        public string MimeType { get; set; }
        public long Size { get; set; }
        public string Md5 { get; set; }
        public string ModifiedRaw { get; set; }
        public int? VideoWidth { get; set; }
        public int? VideoHeight { get; set; }
        public long? VideoDurationMs { get; set; }
        public string ShortcutTargetId { get; set; }

        public bool IsFolder => MimeType == DriveMime.Folder;
        public bool IsShortcut => MimeType == DriveMime.Shortcut;
    }

    // A materialized index entry: a file or folder resolved to a cloud-root-relative path.
    public class DriveFileEntry
    {
        public string FileId { get; set; }
        public string DriveId { get; set; }
        public int DriveRank { get; set; }
        public string ParentId { get; set; }
        public string Name { get; set; }

        // Cloud-root-relative, '/'-joined, no leading/trailing slash.
        public string Path { get; set; }

        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string Md5 { get; set; }
        public string ModifiedRaw { get; set; }
        public int? VideoWidth { get; set; }
        public int? VideoHeight { get; set; }
        public long? VideoDurationMs { get; set; }
        public string ShortcutTargetId { get; set; }

        public DateTime LastWriteUtc =>
            DateTime.TryParse(ModifiedRaw, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.UtcNow;
    }

    public static class DriveMime
    {
        public const string Folder = "application/vnd.google-apps.folder";
        public const string Shortcut = "application/vnd.google-apps.shortcut";
    }
}
