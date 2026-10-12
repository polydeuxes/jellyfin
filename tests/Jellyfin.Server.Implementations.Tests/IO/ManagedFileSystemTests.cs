using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Server.Implementations.IO;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.IO;

public partial class ManagedFileSystemTests
{
    private readonly IFixture _fixture;
    private readonly ManagedFileSystem _sut;

    public ManagedFileSystemTests()
    {
        _fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        _sut = _fixture.Create<ManagedFileSystem>();
    }

    [Fact]
    public void GetScanFileSystemEntries_ListsTwoExistingFiles()
    {
        var directory = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(directory.FullName, "first.mkv"), "one");
        File.WriteAllText(Path.Combine(directory.FullName, "second.mkv"), "two");

        var entries = _sut.GetScanFileSystemEntries(directory.FullName).ToArray();

        Assert.Equal(new[] { "first.mkv", "second.mkv" }, entries.Select(entry => entry.Name).OrderBy(name => name));
        Assert.All(entries, entry => Assert.True(entry.Exists && !entry.IsDirectory));
        Directory.Delete(directory.FullName, true);
    }

    [Fact]
    public void GetScanFileSystemEntries_TraversableNonListableDirectory_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Unix file modes");
            return;
        }

        Assert.SkipWhen(Environment.UserName == "root", "Requires unprivileged Unix user");
        var directory = Directory.CreateTempSubdirectory();
        var first = Path.Combine(directory.FullName, "first.mkv");
        var second = Path.Combine(directory.FullName, "second.mkv");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        var priorMode = File.GetUnixFileMode(directory.FullName);
        try
        {
            File.SetUnixFileMode(directory.FullName, UnixFileMode.UserExecute);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.Throws<UnauthorizedAccessException>(() => _sut.GetScanFileSystemEntries(directory.FullName).ToArray());
            // Folder catches the listing failure before comparing or deleting stored children.
            var storedChildren = new[] { first, second };
            Assert.Equal(2, storedChildren.Count(File.Exists));
        }
        finally
        {
            File.SetUnixFileMode(directory.FullName, priorMode);
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task FolderValidation_TraversableNonListableDirectory_KeepsTwoStoredFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Unix file modes");
            return;
        }

        Assert.SkipWhen(Environment.UserName == "root", "Requires unprivileged Unix user");
        var directory = Directory.CreateTempSubdirectory();
        var firstPath = Path.Combine(directory.FullName, "first.mkv");
        var secondPath = Path.Combine(directory.FullName, "second.mkv");
        await File.WriteAllTextAsync(firstPath, "one", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(secondPath, "two", TestContext.Current.CancellationToken).ConfigureAwait(true);
        var priorMode = File.GetUnixFileMode(directory.FullName);
        var previousLibrary = BaseItem.LibraryManager;
        var previousRepository = BaseItem.ItemRepository;
        var previousLogger = BaseItem.Logger;
        var previousMediaSourceManager = BaseItem.MediaSourceManager;
        var library = new Mock<ILibraryManager>();
        var repository = new Mock<MediaBrowser.Controller.Persistence.IItemRepository>(MockBehavior.Strict);
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        try
        {
            BaseItem.LibraryManager = library.Object;
            BaseItem.ItemRepository = repository.Object;
            BaseItem.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<BaseItem>.Instance;
            BaseItem.MediaSourceManager = mediaSourceManager.Object;
            var first = new Movie { Id = Guid.NewGuid(), Path = firstPath };
            var second = new Movie { Id = Guid.NewGuid(), Path = secondPath };
            repository.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([first, second]);
            var folder = new Folder { Id = Guid.NewGuid(), Path = directory.FullName, Children = [first, second] };
            File.SetUnixFileMode(directory.FullName, UnixFileMode.UserExecute);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            await folder.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(new DirectoryService(_sut)), recursive: false, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(new[] { first.Id, second.Id }, folder.Children.Select(child => child.Id));
            repository.Verify(x => x.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once);
            repository.VerifyNoOtherCalls();
        }
        finally
        {
            File.SetUnixFileMode(directory.FullName, priorMode);
            Directory.Delete(directory.FullName, true);
            BaseItem.LibraryManager = previousLibrary;
            BaseItem.ItemRepository = previousRepository;
            BaseItem.Logger = previousLogger;
            BaseItem.MediaSourceManager = previousMediaSourceManager;
        }
    }

    [Fact]
    public void MoveDirectory_SameFileSystem_Correct()
        => MoveDirectoryInternal();

    [Fact]
    public void MoveDirectory_DifferentFileSystem_Correct()
    {
        const string DestinationParent = "/dev/shm";

        Assert.SkipUnless(Directory.Exists(DestinationParent), $"{DestinationParent} is not available");

        MoveDirectoryInternal(DestinationParent);
    }

    internal void MoveDirectoryInternal(string? destinationParent = null)
    {
        const string TempFile0 = "tempfile0";
        const string TempFile1 = "tempfile1";

        destinationParent ??= Path.GetTempPath();

        var sourceDir = Directory.CreateTempSubdirectory();
        var destinationDir = Path.Join(destinationParent, Path.GetRandomFileName());
        FileHelper.CreateEmpty(Path.Join(sourceDir.FullName, TempFile0));
        FileHelper.CreateEmpty(Path.Join(sourceDir.FullName, TempFile1));

        _sut.MoveDirectory(sourceDir.FullName, destinationDir);

        Assert.True(Directory.Exists(destinationDir));
        Assert.True(File.Exists(Path.Join(destinationDir, TempFile0)));
        Assert.True(File.Exists(Path.Join(destinationDir, TempFile1)));
        Assert.False(Directory.Exists(sourceDir.FullName));

        Directory.Delete(destinationDir, true);
    }

    [Theory]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "../Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Music/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "../../Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Music/Playlists/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "/mnt/Beethoven/Misc/Moonlight Sonata.mp3", "/mnt/Beethoven/Misc/Moonlight Sonata.mp3")]
    public void MakeAbsolutePathCorrectlyHandlesRelativeFilePathsOnUnixLike(
        string folderPath,
        string filePath,
        string expectedAbsolutePath)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test");

        var generatedPath = _sut.MakeAbsolutePath(folderPath, filePath);
        Assert.Equal(expectedAbsolutePath, generatedPath);
    }

    [Theory]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"..\Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Music\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"..\..\Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Music\Playlists\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"D:\\Beethoven\Misc\Moonlight Sonata.mp3", @"D:\\Beethoven\Misc\Moonlight Sonata.mp3")]
    public void MakeAbsolutePathCorrectlyHandlesRelativeFilePathsOnWindows(
        string folderPath,
        string filePath,
        string expectedAbsolutePath)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test");

        var generatedPath = _sut.MakeAbsolutePath(folderPath, filePath);

        Assert.Equal(expectedAbsolutePath, generatedPath);
    }

    [Theory]
    [InlineData("ValidFileName", "ValidFileName")]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("Invalid\0", "Invalid ")]
    [InlineData("AC/DC\0KD/A", "AC DC KD A")]
    public void GetValidFilename_ReturnsValidFilename(string filename, string expectedFileName)
    {
        Assert.Equal(expectedFileName, _sut.GetValidFilename(filename));
    }

    [Theory]
    [InlineData("/media", "/media/tv", true)]
    [InlineData("/media", "/media/tv/show/episode.mkv", true)]
    [InlineData("/media/", "/media/tv", true)]
    [InlineData("/", "/media", true)]
    [InlineData("/media", "/media", false)]
    [InlineData("/media", "/media/", true)]
    [InlineData("/media", "/data/media/tv", false)]
    [InlineData("/media", "/mediastuff/tv", false)]
    [InlineData("/data/media", "/data/media/tv", true)]
    [InlineData("/media/tv", "/media", false)]
    [InlineData("/MEDIA", "/media/tv", false)]
    public void ContainsSubPath_Unix_ReturnsExpected(string parentPath, string path, bool expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test");

        Assert.Equal(expected, _sut.ContainsSubPath(parentPath, path));
    }

    [Theory]
    [InlineData(@"C:\media", @"C:\media\tv", true)]
    [InlineData(@"C:\media\", @"C:\media\tv", true)]
    [InlineData(@"C:\", @"C:\media", true)]
    [InlineData(@"C:\media", @"C:\media", false)]
    [InlineData(@"C:\media", @"C:\data\media\tv", false)]
    [InlineData(@"C:\media", @"C:\mediastuff\tv", false)]
    [InlineData(@"C:\MEDIA", @"C:\media\tv", true)]
    [InlineData(@"C:\media", @"C:\media/tv", true)]
    public void ContainsSubPath_Windows_ReturnsExpected(string parentPath, string path, bool expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test");

        Assert.Equal(expected, _sut.ContainsSubPath(parentPath, path));
    }

    [Fact]
    public void GetFileInfo_DanglingSymlink_ExistsFalse()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test");

        string testFileDir = Path.Combine(Path.GetTempPath(), "jellyfin-test-data");
        string testFileName = Path.Combine(testFileDir, Path.GetRandomFileName() + "-danglingsym.link");

        Directory.CreateDirectory(testFileDir);
        Assert.Equal(0, symlink("thispathdoesntexist", testFileName));
        Assert.True(File.Exists(testFileName));

        var metadata = _sut.GetFileInfo(testFileName);
        Assert.False(metadata.Exists);
    }

    [SuppressMessage("Naming Rules", "SA1300:ElementMustBeginWithUpperCaseLetter", Justification = "Have to")]
    [LibraryImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
    private static partial int symlink([MarshalAs(UnmanagedType.LPStr)] string target, [MarshalAs(UnmanagedType.LPStr)] string linkpath);
}
