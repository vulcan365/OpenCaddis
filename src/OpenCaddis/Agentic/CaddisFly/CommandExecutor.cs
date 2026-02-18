using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenCaddis.Agentic.CaddisFly;

/// <summary>
/// Executes individual pipeline steps via subprocess or built-in handlers.
/// </summary>
public sealed class CommandExecutor
{
    private readonly ILogger _logger;
    private readonly int _defaultTimeoutSeconds;
    private readonly int _maxOutputLength;
    private readonly string _workingDirectory;

    // Commands whose args are passed via -c "..." (shell-style)
    private static readonly HashSet<string> ShellStyleExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "pwsh", "bash", "python3", "python", "node"
    };

    private readonly Dictionary<string, string> _commands;

    public CommandExecutor(ILogger logger, int defaultTimeoutSeconds, int maxOutputLength, string workingDirectory,
        Dictionary<string, string>? customCommands = null)
    {
        _logger = logger;
        _defaultTimeoutSeconds = defaultTimeoutSeconds;
        _maxOutputLength = maxOutputLength;
        _workingDirectory = workingDirectory;

        // Initialize with defaults
        _commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["powershell"] = "pwsh",
            ["bash"] = "bash",
            ["docker"] = "docker",
            ["python"] = "python3",
            ["node"] = "node",
            ["curl"] = "curl",
            ["git"] = "git",
            ["dotnet"] = "dotnet",
            ["npm"] = "npm",
        };

        // Merge custom commands (overrides allowed)
        if (customCommands is not null)
        {
            foreach (var (key, value) in customCommands)
                _commands[key] = value;
        }
    }

    /// <summary>
    /// Executes a single pipeline step. Returns a step result.
    /// Built-in commands (approve, echo, set-var) are handled inline.
    /// Shell commands are executed as subprocesses.
    /// </summary>
    public async Task<CaddisFlyStepResult> ExecuteStepAsync(
        PipelineStep step,
        string previousOutput,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // Built-in: echo
        if (step.Command.Equals("echo", StringComparison.OrdinalIgnoreCase))
        {
            var message = step.Args.TryGetValue("message", out var m) ? m
                : step.Args.TryGetValue("arg1", out var a) ? a
                : previousOutput;
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = "echo",
                Output = message,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Built-in: set-var (just passes through value)
        if (step.Command.Equals("set-var", StringComparison.OrdinalIgnoreCase))
        {
            var value = step.Args.TryGetValue("value", out var v) ? v : previousOutput;
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = "set-var",
                Output = value,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Built-in: approve — this should be handled by the runtime, not executor
        if (step.Command.Equals("approve", StringComparison.OrdinalIgnoreCase))
        {
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = "approve",
                Output = previousOutput,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Subprocess execution
        return await ExecuteSubprocessAsync(step, previousOutput, sw, cancellationToken);
    }

    private async Task<CaddisFlyStepResult> ExecuteSubprocessAsync(
        PipelineStep step,
        string previousOutput,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        // Resolve the executable
        if (!_commands.TryGetValue(step.Command, out var executable))
        {
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                ExitCode = -1,
                Error = $"Unknown command '{step.Command}'. Supported commands: {string.Join(", ", _commands.Keys)}, echo, set-var, approve.",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Build the command string from args
        var commandStr = step.Args.TryGetValue("command", out var cmd) ? cmd
            : step.Args.TryGetValue("arg1", out var a) ? a
            : "";

        if (string.IsNullOrWhiteSpace(commandStr))
        {
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                ExitCode = -1,
                Error = "No command argument provided for subprocess step.",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Safety check
        var rejection = CommandSafetyValidator.CheckCommand(commandStr);
        if (rejection is not null)
        {
            _logger.LogWarning("CaddisFly step '{StepName}' blocked: {Command}", step.Name, commandStr);
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                ExitCode = -1,
                Error = rejection,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Build process
        var psi = BuildProcessStartInfo(executable, commandStr);

        _logger.LogDebug("CaddisFly executing step '{StepName}': {Executable} {Command}", step.Name, executable, commandStr);

        var timeout = step.TimeoutSeconds ?? _defaultTimeoutSeconds;

        try
        {
            return await RunProcessAsync(step, psi, previousOutput, timeout, sw, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                ExitCode = -1,
                Error = "Step was cancelled.",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CaddisFly step '{StepName}' failed", step.Name);
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                ExitCode = -1,
                Error = $"Execution failed: {ex.Message}",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    private ProcessStartInfo BuildProcessStartInfo(string executable, string commandStr)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (executable == "pwsh")
        {
            var escaped = commandStr.Replace("\"", "\\\"");
            psi.FileName = "pwsh";
            psi.Arguments = $"-NoProfile -NonInteractive -Command \"{escaped}\"";
        }
        else if (ShellStyleExecutables.Contains(executable) && executable != "pwsh")
        {
            // bash, python3, python, node — use -c "command"
            psi.FileName = executable;
            psi.Arguments = $"-c \"{commandStr.Replace("\"", "\\\"")}\"";
        }
        else
        {
            // docker, curl, git, dotnet, npm, and all custom executables — args passed directly
            psi.FileName = executable;
            psi.Arguments = commandStr;
        }

        return psi;
    }

    private async Task<CaddisFlyStepResult> RunProcessAsync(
        PipelineStep step,
        ProcessStartInfo psi,
        string previousOutput,
        int timeoutSeconds,
        Stopwatch sw,
        CancellationToken cancellationToken)
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
        }
        catch (Win32Exception)
        {
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                ExitCode = -1,
                Error = $"Executable '{psi.FileName}' not found. Is it installed in this environment?",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Pipe previous output as stdin if available
        if (!string.IsNullOrEmpty(previousOutput))
        {
            await process.StandardInput.WriteAsync(previousOutput);
        }
        process.StandardInput.Close();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("CaddisFly step '{StepName}' timed out after {Timeout}s", step.Name, timeoutSeconds);
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return new CaddisFlyStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                ExitCode = -1,
                Error = $"Step timed out after {timeoutSeconds} seconds.",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        var stdoutText = Truncate(stdout.ToString().TrimEnd());
        var stderrText = Truncate(stderr.ToString().TrimEnd());

        return new CaddisFlyStepResult
        {
            StepName = step.Name,
            Command = step.Command,
            ExitCode = process.ExitCode,
            Output = stdoutText,
            Error = stderrText,
            DurationMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private string Truncate(string text)
    {
        if (text.Length <= _maxOutputLength)
            return text;

        return text[.._maxOutputLength] + $"\n... (output truncated at {_maxOutputLength} characters)";
    }
}
