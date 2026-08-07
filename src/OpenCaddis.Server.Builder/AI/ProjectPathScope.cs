namespace OpenCaddis.Server.Builder.AI;

internal sealed class ProjectPathScope
{
    private static readonly HashSet<string> ProtectedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            "bin",
            "obj"
        };

    private readonly StringComparison pathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public ProjectPathScope(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var fullProjectPath = Path.GetFullPath(projectPath);
        RootPath = File.Exists(fullProjectPath)
            ? Path.GetDirectoryName(fullProjectPath)!
            : fullProjectPath;

        if (!Directory.Exists(RootPath))
        {
            throw new DirectoryNotFoundException(
                $"The project directory '{RootPath}' does not exist.");
        }

        EnsureNotReparsePoint(RootPath);
    }

    public string RootPath { get; }

    public string Resolve(string relativePath, bool forMutation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Paths must be relative to the configured project directory.");
        }

        var fullPath = Path.GetFullPath(relativePath, RootPath);
        var rootPrefix = Path.TrimEndingDirectorySeparator(RootPath) + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(RootPath, pathComparison) &&
            !fullPath.StartsWith(rootPrefix, pathComparison))
        {
            throw new InvalidOperationException("The requested path is outside the configured project directory.");
        }

        var normalizedRelativePath = Path.GetRelativePath(RootPath, fullPath);
        var segments = normalizedRelativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (forMutation && segments.Any(ProtectedDirectories.Contains))
        {
            var protectedDirectory = segments.First(ProtectedDirectories.Contains);
            throw new InvalidOperationException(
                $"Writing inside '{protectedDirectory}' is not allowed.");
        }

        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    private void EnsureNoReparsePoints(string fullPath)
    {
        var relativePath = Path.GetRelativePath(RootPath, fullPath);
        var currentPath = RootPath;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (File.Exists(currentPath) || Directory.Exists(currentPath))
            {
                EnsureNotReparsePoint(currentPath);
            }
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Symbolic links and directory junctions are not allowed in the project scope: '{path}'.");
        }
    }
}
