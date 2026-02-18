using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic;
using OpenCaddis.Agentic.CaddisFly;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("PowerShell")]
public sealed class PowerShellPlugin : IFabrPlugin
{
    private IFabrAgentHost? _host;
    private ILogger<PowerShellPlugin> _logger = null!;
    private string _workingDirectory = Path.Combine(Path.GetTempPath(), "OpenCaddis") + Path.DirectorySeparatorChar;
    private int _timeoutSeconds = 30;
    private int _maxOutputLength = 10000;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<PowerShellPlugin>>();

        var workDir = config.GetPluginSetting("PowerShell", "WorkingDirectory");
        if (!string.IsNullOrWhiteSpace(workDir))
            _workingDirectory = workDir;

        var timeout = config.GetPluginSetting("PowerShell", "TimeoutSeconds");
        if (int.TryParse(timeout, out var t) && t > 0)
            _timeoutSeconds = t;

        var maxOutput = config.GetPluginSetting("PowerShell", "MaxOutputLength");
        if (int.TryParse(maxOutput, out var m) && m > 0)
            _maxOutputLength = m;

        if (!Directory.Exists(_workingDirectory))
            Directory.CreateDirectory(_workingDirectory);

        _logger.LogInformation("PowerShellPlugin initialized (workingDir: {WorkingDirectory}, timeout: {Timeout}s)", _workingDirectory, _timeoutSeconds);
        return Task.CompletedTask;
    }

    [Description("Execute a single PowerShell command on the host machine. Returns stdout, stderr, and exit code. Commands are validated against a safety blocklist before execution.")]
    public async Task<string> RunCommand(
        [Description("The PowerShell command to execute")] string command)
    {
        var rejection = CheckBlocklist(command);
        if (rejection is not null)
        {
            _logger.LogWarning("PowerShell command blocked: {Command}", command);
            return rejection;
        }

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Running PowerShell command...");

        _logger.LogDebug("Executing PowerShell command: {Command}", command);

        var escapedCommand = command.Replace("\"", "\\\"");
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -NonInteractive -Command \"{escapedCommand}\"",
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return await ExecuteProcessAsync(psi);
    }

    [Description("Execute a multi-line PowerShell script on the host machine. Each line is validated against a safety blocklist. The script is written to a temp file, executed, then cleaned up. Returns stdout, stderr, and exit code.")]
    public async Task<string> RunScript(
        [Description("The PowerShell script content (multiple lines allowed)")] string script)
    {
        // Validate each line against the blocklist
        var lines = script.Split('\n');
        foreach (var line in lines)
        {
            var rejection = CheckBlocklist(line);
            if (rejection is not null)
            {
                _logger.LogWarning("PowerShell script blocked on line: {Line}", line.Trim());
                return rejection;
            }
        }

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Running PowerShell script...");

        _logger.LogDebug("Executing PowerShell script ({LineCount} lines)", lines.Length);

        var tempFile = Path.Combine(Path.GetTempPath(), $"oc-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tempFile, script);

            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -NonInteractive -File \"{tempFile}\"",
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            return await ExecuteProcessAsync(psi);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    private async Task<string> ExecuteProcessAsync(ProcessStartInfo psi)
    {
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PowerShell command timed out after {Timeout}s", _timeoutSeconds);
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return $"Error: Command timed out after {_timeoutSeconds} seconds and was terminated.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell execution failed");
            return $"Error: Failed to execute PowerShell — {ex.Message}";
        }

        var output = new StringBuilder();

        var stdoutText = stdout.ToString().TrimEnd();
        var stderrText = stderr.ToString().TrimEnd();

        if (!string.IsNullOrEmpty(stdoutText))
        {
            output.AppendLine("=== STDOUT ===");
            output.AppendLine(Truncate(stdoutText));
        }

        if (!string.IsNullOrEmpty(stderrText))
        {
            output.AppendLine("=== STDERR ===");
            output.AppendLine(Truncate(stderrText));
        }

        output.AppendLine($"=== EXIT CODE: {process.ExitCode} ===");

        return output.ToString().TrimEnd();
    }

    private static string? CheckBlocklist(string input)
        => CommandSafetyValidator.CheckCommand(input);

    private string Truncate(string text)
    {
        if (text.Length <= _maxOutputLength)
            return text;

        return text[.._maxOutputLength] + $"\n... (output truncated at {_maxOutputLength} characters)";
    }
}
