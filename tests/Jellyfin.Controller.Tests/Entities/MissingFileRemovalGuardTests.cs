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
            Assert.True(MissingFileRemovalGuard.CanRemove(Path.Combine(directory, "deleted.mkv"), directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFileAtEmptyExposedMountpointIsKept()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(MissingFileRemovalGuard.CanRemove(Path.Combine(directory, "missing.mkv"), directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void MissingChildDirectoryUnderAvailableLibraryRootCanBeRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "movie");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "available.mkv"), string.Empty);
            Assert.True(MissingFileRemovalGuard.CanRemove(child, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmptyNestedMountpointUnderAvailableLibraryRootIsKept()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var branch = Path.Combine(root, "branch");
        Directory.CreateDirectory(branch);
        try
        {
            File.WriteAllText(Path.Combine(root, "available.mkv"), string.Empty);
            Assert.False(MissingFileRemovalGuard.CanRemove(Path.Combine(branch, "missing.mkv"), root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingFileUnderUnavailableDirectoryIsKept()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.False(MissingFileRemovalGuard.CanRemove(Path.Combine(directory, "unavailable.mkv"), directory));
    }

    [Fact]
    public void InvalidPathIsKept()
    {
        Assert.False(MissingFileRemovalGuard.CanRemove("\0"));
    }
}
