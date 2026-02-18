using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic.CaddisFly;

namespace OpenCaddis.Services;

/// <summary>
/// File-based persistence for CaddisFly run states. Stores runs as JSON files
/// in a configurable directory. Only runs paused at approval gates are meaningfully
/// resumable; completed/failed runs are kept for status queries with retention cleanup.
/// </summary>
public sealed class CaddisFlyRunStore
{
    private readonly ILogger<CaddisFlyRunStore> _logger;
    private string _runsPath;
    private readonly TimeSpan _retentionPeriod = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CaddisFlyRunStore(ILogger<CaddisFlyRunStore> logger)
    {
        _logger = logger;
        _runsPath = Path.Combine(Path.GetTempPath(), "OpenCaddis", "runs");
    }

    public void SetRunsPath(string path) => _runsPath = path;

    public async Task SaveAsync(CaddisFlyRunState state)
    {
        try
        {
            Directory.CreateDirectory(_runsPath);
            var filePath = Path.Combine(_runsPath, $"{state.RunId}.json");
            var dto = state.ToDto();
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist run state {RunId}", state.RunId);
        }
    }

    public async Task<CaddisFlyRunState?> LoadAsync(string runId)
    {
        var filePath = Path.Combine(_runsPath, $"{runId}.json");
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var dto = JsonSerializer.Deserialize<CaddisFlyRunStateDto>(json, JsonOptions);
            return dto is not null ? CaddisFlyRunState.FromDto(dto) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load run state {RunId}", runId);
            return null;
        }
    }

    public async Task<List<CaddisFlyRunState>> LoadPausedRunsAsync()
    {
        var result = new List<CaddisFlyRunState>();
        if (!Directory.Exists(_runsPath)) return result;

        foreach (var file in Directory.GetFiles(_runsPath, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var dto = JsonSerializer.Deserialize<CaddisFlyRunStateDto>(json, JsonOptions);
                if (dto is not null && dto.Status == CaddisFlyStatus.NeedsApproval)
                {
                    result.Add(CaddisFlyRunState.FromDto(dto));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load run state from {File}", file);
            }
        }

        return result;
    }

    public void Delete(string runId)
    {
        var filePath = Path.Combine(_runsPath, $"{runId}.json");
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete run state {RunId}", runId);
        }
    }

    public async Task WriteStepLogAsync(string runId, int stepIndex, CaddisFlyStepResult result, PipelineStep step)
    {
        try
        {
            var logDir = Path.Combine(_runsPath, runId);
            Directory.CreateDirectory(logDir);

            var safeName = string.Join("_", step.Name.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"step-{stepIndex + 1:D3}-{safeName}.log";
            var filePath = Path.Combine(logDir, fileName);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Step: {step.Name} ===");
            sb.AppendLine($"Command: {step.Command}");
            sb.AppendLine($"Args: {System.Text.Json.JsonSerializer.Serialize(step.Args)}");
            sb.AppendLine($"Started: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine($"Duration: {result.DurationMs:F0}ms");
            sb.AppendLine($"Exit Code: {result.ExitCode}");
            sb.AppendLine($"Attempt: {result.Attempt}");
            sb.AppendLine();
            sb.AppendLine("=== STDOUT ===");
            sb.AppendLine(string.IsNullOrEmpty(result.Output) ? "(empty)" : result.Output);
            sb.AppendLine();
            sb.AppendLine("=== STDERR ===");
            sb.AppendLine(string.IsNullOrEmpty(result.Error) ? "(empty)" : result.Error);

            await File.WriteAllTextAsync(filePath, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write step log for run {RunId} step {StepIndex}", runId, stepIndex);
        }
    }

    public async Task<List<(string FileName, string Content)>> ReadStepLogsAsync(string runId)
    {
        var result = new List<(string FileName, string Content)>();
        var logDir = Path.Combine(_runsPath, runId);

        if (!Directory.Exists(logDir))
            return result;

        var files = Directory.GetFiles(logDir, "step-*.log").OrderBy(f => f).ToArray();
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                result.Add((Path.GetFileName(file), content));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read step log {File}", file);
            }
        }

        return result;
    }

    public void CleanupExpired()
    {
        if (!Directory.Exists(_runsPath)) return;

        var cutoff = DateTimeOffset.UtcNow - _retentionPeriod;
        foreach (var file in Directory.GetFiles(_runsPath, "*.json"))
        {
            try
            {
                var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                if (lastWrite < cutoff)
                {
                    File.Delete(file);
                    _logger.LogDebug("Cleaned up expired run state: {File}", Path.GetFileName(file));

                    // Also delete the corresponding log directory
                    var runId = Path.GetFileNameWithoutExtension(file);
                    var logDir = Path.Combine(_runsPath, runId);
                    if (Directory.Exists(logDir))
                    {
                        Directory.Delete(logDir, recursive: true);
                        _logger.LogDebug("Cleaned up expired run logs: {Dir}", runId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup run state file {File}", file);
            }
        }
    }
}
