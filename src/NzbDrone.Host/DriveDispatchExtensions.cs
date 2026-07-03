using DryIoc;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Drive;
using NzbDrone.Core.MediaFiles.MediaInfo;

namespace NzbDrone.Host
{
    public static class DriveDispatchExtensions
    {
        // Wrap the real IDiskProvider with DispatchDiskProvider. Convention registration
        // (RegisterMany) also picks DispatchDiskProvider up as a plain IDiskProvider, which
        // would collide with the real provider, so drop that registration first and re-add it
        // as a proper decorator. DispatchDiskProvider passes through when the backend is
        // disabled, so this is safe to wire unconditionally.
        public static IContainer AddDriveDispatch(this IContainer container)
        {
            container.Unregister<IDiskProvider>(
                condition: factory => factory.ImplementationType == typeof(DispatchDiskProvider));

            container.Register<IDiskProvider, DispatchDiskProvider>(reuse: Reuse.Singleton, setup: Setup.Decorator);

            // Same decorator pattern for mediainfo: cache ffprobe results by Drive file ID
            // (probe-once) and serve runtime from Drive videoMediaMetadata.
            container.Unregister<IVideoFileInfoReader>(
                condition: factory => factory.ImplementationType == typeof(DriveCachingVideoFileInfoReader));

            container.Register<IVideoFileInfoReader, DriveCachingVideoFileInfoReader>(reuse: Reuse.Singleton, setup: Setup.Decorator);

            return container;
        }
    }
}
