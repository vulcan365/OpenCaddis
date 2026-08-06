using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;

namespace OpenCaddis.Server.Builder.AI.Plugins;

[PluginAlias(Alias)]
[Description("Project-scoped source file reading and editing tools")]
[FabrCoreCapabilities("Lists, reads, creates, replaces, and deletes text files strictly inside one configured .NET project directory.")]
[FabrCoreNote("Absolute paths, path traversal, symbolic links, junctions, and writes to bin, obj, .git, or .vs are blocked.")]
public sealed class ProjectFilesystemPlugin : IFabrCorePlugin
{
    public const string Alias = "project-files";
    private const int MaxFileCharacters = 2_000_000;
    private ProjectPathScope scope = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        var projectPath = config.GetPluginSetting(Alias, "ProjectPath")
            ?? throw new InvalidOperationException($"{Alias}:ProjectPath is required.");
        scope = new ProjectPathScope(projectPath);
        return Task.CompletedTask;
    }

    [Description("Lists files in the configured project. Use this before reading or changing code. Generated bin and obj files are omitted.")]
    public string ListProjectFiles(
        [Description("Directory relative to the project root, or '.' for the root")] string relativeDirectory = ".",
        [Description("File search pattern such as '*.cs' or '*'")] string searchPattern = "*",
        [Description("Whether to include subdirectories")] bool recursive = true,
        [Description("Maximum files to return, from 1 to 500")] int maxResults = 200)
    {
        try
        {
            maxResults = Math.Clamp(maxResults, 1, 500);
            var directory = scope.Resolve(relativeDirectory);
            if (!Directory.Exists(directory))
            {
                return $"Error: Directory '{relativeDirectory}' does not exist.";
            }

            if (searchPattern.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                return "Error: searchPattern cannot contain directory separators.";
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var files = Directory.EnumerateFiles(directory, searchPattern, options)
                .Where(path => !IsGeneratedPath(Path.GetRelativePath(scope.RootPath, path)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .Select(path => Path.GetRelativePath(scope.RootPath, path))
                .ToArray();
            return files.Length == 0
                ? "No matching files found."
                : string.Join(Environment.NewLine, files);
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Reads a UTF-8 text file from the configured project with line numbers. Use startLine and maxLines for large files.")]
    public async Task<string> ReadTextFile(
        [Description("File path relative to the project root")] string relativePath,
        [Description("First one-based line to return")] int startLine = 1,
        [Description("Maximum number of lines to return, from 1 to 1000")] int maxLines = 400)
    {
        try
        {
            var path = scope.Resolve(relativePath);
            if (!File.Exists(path))
            {
                return $"Error: File '{relativePath}' does not exist.";
            }

            if (new FileInfo(path).Length > MaxFileCharacters * 4L)
            {
                return $"Error: File '{relativePath}' is too large to read as a coding tool.";
            }

            startLine = Math.Max(startLine, 1);
            maxLines = Math.Clamp(maxLines, 1, 1000);
            var lines = await File.ReadAllLinesAsync(path);
            return string.Join(
                Environment.NewLine,
                lines.Skip(startLine - 1)
                    .Take(maxLines)
                    .Select((line, index) => $"{startLine + index,5}: {line}"));
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Creates or overwrites a UTF-8 text file inside the configured project. Prefer ReplaceText for small edits to existing files.")]
    public async Task<string> WriteTextFile(
        [Description("File path relative to the project root")] string relativePath,
        [Description("Complete UTF-8 text content to write")] string content,
        [Description("Whether an existing file may be overwritten")] bool overwrite = false)
    {
        try
        {
            if (content.Length > MaxFileCharacters)
            {
                return $"Error: Content exceeds the {MaxFileCharacters:N0} character limit.";
            }

            var path = scope.Resolve(relativePath, forMutation: true);
            if (File.Exists(path) && !overwrite)
            {
                return $"Error: File '{relativePath}' already exists. Set overwrite to true or use ReplaceText.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            return $"Wrote {content.Length:N0} characters to '{relativePath}'.";
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Replaces exact text in an existing UTF-8 project file. By default the old text must occur exactly once, which protects against broad accidental edits.")]
    public async Task<string> ReplaceText(
        [Description("File path relative to the project root")] string relativePath,
        [Description("Exact existing text to replace")] string oldText,
        [Description("Replacement text")] string newText,
        [Description("Replace every occurrence instead of requiring exactly one")] bool replaceAll = false)
    {
        try
        {
            if (string.IsNullOrEmpty(oldText))
            {
                return "Error: oldText cannot be empty.";
            }

            var path = scope.Resolve(relativePath, forMutation: true);
            if (!File.Exists(path))
            {
                return $"Error: File '{relativePath}' does not exist.";
            }

            var content = await File.ReadAllTextAsync(path);
            var occurrences = CountOccurrences(content, oldText);
            if (occurrences == 0)
            {
                return "Error: oldText was not found. Read the current file and retry with an exact match.";
            }

            if (!replaceAll && occurrences != 1)
            {
                return $"Error: oldText occurs {occurrences} times. Supply more context or set replaceAll to true.";
            }

            var updated = replaceAll
                ? content.Replace(oldText, newText, StringComparison.Ordinal)
                : content[..content.IndexOf(oldText, StringComparison.Ordinal)] + newText +
                  content[(content.IndexOf(oldText, StringComparison.Ordinal) + oldText.Length)..];
            if (updated.Length > MaxFileCharacters)
            {
                return $"Error: The updated file would exceed the {MaxFileCharacters:N0} character limit.";
            }

            await File.WriteAllTextAsync(path, updated);
            return $"Replaced {(replaceAll ? occurrences : 1)} occurrence(s) in '{relativePath}'.";
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Deletes one file inside the configured project. Directories and generated/protected paths cannot be deleted.")]
    public string DeleteFile(
        [Description("File path relative to the project root")] string relativePath)
    {
        try
        {
            var path = scope.Resolve(relativePath, forMutation: true);
            if (!File.Exists(path))
            {
                return $"Error: File '{relativePath}' does not exist.";
            }

            File.Delete(path);
            return $"Deleted '{relativePath}'.";
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static bool IsGeneratedPath(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".vs", StringComparison.OrdinalIgnoreCase));
    }
}
