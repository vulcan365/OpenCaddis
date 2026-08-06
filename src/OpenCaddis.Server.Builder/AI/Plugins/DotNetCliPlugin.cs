using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace OpenCaddis.Server.Builder.AI.Plugins;

[PluginAlias(Alias)]
[Description("Controlled .NET CLI tools for one configured project and its solution")]
[FabrCoreCapabilities("Restores, builds, tests, formats, manages package references, and copies only the built project DLL to the configured OpenCaddis add-on directory.")]
[FabrCoreNote("Commands are explicit tool methods; no arbitrary shell or command string is exposed.")]
public sealed partial class DotNetCliPlugin : IFabrCorePlugin
{
    public const string Alias = "dotnet-cli";
    private const int MaxOutputCharacters = 40_000;
    private IFabrCoreAgentHost agentHost = default!;
    private string projectFilePath = string.Empty;
    private string solutionFilePath = string.Empty;
    private string outputPath = string.Empty;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        projectFilePath = RequireFile(config, "ProjectPath", ".csproj");
        solutionFilePath = RequireFile(config, "SolutionPath", ".sln", ".slnx");
        outputPath = Path.GetFullPath(
            config.GetPluginSetting(Alias, "OutputPath")
            ?? throw new InvalidOperationException($"{Alias}:OutputPath is required."));
        Directory.CreateDirectory(outputPath);
        return Task.CompletedTask;
    }

    [Description("Runs dotnet restore for the configured project.")]
    public Task<string> RestoreProject() => RunToolAsync(
        "Restoring project..",
        ["restore", projectFilePath]);

    [Description("Cleans build outputs for the configured project.")]
    public Task<string> CleanProject(
        [Description("Build configuration: Debug or Release")] string configuration = "Debug")
    {
        try
        {
            return RunToolAsync(
                "Cleaning project..",
                ["clean", projectFilePath, "--configuration", ValidateConfiguration(configuration)]);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Builds the configured project and returns compiler/MSBuild output.")]
    public Task<string> BuildProject(
        [Description("Build configuration: Debug or Release")] string configuration = "Debug")
    {
        try
        {
            return RunToolAsync(
                "Building project..",
                ["build", projectFilePath, "--configuration", ValidateConfiguration(configuration)]);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Builds every project in the configured solution.")]
    public Task<string> BuildSolution(
        [Description("Build configuration: Debug or Release")] string configuration = "Debug")
    {
        try
        {
            return RunToolAsync(
                "Building solution..",
                ["build", solutionFilePath, "--configuration", ValidateConfiguration(configuration)]);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Runs dotnet test for the configured solution so related test projects execute too.")]
    public Task<string> TestSolution(
        [Description("Build configuration: Debug or Release")] string configuration = "Debug")
    {
        try
        {
            return RunToolAsync(
                "Running solution tests..",
                ["test", solutionFilePath, "--configuration", ValidateConfiguration(configuration)]);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Runs dotnet format on the configured project and writes formatting changes to source files.")]
    public Task<string> FormatProject() => RunToolAsync(
        "Formatting project..",
        ["format", projectFilePath, "--verbosity", "minimal"]);

    [Description("Lists direct and transitive NuGet package references for the configured project.")]
    public Task<string> ListPackages(
        [Description("Whether to include transitive packages")] bool includeTransitive = true)
    {
        var arguments = new List<string> { "list", projectFilePath, "package" };
        if (includeTransitive)
        {
            arguments.Add("--include-transitive");
        }

        return RunToolAsync("Listing packages..", arguments);
    }

    [Description("Adds or updates a NuGet package reference in the configured project.")]
    public Task<string> AddPackage(
        [Description("NuGet package ID, for example Humanizer.Core")] string packageId,
        [Description("Optional exact package version; leave empty for NuGet's current stable version")] string? version = null)
    {
        try
        {
            ValidatePackageId(packageId);
            var arguments = new List<string>
            {
                "add", projectFilePath, "package", packageId
            };
            if (!string.IsNullOrWhiteSpace(version))
            {
                arguments.Add("--version");
                arguments.Add(version.Trim());
            }

            return RunToolAsync($"Adding {packageId}..", arguments);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Removes a NuGet package reference from the configured project.")]
    public Task<string> RemovePackage(
        [Description("NuGet package ID to remove")] string packageId)
    {
        try
        {
            ValidatePackageId(packageId);
            return RunToolAsync(
                $"Removing {packageId}..",
                ["remove", projectFilePath, "package", packageId]);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Lists project-to-project references for the configured project.")]
    public Task<string> ListProjectReferences() => RunToolAsync(
        "Listing project references..",
        ["list", projectFilePath, "reference"]);

    [Description("Adds a project-to-project reference. The referenced .csproj must be inside the configured solution directory.")]
    public Task<string> AddProjectReference(
        [Description("Referenced .csproj path relative to the solution directory")] string relativeProjectPath)
    {
        try
        {
            var referencedProject = ResolveSolutionProject(relativeProjectPath);
            return RunToolAsync(
                $"Adding reference to {Path.GetFileNameWithoutExtension(referencedProject)}..",
                ["add", projectFilePath, "reference", referencedProject]);
        }
        catch (Exception exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Removes a project-to-project reference. The referenced .csproj must be inside the configured solution directory.")]
    public Task<string> RemoveProjectReference(
        [Description("Referenced .csproj path relative to the solution directory")] string relativeProjectPath)
    {
        try
        {
            var referencedProject = ResolveSolutionProject(relativeProjectPath);
            return RunToolAsync(
                $"Removing reference to {Path.GetFileNameWithoutExtension(referencedProject)}..",
                ["remove", projectFilePath, "reference", referencedProject]);
        }
        catch (Exception exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    [Description("Builds the configured project in Release and copies only its primary DLL to the OpenCaddis Server add-on directory.")]
    public async Task<string> PublishAddon()
    {
        agentHost.SetStatusMessage("Building release add-on..");
        try
        {
            var build = await RunDotNetAsync(
                ["build", projectFilePath, "--configuration", "Release"]);
            var targetQuery = await RunDotNetAsync(
                [
                    "msbuild", projectFilePath,
                    "-getProperty:TargetPath",
                    "-property:Configuration=Release",
                    "-nologo"
                ]);
            var targetPath = targetQuery.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => Path.IsPathRooted(line) ? Path.GetFullPath(line) : string.Empty)
                .FirstOrDefault(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                ?? throw new InvalidOperationException(
                    "dotnet msbuild did not return an existing TargetPath DLL.");

            Directory.CreateDirectory(outputPath);
            var destinationPath = Path.Combine(outputPath, Path.GetFileName(targetPath));
            if (!string.Equals(
                    targetPath,
                    destinationPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                File.Copy(targetPath, destinationPath, overwrite: true);
            }

            return $"{build.Output}{Environment.NewLine}Published single add-on DLL to '{destinationPath}'.";
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
        finally
        {
            agentHost.SetStatusMessage(null);
        }
    }

    private async Task<string> RunToolAsync(
        string statusMessage,
        IReadOnlyList<string> arguments)
    {
        agentHost.SetStatusMessage(statusMessage);
        try
        {
            return (await RunDotNetAsync(arguments)).Output;
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
        finally
        {
            agentHost.SetStatusMessage(null);
        }
    }

    private async Task<DotNetResult> RunDotNetAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(projectFilePath)!,
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
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The .NET CLI could not be started. Install the .NET 10 SDK and ensure 'dotnet' is on PATH.",
                exception);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = string.Join(
            Environment.NewLine,
            new[] { (await standardOutput).Trim(), (await standardError).Trim() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        output = output.Length <= MaxOutputCharacters
            ? output
            : $"[Earlier output truncated]{Environment.NewLine}{output[^MaxOutputCharacters..]}";
        if (process.ExitCode != 0)
        {
            throw new BuilderCommandException(
                "dotnet " + string.Join(" ", arguments),
                process.ExitCode,
                output);
        }

        return new DotNetResult(output);
    }

    private static string RequireFile(
        AgentConfiguration config,
        string settingName,
        params string[] allowedExtensions)
    {
        var path = Path.GetFullPath(
            config.GetPluginSetting(Alias, settingName)
            ?? throw new InvalidOperationException($"{Alias}:{settingName} is required."));
        if (!File.Exists(path) ||
            !allowedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                $"{Alias}:{settingName} must identify an existing {string.Join(" or ", allowedExtensions)} file.",
                path);
        }

        return path;
    }

    private string ResolveSolutionProject(string relativeProjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeProjectPath);
        if (Path.IsPathRooted(relativeProjectPath))
        {
            throw new InvalidOperationException("The referenced project path must be relative to the solution directory.");
        }

        var solutionDirectory = Path.GetDirectoryName(solutionFilePath)!;
        var projectPath = Path.GetFullPath(relativeProjectPath, solutionDirectory);
        var rootPrefix = Path.TrimEndingDirectorySeparator(solutionDirectory) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!projectPath.StartsWith(rootPrefix, comparison) ||
            !projectPath.EndsWith(".csproj", comparison) ||
            !File.Exists(projectPath))
        {
            throw new InvalidOperationException(
                "The referenced project must be an existing .csproj inside the configured solution directory.");
        }

        return projectPath;
    }

    private static string ValidateConfiguration(string configuration)
    {
        if (configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        if (configuration.Equals("Release", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        throw new ArgumentException("Configuration must be Debug or Release.", nameof(configuration));
    }

    private static void ValidatePackageId(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (!PackageIdPattern().IsMatch(packageId.Trim()))
        {
            throw new ArgumentException(
                "Package IDs may contain only letters, numbers, periods, hyphens, and underscores.",
                nameof(packageId));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex PackageIdPattern();

    private sealed record DotNetResult(string Output);
}
