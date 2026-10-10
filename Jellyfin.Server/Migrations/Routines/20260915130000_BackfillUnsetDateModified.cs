using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Gives never-refreshed files a known modification time before their first scan.
/// </summary>
[JellyfinMigration("2026-09-15T13:00:00", nameof(BackfillUnsetDateModified))]
internal class BackfillUnsetDateModified : IAsyncMigrationRoutine
{
    private const int BatchSize = 500;

    private readonly ILogger _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _provider;
    private readonly IFileSystem _fileSystem;

    public BackfillUnsetDateModified(
        ILogger<BackfillUnsetDateModified> logger,
        IStartupLogger<BackfillUnsetDateModified> startupLogger,
        IDbContextFactory<JellyfinDbContext> provider,
        IFileSystem fileSystem)
    {
        _logger = startupLogger.With(logger);
        _provider = provider;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        await using var context = await _provider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await context.BaseItems.AsNoTracking()
            .Where(item => item.DateModified == null || item.DateModified == DateTime.MinValue)
            .Select(item => new { item.Id, item.Type, item.Path })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var timer = Stopwatch.StartNew();
        var updated = 0;
        var missing = 0;
        var unreachable = 0;
        var pending = 0;
        _logger.LogInformation("Backfilling DateModified for {Count} items with unset dates", candidates.Count);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            if (string.IsNullOrEmpty(candidate.Path))
            {
                missing++;
            }
            else
            {
                try
                {
                    var info = _fileSystem.GetFileSystemInfo(candidate.Path);
                    if (!info.Exists)
                    {
                        missing++;
                    }
                    else if (info.IsDirectory || info.LastWriteTimeUtc == DateTime.MinValue)
                    {
                        unreachable++;
                    }
                    else
                    {
                        var item = new BaseItemEntity { Id = candidate.Id, Type = candidate.Type };
                        context.BaseItems.Attach(item);
                        item.DateModified = info.LastWriteTimeUtc;
                        context.Entry(item).Property(entity => entity.DateModified).IsModified = true;
                        updated++;
                        pending++;
                    }
                }
                catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    unreachable++;
                    _logger.LogWarning(exception, "Cannot read file modification time for {Path}; leaving DateModified unset", candidate.Path);
                }
            }

            if (pending >= BatchSize)
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                context.ChangeTracker.Clear();
                pending = 0;
            }

            if ((index + 1) % BatchSize == 0 || index + 1 == candidates.Count)
            {
                _logger.LogInformation(
                    "DateModified backfill: {Processed}/{Total} checked, {Updated} updated, {Missing} missing, {Unreachable} unreachable in {Elapsed}",
                    index + 1,
                    candidates.Count,
                    updated,
                    missing,
                    unreachable,
                    timer.Elapsed);
            }
        }

        if (pending > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "DateModified backfill complete: {Updated} updated, {Missing} missing, {Unreachable} unreachable in {Elapsed}",
            updated,
            missing,
            unreachable,
            timer.Elapsed);
    }
}
