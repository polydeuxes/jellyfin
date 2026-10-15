using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.Manager;

public partial class ItemImageProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefreshImages_PrimaryThreshold_CurrentMetadataChangesDuringQualification(bool samePath, bool restoreDimensions)
    {
        var item = GetItemWithImages(ImageType.Primary, 1, true);
        var original = item.GetImageInfo(ImageType.Primary, 0);
        original.Width = 400;
        original.Height = 600;
        var oldModified = original.DateModified;
        var manualPath = samePath ? original.Path : Path.Combine(Path.GetTempPath(), "manual-qualified.jpg");
        var options = GetLibraryOptions(item, ImageType.Primary, 1);
        options.TypeOptions[0].ImageOptions[0].MinWidth = 1000;
        var remote = new Mock<IRemoteImageProvider>(MockBehavior.Strict);
        remote.SetupGet(p => p.Name).Returns("qualification-threshold-test");
        remote.Setup(p => p.GetSupportedImages(item)).Returns(new[] { ImageType.Primary });
        remote.Setup(p => p.GetImageResponse(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var manager = new Mock<IProviderManager>(MockBehavior.Strict);
        manager.Setup(p => p.GetAvailableRemoteImages(item, It.IsAny<RemoteImageQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new RemoteImageInfo { Type = ImageType.Primary, Url = "https://fixture.invalid/poster", Width = 1600 } });
        var processor = new Mock<IImageProcessor>(MockBehavior.Strict);
        processor.Setup(p => p.GetImageDimensions(It.IsAny<string>())).Returns((string path) =>
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            item.SetImagePath(ImageType.Primary, 0, new MediaBrowser.Model.IO.FileSystemMetadata { FullName = manualPath });
            Assert.Same(original, item.GetImageInfo(ImageType.Primary, 0));
            Assert.Equal(0, original.Width);
            Assert.Equal(0, original.Height);
            if (restoreDimensions)
            {
                // Also exercise refreshed dimensions: timestamp remains an identity signal.
                original.Width = 400;
                original.Height = 600;
                original.DateModified = oldModified.AddSeconds(1);
            }

            return new ImageDimensions(1600, 900);
        });
        var result = await new WiredMovieService(manager.Object, processor.Object).Images.RefreshImages(
            item,
            options,
            new List<IImageProvider> { remote.Object },
            new ImageRefreshOptions(Mock.Of<IDirectoryService>()) { ImageRefreshMode = MetadataRefreshMode.Default },
            TestContext.Current.CancellationToken);
        processor.Verify(p => p.GetImageDimensions(It.IsAny<string>()), Times.Once());
        manager.Verify(p => p.SaveImage(item, It.IsAny<Stream>(), It.IsAny<string>(), ImageType.Primary, null, It.IsAny<CancellationToken>()), Times.Never());
        Assert.Equal(manualPath, original.Path);
        Assert.False(result.UpdateType.HasFlag(ItemUpdateType.ImageUpdate));
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public async Task RefreshImages_PrimaryThreshold_ManualChoiceDuringFetchSurvives()
    {
        var token = TestContext.Current.CancellationToken;
        var item = GetItemWithImages(ImageType.Primary, 1, true);
        item.GetImageInfo(ImageType.Primary, 0).Width = 400;
        var options = GetLibraryOptions(item, ImageType.Primary, 1);
        options.TypeOptions[0].ImageOptions[0].MinWidth = 1000;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeline = new List<string>();
        var manualBytes = new byte[] { 4, 5, 6 };
        var candidateBytes = new byte[] { 1, 2, 3 };
        var manualPath = Path.Combine(Path.GetTempPath(), "manual-choice.jpg");
        var candidatePath = Path.Combine(Path.GetTempPath(), "automatic-candidate.jpg");
        var remote = new Mock<IRemoteImageProvider>(MockBehavior.Strict);
        remote.SetupGet(p => p.Name).Returns("paused-threshold-test");
        remote.Setup(p => p.GetSupportedImages(item)).Returns(new[] { ImageType.Primary });
        remote.Setup(p => p.GetImageResponse(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                timeline.Add("fetch-paused");
                entered.SetResult();
                await resume.Task.WaitAsync(token);
                timeline.Add("fetch-resumed");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(candidateBytes) };
            });
        var manager = new Mock<IProviderManager>(MockBehavior.Strict);
        manager.Setup(p => p.GetAvailableRemoteImages(item, It.IsAny<RemoteImageQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new RemoteImageInfo { Type = ImageType.Primary, Url = "https://fixture.invalid/poster", Width = 1600 } });
        // Model only ImageSaver's final state publication with the real BaseItem method.
        // Neither the API controller, ProviderManager nor ImageSaver file IO is executed.
        manager.Setup(p => p.SaveImage(item, It.IsAny<Stream>(), It.IsAny<string>(), ImageType.Primary, null, It.IsAny<CancellationToken>()))
            .Returns(async (BaseItem target, Stream source, string mime, ImageType type, int? index, CancellationToken cancellation) =>
            {
                using var bytes = new MemoryStream();
                await source.CopyToAsync(bytes, cancellation);
                var manual = bytes.ToArray().AsSpan().SequenceEqual(manualBytes);
                Assert.Equal(manual ? manualBytes : candidateBytes, bytes.ToArray());
                timeline.Add(manual ? "manual-save" : "automatic-save");
                target.SetImagePath(type, index ?? 0, new MediaBrowser.Model.IO.FileSystemMetadata { FullName = manual ? manualPath : candidatePath });
            });
        var processor = new Mock<IImageProcessor>(MockBehavior.Strict);
        processor.Setup(p => p.GetImageDimensions(It.IsAny<string>())).Returns((string path) =>
        {
            Assert.Equal(candidateBytes, File.ReadAllBytes(path));
            timeline.Add("candidate-qualified");
            return new ImageDimensions(1600, 900);
        });
        var subject = new WiredMovieService(manager.Object, processor.Object).Images;
        var refresh = subject.RefreshImages(item, options, new List<IImageProvider> { remote.Object }, new ImageRefreshOptions(Mock.Of<IDirectoryService>()) { ImageRefreshMode = MetadataRefreshMode.Default }, token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            using var manualStream = new MemoryStream(manualBytes);
            await manager.Object.SaveImage(item, manualStream, "image/jpeg", ImageType.Primary, null, token);
            Assert.Equal(manualPath, item.GetImageInfo(ImageType.Primary, 0).Path);
            Assert.False(refresh.IsCompleted);
        }
        finally
        {
            resume.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5), token);
        }

        // Desired behavior: deliberately RED if the stale decision publishes over the manual choice.
        Assert.True(item.GetImageInfo(ImageType.Primary, 0).Path == manualPath, $"Timeline: {string.Join(", ", timeline)}; final path: {item.GetImageInfo(ImageType.Primary, 0).Path}");
        Assert.False((await refresh).UpdateType.HasFlag(ItemUpdateType.ImageUpdate));
    }

    [Theory]
    [InlineData(1600, 1000, 1, MetadataRefreshMode.Default, "success", false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.Default, "success", true)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.Default, "none", false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.Default, "downloadfail", false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.Default, "invalid", false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.Default, "small", false)]
    [InlineData(400, 0, 1, MetadataRefreshMode.Default, "success", false)]
    [InlineData(400, 1000, 0, MetadataRefreshMode.Default, "success", false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.None, "success", false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.ValidationOnly, "success", false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.Default, "success", false, false, false)]
    [InlineData(400, 1000, 1, MetadataRefreshMode.Default, "success", false, true, true)]
    public async Task RefreshImages_PrimaryThreshold(int currentWidth, int minimum, int limit, MetadataRefreshMode mode, string candidate, bool expectedSave, bool processorEnabled = true, bool locked = false)
    {
        var item = GetItemWithImages(ImageType.Primary, 1, true);
        item.IsLocked = locked;
        var oldImage = item.GetImageInfo(ImageType.Primary, 0);
        oldImage.Width = currentWidth;
        var oldPath = oldImage.Path;
        var options = GetLibraryOptions(item, ImageType.Primary, limit);
        options.TypeOptions[0].ImageOptions[0].MinWidth = minimum;
        var remote = new Mock<IRemoteImageProvider>(MockBehavior.Strict);
        remote.SetupGet(p => p.Name).Returns("threshold-test");
        remote.Setup(p => p.GetSupportedImages(item)).Returns(new[] { ImageType.Primary });
        remote.Setup(p => p.GetImageResponse(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => candidate == "downloadfail"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var manager = new Mock<IProviderManager>(MockBehavior.Strict);
        manager.Setup(p => p.GetAvailableRemoteImages(item, It.IsAny<RemoteImageQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate == "none" ? Array.Empty<RemoteImageInfo>() : new[]
            {
                new RemoteImageInfo { Type = ImageType.Primary, Url = "https://fixture.invalid/poster", Width = 1600 }
            });
        var processor = new Mock<IImageProcessor>(MockBehavior.Strict);
        processor.Setup(p => p.GetImageDimensions(It.IsAny<string>())).Returns((string path) =>
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            if (candidate == "invalid")
            {
                throw new InvalidDataException("invalid raster fixture");
            }

            return new ImageDimensions(candidate == "small" ? 100 : 1600, 900);
        });
        manager.Setup(p => p.SaveImage(item, It.IsAny<Stream>(), It.IsAny<string>(), ImageType.Primary, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var subject = processorEnabled
            ? new WiredMovieService(manager.Object, processor.Object).Images
            : new MediaBrowser.Providers.Manager.ItemImageProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, manager.Object, Mock.Of<MediaBrowser.Model.IO.IFileSystem>());
        var result = await subject.RefreshImages(item, options, new List<IImageProvider> { remote.Object }, new ImageRefreshOptions(Mock.Of<IDirectoryService>()) { ImageRefreshMode = mode }, TestContext.Current.CancellationToken);
        manager.Verify(p => p.SaveImage(item, It.IsAny<Stream>(), It.IsAny<string>(), ImageType.Primary, null, It.IsAny<CancellationToken>()), expectedSave ? Times.Once() : Times.Never());
        Assert.Equal(expectedSave, result.UpdateType.HasFlag(ItemUpdateType.ImageUpdate));
        Assert.Same(oldImage, item.GetImageInfo(ImageType.Primary, 0));
        Assert.Equal(oldPath, item.GetImageInfo(ImageType.Primary, 0).Path);
        if (currentWidth >= minimum || minimum == 0 || limit == 0 || mode <= MetadataRefreshMode.ValidationOnly)
        {
            manager.Verify(p => p.GetAvailableRemoteImages(item, It.IsAny<RemoteImageQuery>(), It.IsAny<CancellationToken>()), Times.Never());
        }
    }

    private sealed class WiredMovieService : MediaBrowser.Providers.Movies.MovieMetadataService
    {
        public WiredMovieService(IProviderManager manager, IImageProcessor processor)
            : base(
                Mock.Of<MediaBrowser.Controller.Configuration.IServerConfigurationManager>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaBrowser.Providers.Movies.MovieMetadataService>.Instance,
                manager,
                Mock.Of<MediaBrowser.Model.IO.IFileSystem>(),
                Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
                Mock.Of<MediaBrowser.Controller.IO.IExternalDataManager>(),
                Mock.Of<MediaBrowser.Controller.Persistence.IItemRepository>(),
                processor)
        {
        }

        public MediaBrowser.Providers.Manager.ItemImageProvider Images => ImageProvider;
    }
}
