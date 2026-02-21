using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("TaskManager")]
public sealed class TaskManagerPlugin : IFabrCorePlugin
{
    private IFabrCoreAgentHost? _host;
    private ILogger<TaskManagerPlugin> _logger = null!;
    private string _filePath = Path.Combine(Directory.GetCurrentDirectory(), "tasks.json");
    private TaskList _taskList = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrCoreAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<TaskManagerPlugin>>();

        var rootPath = config.GetPluginSetting("TaskManager", "RootPath");
        var fileName = config.GetPluginSetting("TaskManager", "FileName") ?? "tasks.json";

        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            var dir = Path.GetFullPath(rootPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, fileName);
        }
        else
        {
            _filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        }

        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                _taskList = JsonSerializer.Deserialize<TaskList>(json, JsonOptions) ?? new TaskList();
                _logger.LogDebug("Loaded {TaskCount} existing tasks from {FilePath}", _taskList.Tasks.Count, _filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load task file {FilePath}, starting with empty task list", _filePath);
                _taskList = new TaskList();
            }
        }

        _logger.LogInformation("TaskManagerPlugin initialized (file: {FilePath})", _filePath);
        return Task.CompletedTask;
    }

    private void Save()
    {
        _taskList.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(_taskList, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    // --- Tools ---

    [Description("Set the overall goal/objective for the current task plan. Clears any previous goal.")]
    public async Task<string> SetGoal(
        [Description("The high-level objective this plan should accomplish")] string goal)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Setting goal...");

        _taskList.Goal = goal;
        if (_taskList.CreatedAt == default)
            _taskList.CreatedAt = DateTimeOffset.UtcNow;
        Save();
        _logger.LogDebug("Goal set: {Goal}", goal);
        return $"Goal set: {goal}";
    }

    [Description("Add a new task to the plan. Returns the assigned task ID.")]
    public async Task<string> AddTask(
        [Description("Short title describing what this task accomplishes")] string title,
        [Description("Detailed description of the work to be done")] string? description = null,
        [Description("Priority from 1 (highest) to 4 (lowest). Default is 2.")] int priority = 2,
        [Description("Comma-separated list of task IDs that must be completed before this task can start")] string? dependsOn = null)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Adding task '{title}'...");

        var id = $"t-{_taskList.NextId:D3}";
        _taskList.NextId++;

        var deps = new List<string>();
        if (!string.IsNullOrWhiteSpace(dependsOn))
        {
            deps = dependsOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Validate dependency IDs exist
            foreach (var dep in deps)
            {
                if (_taskList.Tasks.All(t => t.Id != dep))
                    return $"Error: Dependency task '{dep}' not found. Task was not added.";
            }
        }

        priority = Math.Clamp(priority, 1, 4);

        var task = new TaskItem
        {
            Id = id,
            Title = title,
            Description = description,
            Priority = priority,
            DependsOn = deps,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _taskList.Tasks.Add(task);
        if (_taskList.CreatedAt == default)
            _taskList.CreatedAt = DateTimeOffset.UtcNow;
        Save();

        _logger.LogDebug("Added task {TaskId}: '{Title}' (priority {Priority})", id, title, priority);
        return $"Added task {id}: {title} (priority {priority}{(deps.Count > 0 ? $", depends on: {string.Join(", ", deps)}" : "")})";
    }

    [Description("Update a task's status. Use 'in_progress' when starting work, 'completed' when done, 'blocked' when stuck.")]
    public async Task<string> UpdateTask(
        [Description("The task ID to update (e.g. 't-001')")] string taskId,
        [Description("New status: pending, in_progress, completed, or blocked")] string status,
        [Description("Result or notes about the work done, or reason for being blocked")] string? result = null)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Updating task {taskId}...");

        var task = _taskList.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
        {
            _logger.LogWarning("Task not found: {TaskId}", taskId);
            return $"Error: Task '{taskId}' not found.";
        }

        if (!Enum.TryParse<TaskStatus>(status, ignoreCase: true, out var newStatus))
            return $"Error: Invalid status '{status}'. Use: pending, in_progress, completed, or blocked.";

        task.Status = newStatus;
        task.Result = result ?? task.Result;

        if (newStatus == TaskStatus.Completed)
            task.CompletedAt = DateTimeOffset.UtcNow;

        Save();

        _logger.LogDebug("Task {TaskId} updated to {Status}", taskId, status);
        var msg = $"Task {taskId} updated to {status}.";
        if (result is not null)
            msg += $" Result: {result}";
        return msg;
    }

    [Description("Get all tasks with a progress summary. Shows the goal, each task's status, and overall completion.")]
    public async Task<string> GetTasks()
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Getting tasks...");
        if (_taskList.Tasks.Count == 0)
            return "No tasks in the plan. Use SetGoal and AddTask to create a plan.";

        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(_taskList.Goal))
            sb.AppendLine($"Goal: {_taskList.Goal}");

        sb.AppendLine();

        var completed = _taskList.Tasks.Count(t => t.Status == TaskStatus.Completed);
        var inProgress = _taskList.Tasks.Count(t => t.Status == TaskStatus.InProgress);
        var blocked = _taskList.Tasks.Count(t => t.Status == TaskStatus.Blocked);
        var pending = _taskList.Tasks.Count(t => t.Status == TaskStatus.Pending);
        var total = _taskList.Tasks.Count;

        sb.AppendLine($"Progress: {completed}/{total} completed | {inProgress} in progress | {blocked} blocked | {pending} pending");
        sb.AppendLine();

        foreach (var task in _taskList.Tasks)
        {
            var statusIcon = task.Status switch
            {
                TaskStatus.Completed => "[x]",
                TaskStatus.InProgress => "[>]",
                TaskStatus.Blocked => "[!]",
                _ => "[ ]"
            };

            sb.AppendLine($"{statusIcon} {task.Id}: {task.Title} (P{task.Priority})");

            if (task.DependsOn.Count > 0)
                sb.AppendLine($"    Depends on: {string.Join(", ", task.DependsOn)}");
            if (!string.IsNullOrWhiteSpace(task.Result))
                sb.AppendLine($"    Result: {task.Result}");
        }

        return sb.ToString().TrimEnd();
    }

    [Description("Get the next task to work on: the highest-priority pending task whose dependencies are all completed.")]
    public async Task<string> GetNextTask()
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Finding next task...");
        var completedIds = _taskList.Tasks
            .Where(t => t.Status == TaskStatus.Completed)
            .Select(t => t.Id)
            .ToHashSet();

        var next = _taskList.Tasks
            .Where(t => t.Status == TaskStatus.Pending)
            .Where(t => t.DependsOn.All(d => completedIds.Contains(d)))
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .FirstOrDefault();

        if (next is null)
        {
            var remaining = _taskList.Tasks.Count(t => t.Status is TaskStatus.Pending or TaskStatus.InProgress or TaskStatus.Blocked);
            if (remaining == 0)
                return "All tasks are completed! Use GetTasks to see the final summary.";
            return "No tasks are ready — remaining tasks are blocked or have unmet dependencies.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Next task: {next.Id}");
        sb.AppendLine($"Title: {next.Title}");
        sb.AppendLine($"Priority: {next.Priority}");
        if (!string.IsNullOrWhiteSpace(next.Description))
            sb.AppendLine($"Description: {next.Description}");
        if (next.DependsOn.Count > 0)
            sb.AppendLine($"Dependencies (all met): {string.Join(", ", next.DependsOn)}");

        return sb.ToString().TrimEnd();
    }

    [Description("Remove a task from the plan. Also removes it from other tasks' dependency lists.")]
    public async Task<string> RemoveTask(
        [Description("The task ID to remove (e.g. 't-001')")] string taskId)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Removing task {taskId}...");

        var task = _taskList.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
        {
            _logger.LogWarning("Cannot remove task — not found: {TaskId}", taskId);
            return $"Error: Task '{taskId}' not found.";
        }

        _taskList.Tasks.Remove(task);

        // Clean up dependency references
        foreach (var other in _taskList.Tasks)
        {
            other.DependsOn.Remove(taskId);
        }

        Save();
        _logger.LogInformation("Removed task {TaskId}: '{Title}'", taskId, task.Title);
        return $"Removed task {taskId}: {task.Title}";
    }

    [Description("Clear all tasks and the goal. Use this to start a completely new plan.")]
    public async Task<string> ClearTasks()
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Clearing tasks...");

        var count = _taskList.Tasks.Count;
        _taskList = new TaskList();
        Save();
        _logger.LogInformation("Cleared {Count} task(s)", count);
        return $"Cleared {count} task(s). Ready for a new plan.";
    }

    // --- Data Model ---

    public class TaskItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public TaskStatus Status { get; set; } = TaskStatus.Pending;
        public string? Result { get; set; }
        public int Priority { get; set; } = 2;
        public List<string> DependsOn { get; set; } = new();
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }

    public class TaskList
    {
        public string? Goal { get; set; }
        public List<TaskItem> Tasks { get; set; } = new();
        public int NextId { get; set; } = 1;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TaskStatus
    {
        Pending,
        InProgress,
        Completed,
        Blocked
    }
}
