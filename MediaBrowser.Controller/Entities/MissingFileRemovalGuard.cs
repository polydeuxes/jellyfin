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
    /// Returns true only when the parent directory can be read and contains another entry.
    /// An empty exposed mountpoint cannot establish that its storage is available.
    /// </summary>
    /// <param name="path">The path of the missing item.</param>
    /// <returns>Whether its parent directory can be enumerated.</returns>
    public static bool CanRemove(string? path)
    {
        try
        {
            var parent = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
            {
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(parent).GetEnumerator();
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
