namespace OpenCaddis.Server.Builder;

public sealed record BuilderOperationResult(
    BuilderWorkspaceInfo Workspace,
    string Output);
