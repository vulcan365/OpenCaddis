namespace OpenCaddis.Services;

public enum PluginSettingType
{
    Text,
    Number,
    Boolean,
    Url,
    Path
}

public record PluginSettingDefinition(
    string Key,
    string Label,
    string DefaultValue,
    string Description,
    PluginSettingType SettingType);

public static class PluginSettingsRegistry
{
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "OpenCaddis");

    private static readonly Dictionary<string, List<PluginSettingDefinition>> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FileSystem"] =
        [
            new("RootPath", "Root Path", Path.Combine(BasePath, "files") + Path.DirectorySeparatorChar, "Base directory for file operations", PluginSettingType.Path)
        ],
        ["WebBrowser"] =
        [
            new("TimeoutMs", "Timeout (ms)", "30000", "Page load timeout in milliseconds", PluginSettingType.Number),
            new("MaxContentLength", "Max Content Length", "50000", "Maximum characters to return from page content", PluginSettingType.Number),
            new("ScreenshotPath", "Screenshot Path", Path.Combine(BasePath, "screenshots") + Path.DirectorySeparatorChar, "Directory to save screenshots", PluginSettingType.Path),
            new("Headless", "Headless Mode", "true", "Run browser without visible window", PluginSettingType.Boolean)
        ],
        ["PowerShell"] =
        [
            new("WorkingDirectory", "Working Directory", BasePath + Path.DirectorySeparatorChar, "Default working directory for commands", PluginSettingType.Path),
            new("TimeoutSeconds", "Timeout (seconds)", "30", "Command execution timeout", PluginSettingType.Number),
            new("MaxOutputLength", "Max Output Length", "10000", "Maximum characters to capture from output", PluginSettingType.Number)
        ],
        ["TaskManager"] =
        [
            new("RootPath", "Root Path", Path.Combine(BasePath, "tasks") + Path.DirectorySeparatorChar, "Directory to store task files", PluginSettingType.Path),
            new("FileName", "File Name", "tasks.json", "Name of the task data file", PluginSettingType.Text)
        ],
        ["Docker"] =
        [
            new("TimeoutSeconds", "Timeout (seconds)", "30", "Container command timeout", PluginSettingType.Number),
            new("MaxOutputLength", "Max Output Length", "10000", "Maximum characters to capture from output", PluginSettingType.Number),
            new("Shell", "Shell", "/bin/bash", "Shell to use inside containers", PluginSettingType.Text)
        ],
        ["Microsoft365Email"] =
        [
            new("MaxResults", "Max Results", "25", "Maximum emails to return per query", PluginSettingType.Number),
            new("MaxBodyLength", "Max Body Length", "10000", "Maximum characters per email body", PluginSettingType.Number)
        ],
        ["Memory"] =
        [
            new("MaxResults", "Default Max Results", "5", "Number of search results to return", PluginSettingType.Number)
        ],
        ["CaddisFly"] =
        [
            new("WorkingDirectory", "Working Directory", BasePath + Path.DirectorySeparatorChar, "Default working directory for pipeline commands", PluginSettingType.Path),
            new("WorkflowPath", "Workflow Path", Path.Combine(BasePath, "workflows") + Path.DirectorySeparatorChar, "Directory containing .yaml workflow files", PluginSettingType.Path),
            new("TimeoutSeconds", "Timeout (seconds)", "60", "Default per-step execution timeout", PluginSettingType.Number),
            new("MaxOutputLength", "Max Output Length", "10000", "Maximum characters to capture per step", PluginSettingType.Number)
        ]
    };

    public static IReadOnlyList<PluginSettingDefinition> GetSettings(string pluginAlias)
        => Settings.TryGetValue(pluginAlias, out var defs) ? defs : [];

    public static bool HasSettings(string pluginAlias)
        => Settings.TryGetValue(pluginAlias, out var defs) && defs.Count > 0;
}
