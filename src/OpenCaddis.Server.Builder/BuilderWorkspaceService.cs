using System.Diagnostics;
using System.Text;

namespace OpenCaddis.Server.Builder;

public sealed class BuilderWorkspaceService
{
    public const string FabrCoreSdkVersion = "1.6.3";

    private readonly SemaphoreSlim commandLock = new(1, 1);

    public BuilderWorkspaceInfo Inspect(string solutionDirectory)
    {
        var fullPath = NormalizeSolutionDirectory(solutionDirectory);
        if (!Directory.Exists(fullPath))
        {
            return new BuilderWorkspaceInfo(fullPath, []);
        }

        var solutionFiles = Directory
            .EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSolutionFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BuilderWorkspaceInfo(fullPath, solutionFiles);
    }

    public async Task<BuilderOperationResult> CreateSolutionAsync(
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizeSolutionDirectory(solutionDirectory);
        await commandLock.WaitAsync(cancellationToken);
        try
        {
            var existingWorkspace = Inspect(fullPath);
            if (existingWorkspace.SolutionFiles.Count > 0)
            {
                throw new InvalidOperationException(
                    $"A solution already exists in '{fullPath}'.");
            }

            Directory.CreateDirectory(fullPath);
            var solutionName = CreateSolutionName(fullPath);
            var command = await RunDotNetAsync(
                fullPath,
                [
                    "new", "sln",
                    "--name", solutionName,
                    "--output", fullPath,
                    "--format", "slnx",
                    "--no-update-check"
                ],
                cancellationToken);

            var workspace = Inspect(fullPath);
            if (!workspace.HasSolution)
            {
                throw new InvalidOperationException(
                    "The .NET CLI completed without creating exactly one solution file.");
            }

            return new BuilderOperationResult(workspace, command.Output);
        }
        finally
        {
            commandLock.Release();
        }
    }

    public async Task<BuilderOperationResult> CreateClassLibraryAsync(
        string solutionDirectory,
        string projectName,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizeSolutionDirectory(solutionDirectory);
        var normalizedProjectName = NormalizeProjectName(projectName);
        await commandLock.WaitAsync(cancellationToken);
        try
        {
            var workspace = Inspect(fullPath);
            if (!workspace.HasSolution)
            {
                throw workspace.HasMultipleSolutions
                    ? new InvalidOperationException(
                        $"More than one solution exists in '{fullPath}'. Keep exactly one solution in the folder.")
                    : new InvalidOperationException(
                        $"Create a solution in '{fullPath}' before adding a project.");
            }

            var projectDirectory = Path.GetFullPath(Path.Combine(fullPath, normalizedProjectName));
            if (Directory.Exists(projectDirectory))
            {
                throw new InvalidOperationException(
                    $"The project directory '{projectDirectory}' already exists.");
            }

            var projectFilePath = Path.Combine(projectDirectory, $"{normalizedProjectName}.csproj");
            var output = new StringBuilder();

            var createResult = await RunDotNetAsync(
                fullPath,
                [
                    "new", "classlib",
                    "--name", normalizedProjectName,
                    "--output", projectDirectory,
                    "--framework", "net10.0",
                    "--no-restore",
                    "--no-update-check"
                ],
                cancellationToken);
            AppendOutput(output, createResult);

            var packageResult = await RunDotNetAsync(
                fullPath,
                [
                    "add", projectFilePath,
                    "package", "FabrCore.Sdk",
                    "--version", FabrCoreSdkVersion
                ],
                cancellationToken);
            AppendOutput(output, packageResult);

            var solutionResult = await RunDotNetAsync(
                fullPath,
                ["sln", workspace.SolutionFilePath!, "add", projectFilePath],
                cancellationToken);
            AppendOutput(output, solutionResult);

            return new BuilderOperationResult(Inspect(fullPath), output.ToString().Trim());
        }
        finally
        {
            commandLock.Release();
        }
    }

    public static string NormalizeSolutionDirectory(string solutionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionDirectory);
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(solutionDirectory.Trim())));
    }

    public static string NormalizeProjectName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        var normalizedName = projectName.Trim();
        if (!char.IsLetterOrDigit(normalizedName[0]) ||
            normalizedName.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Project names must start with a letter or number and contain only letters, numbers, periods, hyphens, or underscores.",
                nameof(projectName));
        }

        return normalizedName;
    }

    private static bool IsSolutionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateSolutionName(string solutionDirectory)
    {
        var directoryName = new DirectoryInfo(solutionDirectory).Name;
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return "OpenCaddisSolution";
        }

        var name = new string(directoryName
            .Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                    ? character
                    : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(name.Trim('.', '-', '_'))
            ? "OpenCaddisSolution"
            : name;
    }

    private static void AppendOutput(StringBuilder output, DotNetCommandResult result)
    {
        if (output.Length > 0)
        {
            output.AppendLine();
        }

        output.AppendLine($"> {result.Command}");
        output.AppendLine(result.Output);
    }

    private static async Task<DotNetCommandResult> RunDotNetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The .NET CLI process could not be started.");
            }
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The .NET CLI could not be started. Install the .NET 10 SDK and ensure 'dotnet' is on PATH.",
                exception);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var output = JoinOutput(await standardOutput, await standardError);
        var command = FormatCommand(arguments);
        if (process.ExitCode != 0)
        {
            throw new BuilderCommandException(command, process.ExitCode, output);
        }

        return new DotNetCommandResult(command, output);
    }

    private static string JoinOutput(string standardOutput, string standardError) =>
        string.Join(
            Environment.NewLine,
            new[] { standardOutput.Trim(), standardError.Trim() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatCommand(IEnumerable<string> arguments) =>
        "dotnet " + string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace)
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;

    private sealed record DotNetCommandResult(string Command, string Output);
}
