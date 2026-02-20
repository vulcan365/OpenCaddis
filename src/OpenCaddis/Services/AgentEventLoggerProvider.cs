using System.Text.Json;
using System.Threading.Channels;
using Fabr.Client;
using Fabr.Core;

namespace OpenCaddis.Services;

public sealed class AgentEventLoggerProvider : ILoggerProvider
{
    private static readonly string[] SuppressedPrefixes =
        ["Fabr.", "Orleans.", "OpenCaddis.Services.AgentEventLoggerProvider"];

    private static readonly AsyncLocal<bool> _isForwarding = new();

    private readonly Channel<LogEntry> _channel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly IServiceProvider _serviceProvider;

    private volatile bool _isReady;
    private string? _agentHandle;
    private IDirectMessageSender? _messageSender;

    public AgentEventLoggerProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _processingTask = Task.Run(() => ProcessLogEntriesAsync(_cts.Token));
    }

    public void SignalReady(string agentHandle)
    {
        _agentHandle = agentHandle;
        _messageSender = _serviceProvider.GetRequiredService<IDirectMessageSender>();
        _isReady = true;
    }

    public ILogger CreateLogger(string categoryName) => new AgentEventLogger(this, categoryName);

    internal bool ShouldForward(string category, LogLevel logLevel)
    {
        if (!_isReady)
            return false;

        if (logLevel < LogLevel.Information)
            return false;

        if (_isForwarding.Value)
            return false;

        for (var i = 0; i < SuppressedPrefixes.Length; i++)
        {
            if (category.StartsWith(SuppressedPrefixes[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    internal void EnqueueLog(LogEntry entry)
    {
        _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessLogEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (!_isReady || _messageSender is null || _agentHandle is null)
                    continue;

                try
                {
                    _isForwarding.Value = true;

                    var json = JsonSerializer.Serialize(new
                    {
                        level = entry.LogLevel.ToString(),
                        category = entry.Category,
                        message = entry.Message,
                        timestamp = entry.Timestamp.ToString("o"),
                        eventId = entry.EventId.Id,
                        exception = entry.Exception?.ToString()
                    });

                    await _messageSender.SendEventAsync(new AgentMessage
                    {
                        ToHandle = _agentHandle,
                        MessageType = "log",
                        Message = json
                    });
                }
                catch
                {
                    // Swallow send failures — never disrupt the logging pipeline
                }
                finally
                {
                    _isForwarding.Value = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    public void Dispose()
    {
        _isReady = false;
        _channel.Writer.TryComplete();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Swallow — shutting down
        }
        _cts.Dispose();
    }
}

internal sealed class AgentEventLogger : ILogger
{
    private readonly AgentEventLoggerProvider _provider;
    private readonly string _category;

    public AgentEventLogger(AgentEventLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _provider.ShouldForward(_category, logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!_provider.ShouldForward(_category, logLevel))
            return;

        _provider.EnqueueLog(new LogEntry
        {
            LogLevel = logLevel,
            Category = _category,
            Message = formatter(state, exception),
            EventId = eventId,
            Exception = exception,
            Timestamp = DateTimeOffset.UtcNow
        });
    }
}

internal readonly struct LogEntry
{
    public required LogLevel LogLevel { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public required EventId EventId { get; init; }
    public required Exception? Exception { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
