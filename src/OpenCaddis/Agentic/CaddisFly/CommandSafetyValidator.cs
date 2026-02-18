namespace OpenCaddis.Agentic.CaddisFly;

/// <summary>
/// Shared safety blocklist for command execution across PowerShell and CaddisFly plugins.
/// </summary>
public static class CommandSafetyValidator
{
    private static readonly string[] BlockedPatterns =
    [
        // Destructive disk/system commands
        "format-volume",
        "clear-disk",
        "initialize-disk",
        "stop-computer",
        "restart-computer",
        "set-executionpolicy",

        // Eval-based bypass prevention
        "invoke-expression",
        "iex ",
        "iex(",
        "|iex",

        // Shell spawning to bypass blocklist
        "start-process powershell",
        "start-process pwsh",
        "start-process cmd",

        // Registry destruction
        "reg delete",

        // Unix/bash destructive patterns
        "rm -rf /",
        "mkfs.",
        "dd if=",
        ":(){",           // fork bomb
        "shutdown",
        "reboot",
        "init 0",
        "init 6",

        // Python dangerous patterns
        "os.system(\"rm ",
        "os.system('rm ",
        "shutil.rmtree(\"/\"",
        "shutil.rmtree('/'",

        // Node dangerous patterns
        "require('child_process')",
        "require(\"child_process\")",

        // curl piped to shell
        "curl|bash",
        "curl|sh",
        "curl |bash",
        "curl |sh",
        "curl | bash",
        "curl | sh",
        "wget|bash",
        "wget|sh",
        "wget |bash",
        "wget |sh",
        "wget | bash",
        "wget | sh",

        // git force push to main/master
        "push --force origin main",
        "push --force origin master",
        "push -f origin main",
        "push -f origin master",
    ];

    private static readonly string[] SystemPaths =
    [
        @"c:\windows",
        @"c:\program files",
        @"c:\program files (x86)",
        "/bin",
        "/sbin",
        "/usr/bin",
        "/usr/sbin",
        "/etc",
        "/boot",
    ];

    private static readonly string[] CriticalServices =
    [
        "wininit", "csrss", "lsass", "services", "svchost",
        "smss", "winlogon", "spoolsv", "wuauserv", "bits",
    ];

    /// <summary>
    /// Checks a command string against the safety blocklist.
    /// Returns an error message if blocked, or null if safe.
    /// </summary>
    public static string? CheckCommand(string input)
    {
        var normalized = input.Trim().ToLowerInvariant();

        foreach (var pattern in BlockedPatterns)
        {
            if (normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return $"Blocked: Command contains disallowed pattern '{pattern}'. This operation is not permitted for safety reasons.";
        }

        // Check Remove-Item -Recurse on system paths
        if (normalized.Contains("remove-item") && normalized.Contains("-recurse"))
        {
            foreach (var sysPath in SystemPaths)
            {
                if (normalized.Contains(sysPath, StringComparison.OrdinalIgnoreCase))
                    return $"Blocked: Recursive deletion on system path '{sysPath}' is not permitted.";
            }
        }

        // Check rm -rf on system paths
        if (normalized.Contains("rm ") && normalized.Contains("-r"))
        {
            foreach (var sysPath in SystemPaths)
            {
                if (normalized.Contains(sysPath, StringComparison.OrdinalIgnoreCase))
                    return $"Blocked: Recursive deletion on system path '{sysPath}' is not permitted.";
            }
        }

        // Check Remove-ItemProperty on HKLM:
        if (normalized.Contains("remove-itemproperty") && normalized.Contains("hklm:"))
            return "Blocked: Removing registry properties from HKLM is not permitted.";

        // Check Stop-Service / Set-Service on critical services
        if (normalized.Contains("stop-service") || normalized.Contains("set-service"))
        {
            foreach (var svc in CriticalServices)
            {
                if (normalized.Contains(svc, StringComparison.OrdinalIgnoreCase))
                    return $"Blocked: Modifying critical system service '{svc}' is not permitted.";
            }
        }

        return null;
    }
}
