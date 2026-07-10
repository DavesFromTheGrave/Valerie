using System.Text.RegularExpressions;

namespace Valerie.Services;

/// <summary>Output-directory and filename helpers for the hosted (xAI) media generators.
/// Directory resolution matches the ComfyUI generator's behavior: cwd first (dotnet run),
/// then walking up from the bin directory.</summary>
internal static class OutputPaths
{
    public static string EnsureDir(string path)
    {
        var dir = ResolveDir(path);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ResolveDir(string path)
    {
        if (Path.IsPathRooted(path)) return path;

        var fromCwd = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (Directory.Exists(fromCwd)) return fromCwd;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < 5; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDir, path));
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(baseDir);
            if (parent is null) break;
            baseDir = parent;
        }
        return fromCwd;
    }

    /// <summary>Filesystem-safe slug of a prompt, matching the ComfyUI naming convention.</summary>
    public static string SafeSlug(string text, int max = 60)
    {
        var safe = Regex.Replace(text, @"[^a-zA-Z0-9_-]", "_").ToLowerInvariant();
        return safe.Length <= max ? safe : safe[..max];
    }

    /// <summary>Most recently written image in a directory, or null if there are none.</summary>
    public static string? NewestImage(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
