using Microsoft.Maui.Storage;
using OpenCaddis.App.Services;
using OpenCaddis.Server.Builder;

namespace OpenCaddis.App;

public partial class BuilderPage : ContentPage
{
    private const string SolutionPathPreferenceKey = "OpenCaddis.Builder.SolutionPath";
    private readonly ServerController serverController;
    private readonly BuilderWorkspaceService workspaceService;
    private bool isBusy;

    public BuilderPage(
        ServerController serverController,
        BuilderWorkspaceService workspaceService)
    {
        InitializeComponent();
        this.serverController = serverController;
        this.workspaceService = workspaceService;

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
                return;
            }

            if (workspace.HasMultipleSolutions)
            {
                SolutionStatusLabel.Text =
                    "More than one solution was found. Keep exactly one .sln or .slnx file in this folder.";
                CreateSolutionButton.IsVisible = false;
                NewProjectButton.IsEnabled = false;
                return;
            }

            SolutionStatusLabel.Text = Directory.Exists(workspace.SolutionDirectory)
                ? "No solution exists in this folder."
                : "This folder does not exist yet. It will be created with the solution.";
            CreateSolutionButton.IsVisible = true;
            CreateSolutionButton.IsEnabled = true;
            NewProjectButton.IsEnabled = false;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException)
        {
            SolutionStatusLabel.Text = exception.Message;
            CreateSolutionButton.IsVisible = false;
            NewProjectButton.IsEnabled = false;
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

    private static string GetDefaultWorkspaceRoot()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documentsPath)
            ? FileSystem.Current.AppDataDirectory
            : documentsPath;
    }
}
