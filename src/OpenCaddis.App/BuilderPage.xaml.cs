using Microsoft.Maui.Storage;
using OpenCaddis.App.Services;
using OpenCaddis.Server.Builder;

namespace OpenCaddis.App;

public partial class BuilderPage : ContentPage
{
    private const string SolutionPathPreferenceKey = "OpenCaddis.Builder.SolutionPath";
    private readonly ServerController serverController;
    private readonly BuilderWorkspaceService workspaceService;
    private readonly AddonBuilderAgentProvisioner agentProvisioner;
    private bool isBusy;
    private bool isLoadingProjects;
    private bool projectsRefreshPending;
    private string? loadedSolutionFilePath;
    private string? loadingSolutionFilePath;

    public BuilderPage(
        ServerController serverController,
        BuilderWorkspaceService workspaceService,
        AddonBuilderAgentProvisioner agentProvisioner)
    {
        InitializeComponent();
        this.serverController = serverController;
        this.workspaceService = workspaceService;
        this.agentProvisioner = agentProvisioner;

        var defaultSolutionPath = Path.Combine(GetDefaultWorkspaceRoot(), "OpenCaddis");
        SolutionPathEntry.Text = Preferences.Default.Get(
            SolutionPathPreferenceKey,
            defaultSolutionPath);

        serverController.StatusChanged += OnServerStatusChanged;
        RefreshPage();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshPage();
    }

    private bool IsBuilderRunning =>
        serverController.State == ServerState.Running &&
        serverController.CurrentMode == OpenCaddisServerMode.Builder;

    private async void OnCreateSolutionClicked(object? sender, EventArgs e)
    {
        if (!TryGetSolutionPath(out var solutionPath))
        {
            return;
        }

        await RunOperationAsync(
            "Creating the .NET solution...",
            () => workspaceService.CreateSolutionAsync(solutionPath),
            "Solution created.");
    }

    private void OnShowNewProjectClicked(object? sender, EventArgs e)
    {
        NewProjectPanel.IsVisible = true;
        ProjectNameEntry.Focus();
    }

    private void OnCancelProjectClicked(object? sender, EventArgs e)
    {
        NewProjectPanel.IsVisible = false;
        ProjectNameEntry.Text = string.Empty;
        ProjectNameValidationLabel.Text = string.Empty;
    }

    private async void OnCreateProjectClicked(object? sender, EventArgs e)
    {
        if (!TryGetSolutionPath(out var solutionPath))
        {
            return;
        }

        string projectName;
        try
        {
            projectName = BuilderWorkspaceService.NormalizeProjectName(
                ProjectNameEntry.Text ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            ProjectNameValidationLabel.Text = exception.Message;
            return;
        }

        var succeeded = await RunOperationAsync(
            $"Creating {projectName}...",
            () => workspaceService.CreateClassLibraryAsync(
                solutionPath,
                projectName),
            $"Project {projectName} created and added to the solution.");
        if (succeeded)
        {
            NewProjectPanel.IsVisible = false;
            ProjectNameEntry.Text = string.Empty;
            ProjectNameValidationLabel.Text = string.Empty;
        }
    }

    private void OnSolutionPathTextChanged(object? sender, TextChangedEventArgs e)
    {
        Preferences.Default.Set(SolutionPathPreferenceKey, e.NewTextValue ?? string.Empty);
        NewProjectPanel.IsVisible = false;
        RefreshWorkspaceState();
    }

    private void OnProjectNameTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.NewTextValue))
        {
            ProjectNameValidationLabel.Text = string.Empty;
            CreateProjectButton.IsEnabled = false;
            return;
        }

        try
        {
            BuilderWorkspaceService.NormalizeProjectName(e.NewTextValue);
            ProjectNameValidationLabel.Text = string.Empty;
            CreateProjectButton.IsEnabled = IsBuilderRunning && !isBusy;
        }
        catch (ArgumentException exception)
        {
            ProjectNameValidationLabel.Text = exception.Message;
            CreateProjectButton.IsEnabled = false;
        }
    }

    private async void OnRefreshProjectsClicked(object? sender, EventArgs e)
    {
        if (!TryGetWorkspace(out var workspace) || !workspace.HasSolution)
        {
            return;
        }

        loadedSolutionFilePath = null;
        await RefreshProjectsAsync(workspace);
    }

    private async void OnCreateAgentClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button ||
            button.CommandParameter is not BuilderProjectInfo project ||
            !TryGetWorkspace(out var workspace) ||
            !workspace.HasSolution ||
            !IsBuilderRunning)
        {
            return;
        }

        button.IsEnabled = false;
        button.Text = "Creating...";
        OperationPanel.IsVisible = true;
        OperationActivityIndicator.IsVisible = true;
        OperationActivityIndicator.IsRunning = true;
        OperationStatusLabel.Text = $"Creating coding agent for {project.Name}...";
        try
        {
            var health = await agentProvisioner.CreateAgentAsync(
                serverController.ServerUri,
                serverController.CurrentAdminApiKey,
                project,
                workspace.SolutionFilePath!,
                serverController.CurrentAddOnPath);
            button.Text = "Agent ready";
            OperationStatusLabel.Text =
                $"{health.Handle} is {health.State}. Open Surface to chat with it.";
            CommandOutputEditor.Text = health.Message;
            CommandOutputEditor.IsVisible = !string.IsNullOrWhiteSpace(health.Message);
        }
        catch (Exception exception)
        {
            button.Text = "Retry agent";
            button.IsEnabled = IsBuilderRunning;
            OperationStatusLabel.Text = $"Could not create the coding agent for {project.Name}.";
            CommandOutputEditor.Text = exception.Message;
            CommandOutputEditor.IsVisible = true;
            await DisplayAlertAsync("Agent creation failed", exception.Message, "OK");
        }
        finally
        {
            OperationActivityIndicator.IsRunning = false;
            OperationActivityIndicator.IsVisible = false;
        }
    }

    private void OnServerStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.Dispatch(RefreshPage);
    }

    private void RefreshPage()
    {
        var isBuilderRunning = IsBuilderRunning;
        BuilderContent.IsVisible = isBuilderRunning;
        UnavailableView.IsVisible = !isBuilderRunning;

        if (!isBuilderRunning)
        {
            UnavailableMessageLabel.Text = serverController.State switch
            {
                ServerState.Running when serverController.CurrentMode == OpenCaddisServerMode.Server =>
                    "OpenCaddis Server is running. Stop it and start Server Builder mode to manage projects.",
                ServerState.Starting or ServerState.Stopping => serverController.StatusMessage,
                ServerState.Failed => serverController.StatusMessage,
                _ => "Start OpenCaddis Server Builder to manage projects."
            };
            return;
        }

        RefreshWorkspaceState();
    }

    private void RefreshWorkspaceState()
    {
        if (!IsBuilderRunning || isBusy)
        {
            return;
        }

        SolutionPathEntry.IsEnabled = true;
        var path = SolutionPathEntry.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            SolutionStatusLabel.Text = "Enter the folder where the .NET solution will live.";
            CreateSolutionButton.IsVisible = false;
            NewProjectButton.IsEnabled = false;
            ClearProjects();
            return;
        }

        try
        {
            var workspace = workspaceService.Inspect(path);
            if (workspace.HasSolution)
            {
                SolutionStatusLabel.Text =
                    $"Solution ready: {Path.GetFileName(workspace.SolutionFilePath)}";
                CreateSolutionButton.IsVisible = false;
                NewProjectButton.IsEnabled = true;
                ProjectsPanel.IsVisible = true;
                if (!string.Equals(
                        loadedSolutionFilePath,
                        workspace.SolutionFilePath,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    _ = RefreshProjectsAsync(workspace);
                }
                return;
            }

            if (workspace.HasMultipleSolutions)
            {
                SolutionStatusLabel.Text =
                    "More than one solution was found. Keep exactly one .sln or .slnx file in this folder.";
                CreateSolutionButton.IsVisible = false;
                NewProjectButton.IsEnabled = false;
                ClearProjects();
                return;
            }

            SolutionStatusLabel.Text = Directory.Exists(workspace.SolutionDirectory)
                ? "No solution exists in this folder."
                : "This folder does not exist yet. It will be created with the solution.";
            CreateSolutionButton.IsVisible = true;
            CreateSolutionButton.IsEnabled = true;
            NewProjectButton.IsEnabled = false;
            ClearProjects();
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException)
        {
            SolutionStatusLabel.Text = exception.Message;
            CreateSolutionButton.IsVisible = false;
            NewProjectButton.IsEnabled = false;
            ClearProjects();
        }
    }

    private async Task RefreshProjectsAsync(BuilderWorkspaceInfo workspace)
    {
        if (!workspace.HasSolution || !IsBuilderRunning)
        {
            return;
        }

        if (isLoadingProjects)
        {
            projectsRefreshPending |= !PathsEqual(
                loadingSolutionFilePath,
                workspace.SolutionFilePath);
            return;
        }

        isLoadingProjects = true;
        loadingSolutionFilePath = workspace.SolutionFilePath;
        ProjectsPanel.IsVisible = true;
        ProjectsActivityIndicator.IsVisible = true;
        ProjectsActivityIndicator.IsRunning = true;
        RefreshProjectsButton.IsEnabled = false;
        ProjectsStatusLabel.Text = "Loading projects through Roslyn...";
        try
        {
            var projects = await workspaceService.GetProjectsAsync(workspace.SolutionDirectory);
            ProjectsCollectionView.ItemsSource = projects;
            loadedSolutionFilePath = workspace.SolutionFilePath;
            ProjectsStatusLabel.Text = projects.Count == 1
                ? "1 project found."
                : $"{projects.Count} projects found.";
        }
        catch (Exception exception)
        {
            ProjectsCollectionView.ItemsSource = null;
            loadedSolutionFilePath = null;
            ProjectsStatusLabel.Text = $"Unable to load projects: {exception.Message}";
        }
        finally
        {
            isLoadingProjects = false;
            loadingSolutionFilePath = null;
            ProjectsActivityIndicator.IsRunning = false;
            ProjectsActivityIndicator.IsVisible = false;
            RefreshProjectsButton.IsEnabled = IsBuilderRunning && !isBusy;
            if (projectsRefreshPending)
            {
                projectsRefreshPending = false;
                loadedSolutionFilePath = null;
                if (TryGetWorkspace(out var currentWorkspace) && currentWorkspace.HasSolution)
                {
                    _ = RefreshProjectsAsync(currentWorkspace);
                }
            }
        }
    }

    private async Task<bool> RunOperationAsync(
        string progressMessage,
        Func<Task<BuilderOperationResult>> operation,
        string successMessage)
    {
        if (!IsBuilderRunning || isBusy)
        {
            return false;
        }

        SetBusy(true, progressMessage);
        try
        {
            var result = await operation();
            loadedSolutionFilePath = null;
            OperationStatusLabel.Text = successMessage;
            CommandOutputEditor.Text = result.Output;
            CommandOutputEditor.IsVisible = !string.IsNullOrWhiteSpace(result.Output);
            return true;
        }
        catch (Exception exception)
        {
            OperationStatusLabel.Text = "The operation failed.";
            CommandOutputEditor.Text = exception is BuilderCommandException commandException
                ? commandException.Output
                : exception.Message;
            CommandOutputEditor.IsVisible = true;
            await DisplayAlertAsync("Builder operation failed", exception.Message, "OK");
            return false;
        }
        finally
        {
            SetBusy(false, OperationStatusLabel.Text);
            RefreshWorkspaceState();
        }
    }

    private void SetBusy(bool busy, string statusMessage)
    {
        isBusy = busy;
        OperationPanel.IsVisible = true;
        OperationStatusLabel.Text = statusMessage;
        OperationActivityIndicator.IsRunning = busy;
        OperationActivityIndicator.IsVisible = busy;
        SolutionPathEntry.IsEnabled = !busy;
        CreateSolutionButton.IsEnabled = !busy;
        NewProjectButton.IsEnabled = !busy;
        ProjectNameEntry.IsEnabled = !busy;
        CreateProjectButton.IsEnabled = !busy;
        CancelProjectButton.IsEnabled = !busy;
        RefreshProjectsButton.IsEnabled = !busy && !isLoadingProjects;
    }

    private bool TryGetSolutionPath(out string solutionPath)
    {
        try
        {
            solutionPath = BuilderWorkspaceService.NormalizeSolutionDirectory(
                SolutionPathEntry.Text ?? string.Empty);
            Preferences.Default.Set(SolutionPathPreferenceKey, solutionPath);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException)
        {
            solutionPath = string.Empty;
            _ = DisplayAlertAsync("Invalid solution folder", exception.Message, "OK");
            return false;
        }
    }

    private bool TryGetWorkspace(out BuilderWorkspaceInfo workspace)
    {
        workspace = default!;
        if (!TryGetSolutionPath(out var solutionPath))
        {
            return false;
        }

        try
        {
            workspace = workspaceService.Inspect(solutionPath);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException)
        {
            _ = DisplayAlertAsync("Unable to inspect solution", exception.Message, "OK");
            return false;
        }
    }

    private void ClearProjects()
    {
        ProjectsPanel.IsVisible = false;
        ProjectsCollectionView.ItemsSource = null;
        loadedSolutionFilePath = null;
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string GetDefaultWorkspaceRoot()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documentsPath)
            ? FileSystem.Current.AppDataDirectory
            : documentsPath;
    }
}
