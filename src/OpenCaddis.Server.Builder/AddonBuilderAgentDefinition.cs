using FabrCore.Core;
using OpenCaddis.Server.Builder.AI.Agents;
using OpenCaddis.Server.Builder.AI.Plugins;

namespace OpenCaddis.Server.Builder;

public static class AddonBuilderAgentDefinition
{
    public const string DefaultPrincipalHandle = "local-user";

    public static AgentConfiguration CreateConfiguration(
        BuilderProjectInfo project,
        string solutionFilePath,
        string outputPath,
        string principalHandle = DefaultPrincipalHandle,
        string modelName = "default")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var fullProjectPath = Path.GetFullPath(project.ProjectFilePath);
        var fullSolutionPath = Path.GetFullPath(solutionFilePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(fullProjectPath))
        {
            throw new FileNotFoundException("The project file does not exist.", fullProjectPath);
        }

        if (!File.Exists(fullSolutionPath))
        {
            throw new FileNotFoundException("The solution file does not exist.", fullSolutionPath);
        }

        return new AgentConfiguration
        {
            Handle = $"{principalHandle}:{project.AgentHandle}",
            AgentType = AddonBuilderAgent.Alias,
            Models = modelName.Trim(),
            Description = $"Coding agent for {project.Name}",
            SystemPrompt = CreateSystemPrompt(project, fullSolutionPath, fullOutputPath),
            Plugins =
            [
                RoslynCodeAnalysisPlugin.Alias,
                DotNetCliPlugin.Alias,
                ProjectFilesystemPlugin.Alias
            ],
            Args = new Dictionary<string, string>
            {
                [$"{RoslynCodeAnalysisPlugin.Alias}:ProjectPath"] = fullProjectPath,
                [$"{ProjectFilesystemPlugin.Alias}:ProjectPath"] = fullProjectPath,
                [$"{DotNetCliPlugin.Alias}:ProjectPath"] = fullProjectPath,
                [$"{DotNetCliPlugin.Alias}:SolutionPath"] = fullSolutionPath,
                [$"{DotNetCliPlugin.Alias}:OutputPath"] = fullOutputPath
            },
            ForceReconfigure = false
        };
    }

    private static string CreateSystemPrompt(
        BuilderProjectInfo project,
        string solutionFilePath,
        string outputPath) => $"""
        You are the dedicated coding agent for the .NET project '{project.Name}'.

        Scope:
        - Project file: {Path.GetFullPath(project.ProjectFilePath)}
        - Project directory: {Path.GetFullPath(project.ProjectDirectory)}
        - Solution file: {solutionFilePath}
        - OpenCaddis add-on DLL output: {outputPath}

        Working rules:
        1. Stay within this project. Do not claim to edit files outside the project directory.
        2. Inspect the project with Roslyn and project-files tools before changing code.
        3. Make the smallest coherent source change that satisfies the user's request.
        4. Use exact-text replacement for focused edits and full-file writes only when appropriate.
        5. After filesystem edits, reload the Roslyn project before requesting semantic diagnostics.
        6. Build the project and run the solution tests after meaningful code changes. Resolve failures caused by your changes.
        7. Publish the add-on only when the user asks to publish/deploy it or when their request clearly requires the running Server to load the new DLL.
        8. Report changed files, verification performed, and the published DLL path when applicable.
        9. Never invent tool results. If a tool reports an error, explain it and take a safe corrective step.
        """;
}
