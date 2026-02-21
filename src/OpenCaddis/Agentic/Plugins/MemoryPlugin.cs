using System.ComponentModel;
using System.Text;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Services;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("Memory")]
public sealed class MemoryPlugin : IFabrCorePlugin
{
    private IFabrCoreAgentHost? _host;
    private ILogger<MemoryPlugin> _logger = null!;
    private MemoryService _memoryService = null!;
    private string _agentSource = "unknown";
    private int _maxResults = 5;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrCoreAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<MemoryPlugin>>();
        _memoryService = serviceProvider.GetRequiredService<MemoryService>();

        if (_host is not null)
            _agentSource = _host.GetHandle();

        var maxResultsSetting = config.GetPluginSetting("Memory", "MaxResults");
        if (int.TryParse(maxResultsSetting, out var maxResults))
            _maxResults = maxResults;

        _logger.LogInformation("MemoryPlugin initialized (source: {Source}, maxResults: {MaxResults})", _agentSource, _maxResults);
        return Task.CompletedTask;
    }

    [Description("Search your persistent memory for relevant information. Use this before answering questions where past context might help.")]
    public async Task<string> MemorySearch(
        [Description("The search query — describe what you're looking for")] string query,
        [Description("Maximum number of results to return")] int? maxResults = null)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Searching memory...");

        _logger.LogDebug("Memory search: query='{Query}', maxResults={MaxResults}", query, maxResults ?? _maxResults);

        var results = await _memoryService.SearchAsync(query, maxResults ?? _maxResults, _agentSource);

        if (results.Count == 0)
        {
            _logger.LogDebug("Memory search returned no results for '{Query}'", query);
            return "No memories found matching your query.";
        }

        _logger.LogDebug("Memory search returned {Count} result(s) for '{Query}'", results.Count, query);

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memory result(s):");
        sb.AppendLine();

        foreach (var result in results)
        {
            var similarity = 1.0 - result.Distance;
            sb.AppendLine($"--- [similarity: {similarity:F3}] ---");

            if (result.Record.Metadata is not null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result.Record.Metadata);
                    if (doc.RootElement.TryGetProperty("title", out var titleProp))
                        sb.AppendLine($"Title: {titleProp.GetString()}");
                }
                catch { /* metadata parse failure is non-fatal */ }
            }

            sb.AppendLine(result.Record.Content);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    [Description("Save information to your persistent memory. Memories persist across conversations. Use this to remember important facts, user preferences, and key decisions.")]
    public async Task<string> MemorySave(
        [Description("The content to remember")] string content,
        [Description("Optional short title or label for this memory")] string? title = null)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Saving to memory...");

        var chunks = await _memoryService.SaveAsync(content, _agentSource, title);
        _logger.LogInformation("Saved memory: {Chunks} chunk(s), title='{Title}'", chunks, title ?? "(none)");
        var titleMsg = title is not null ? $" (title: {title})" : "";
        return $"Saved to memory{titleMsg}. Stored as {chunks} chunk(s).";
    }

    [Description("Delete ALL of your memories permanently. This cannot be undone. You must pass 'CONFIRM' to proceed.")]
    public async Task<string> MemoryForget(
        [Description("Must be exactly 'CONFIRM' to proceed with deletion")] string confirmation)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Processing memory deletion...");

        if (confirmation != "CONFIRM")
        {
            _logger.LogWarning("Memory forget rejected — confirmation was '{Confirmation}' instead of 'CONFIRM'", confirmation);
            return "Safety check failed. Pass confirmation='CONFIRM' to delete all memories. This action cannot be undone.";
        }

        var count = await _memoryService.DeleteBySourceAsync(_agentSource);
        _logger.LogWarning("Deleted all {Count} memory record(s) for source '{Source}'", count, _agentSource);
        return $"Deleted {count} memory record(s). All memories for this agent have been cleared.";
    }
}
