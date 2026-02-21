using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("Docker")]
public sealed class DockerPlugin : IFabrCorePlugin, IDisposable
{
    private IFabrCoreAgentHost? _host;
    private ILogger<DockerPlugin> _logger = null!;
    private Process? _shellProcess;
    private readonly SemaphoreSlim _sessionSemaphore = new(1, 1);

    private int _timeoutSeconds = 60;
    private int _maxOutputLength = 10000;
    private string _shell = "bash";

    private static readonly Regex InteractiveFlagRegex = new(
        @"(?:^|\s)(-it|-ti|--interactive)(?:\s|$)",
        RegexOptions.Compiled);

    private static readonly string[] BlockedOperators =
    [
        "&&", "||", ";", "|", "`", "$(", "\n"
    ];

    private static readonly string[] BlockedRedirects = [">", "<"];

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrCoreAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<DockerPlugin>>();

        var timeout = config.GetPluginSetting("Docker", "TimeoutSeconds");
        if (int.TryParse(timeout, out var t) && t > 0)
            _timeoutSeconds = t;

        var maxOutput = config.GetPluginSetting("Docker", "MaxOutputLength");
        if (int.TryParse(maxOutput, out var m) && m > 0)
            _maxOutputLength = m;

        var shell = config.GetPluginSetting("Docker", "Shell");
        if (!string.IsNullOrWhiteSpace(shell))
            _shell = shell;

        _logger.LogInformation("DockerPlugin initialized (shell: {Shell}, timeout: {Timeout}s)", _shell, _timeoutSeconds);
        return Task.CompletedTask;
    }

    [Description("Execute a Docker CLI command in a persistent shell session. The session stays alive between calls so environment and state are preserved. Commands must start with 'docker'. Returns stdout, stderr, and exit code.")]
    public async Task<string> RunDockerCommand(
        [Description("The docker command to execute (e.g. 'docker ps', 'docker images', 'docker compose up -d')")] string command)
    {
        var rejection = ValidateCommand(command);
        if (rejection is not null)
        {
            _logger.LogWarning("Docker command blocked: {Command}", command);
            return rejection;
        }

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Running Docker command...");

        _logger.LogDebug("Executing Docker command: {Command}", command);

        await _sessionSemaphore.WaitAsync();
        try
        {
            EnsureSessionAlive();
            var result = await ExecuteInSessionAsync(command);
            _logger.LogDebug("Docker command completed");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker command failed: {Command}", command);
            return $"Error: Failed to execute Docker command — {ex.Message}";
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    [Description("Check the status of the persistent Docker shell session. Reports whether the session is not started, alive (with PID), or dead.")]
    public string GetSessionStatus()
    {
        if (_shellProcess is null)
            return "Session status: Not started";

        if (_shellProcess.HasExited)
            return $"Session status: Dead (PID {_shellProcess.Id}, exit code {_shellProcess.ExitCode})";

        return $"Session status: Alive (PID {_shellProcess.Id})";
    }

    [Description("Kill the current Docker shell session and start a fresh one. Use this if the session becomes unresponsive.")]
    public async Task<string> RestartSession()
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Restarting Docker session...");

        await _sessionSemaphore.WaitAsync();
        try
        {
            KillSession();
            StartSession();
            _logger.LogInformation("Docker session restarted (PID: {Pid})", _shellProcess!.Id);
            return $"Session restarted. New PID: {_shellProcess!.Id}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restart Docker session");
            return $"Error: Failed to restart session — {ex.Message}";
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    private void EnsureSessionAlive()
    {
        if (_shellProcess is null || _shellProcess.HasExited)
        {
            KillSession();
            StartSession();
        }
    }

    private void StartSession()
    {
        _logger.LogDebug("Starting Docker shell session ({Shell})", _shell);
        var psi = new ProcessStartInfo
        {
            FileName = _shell,
            Arguments = "--norc --noprofile",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["TERM"] = "dumb",
                ["PS1"] = "",
            }
        };

        _shellProcess = new Process { StartInfo = psi };
        _shellProcess.Start();
    }

    private async Task<string> ExecuteInSessionAsync(string command)
    {
        var process = _shellProcess!;
        var sentinel = $"__SENTINEL_{Guid.NewGuid():N}__";
        var exitMarker = $"__EXIT_{Guid.NewGuid():N}__";

        // Write the command, followed by exit code capture and sentinels
        var wrappedCommand = $"{command}\necho \"{exitMarker}$?\"\necho \"{sentinel}\"\necho \"{sentinel}\" >&2\n";
        await process.StandardInput.WriteAsync(wrappedCommand);
        await process.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        int exitCode = -1;

        var timedOut = false;

        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    if (line == sentinel) break;
                    if (line.StartsWith(exitMarker))
                    {
                        int.TryParse(line[exitMarker.Length..], out exitCode);
                        continue;
                    }
                    stdoutBuilder.AppendLine(line);
                }
            }
            catch (OperationCanceledException) { timedOut = true; }
        });

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    if (line == sentinel) break;
                    stderrBuilder.AppendLine(line);
                }
            }
            catch (OperationCanceledException) { timedOut = true; }
        });

        await Task.WhenAll(stdoutTask, stderrTask);

        if (timedOut)
        {
            _logger.LogWarning("Docker command timed out after {Timeout}s", _timeoutSeconds);
            return $"Error: Command timed out after {_timeoutSeconds} seconds. The session is still alive — you can run another command or restart the session.";
        }

        return FormatOutput(stdoutBuilder, stderrBuilder, exitCode);
    }

    private string FormatOutput(StringBuilder stdout, StringBuilder stderr, int exitCode)
    {
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

        output.AppendLine($"=== EXIT CODE: {exitCode} ===");

        return output.ToString().TrimEnd();
    }

    private string? ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Error: Command cannot be empty.";

        var trimmed = command.Trim();

        // Must start with "docker"
        if (!trimmed.StartsWith("docker", StringComparison.OrdinalIgnoreCase))
            return "Blocked: Command must start with 'docker'. This plugin only executes Docker CLI commands.";

        // Block shell operators that could chain non-docker commands
        foreach (var op in BlockedOperators)
        {
            if (trimmed.Contains(op, StringComparison.Ordinal))
                return $"Blocked: Command contains disallowed shell operator '{op}'. Only single Docker commands are allowed.";
        }

        // Block I/O redirection
        foreach (var redir in BlockedRedirects)
        {
            if (trimmed.Contains(redir, StringComparison.Ordinal))
                return $"Blocked: Command contains disallowed I/O redirection '{redir}'. Use docker logs or docker cp instead.";
        }

        // Block interactive flags on exec/run that would hang the session
        if (trimmed.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("run", StringComparison.OrdinalIgnoreCase))
        {
            if (InteractiveFlagRegex.IsMatch(trimmed))
                return "Blocked: Interactive flags (-it, -ti, --interactive) are not allowed as they would hang the session. Use non-interactive alternatives.";
        }

        return null;
    }

    private void KillSession()
    {
        if (_shellProcess is null) return;

        try
        {
            if (!_shellProcess.HasExited)
            {
                _shellProcess.StandardInput.WriteLine("exit");
                _shellProcess.WaitForExit(3000);

                if (!_shellProcess.HasExited)
                    _shellProcess.Kill(entireProcessTree: true);
            }
        }
        catch { /* best-effort cleanup */ }

        _shellProcess.Dispose();
        _shellProcess = null;
    }

    private string Truncate(string text)
    {
        if (text.Length <= _maxOutputLength)
            return text;

        return text[.._maxOutputLength] + $"\n... (output truncated at {_maxOutputLength} characters)";
    }

    public void Dispose()
    {
        KillSession();
        _sessionSemaphore.Dispose();
    }
}
