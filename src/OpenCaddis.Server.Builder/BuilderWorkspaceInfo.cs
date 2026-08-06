namespace OpenCaddis.Server.Builder;

public sealed record BuilderWorkspaceInfo(
    string SolutionDirectory,
    IReadOnlyList<string> SolutionFiles)
{
    public bool HasSolution => SolutionFiles.Count == 1;

    public bool HasMultipleSolutions => SolutionFiles.Count > 1;

    public string? SolutionFilePath => HasSolution ? SolutionFiles[0] : null;
}
