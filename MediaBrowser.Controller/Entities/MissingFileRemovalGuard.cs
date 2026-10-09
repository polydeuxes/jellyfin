using System;
using System.IO;
using System.Security;

namespace MediaBrowser.Controller.Entities;

/// <summary>
/// Checks whether a missing item's containing directory has evidence of available storage before catalog removal.
/// </summary>
public static class MissingFileRemovalGuard
{
    /// <summary>
    /// Returns true only when the containing directory has another visible entry.
    /// An empty directory can be an exposed mountpoint at any depth.
    /// </summary>
    /// <param name="path">The path of the missing item.</param>
    /// <param name="libraryRoot">The configured library root for the item.</param>
    /// <returns>Whether the containing directory establishes available storage.</returns>
    public static bool CanRemove(string? path, string? libraryRoot = null)
    {
        try
        {
            var parent = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
            {
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(parent).GetEnumerator();
            if (entries.MoveNext())
            {
                return true;
            }

            return false;
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
