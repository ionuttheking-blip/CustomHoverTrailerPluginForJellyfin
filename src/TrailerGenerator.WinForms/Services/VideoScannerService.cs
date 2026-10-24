using TrailerGenerator.WinForms.Models;

namespace TrailerGenerator.WinForms.Services;

/// <summary>
/// Scans media folders for video files and checks whether *name_of_video*-trailer.mp4 exists in the same folder.
/// </summary>
public sealed class VideoScannerService
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".flv", ".mpg", ".mpeg"
    };

    /// <summary>
    /// Scans the provided root folders recursively for main video files, ignoring existing *-trailer.* files and extras subfolders.
    /// </summary>
    public List<VideoEntry> ScanFolders(IEnumerable<string> rootFolders)
    {
        var results = new List<VideoEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootFolders)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var filePath in EnumerateVideoFilesSafe(root))
            {
                if (!seenPaths.Add(filePath))
                {
                    continue;
                }

                var hasTrailer = File.Exists(GetExpectedTrailerPath(filePath));
                results.Add(new VideoEntry
                {
                    VideoPath = filePath,
                    HasTrailer = hasTrailer,
                    WasGenerated = false,
                    IsSelected = !hasTrailer,
                    StatusMessage = hasTrailer ? "Exists (Skipped)" : "Missing Trailer"
                });
            }
        }

        return results
            .OrderBy(v => v.HasExistingTrailer)
            .ThenBy(v => v.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetExpectedTrailerPath(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(dir, $"{nameWithoutExt}-trailer.mp4");
    }

    private static IEnumerable<string> EnumerateVideoFilesSafe(string rootDirectory)
    {
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(rootDirectory);

        while (pendingDirs.Count > 0)
        {
            var currentDir = pendingDirs.Pop();

            // Skip dedicated "trailers" or "extras" subdirectories so we don't generate trailers of extras
            var dirName = Path.GetFileName(currentDir);
            if (string.Equals(dirName, "trailers", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dirName, "extras", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dirName, "featurettes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dirName, "behind the scenes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] files = [];
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (!SupportedVideoExtensions.Contains(ext))
                {
                    continue;
                }

                var nameWithoutExt = Path.GetFileNameWithoutExtension(file);

                // Ignore files that are already trailers (e.g. *name*-trailer.mp4 or trailer.mp4)
                if (nameWithoutExt.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase) ||
                    nameWithoutExt.EndsWith("_trailer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nameWithoutExt, "trailer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }

            try
            {
                foreach (var subDir in Directory.GetDirectories(currentDir))
                {
                    pendingDirs.Push(subDir);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore inaccessible subdirectories
            }
            catch (IOException)
            {
                // Ignore IO errors on subdirectories
            }
        }
    }
}
