namespace OpenCaddis.Server.Builder;

public sealed class BuilderCommandException : InvalidOperationException
{
    public BuilderCommandException(
        string command,
        int exitCode,
        string output)
        : base(CreateMessage(command, exitCode, output))
    {
        Command = command;
        ExitCode = exitCode;
        Output = output;
    }

    public string Command { get; }

    public int ExitCode { get; }

    public string Output { get; }

    private static string CreateMessage(string command, int exitCode, string output)
    {
        var detail = string.IsNullOrWhiteSpace(output)
            ? "The command did not return any output."
            : output.Trim();
        return $"'{command}' failed with exit code {exitCode}.{Environment.NewLine}{detail}";
    }
}
