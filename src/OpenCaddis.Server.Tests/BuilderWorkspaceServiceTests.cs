using OpenCaddis.Server.Builder;

namespace OpenCaddis.Server.Tests;

[TestClass]
public sealed class BuilderWorkspaceServiceTests
{
    [TestMethod]
    public async Task Creates_solution_and_fabrcore_class_library()
    {
        var workspacePath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Builder.Workspace.Tests",
            Guid.NewGuid().ToString("N"));
        var service = new BuilderWorkspaceService();

        try
        {
            var initialWorkspace = service.Inspect(workspacePath);
            Assert.IsFalse(initialWorkspace.HasSolution);

            var solutionResult = await service.CreateSolutionAsync(workspacePath);
            Assert.IsTrue(solutionResult.Workspace.HasSolution);
            Assert.IsTrue(solutionResult.Workspace.SolutionFilePath!.EndsWith(".slnx"));

            const string projectName = "Sample.Agents";
            var projectResult = await service.CreateClassLibraryAsync(
                workspacePath,
                projectName);

            Assert.IsTrue(projectResult.Workspace.HasSolution);
            var projectFilePath = Path.Combine(
                workspacePath,
                projectName,
                $"{projectName}.csproj");
            Assert.IsTrue(File.Exists(projectFilePath));

            var projectFile = await File.ReadAllTextAsync(projectFilePath);
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", projectFile);
            Assert.Contains("FabrCore.Sdk", projectFile);
            Assert.Contains(BuilderWorkspaceService.FabrCoreSdkVersion, projectFile);

            var solutionFile = await File.ReadAllTextAsync(
                projectResult.Workspace.SolutionFilePath!);
            Assert.Contains(projectName, solutionFile);
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }
        }
    }

    [DataRow("Project Name")]
    [DataRow("../Project")]
    [DataRow("-Project")]
    [TestMethod]
    public void Rejects_project_names_that_could_escape_or_break_the_workspace(string projectName)
    {
        Assert.Throws<ArgumentException>(
            () => BuilderWorkspaceService.NormalizeProjectName(projectName));
    }
}
