namespace OpenCaddis.Services;

public class VectorStoreBootstrapService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly VectorStoreService _vectorStore;
    private readonly ILogger<VectorStoreBootstrapService> _logger;

    public VectorStoreBootstrapService(
        IHostApplicationLifetime lifetime,
        VectorStoreService vectorStore,
        ILogger<VectorStoreBootstrapService> logger)
    {
        _lifetime = lifetime;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource();
        using var registration = _lifetime.ApplicationStarted.Register(() => tcs.SetResult());
        await tcs.Task;

        try
        {
            var result = await _vectorStore.VerifyAsync();
            _logger.LogInformation("Vector store verification: {Result}", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector store verification failed");
        }
    }
}
