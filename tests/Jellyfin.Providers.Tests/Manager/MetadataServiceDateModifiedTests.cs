using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.Manager;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.Manager;

public class MetadataServiceDateModifiedTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BeforeSaveInternal_PrunesOnlyWhenStoredDateWasSet(bool storedDateWasSet)
    {
        var fileTime = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var originalDateCreated = fileTime.AddDays(-10);
        var creationTime = fileTime.AddDays(-2);
        var item = new Movie
        {
            Path = "/media/movie.mkv",
            DateModified = storedDateWasSet ? fileTime.AddDays(-1) : DateTime.MinValue,
            DateCreated = originalDateCreated
        };
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.GetFileSystemInfo(item.Path))
            .Returns(new FileSystemMetadata { Exists = true, LastWriteTimeUtc = fileTime, CreationTimeUtc = creationTime });
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.GetConfiguration("metadata"))
            .Returns(new MetadataConfiguration { UseFileCreationTimeForDateAdded = true });
        var externalData = new Mock<IExternalDataManager>();
        externalData.Setup(m => m.DeleteExternalItemDataAsync(item, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new TestMetadataService(configuration.Object, fileSystem.Object, externalData.Object);

        var result = service.BeforeSave(item);

        Assert.Equal(fileTime, item.DateModified);
        Assert.Equal(storedDateWasSet ? creationTime : originalDateCreated, item.DateCreated);
        Assert.True(result.HasFlag(ItemUpdateType.MetadataImport));
        externalData.Verify(
            m => m.DeleteExternalItemDataAsync(item, It.IsAny<CancellationToken>()),
            storedDateWasSet ? Times.Once() : Times.Never());
    }

    private sealed class TestMetadataService : MetadataService<Movie, MovieInfo>
    {
        public TestMetadataService(
            IServerConfigurationManager configuration,
            IFileSystem fileSystem,
            IExternalDataManager externalData)
            : base(
                configuration,
                NullLogger<MetadataService<Movie, MovieInfo>>.Instance,
                Mock.Of<IProviderManager>(),
                fileSystem,
                Mock.Of<ILibraryManager>(),
                externalData,
                Mock.Of<IItemRepository>())
        {
        }

        public ItemUpdateType BeforeSave(Movie item)
            => BeforeSaveInternal(item, false, ItemUpdateType.None);
    }
}
