using OpenCaddis.Server.Builder;
using OpenCaddis.Server.Builder.AI.Agents;
using OpenCaddis.Server.Builder.AI.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using FabrCore.Sdk;
using FabrCore.Surface.CommandCenter;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.IO.Compression;
using FabrCore.Core.Skills;

namespace OpenCaddis.Server.Tests;

[TestClass]
public sealed class BuilderWorkspaceServiceTests
{
    [TestMethod]
    public void Agent_provisioner_removes_the_trailing_slash_from_the_host_api_url()
    {
        var normalized = AddonBuilderAgentProvisioner.NormalizeHostUrl(
            new Uri("http://localhost:5083/"));

        Assert.AreEqual("http://localhost:5083", normalized);
    }

    [TestMethod]
    public async Task Agent_provisioner_adds_created_project_agent_to_surface_preferences()
    {
        HttpRequestMessage? savedRequest = null;
        SurfacePreferences? savedPreferences = null;
        var handler = new DelegateHttpMessageHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            savedRequest = request;
            savedPreferences = await request.Content!.ReadFromJsonAsync<SurfacePreferences>();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var httpClient = new HttpClient(handler);
        const string agentHandle = "local-user:addon-builder-sample-12345678";

        await AddonBuilderAgentProvisioner.AddToSurfaceAsync(
            httpClient,
            NullLoggerFactory.Instance,
            new Uri("http://localhost:5083/"),
            "local-user",
            agentHandle);

        Assert.IsNotNull(savedRequest);
        Assert.AreEqual(HttpMethod.Put, savedRequest.Method);
        Assert.AreEqual(
            "http://localhost:5083/fabrcoreapi/Storage/surface/command-center/preferences",
            savedRequest.RequestUri!.AbsoluteUri);
        Assert.AreEqual("local-user", savedRequest.Headers.GetValues("x-user-handle").Single());
        Assert.IsNotNull(savedPreferences);
        Assert.Contains(agentHandle, savedPreferences.SurfaceAgentHandles);
    }

    [TestMethod]
    public async Task Embedded_builder_skills_are_individually_published_and_pinned()
    {
        var packages = AddonBuilderSkillPackages.GetPackages();
        Assert.HasCount(25, packages);
        Assert.HasCount(24, packages.Where(package =>
            package.Name.StartsWith("fabrcore", StringComparison.Ordinal)));
        Assert.IsTrue(packages.Any(package => package.Name == "opencaddis"));
        Assert.HasCount(packages.Count, packages.Select(package => package.Name).Distinct(StringComparer.Ordinal));

        foreach (var package in packages)
        {
            Assert.HasCount(64, package.Version);
            using var archive = new ZipArchive(package.OpenRead(), ZipArchiveMode.Read);
            Assert.IsNotNull(archive.GetEntry("SKILL.md"), $"{package.Name} must contain SKILL.md at its root.");
            Assert.IsTrue(archive.Entries.All(entry =>
                entry.FullName.Split('/').Length - 1 <= FabrCoreSkillStorage.MaxResourceDepth));
        }

        var openCaddisPackage = packages.Single(package => package.Name == "opencaddis");
        using (var archive = new ZipArchive(openCaddisPackage.OpenRead(), ZipArchiveMode.Read))
        {
            Assert.IsNotNull(archive.GetEntry("agents/openai.yaml"));
            Assert.IsNotNull(archive.GetEntry("references/connection-model.md"));
            Assert.IsNotNull(archive.GetEntry("references/provider-recipes.md"));
            Assert.IsNotNull(archive.GetEntry("references/fabrcore-agent-integration.md"));
            Assert.IsNotNull(archive.GetEntry("references/development-and-operations.md"));
            using var reader = new StreamReader(archive.GetEntry("SKILL.md")!.Open());
            var markdown = await reader.ReadToEndAsync();
            Assert.Contains("IOpenCaddisAddonModule", markdown);
            Assert.Contains("AuthorizeHttpRequestAsync", markdown);
        }

        var published = new List<string>();
        var references = await AddonBuilderSkillPackages.PublishAsync(
            (principalId, name, version, zipStream, _) =>
            {
                Assert.AreEqual("local-user", principalId);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
                Assert.IsNotNull(archive.GetEntry("SKILL.md"));
                published.Add($"{name}@{version}");
                return Task.FromResult(new FabrCoreSkillPublishResult
                {
                    Manifest = new FabrCoreSkillManifest { Name = name, Version = version }
                });
            },
            "local-user");

        CollectionAssert.AreEqual(packages.Select(package => package.Reference).ToArray(), references.ToArray());
        CollectionAssert.AreEqual(packages.Select(package => package.Reference).ToArray(), published.ToArray());
    }

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
            Assert.Contains("OpenCaddis.Sdk", projectFile);
            Assert.Contains(BuilderWorkspaceService.OpenCaddisSdkVersion, projectFile);

            var solutionFile = await File.ReadAllTextAsync(
                projectResult.Workspace.SolutionFilePath!);
            Assert.Contains(projectName, solutionFile);

            var projects = await service.GetProjectsAsync(workspacePath);
            Assert.HasCount(1, projects);
            var project = projects[0];
            Assert.AreEqual(projectName, project.Name);
            Assert.IsTrue(project.AgentHandle.StartsWith("addon-builder-sample-agents-"));

            var agentConfiguration = AddonBuilderAgentDefinition.CreateConfiguration(
                project,
                projectResult.Workspace.SolutionFilePath!,
                Path.Combine(workspacePath, "published"));
            Assert.AreEqual(AddonBuilderAgent.Alias, agentConfiguration.AgentType);
            Assert.AreEqual($"local-user:{project.AgentHandle}", agentConfiguration.Handle);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    RoslynCodeAnalysisPlugin.Alias,
                    DotNetCliPlugin.Alias,
                    ProjectFilesystemPlugin.Alias
                },
                agentConfiguration.Plugins);
            Assert.AreEqual(
                project.ProjectFilePath,
                agentConfiguration.Args[$"{RoslynCodeAnalysisPlugin.Alias}:ProjectPath"]);
            Assert.AreEqual("todo", agentConfiguration.Args[HarnessArgs.Loop]);
            CollectionAssert.AreEqual(
                AddonBuilderSkillPackages.References.ToArray(),
                agentConfiguration.Args[HarnessArgs.Skills].Split(','));
            Assert.IsTrue(agentConfiguration.ForceReconfigure);

            await using var roslynPlugin = new RoslynCodeAnalysisPlugin();
            await roslynPlugin.InitializeAsync(
                agentConfiguration,
                new ServiceCollection().BuildServiceProvider());
            var overview = await roslynPlugin.GetProjectOverview();
            Assert.Contains(projectName, overview);
            var symbols = await roslynPlugin.FindSymbols("Class1");
            Assert.Contains("Class1", symbols);

            var agentHost = DispatchProxy.Create<IFabrCoreAgentHost, NoOpAgentHostProxy>();
            var pluginServices = new ServiceCollection()
                .AddSingleton(agentHost)
                .BuildServiceProvider();
            var dotNetPlugin = new DotNetCliPlugin();
            await dotNetPlugin.InitializeAsync(agentConfiguration, pluginServices);
            var buildOutput = await dotNetPlugin.BuildProject();
            Assert.DoesNotContain("Error:", buildOutput);
            var publishOutput = await dotNetPlugin.PublishAddon();
            Assert.Contains("Published single add-on DLL", publishOutput);
            Assert.IsTrue(File.Exists(Path.Combine(
                workspacePath,
                "published",
                $"{projectName}.dll")));
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Filesystem_plugin_edits_only_inside_its_project_scope()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Builder.Files.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var projectPath = Path.Combine(rootPath, "Scoped.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var config = new FabrCore.Core.AgentConfiguration
        {
            Args = new Dictionary<string, string>
            {
                [$"{ProjectFilesystemPlugin.Alias}:ProjectPath"] = projectPath
            }
        };

        try
        {
            var plugin = new ProjectFilesystemPlugin();
            await plugin.InitializeAsync(config, new ServiceCollection().BuildServiceProvider());

            var writeResult = await plugin.WriteTextFile("Calculator.cs", "public class Calculator { }", overwrite: false);
            Assert.StartsWith("Wrote", writeResult);
            var replaceResult = await plugin.ReplaceText("Calculator.cs", "{ }", "{ public int Add(int a, int b) => a + b; }");
            Assert.StartsWith("Replaced", replaceResult);
            var readResult = await plugin.ReadTextFile("Calculator.cs");
            Assert.Contains("Add(int a, int b)", readResult);

            var escapedWrite = await plugin.WriteTextFile("..\\outside.cs", "blocked", overwrite: true);
            Assert.StartsWith("Error:", escapedWrite);
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(rootPath)!, "outside.cs")));

            var generatedWrite = await plugin.WriteTextFile("nested\\obj\\generated.cs", "blocked", overwrite: true);
            Assert.StartsWith("Error:", generatedWrite);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
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

    private class NoOpAgentHostProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void))
            {
                return null;
            }

            if (targetMethod?.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
