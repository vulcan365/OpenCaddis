namespace OpenCaddis.Server.Builder;

public sealed record BuilderProjectInfo(
    string Name,
    string ProjectFilePath,
    string ProjectDirectory,
    string Language,
    string AgentHandle);
