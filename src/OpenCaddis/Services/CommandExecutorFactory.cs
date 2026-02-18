using OpenCaddis.Agentic.CaddisFly;

namespace OpenCaddis.Services;

/// <summary>
/// Singleton factory that provides access to a CommandExecutor configured by the CaddisFly plugin.
/// Allows Chat.razor to create executors for direct approval-gate resumption without LLM round-trip.
/// </summary>
public sealed class CommandExecutorFactory
{
    private CommandExecutor? _executor;

    public void Configure(CommandExecutor executor) => _executor = executor;

    public CommandExecutor? GetExecutor() => _executor;
}
