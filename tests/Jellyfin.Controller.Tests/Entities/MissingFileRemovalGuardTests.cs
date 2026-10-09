using System;
using System.IO;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

public class MissingFileRemovalGuardTests
{
    [Fact]
    public void MissingFileBesideAvailableEntryCanBeRemoved()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "available.mkv"), string.Empty);
            Assert.True(MissingFileRemovalGuard.CanRemove(Path.Combine(directory, "deleted.mkv")));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void MissingFileAtEmptyExposedMountpointIsKept()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(MissingFileRemovalGuard.CanRemove(Path.Combine(directory, "missing.mkv")));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void MissingFileUnderUnavailableDirectoryIsKept()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.False(MissingFileRemovalGuard.CanRemove(Path.Combine(directory, "unavailable.mkv")));
    }

    [Fact]
    public void InvalidPathIsKept()
    {
        Assert.False(MissingFileRemovalGuard.CanRemove("\0"));
    }
}
