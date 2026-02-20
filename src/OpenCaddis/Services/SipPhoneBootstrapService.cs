namespace OpenCaddis.Services;

public class SipPhoneBootstrapService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SipRegistrationService _sipService;
    private readonly ILogger<SipPhoneBootstrapService> _logger;

    public SipPhoneBootstrapService(
        IHostApplicationLifetime lifetime,
        SipRegistrationService sipService,
        ILogger<SipPhoneBootstrapService> logger)
    {
        _lifetime = lifetime;
        _sipService = sipService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource();
        using var registration = _lifetime.ApplicationStarted.Register(() => tcs.SetResult());
        await tcs.Task;

        try
        {
            await _sipService.TryRestoreRegistrationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SIP phone bootstrap failed");
        }
    }
}
