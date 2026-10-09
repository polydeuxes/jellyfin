using System;
using System.IO;
using System.Security;

namespace MediaBrowser.Controller.Entities;

/// <summary>
/// Checks whether a missing item's configured library root has evidence of available storage before catalog removal.
/// </summary>
public static class MissingFileRemovalGuard
{
    /// <summary>
    /// Returns true only when the configured library root is listable and has a visible entry.
    /// An empty root can be an exposed mountpoint; a missing folder below an available root is a deletion.
    /// </summary>
    /// <param name="path">The path of the missing item.</param>
    /// <param name="libraryRoot">The configured library root for the item.</param>
    /// <returns>Whether the configured root establishes available storage.</returns>
    public static bool CanRemove(string? path, string? libraryRoot = null)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(libraryRoot))
            {
                return false;
            }

            var root = Path.GetFullPath(libraryRoot);
            var item = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, item);
            if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            return entries.MoveNext();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
