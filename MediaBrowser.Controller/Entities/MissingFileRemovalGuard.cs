using System;
using System.IO;
using System.Security;

namespace MediaBrowser.Controller.Entities;

/// <summary>
/// Checks whether a missing item's containing directory can be enumerated before catalog removal.
/// </summary>
public static class MissingFileRemovalGuard
{
    /// <summary>
    /// Returns true only when the parent directory can be read. An empty directory is valid.
    /// </summary>
    public static bool CanRemove(string? path)
    {
        var parent = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(parent).GetEnumerator();
            entries.MoveNext();
            return true;
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
    }
}
