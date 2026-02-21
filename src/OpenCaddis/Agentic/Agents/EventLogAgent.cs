using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenCaddis.Agentic.Agents;

[AgentAlias("eventlog")]
public class EventLogAgent : FabrCoreAgentProxy
{
    private const int MaxLogEntries = 1000;

    private const string DefaultSystemPrompt =
        """
        You are an application log analyst. You have access to a rolling buffer of the most recent application log entries.
        Use your tools to search, filter, and summarize logs when answering questions.
        When presenting logs, format them clearly. Highlight errors and warnings.
        If asked about issues or problems, start by checking for Error and Warning level logs.
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private AIAgent? _agent;
    private AgentSession? _session;

    // Ring buffer — O(1) writes, no allocations on add
    private readonly LogRecord[] _logs = new LogRecord[MaxLogEntries];
    private int _head;
    private int _count;

    public EventLogAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        var modelConfig = config.Args?.GetValueOrDefault("ModelConfig") ?? "default";

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetRecentLogs),
            AIFunctionFactory.Create(SearchLogs),
            AIFunctionFactory.Create(SearchLogsByLevel),
            AIFunctionFactory.Create(SearchLogsByCategory),
            AIFunctionFactory.Create(SearchLogsByTimeRange),
            AIFunctionFactory.Create(GetLogStats),
            AIFunctionFactory.Create(GetLogCount),
        };

        (_agent, _session, _) = await CreateChatClientAgent(
            modelConfig,
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools,
            configureOptions: opts =>
            {
                if (string.IsNullOrWhiteSpace(config.SystemPrompt))
                    opts.ChatOptions!.Instructions = DefaultSystemPrompt;
            }
        );

        logger.LogInformation("EventLogAgent initialized with {ToolCount} log analysis tools", tools.Count);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        logger.LogInformation(
            "EventLogAgent received query from {From}: {Message}",
            message.FromHandle, Truncate(message.Message ?? "", 200));

        try
        {
            var result = await _agent!.RunAsync(message.Message ?? string.Empty, _session);
            response.Message = result.Text ?? "No response";

            logger.LogInformation(
                "EventLogAgent completed query response ({Length} chars, {BufferCount} entries in buffer)",
                response.Message.Length, _count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EventLogAgent error processing query");
            response.Message = $"Error: {ex.Message}";
        }

        return response;
    }

    public override Task OnEvent(AgentMessage message)
    {
        // Only process log events; silently ignore anything else.
        // Explicitly overridden (even as noop for non-log events) to prevent
        // the base class from logging, which would cause recursion.
        if (message.MessageType != "log" || string.IsNullOrEmpty(message.Message))
            return Task.CompletedTask;

        try
        {
            var record = JsonSerializer.Deserialize<LogRecord>(message.Message, JsonOpts);
            if (record is not null)
            {
                _logs[_head] = record;
                _head = (_head + 1) % MaxLogEntries;
                if (_count < MaxLogEntries)
                    _count++;
            }
        }
        catch (Exception ex)
        {
            // Store the failure as a synthetic log entry so it's visible via tools.
            // We don't use logger here to avoid recursion through the log provider.
            var errorRecord = new LogRecord
            {
                Level = "Error",
                Category = "OpenCaddis.Agentic.Agents.EventLogAgent",
                Message = $"Failed to deserialize log event: {ex.Message}. Raw payload: {message.Message}",
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                EventId = 0,
                Exception = ex.ToString()
            };
            _logs[_head] = errorRecord;
            _head = (_head + 1) % MaxLogEntries;
            if (_count < MaxLogEntries)
                _count++;
        }

        return Task.CompletedTask;
    }

    // ─── Tool Methods (agent-local, not in global registry) ──────────

    [Description("Get the most recent log entries, newest first.")]
    private string GetRecentLogs(
        [Description("Number of log entries to retrieve (default 20)")] int count = 20)
    {
        logger.LogInformation("GetRecentLogs called with count={Count}", count);

        if (_count == 0)
            return "No log entries in buffer.";

        count = Math.Clamp(count, 1, _count);
        var results = new LogRecord[count];

        for (int i = 0; i < count; i++)
        {
            var index = ((_head - 1 - i) % MaxLogEntries + MaxLogEntries) % MaxLogEntries;
            results[i] = _logs[index];
        }

        logger.LogInformation("GetRecentLogs returning {ResultCount} entries", count);
        return JsonSerializer.Serialize(results, JsonOpts);
    }

    [Description("Search log messages and categories for text (case-insensitive), newest matches first.")]
    private string SearchLogs(
        [Description("Text to search for in log messages and categories")] string query,
        [Description("Maximum results to return (default 50)")] int maxResults = 50)
    {
        logger.LogInformation("SearchLogs called with query='{Query}', maxResults={MaxResults}", query, maxResults);

        if (_count == 0)
            return "No log entries in buffer.";

        maxResults = Math.Clamp(maxResults, 1, _count);
        var results = new List<LogRecord>();

        for (int i = 0; i < _count && results.Count < maxResults; i++)
        {
            var index = ((_head - 1 - i) % MaxLogEntries + MaxLogEntries) % MaxLogEntries;
            var entry = _logs[index];
            if (entry.Message?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || entry.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || entry.Exception?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                results.Add(entry);
            }
        }

        logger.LogInformation("SearchLogs found {ResultCount} entries matching '{Query}'", results.Count, query);
        return results.Count == 0
            ? $"No log entries matching '{query}'."
            : JsonSerializer.Serialize(results, JsonOpts);
    }

    [Description("Filter logs by severity level, newest matches first.")]
    private string SearchLogsByLevel(
        [Description("Log level: Trace, Debug, Information, Warning, Error, or Critical")] string level,
        [Description("Maximum results to return (default 50)")] int maxResults = 50)
    {
        logger.LogInformation("SearchLogsByLevel called with level='{Level}', maxResults={MaxResults}", level, maxResults);

        if (_count == 0)
            return "No log entries in buffer.";

        maxResults = Math.Clamp(maxResults, 1, _count);
        var results = new List<LogRecord>();

        for (int i = 0; i < _count && results.Count < maxResults; i++)
        {
            var index = ((_head - 1 - i) % MaxLogEntries + MaxLogEntries) % MaxLogEntries;
            var entry = _logs[index];
            if (string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase))
                results.Add(entry);
        }

        logger.LogInformation("SearchLogsByLevel found {ResultCount} entries with level '{Level}'", results.Count, level);
        return results.Count == 0
            ? $"No log entries with level '{level}'."
            : JsonSerializer.Serialize(results, JsonOpts);
    }

    [Description("Filter logs by category prefix (e.g. 'OpenCaddis.Services'), newest matches first.")]
    private string SearchLogsByCategory(
        [Description("Category prefix to match")] string categoryPrefix,
        [Description("Maximum results to return (default 50)")] int maxResults = 50)
    {
        logger.LogInformation("SearchLogsByCategory called with prefix='{CategoryPrefix}', maxResults={MaxResults}", categoryPrefix, maxResults);

        if (_count == 0)
            return "No log entries in buffer.";

        maxResults = Math.Clamp(maxResults, 1, _count);
        var results = new List<LogRecord>();

        for (int i = 0; i < _count && results.Count < maxResults; i++)
        {
            var index = ((_head - 1 - i) % MaxLogEntries + MaxLogEntries) % MaxLogEntries;
            var entry = _logs[index];
            if (entry.Category?.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase) == true)
                results.Add(entry);
        }

        logger.LogInformation("SearchLogsByCategory found {ResultCount} entries with prefix '{CategoryPrefix}'", results.Count, categoryPrefix);
        return results.Count == 0
            ? $"No log entries with category prefix '{categoryPrefix}'."
            : JsonSerializer.Serialize(results, JsonOpts);
    }

    [Description("Filter logs within a time range (ISO 8601 timestamps), newest matches first.")]
    private string SearchLogsByTimeRange(
        [Description("Start time in ISO 8601 format (e.g. '2025-01-15T10:00:00Z')")] string from,
        [Description("End time in ISO 8601 format (e.g. '2025-01-15T11:00:00Z')")] string to,
        [Description("Maximum results to return (default 100)")] int maxResults = 100)
    {
        logger.LogInformation("SearchLogsByTimeRange called with from='{From}', to='{To}', maxResults={MaxResults}", from, to, maxResults);

        if (_count == 0)
            return "No log entries in buffer.";

        if (!DateTimeOffset.TryParse(from, out var fromTime)
            || !DateTimeOffset.TryParse(to, out var toTime))
        {
            logger.LogWarning("SearchLogsByTimeRange received invalid time format: from='{From}', to='{To}'", from, to);
            return "Invalid time format. Use ISO 8601 (e.g. '2025-01-15T10:00:00Z').";
        }

        maxResults = Math.Clamp(maxResults, 1, _count);
        var results = new List<LogRecord>();

        for (int i = 0; i < _count && results.Count < maxResults; i++)
        {
            var index = ((_head - 1 - i) % MaxLogEntries + MaxLogEntries) % MaxLogEntries;
            var entry = _logs[index];
            if (DateTimeOffset.TryParse(entry.Timestamp, out var ts) && ts >= fromTime && ts <= toTime)
                results.Add(entry);
        }

        logger.LogInformation("SearchLogsByTimeRange found {ResultCount} entries between '{From}' and '{To}'", results.Count, from, to);
        return results.Count == 0
            ? $"No log entries between {from} and {to}."
            : JsonSerializer.Serialize(results, JsonOpts);
    }

    [Description("Get summary statistics: total count, counts per severity level, and top 10 categories by volume.")]
    private string GetLogStats()
    {
        logger.LogInformation("GetLogStats called with {Count} entries in buffer", _count);

        if (_count == 0)
            return "No log entries in buffer.";

        var levelCounts = new Dictionary<string, int>();
        var categoryCounts = new Dictionary<string, int>();
        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;

        for (int i = 0; i < _count; i++)
        {
            var index = ((_head - 1 - i) % MaxLogEntries + MaxLogEntries) % MaxLogEntries;
            var entry = _logs[index];

            var level = entry.Level ?? "Unknown";
            levelCounts[level] = levelCounts.GetValueOrDefault(level) + 1;

            var category = entry.Category ?? "Unknown";
            categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;

            if (DateTimeOffset.TryParse(entry.Timestamp, out var ts))
            {
                if (earliest is null || ts < earliest) earliest = ts;
                if (latest is null || ts > latest) latest = ts;
            }
        }

        var topCategories = categoryCounts
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        logger.LogInformation("GetLogStats returning stats: {LevelCount} levels, {CategoryCount} categories", levelCounts.Count, categoryCounts.Count);
        return JsonSerializer.Serialize(new
        {
            totalEntries = _count,
            bufferCapacity = MaxLogEntries,
            earliest = earliest?.ToString("o"),
            latest = latest?.ToString("o"),
            levelCounts,
            topCategories
        }, JsonOpts);
    }

    [Description("Get the total number of log entries currently stored in the buffer.")]
    private string GetLogCount()
    {
        logger.LogInformation("GetLogCount called: {Count} entries in buffer", _count);
        return $"There are {_count} log entries in the buffer (capacity: {MaxLogEntries}).";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    // ─── Log Record ─────────────────────────────────────────────────

    private sealed class LogRecord
    {
        public string? Level { get; set; }
        public string? Category { get; set; }
        public string? Message { get; set; }
        public string? Timestamp { get; set; }
        public int EventId { get; set; }
        public string? Exception { get; set; }
    }
}
