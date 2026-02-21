using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic;
using OpenCaddis.Agentic.CaddisFly;
using OpenCaddis.Services;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("CaddisFly")]
public sealed class CaddisFlyPlugin : IFabrCorePlugin
{
    private IFabrCoreAgentHost? _host;
    private ILogger<CaddisFlyPlugin> _logger = null!;
    private CaddisFlyRuntimeService _runtime = null!;
    private CaddisFlyRunStore _runStore = null!;
    private CommandExecutor _executor = null!;
    private WorkflowFileLoader _workflowLoader = null!;

    private string _workingDirectory = Path.Combine(Path.GetTempPath(), "OpenCaddis") + Path.DirectorySeparatorChar;
    private string _workflowPath = Path.Combine(Path.GetTempPath(), "OpenCaddis", "workflows") + Path.DirectorySeparatorChar;
    private int _timeoutSeconds = 60;
    private int _maxOutputLength = 10000;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrCoreAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<CaddisFlyPlugin>>();
        _runtime = serviceProvider.GetRequiredService<CaddisFlyRuntimeService>();
        _runStore = serviceProvider.GetRequiredService<CaddisFlyRunStore>();

        var workDir = config.GetPluginSetting("CaddisFly", "WorkingDirectory");
        if (!string.IsNullOrWhiteSpace(workDir))
            _workingDirectory = workDir;

        var wfPath = config.GetPluginSetting("CaddisFly", "WorkflowPath");
        if (!string.IsNullOrWhiteSpace(wfPath))
            _workflowPath = wfPath;

        var timeout = config.GetPluginSetting("CaddisFly", "TimeoutSeconds");
        if (int.TryParse(timeout, out var t) && t > 0)
            _timeoutSeconds = t;

        var maxOutput = config.GetPluginSetting("CaddisFly", "MaxOutputLength");
        if (int.TryParse(maxOutput, out var m) && m > 0)
            _maxOutputLength = m;

        // Parse custom commands config: "terraform=terraform;kubectl=kubectl;az=az"
        Dictionary<string, string>? customCommands = null;
        var customCmdConfig = config.GetPluginSetting("CaddisFly", "CustomCommands");
        if (!string.IsNullOrWhiteSpace(customCmdConfig))
        {
            customCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in customCmdConfig.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    customCommands[parts[0].Trim()] = parts[1].Trim();
            }
        }

        if (!Directory.Exists(_workingDirectory))
            Directory.CreateDirectory(_workingDirectory);
        if (!Directory.Exists(_workflowPath))
            Directory.CreateDirectory(_workflowPath);

        _executor = new CommandExecutor(_logger, _timeoutSeconds, _maxOutputLength, _workingDirectory, customCommands);
        _workflowLoader = new WorkflowFileLoader(_logger, _workflowPath);

        // Configure the executor factory so Chat.razor can resume approval gates directly
        var executorFactory = serviceProvider.GetRequiredService<CommandExecutorFactory>();
        executorFactory.Configure(_executor);

        // Configure run store path and restore any paused runs from disk
        var runStore = serviceProvider.GetRequiredService<CaddisFlyRunStore>();
        var runsPath = Path.Combine(_workingDirectory, "runs");
        runStore.SetRunsPath(runsPath);
        _ = _runtime.RestorePausedRunsAsync();

        _logger.LogInformation("CaddisFlyPlugin initialized (workingDir: {WorkingDirectory}, workflowPath: {WorkflowPath}, timeout: {Timeout}s)",
            _workingDirectory, _workflowPath, _timeoutSeconds);

        return Task.CompletedTask;
    }

    [Description("Run an inline CaddisFly pipeline. Pipe-delimited commands execute sequentially, each receiving the previous step's output. Use 'approve --prompt \"message\"' for approval gates. Built-in commands: powershell, bash, docker, python, node, curl, git, dotnet, npm, echo, set-var, approve. Additional commands can be registered via CustomCommands config. Supports --retries N and --retry-delay N flags. Example: \"bash --command 'curl https://api.example.com' --retries 3 --retry-delay 5 | python --command 'import json,sys; print(json.load(sys.stdin)[\"key\"])'\"")]
    public async Task<string> RunPipeline(
        [Description("Pipe-delimited pipeline string (e.g. \"bash --command 'ls' | approve --prompt 'Continue?'\")")] string pipeline)
    {
        var reporter = new ThinkingProgressReporter(_host);

        CaddisFlyPipeline parsed;
        try
        {
            parsed = PipelineParser.Parse(pipeline);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse pipeline: {Pipeline}", pipeline);
            return CaddisFlyRuntimeService.ToJson(new CaddisFlyEnvelope
            {
                RunId = Guid.NewGuid().ToString("N"),
                Status = CaddisFlyStatus.Error,
                Error = $"Failed to parse pipeline: {ex.Message}"
            });
        }

        var envelope = await _runtime.RunPipelineAsync(parsed, _executor, reporter);
        envelope = await WaitForApprovalResolutionAsync(envelope);
        return CaddisFlyRuntimeService.ToJson(envelope);
    }

    [Description("Run a named CaddisFly workflow from a .yaml file. Workflows define multi-step pipelines with variable substitution.")]
    public async Task<string> RunWorkflow(
        [Description("Name of the workflow file (with or without .yaml extension)")] string name,
        [Description("Optional JSON object of variable overrides (e.g. {\"env\": \"staging\"})")] string? variables = null)
    {
        var reporter = new ThinkingProgressReporter(_host);

        Dictionary<string, string>? vars = null;
        if (!string.IsNullOrWhiteSpace(variables))
        {
            try
            {
                vars = JsonSerializer.Deserialize<Dictionary<string, string>>(variables);
            }
            catch (Exception ex)
            {
                return CaddisFlyRuntimeService.ToJson(new CaddisFlyEnvelope
                {
                    RunId = Guid.NewGuid().ToString("N"),
                    Status = CaddisFlyStatus.Error,
                    Error = $"Invalid variables JSON: {ex.Message}"
                });
            }
        }

        CaddisFlyPipeline pipeline;
        try
        {
            pipeline = _workflowLoader.Load(name, vars);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load workflow: {Name}", name);
            return CaddisFlyRuntimeService.ToJson(new CaddisFlyEnvelope
            {
                RunId = Guid.NewGuid().ToString("N"),
                Status = CaddisFlyStatus.Error,
                Error = $"Failed to load workflow: {ex.Message}"
            });
        }

        var envelope = await _runtime.RunPipelineAsync(pipeline, _executor, reporter);
        envelope = await WaitForApprovalResolutionAsync(envelope);
        return CaddisFlyRuntimeService.ToJson(envelope);
    }

    [Description("Resume a paused CaddisFly pipeline run after an approval gate. Pass the resume token from the NeedsApproval response and whether the user approved or denied.")]
    public async Task<string> ResumeRun(
        [Description("The resume token from the NeedsApproval response")] string resumeToken,
        [Description("Whether the user approved (true) or denied (false)")] bool approved)
    {
        var reporter = new ThinkingProgressReporter(_host);
        var envelope = await _runtime.ResumeRunAsync(resumeToken, approved, _executor, reporter);
        return CaddisFlyRuntimeService.ToJson(envelope);
    }

    [Description("Check the status of a CaddisFly pipeline run by its run ID. Returns the current state, completed steps, and any pending approval.")]
    public string GetRunStatus(
        [Description("The run ID to check")] string runId)
    {
        var envelope = _runtime.GetRunStatus(runId);
        if (envelope is null)
            return CaddisFlyRuntimeService.ToJson(new CaddisFlyEnvelope
            {
                RunId = runId,
                Status = CaddisFlyStatus.Error,
                Error = "Run not found."
            });

        return CaddisFlyRuntimeService.ToJson(envelope);
    }

    [Description("Cancel a running CaddisFly pipeline by its run ID.")]
    public string CancelRun(
        [Description("The run ID to cancel")] string runId)
    {
        var cancelled = _runtime.CancelRun(runId);
        if (!cancelled)
            return CaddisFlyRuntimeService.ToJson(new CaddisFlyEnvelope
            {
                RunId = runId,
                Status = CaddisFlyStatus.Error,
                Error = "Run not found or already completed."
            });

        var envelope = _runtime.GetRunStatus(runId)!;
        return CaddisFlyRuntimeService.ToJson(envelope);
    }

    [Description("List all available CaddisFly workflow files (.yaml) in the workflows directory. Returns name, file name, step count, and description for each.")]
    public string ListWorkflows()
    {
        var workflows = _workflowLoader.ListWorkflows();
        if (workflows.Count == 0)
            return "No workflow files found. Place .yaml workflow files in the workflows directory.";

        return JsonSerializer.Serialize(workflows, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    [Description("Retrieve step-level output logs for a completed CaddisFly run. Returns stdout/stderr, exit codes, and timing for each step. Useful for diagnosing failures or reviewing past run results.")]
    public async Task<string> GetRunLogs(
        [Description("The run ID to retrieve logs for")] string runId)
    {
        var logs = await _runStore.ReadStepLogsAsync(runId);
        if (logs.Count == 0)
            return $"No logs found for run '{runId}'. The run may not exist, may have expired, or may not have produced any step logs.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Run Logs: {runId} ({logs.Count} step(s)) ===");
        sb.AppendLine();

        foreach (var (fileName, content) in logs)
        {
            sb.AppendLine($"--- {fileName} ---");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Blocks until the approval gate is resolved via the Chat UI.
    /// For chained approvals, loops to send a new approval card for each gate.
    /// If _host is null (no UI), returns the envelope as-is (existing behavior).
    /// </summary>
    private async Task<CaddisFlyEnvelope> WaitForApprovalResolutionAsync(CaddisFlyEnvelope envelope)
    {
        while (envelope.Status == CaddisFlyStatus.NeedsApproval && _host is not null)
        {
            var agentHandle = _host.GetHandle();
            if (!ThinkingNotifier.TryGetClientHandle(agentHandle, out var clientHandle))
                break;

            // Register a waiter BEFORE sending the approval card to avoid a race
            var tcs = _runtime.RegisterApprovalWaiter(envelope.RunId);

            await _host.SendMessage(new AgentMessage
            {
                ToHandle = clientHandle,
                FromHandle = agentHandle,
                Kind = MessageKind.OneWay,
                MessageType = "caddisfly-approval",
                Message = envelope.ApprovalPrompt ?? "Approval required to continue.",
                Args = new Dictionary<string, string>
                {
                    ["resumeToken"] = envelope.ResumeToken ?? envelope.RunId,
                    ["approvalPrompt"] = envelope.ApprovalPrompt ?? "Approval required to continue.",
                    ["pipelineName"] = envelope.RunId,
                    ["stepIndex"] = envelope.Steps.Count.ToString(),
                    ["totalSteps"] = (envelope.Steps.Count + 1).ToString()
                }
            });

            // Block the tool call until the user approves/denies (10-minute timeout)
            try
            {
                envelope = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(10));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Approval waiter for run {RunId} timed out after 10 minutes", envelope.RunId);
                _runtime.RemoveApprovalWaiter(envelope.RunId);
                break; // Return NeedsApproval as fallback
            }
        }

        return envelope;
    }
}
