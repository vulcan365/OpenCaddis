using System.ComponentModel;
using System.Text;
using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("FileSystem")]
public sealed class FileSystemPlugin : IFabrPlugin, IDisposable
{
    private string _rootPath = Directory.GetCurrentDirectory();
    private IFabrAgentHost? _host;
    private ILogger<FileSystemPlugin> _logger = null!;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, CancellationTokenSource> _debounceCts = new();

    private const int MaxWatchers = 10;
    private const int DebounceMs = 500;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<FileSystemPlugin>>();

        var root = config.GetPluginSetting("FileSystem", "RootPath");
        if (!string.IsNullOrWhiteSpace(root))
        {
            _rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (!Directory.Exists(_rootPath))
            Directory.CreateDirectory(_rootPath);

        _logger.LogInformation("FileSystemPlugin initialized (root: {RootPath})", _rootPath);
        return Task.CompletedTask;
    }

    private string ResolvePath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_rootPath, relativePath))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.Equals(_rootPath, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Path traversal blocked: '{RelativePath}' resolves outside root {RootPath}", relativePath, _rootPath);
            throw new UnauthorizedAccessException(
                $"Access denied: '{relativePath}' is outside the root directory. " +
                $"All paths must be relative to the root: {_rootPath}");
        }
        return full;
    }

    [Description("Read the contents of a file. Returns the full text content.")]
    public async Task<string> ReadFile(
        [Description("The path to the file, relative to the root directory")] string path)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Reading file {path}...");

        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("File not found: {Path}", path);
            return $"Error: File not found: {path}";
        }

        var content = await File.ReadAllTextAsync(fullPath);
        _logger.LogDebug("Read file {Path} ({Length} chars)", path, content.Length);
        return content;
    }

    [Description("Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Creates parent directories as needed.")]
    public async Task<string> WriteFile(
        [Description("The path to the file, relative to the root directory")] string path,
        [Description("The text content to write")] string content)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Writing to {path}...");

        var fullPath = ResolvePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content);
        _logger.LogInformation("Wrote {Length} chars to {Path}", content.Length, path);
        return $"Successfully wrote {content.Length} characters to {path}";
    }

    [Description("Append content to the end of a file. Creates the file if it doesn't exist.")]
    public async Task<string> AppendToFile(
        [Description("The path to the file, relative to the root directory")] string path,
        [Description("The text content to append")] string content)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Appending to {path}...");

        var fullPath = ResolvePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.AppendAllTextAsync(fullPath, content);
        _logger.LogDebug("Appended {Length} chars to {Path}", content.Length, path);
        return $"Successfully appended {content.Length} characters to {path}";
    }

    [Description("List files and directories at the given path. Returns names, types, and sizes.")]
    public async Task<string> ListDirectory(
        [Description("The directory path relative to the root directory. Use empty string or '.' for the root.")] string path)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Listing directory {(string.IsNullOrWhiteSpace(path) ? "root" : path)}...");

        var fullPath = ResolvePath(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(fullPath))
        {
            _logger.LogDebug("Directory not found: {Path}", path);
            return $"Error: Directory not found: {path}";
        }

        var sb = new StringBuilder();
        var relativeTo = _rootPath;

        var dirs = Directory.GetDirectories(fullPath);
        foreach (var dir in dirs.OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            sb.AppendLine($"[DIR]  {name}/");
        }

        var files = Directory.GetFiles(fullPath);
        foreach (var file in files.OrderBy(f => f))
        {
            try
            {
                var info = new FileInfo(file);
                sb.AppendLine($"[FILE] {info.Name}  ({FormatSize(info.Length)})");
            }
            catch (IOException)
            {
                // Skip device files (NUL, CON, etc.) that can't be stat'd
                sb.AppendLine($"[FILE] {Path.GetFileName(file)}  (device)");
            }
        }

        if (sb.Length == 0)
            return $"Directory '{path}' is empty.";

        return sb.ToString().TrimEnd();
    }

    [Description("Search for files matching a glob pattern (e.g. '*.txt', '**/*.cs'). Returns matching file paths relative to the root.")]
    public async Task<string> SearchFiles(
        [Description("The glob/search pattern (e.g. '*.txt', '**/*.cs', 'docs/*.md')")] string pattern,
        [Description("The directory to search in, relative to root. Use empty string for root.")] string path)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Searching for {pattern}...");

        var fullPath = ResolvePath(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(fullPath))
            return $"Error: Directory not found: {path}";

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = pattern.Contains("**"),
            MatchCasing = MatchCasing.CaseInsensitive
        };

        var searchPattern = pattern.Replace("**/", "");
        var files = Directory.GetFiles(fullPath, searchPattern, options)
            .Select(f => Path.GetRelativePath(_rootPath, f))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            _logger.LogDebug("No files found matching '{Pattern}' in '{Path}'", pattern, path);
            return $"No files found matching '{pattern}' in '{(string.IsNullOrWhiteSpace(path) ? "root" : path)}'.";
        }

        _logger.LogDebug("Search found {Count} file(s) matching '{Pattern}'", files.Count, pattern);
        var sb = new StringBuilder();
        sb.AppendLine($"Found {files.Count} file(s):");
        foreach (var file in files)
        {
            sb.AppendLine($"  {file}");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Get detailed information about a file or directory: size, creation date, last modified date, and attributes.")]
    public async Task<string> GetFileInfo(
        [Description("The path to the file or directory, relative to the root")] string path)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Getting info for {path}...");

        var fullPath = ResolvePath(path);

        if (File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            return $"""
                Path: {path}
                Type: File
                Size: {FormatSize(info.Length)} ({info.Length:N0} bytes)
                Created: {info.CreationTimeUtc:u}
                Modified: {info.LastWriteTimeUtc:u}
                Extension: {info.Extension}
                ReadOnly: {info.IsReadOnly}
                """;
        }

        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            var fileCount = Directory.GetFiles(fullPath).Length;
            var dirCount = Directory.GetDirectories(fullPath).Length;
            return $"""
                Path: {path}
                Type: Directory
                Created: {info.CreationTimeUtc:u}
                Modified: {info.LastWriteTimeUtc:u}
                Files: {fileCount}
                Subdirectories: {dirCount}
                """;
        }

        return $"Error: Path not found: {path}";
    }

    [Description("Delete a file or an empty directory.")]
    public async Task<string> DeleteFile(
        [Description("The path to the file or empty directory to delete, relative to the root")] string path)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Deleting {path}...");

        var fullPath = ResolvePath(path);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Deleted file: {Path}", path);
            return $"Deleted file: {path}";
        }

        if (Directory.Exists(fullPath))
        {
            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
                return $"Error: Directory is not empty: {path}";

            Directory.Delete(fullPath);
            _logger.LogInformation("Deleted directory: {Path}", path);
            return $"Deleted directory: {path}";
        }

        return $"Error: Path not found: {path}";
    }

    [Description("Create a directory. Creates parent directories as needed.")]
    public async Task<string> CreateDirectory(
        [Description("The directory path to create, relative to the root")] string path)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Creating directory {path}...");

        var fullPath = ResolvePath(path);

        if (Directory.Exists(fullPath))
            return $"Directory already exists: {path}";

        Directory.CreateDirectory(fullPath);
        _logger.LogInformation("Created directory: {Path}", path);
        return $"Created directory: {path}";
    }

    [Description("Move or rename a file or directory.")]
    public async Task<string> MoveFile(
        [Description("The current path, relative to the root")] string sourcePath,
        [Description("The new path, relative to the root")] string destinationPath)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Moving {sourcePath}...");

        var fullSource = ResolvePath(sourcePath);
        var fullDest = ResolvePath(destinationPath);

        if (File.Exists(fullSource))
        {
            var destDir = Path.GetDirectoryName(fullDest);
            if (destDir is not null && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Move(fullSource, fullDest);
            _logger.LogInformation("Moved file '{Source}' to '{Destination}'", sourcePath, destinationPath);
            return $"Moved '{sourcePath}' to '{destinationPath}'";
        }

        if (Directory.Exists(fullSource))
        {
            Directory.Move(fullSource, fullDest);
            _logger.LogInformation("Moved directory '{Source}' to '{Destination}'", sourcePath, destinationPath);
            return $"Moved '{sourcePath}' to '{destinationPath}'";
        }

        return $"Error: Source path not found: {sourcePath}";
    }

    [Description("Copy a file to a new location.")]
    public async Task<string> CopyFile(
        [Description("The source file path, relative to the root")] string sourcePath,
        [Description("The destination file path, relative to the root")] string destinationPath)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Copying {sourcePath}...");

        var fullSource = ResolvePath(sourcePath);
        var fullDest = ResolvePath(destinationPath);

        if (!File.Exists(fullSource))
            return $"Error: Source file not found: {sourcePath}";

        var destDir = Path.GetDirectoryName(fullDest);
        if (destDir is not null && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        File.Copy(fullSource, fullDest, overwrite: true);
        _logger.LogInformation("Copied file '{Source}' to '{Destination}'", sourcePath, destinationPath);
        return $"Copied '{sourcePath}' to '{destinationPath}'";
    }

    [Description("Watch a file or directory for changes (created, modified, deleted, renamed). The agent will receive a 'file-change' message when changes occur. Note: on WSL2, changes made by Windows processes on /mnt/c/ paths may not trigger events.")]
    public async Task<string> WatchPath(
        [Description("A unique name for this watcher")] string watcherName,
        [Description("The path to watch, relative to the root directory. Can be a file or directory.")] string path,
        [Description("Optional file filter pattern (e.g. '*.txt'). Only used when watching a directory. Defaults to '*.*'.")] string? filter,
        [Description("Whether to watch subdirectories recursively. Only used when watching a directory. Defaults to false.")] bool includeSubdirectories = false)
    {
        if (_host is null)
            return "Error: Agent host is not available — cannot send file change notifications.";

        await ThinkingNotifier.SendThinkingAsync(_host, $"Setting up watcher on {path}...");

        if (_watchers.ContainsKey(watcherName))
            return $"Error: A watcher named '{watcherName}' already exists. Use StopWatcher first or choose a different name.";

        if (_watchers.Count >= MaxWatchers)
            return $"Error: Maximum number of watchers ({MaxWatchers}) reached. Stop an existing watcher first.";

        var fullPath = ResolvePath(path);

        string watchDir;
        string watchFilter;

        if (File.Exists(fullPath))
        {
            watchDir = Path.GetDirectoryName(fullPath)!;
            watchFilter = Path.GetFileName(fullPath);
        }
        else if (Directory.Exists(fullPath))
        {
            watchDir = fullPath;
            watchFilter = string.IsNullOrWhiteSpace(filter) ? "*.*" : filter;
        }
        else
        {
            return $"Error: Path not found: {path}";
        }

        var watcher = new FileSystemWatcher(watchDir, watchFilter)
        {
            IncludeSubdirectories = includeSubdirectories,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.DirectoryName
        };

        watcher.Created += (_, e) => OnFileEvent(watcherName, e.FullPath, WatcherChangeTypes.Created);
        watcher.Changed += (_, e) => OnFileEvent(watcherName, e.FullPath, WatcherChangeTypes.Changed);
        watcher.Deleted += (_, e) => OnFileEvent(watcherName, e.FullPath, WatcherChangeTypes.Deleted);
        watcher.Renamed += (_, e) => OnFileEvent(watcherName, e.FullPath, WatcherChangeTypes.Renamed);
        watcher.Error += (_, e) => OnFileEvent(watcherName, watchDir, WatcherChangeTypes.Changed);

        _watchers[watcherName] = watcher;

        _logger.LogInformation("Watcher '{WatcherName}' started on {WatchDir} (filter: {Filter}, recursive: {Recursive})", watcherName, watchDir, watchFilter, includeSubdirectories);
        return $"Watcher '{watcherName}' started — watching '{watchDir}' with filter '{watchFilter}' (recursive: {includeSubdirectories}).";
    }

    [Description("Stop and remove a file watcher by name.")]
    public string StopWatcher(
        [Description("The name of the watcher to stop")] string watcherName)
    {
        if (!_watchers.Remove(watcherName, out var watcher))
            return $"Error: No watcher named '{watcherName}' found.";

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        _logger.LogInformation("Watcher '{WatcherName}' stopped", watcherName);

        // Cancel any pending debounce timers for this watcher
        lock (_debounceCts)
        {
            var keysToRemove = _debounceCts.Keys.Where(k => k.StartsWith(watcherName + "|")).ToList();
            foreach (var key in keysToRemove)
            {
                if (_debounceCts.Remove(key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
        }

        return $"Watcher '{watcherName}' stopped.";
    }

    [Description("List all active file watchers with their paths, filters, and settings.")]
    public string ListWatchers()
    {
        if (_watchers.Count == 0)
            return $"No active watchers (0/{MaxWatchers}).";

        var sb = new StringBuilder();
        sb.AppendLine($"Active watchers ({_watchers.Count}/{MaxWatchers}):");
        foreach (var (name, watcher) in _watchers)
        {
            sb.AppendLine($"  [{name}] Path: {watcher.Path}, Filter: {watcher.Filter}, Recursive: {watcher.IncludeSubdirectories}");
        }
        return sb.ToString().TrimEnd();
    }

    private void OnFileEvent(string watcherName, string fullPath, WatcherChangeTypes changeType)
    {
        try
        {
            var debounceKey = $"{watcherName}|{fullPath}|{changeType}";

            lock (_debounceCts)
            {
                // Cancel any existing debounce timer for this exact event
                if (_debounceCts.Remove(debounceKey, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }

                var cts = new CancellationTokenSource();
                _debounceCts[debounceKey] = cts;

                _ = DebounceAndNotifyAsync(debounceKey, watcherName, fullPath, changeType, cts.Token);
            }
        }
        catch
        {
            // Best-effort — swallow exceptions from watcher callbacks
        }
    }

    private async Task DebounceAndNotifyAsync(string debounceKey, string watcherName, string fullPath, WatcherChangeTypes changeType, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceMs, ct);

            // Clean up the CTS entry after successful delay
            lock (_debounceCts)
            {
                _debounceCts.Remove(debounceKey);
            }

            if (_host is null) return;

            var fileName = Path.GetFileName(fullPath);
            var message = new AgentMessage
            {
                ToHandle = _host.GetHandle(),
                FromHandle = _host.GetHandle(),
                MessageType = "file-change",
                Kind = MessageKind.OneWay,
                Channel = "system",
                Message = $"[Watcher '{watcherName}'] {changeType}: '{fileName}'"
            };

            await _host.SendMessage(message);
        }
        catch (OperationCanceledException)
        {
            // Debounce was reset — this is expected
        }
        catch
        {
            // Best-effort — swallow exceptions
        }
    }

    public void Dispose()
    {
        foreach (var (_, watcher) in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        lock (_debounceCts)
        {
            foreach (var (_, cts) in _debounceCts)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _debounceCts.Clear();
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}
