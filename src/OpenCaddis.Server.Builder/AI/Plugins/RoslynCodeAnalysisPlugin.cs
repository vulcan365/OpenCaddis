using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace OpenCaddis.Server.Builder.AI.Plugins;

[PluginAlias(Alias)]
[Description("Roslyn semantic inspection tools for one configured .NET project")]
[FabrCoreCapabilities("Loads the project through MSBuildWorkspace and exposes project structure, documents, declarations, symbol locations, and compiler diagnostics.")]
[FabrCoreNote("Call ReloadProject after filesystem edits so later semantic queries see the current source.")]
public sealed class RoslynCodeAnalysisPlugin : IFabrCorePlugin, IAsyncDisposable
{
    public const string Alias = "roslyn-code";
    private readonly SemaphoreSlim workspaceLock = new(1, 1);
    private readonly ConcurrentQueue<string> workspaceDiagnostics = new();
    private string projectFilePath = string.Empty;
    private ProjectPathScope scope = default!;
    private MSBuildWorkspace? workspace;
    private Project? project;
    private WorkspaceEventRegistration? workspaceFailedRegistration;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        projectFilePath = Path.GetFullPath(
            config.GetPluginSetting(Alias, "ProjectPath")
            ?? throw new InvalidOperationException($"{Alias}:ProjectPath is required."));
        if (!File.Exists(projectFilePath))
        {
            throw new FileNotFoundException("The configured project file does not exist.", projectFilePath);
        }

        scope = new ProjectPathScope(projectFilePath);
        return Task.CompletedTask;
    }

    [Description("Returns the configured project's identity, language, documents, project references, metadata-reference count, and any MSBuild workspace warnings.")]
    public async Task<string> GetProjectOverview()
    {
        try
        {
            var loadedProject = await GetProjectAsync();
            var projectReferences = loadedProject.ProjectReferences
                .Select(reference => loadedProject.Solution.GetProject(reference.ProjectId)?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return JsonSerializer.Serialize(new
            {
                loadedProject.Name,
                loadedProject.AssemblyName,
                loadedProject.Language,
                ProjectFile = projectFilePath,
                ProjectDirectory = scope.RootPath,
                DocumentCount = loadedProject.Documents.Count(),
                AdditionalDocumentCount = loadedProject.AdditionalDocuments.Count(),
                AnalyzerConfigDocumentCount = loadedProject.AnalyzerConfigDocuments.Count(),
                ProjectReferences = projectReferences,
                MetadataReferenceCount = loadedProject.MetadataReferences.Count(),
                WorkspaceDiagnostics = workspaceDiagnostics.ToArray()
            }, JsonOptions);
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Lists source documents known to Roslyn with paths relative to the configured project.")]
    public async Task<string> ListDocuments()
    {
        try
        {
            var loadedProject = await GetProjectAsync();
            var documents = loadedProject.Documents
                .Where(document => !string.IsNullOrWhiteSpace(document.FilePath))
                .Select(document => new
                {
                    document.Name,
                    Path = Path.GetRelativePath(scope.RootPath, document.FilePath!),
                    Folders = document.Folders
                })
                .OrderBy(document => document.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return JsonSerializer.Serialize(documents, JsonOptions);
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Returns the classes, records, structs, interfaces, enums, delegates, constructors, methods, properties, and fields declared in one C# source document.")]
    public async Task<string> GetDocumentOutline(
        [Description("C# document path relative to the project root")] string relativePath)
    {
        try
        {
            var document = await GetDocumentAsync(relativePath);
            var root = await document.GetSyntaxRootAsync()
                ?? throw new InvalidOperationException("Roslyn could not read the document syntax tree.");
            var declarations = root.DescendantNodes()
                .Select(CreateDeclarationSummary)
                .Where(summary => summary is not null)
                .ToArray();
            return JsonSerializer.Serialize(declarations, JsonOptions);
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Finds exact symbol declarations by name in the configured project and returns their kinds, signatures, and source locations.")]
    public async Task<string> FindSymbols(
        [Description("Exact type or member name to find, matched without case sensitivity")] string name)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var loadedProject = await GetProjectAsync();
            var symbols = await SymbolFinder.FindDeclarationsAsync(
                loadedProject,
                name.Trim(),
                ignoreCase: true);
            var results = symbols
                .Take(100)
                .Select(symbol => new
                {
                    Kind = symbol.Kind.ToString(),
                    Name = symbol.Name,
                    Signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    Locations = symbol.Locations
                        .Where(location => location.IsInSource && location.SourceTree?.FilePath is not null)
                        .Select(location => new
                        {
                            Path = Path.GetRelativePath(scope.RootPath, location.SourceTree!.FilePath),
                            Line = location.GetLineSpan().StartLinePosition.Line + 1,
                            Column = location.GetLineSpan().StartLinePosition.Character + 1
                        })
                        .ToArray()
                })
                .ToArray();
            return results.Length == 0
                ? $"No declarations named '{name}' were found."
                : JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Compiles the configured project in memory and returns current Roslyn compiler diagnostics. Optionally restricts results to one relative source path.")]
    public async Task<string> GetCompilerDiagnostics(
        [Description("Optional source path relative to the project root; leave empty for the entire project")] string? relativePath = null,
        [Description("Maximum diagnostics to return, from 1 to 500")] int maxResults = 200)
    {
        try
        {
            var loadedProject = await GetProjectAsync();
            var compilation = await loadedProject.GetCompilationAsync()
                ?? throw new InvalidOperationException("Roslyn could not create the project compilation.");
            string? targetPath = null;
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                targetPath = scope.Resolve(relativePath);
            }

            maxResults = Math.Clamp(maxResults, 1, 500);
            var diagnostics = compilation.GetDiagnostics()
                .Where(diagnostic => targetPath is null ||
                    string.Equals(
                        diagnostic.Location.SourceTree?.FilePath,
                        targetPath,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                .Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
                .OrderByDescending(diagnostic => diagnostic.Severity)
                .ThenBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .Take(maxResults)
                .Select(diagnostic => new
                {
                    diagnostic.Id,
                    Severity = diagnostic.Severity.ToString(),
                    Message = diagnostic.GetMessage(),
                    Path = diagnostic.Location.SourceTree?.FilePath is null
                        ? null
                        : Path.GetRelativePath(scope.RootPath, diagnostic.Location.SourceTree.FilePath),
                    Line = diagnostic.Location.IsInSource
                        ? diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1
                        : (int?)null,
                    Column = diagnostic.Location.IsInSource
                        ? diagnostic.Location.GetLineSpan().StartLinePosition.Character + 1
                        : (int?)null
                })
                .ToArray();
            return diagnostics.Length == 0
                ? "No compiler diagnostics were reported."
                : JsonSerializer.Serialize(diagnostics, JsonOptions);
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
    }

    [Description("Disposes and reloads the Roslyn MSBuild workspace so semantic queries include filesystem edits made during this coding session.")]
    public async Task<string> ReloadProject()
    {
        await workspaceLock.WaitAsync();
        try
        {
            DisposeWorkspace();
            await LoadProjectAsync();
            return $"Reloaded Roslyn workspace for '{Path.GetFileName(projectFilePath)}'.";
        }
        catch (Exception exception)
        {
            return $"Error: {exception.Message}";
        }
        finally
        {
            workspaceLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeWorkspace();
        workspaceLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<Project> GetProjectAsync()
    {
        if (project is not null)
        {
            return project;
        }

        await workspaceLock.WaitAsync();
        try
        {
            return project ?? await LoadProjectAsync();
        }
        finally
        {
            workspaceLock.Release();
        }
    }

    private async Task<Project> LoadProjectAsync()
    {
        workspaceDiagnostics.Clear();
        workspace = RoslynWorkspaceFactory.Create();
        workspaceFailedRegistration = workspace.RegisterWorkspaceFailedHandler(OnWorkspaceFailed);
        project = await workspace.OpenProjectAsync(projectFilePath);
        return project;
    }

    private async Task<Document> GetDocumentAsync(string relativePath)
    {
        var path = scope.Resolve(relativePath);
        var loadedProject = await GetProjectAsync();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return loadedProject.Documents.FirstOrDefault(document =>
                   document.FilePath is not null &&
                   string.Equals(Path.GetFullPath(document.FilePath), path, comparison))
               ?? throw new FileNotFoundException(
                   "The file exists outside Roslyn's document list or is not part of this project.",
                   relativePath);
    }

    private object? CreateDeclarationSummary(SyntaxNode node)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return node switch
        {
            BaseTypeDeclarationSyntax type => new
            {
                Kind = type.Kind().ToString(),
                Name = type.Identifier.ValueText,
                Line = line
            },
            DelegateDeclarationSyntax declaration => new
            {
                Kind = "Delegate",
                Name = declaration.Identifier.ValueText,
                Line = line
            },
            MethodDeclarationSyntax method => new
            {
                Kind = "Method",
                Name = method.Identifier.ValueText,
                Signature = method.ToString().Split(['\r', '\n'], 2)[0],
                Line = line
            },
            ConstructorDeclarationSyntax constructor => new
            {
                Kind = "Constructor",
                Name = constructor.Identifier.ValueText,
                Signature = constructor.ToString().Split(['\r', '\n'], 2)[0],
                Line = line
            },
            PropertyDeclarationSyntax property => new
            {
                Kind = "Property",
                Name = property.Identifier.ValueText,
                Line = line
            },
            FieldDeclarationSyntax field => new
            {
                Kind = "Field",
                Name = string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
                Line = line
            },
            _ => null
        };
    }

    private void OnWorkspaceFailed(WorkspaceDiagnosticEventArgs args)
    {
        workspaceDiagnostics.Enqueue($"{args.Diagnostic.Kind}: {args.Diagnostic.Message}");
    }

    private void DisposeWorkspace()
    {
        project = null;
        workspaceFailedRegistration?.Dispose();
        workspaceFailedRegistration = null;
        if (workspace is not null)
        {
            workspace.Dispose();
            workspace = null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}
