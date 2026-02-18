using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenCaddis.Agentic.CaddisFly;

/// <summary>
/// Loads .yaml workflow definitions and converts them to CaddisFlyPipeline objects.
/// </summary>
public sealed class WorkflowFileLoader
{
    private readonly ILogger _logger;
    private readonly string _workflowPath;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public WorkflowFileLoader(ILogger logger, string workflowPath)
    {
        _logger = logger;
        _workflowPath = workflowPath;
    }

    /// <summary>
    /// Loads a named workflow from the workflows directory.
    /// </summary>
    public CaddisFlyPipeline Load(string workflowName, Dictionary<string, string>? variables = null)
    {
        var filePath = ResolveWorkflowPath(workflowName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Workflow file not found: {workflowName}", filePath);

        _logger.LogDebug("Loading workflow from {FilePath}", filePath);
        var yaml = File.ReadAllText(filePath);

        var def = YamlDeserializer.Deserialize<WorkflowDefinition>(yaml)
            ?? throw new FormatException($"Failed to parse workflow file: {workflowName}");

        var pipeline = ConvertToPipeline(def);

        // Apply variables from the file definition, then runtime overrides
        PipelineParser.ApplyVariables(pipeline, variables);

        return pipeline;
    }

    /// <summary>
    /// Lists all available workflow files in the workflows directory.
    /// </summary>
    public List<WorkflowInfo> ListWorkflows()
    {
        if (!Directory.Exists(_workflowPath))
            return [];

        var files = Directory.GetFiles(_workflowPath, "*.yaml")
            .Concat(Directory.GetFiles(_workflowPath, "*.yml"))
            .OrderBy(f => f);

        var result = new List<WorkflowInfo>();
        foreach (var file in files)
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var def = YamlDeserializer.Deserialize<WorkflowDefinition>(yaml);
                result.Add(new WorkflowInfo
                {
                    Name = def?.Name ?? Path.GetFileNameWithoutExtension(file),
                    FileName = Path.GetFileName(file),
                    StepCount = def?.Steps?.Count ?? 0,
                    Description = def?.Description ?? ""
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse workflow file {File}", file);
                result.Add(new WorkflowInfo
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    FileName = Path.GetFileName(file),
                    StepCount = 0,
                    Description = $"(parse error: {ex.Message})"
                });
            }
        }

        return result;
    }

    private string ResolveWorkflowPath(string workflowName)
    {
        // Accept with or without extension
        if (workflowName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
            workflowName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(_workflowPath, workflowName);
        }

        // Try direct file name match first
        var yamlPath = Path.Combine(_workflowPath, workflowName + ".yaml");
        if (File.Exists(yamlPath))
            return yamlPath;

        var ymlPath = Path.Combine(_workflowPath, workflowName + ".yml");
        if (File.Exists(ymlPath))
            return ymlPath;

        // Fallback: search by the 'name' field inside workflow files
        if (Directory.Exists(_workflowPath))
        {
            foreach (var file in Directory.GetFiles(_workflowPath, "*.yaml")
                .Concat(Directory.GetFiles(_workflowPath, "*.yml")))
            {
                try
                {
                    var yaml = File.ReadAllText(file);
                    var def = YamlDeserializer.Deserialize<WorkflowDefinition>(yaml);
                    if (def?.Name is not null &&
                        def.Name.Equals(workflowName, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
                catch
                {
                    // Skip files that fail to parse during name lookup
                }
            }
        }

        return yamlPath; // Return the .yaml path so the caller gets a clear "not found" error
    }

    private static CaddisFlyPipeline ConvertToPipeline(WorkflowDefinition def)
    {
        var steps = new List<PipelineStep>();

        foreach (var stepDef in def.Steps ?? [])
        {
            // Handle parallel group
            if (stepDef.Parallel is { Count: > 0 })
            {
                var parallelSteps = new List<PipelineStep>();
                foreach (var pDef in stepDef.Parallel)
                {
                    if (pDef.Command?.Equals("approve", StringComparison.OrdinalIgnoreCase) == true)
                        throw new FormatException("Approve commands are not allowed inside parallel groups.");

                    var pArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (pDef.Args is not null)
                    {
                        foreach (var (key, value) in pDef.Args)
                            pArgs[key] = value;
                    }

                    parallelSteps.Add(new PipelineStep
                    {
                        Name = pDef.Name ?? $"parallel-{parallelSteps.Count + 1}",
                        Command = pDef.Command ?? "echo",
                        Args = pArgs,
                        TimeoutSeconds = pDef.Timeout,
                        Retries = pDef.Retries ?? 0,
                        RetryDelaySeconds = pDef.RetryDelaySeconds ?? 2
                    });
                }

                steps.Add(new PipelineStep
                {
                    Name = stepDef.Name ?? $"parallel-group-{steps.Count + 1}",
                    Command = "parallel",
                    ParallelSteps = parallelSteps
                });
                continue;
            }

            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (stepDef.Args is not null)
            {
                foreach (var (key, value) in stepDef.Args)
                    args[key] = value;
            }

            steps.Add(new PipelineStep
            {
                Name = stepDef.Name ?? $"step-{steps.Count + 1}",
                Command = stepDef.Command ?? "echo",
                Args = args,
                TimeoutSeconds = stepDef.Timeout,
                Retries = stepDef.Retries ?? 0,
                RetryDelaySeconds = stepDef.RetryDelaySeconds ?? 2,
                ApprovalPrompt = stepDef.ApprovalPrompt
                    ?? (stepDef.Command?.Equals("approve", StringComparison.OrdinalIgnoreCase) == true
                        ? "Approval required to continue."
                        : null)
            });
        }

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (def.Variables is not null)
        {
            foreach (var (key, value) in def.Variables)
                variables[key] = value;
        }

        return new CaddisFlyPipeline
        {
            Name = def.Name ?? "unnamed",
            Steps = steps,
            Variables = variables
        };
    }
}

// YAML deserialization models

internal sealed class WorkflowDefinition
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Variables { get; set; }
    public List<WorkflowStepDefinition>? Steps { get; set; }
}

internal sealed class WorkflowStepDefinition
{
    public string? Name { get; set; }
    public string? Command { get; set; }
    public Dictionary<string, string>? Args { get; set; }
    public int? Timeout { get; set; }
    public int? Retries { get; set; }
    public int? RetryDelaySeconds { get; set; }
    public string? ApprovalPrompt { get; set; }
    public List<WorkflowStepDefinition>? Parallel { get; set; }
}

public sealed class WorkflowInfo
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public int StepCount { get; init; }
    public string Description { get; init; } = "";
}
