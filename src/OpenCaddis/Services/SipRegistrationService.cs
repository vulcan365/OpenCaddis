using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace OpenCaddis.Services;

public sealed class SipRegistrationService : IDisposable
{
    private readonly IDataProtector _protector;
    private readonly OpenCaddisConfigService _configService;
    private readonly ILogger<SipRegistrationService> _logger;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _userAgent;

    public bool IsRegistered { get; private set; }
    public string? RegisteredServer { get; private set; }
    public string? RegisteredUser { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastRegistrationTime { get; private set; }

    public bool IsInCall { get; private set; }
    public string? ActiveCallFrom { get; private set; }
    public DateTimeOffset? CallStartTime { get; private set; }
    public string? CallId { get; private set; }

    public event Action? OnConnectionChanged;
    public event Action? OnCallStateChanged;

    public SipRegistrationService(
        IDataProtectionProvider dataProtection,
        OpenCaddisConfigService configService,
        ILogger<SipRegistrationService> logger)
    {
        _protector = dataProtection.CreateProtector("OpenCaddis.SipPhone");
        _configService = configService;
        _logger = logger;
    }

    public string EncryptPassword(string plainText) => _protector.Protect(plainText);

    public string DecryptPassword(string encrypted) => _protector.Unprotect(encrypted);

    public async Task StartRegistrationAsync(SipConfigurationDto config)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(config.Domain) || string.IsNullOrWhiteSpace(config.Username))
        {
            LastError = "SIP domain and username are required.";
            OnConnectionChanged?.Invoke();
            return;
        }

        string? password = null;
        if (!string.IsNullOrEmpty(config.EncryptedPassword))
        {
            try
            {
                password = DecryptPassword(config.EncryptedPassword);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt SIP password");
                LastError = "Failed to decrypt saved password. Please re-enter it.";
                OnConnectionChanged?.Invoke();
                return;
            }
        }

        try
        {
            var useTcp = string.Equals(config.Transport, "tcp", StringComparison.OrdinalIgnoreCase);
            _transport = new SIPTransport();
            if (useTcp)
            {
                var tcpChannel = new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0));
                tcpChannel.DisableLocalTCPSocketsCheck = true;
                _transport.AddSIPChannel(tcpChannel);
            }
            else
            {
                _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
            }
            _transport.PreferIPv6NameResolution = false;

            // Resolve the target host to an IP for the SIP endpoint
            var targetHost = config.OutboundProxy ?? config.Domain;
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(targetHost);
                _logger.LogInformation("SIP DNS resolved {Host} -> {Addresses}",
                    targetHost, string.Join(", ", addresses.Select(a => a.ToString())));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SIP DNS resolution failed for {Host}", targetHost);
                LastError = $"DNS resolution failed for {targetHost}";
                OnConnectionChanged?.Invoke();
                return;
            }

            // Build the server string that SIPSorcery will connect to
            var targetIP = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork);
            var serverStr = useTcp
                ? $"sip:{targetIP}:{config.Port};transport=tcp"
                : $"{targetIP}:{config.Port}";

            _regAgent = new SIPRegistrationUserAgent(
                _transport,
                config.Username,
                password ?? string.Empty,
                serverStr,
                120);

            // If domain differs from the connection target, fix up the SIP headers
            // so From/To use the proper SIP domain, not the IP address
            if (!string.IsNullOrWhiteSpace(config.OutboundProxy))
            {
                _regAgent.AdjustRegister = (req) =>
                {
                    var domainUri = SIPURI.ParseSIPURI($"sip:{config.Domain}");
                    var aorUri = SIPURI.ParseSIPURI($"sip:{config.Username}@{config.Domain}");
                    req.URI = domainUri;
                    req.Header.From.FromURI = aorUri;
                    req.Header.To.ToURI = aorUri;
                    return req;
                };
            }

            _regAgent.RegistrationSuccessful += (uri, resp) =>
            {
                IsRegistered = true;
                RegisteredServer = config.OutboundProxy ?? config.Domain;
                RegisteredUser = config.Username;
                LastError = null;
                LastRegistrationTime = DateTimeOffset.UtcNow;
                _logger.LogInformation("SIP registered as {User} on {Domain}", config.Username, config.Domain);
                OnConnectionChanged?.Invoke();
            };

            _regAgent.RegistrationFailed += (uri, resp, msg) =>
            {
                IsRegistered = false;
                LastError = $"Registration failed: {msg}";
                _logger.LogWarning("SIP registration failed: {Message}", msg);
                OnConnectionChanged?.Invoke();
            };

            _regAgent.RegistrationTemporaryFailure += (uri, resp, msg) =>
            {
                LastError = $"Registration temporary failure: {msg}";
                _logger.LogWarning("SIP registration temporary failure: {Message}", msg);
                OnConnectionChanged?.Invoke();
            };

            _regAgent.RegistrationRemoved += (uri, resp) =>
            {
                IsRegistered = false;
                RegisteredServer = null;
                RegisteredUser = null;
                _logger.LogInformation("SIP registration removed");
                OnConnectionChanged?.Invoke();
            };

            _regAgent.Start();
            _logger.LogInformation("SIP registration started for {User}@{Domain} (proxy: {Proxy}:{Port}, transport: {Transport})",
                config.Username, config.Domain, config.OutboundProxy ?? config.Domain, config.Port, config.Transport);

            _userAgent = new SIPUserAgent(_transport, null, true, null);

            _userAgent.OnIncomingCall += async (ua, req) =>
            {
                try
                {
                    _logger.LogInformation("Incoming call from {From}", req.Header.From.FriendlyDescription());

                    var uas = ua.AcceptCall(req);

                    var mediaSession = new VoIPMediaSession();
                    mediaSession.AcceptRtpFromAny = true;

                    var answered = await ua.Answer(uas, mediaSession);
                    if (answered)
                    {
                        IsInCall = true;
                        ActiveCallFrom = req.Header.From.FriendlyDescription();
                        CallStartTime = DateTimeOffset.UtcNow;
                        CallId = req.Header.CallId;
                        _logger.LogInformation("Call answered from {From}", ActiveCallFrom);
                        OnCallStateChanged?.Invoke();
                    }
                    else
                    {
                        _logger.LogWarning("Failed to answer call from {From}", req.Header.From.FriendlyDescription());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling incoming call");
                }
            };

            _userAgent.OnCallHungup += (dialogue) =>
            {
                _logger.LogInformation("Call ended: {CallId}", dialogue?.CallId);
                IsInCall = false;
                ActiveCallFrom = null;
                CallStartTime = null;
                CallId = null;
                OnCallStateChanged?.Invoke();
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SIP registration");
            LastError = $"Failed to start registration: {ex.Message}";
            IsRegistered = false;
            Stop();
            OnConnectionChanged?.Invoke();
        }
    }

    public void HangupCall()
    {
        if (_userAgent is not null && IsInCall)
        {
            _userAgent.Hangup();
            IsInCall = false;
            ActiveCallFrom = null;
            CallStartTime = null;
            CallId = null;
            _logger.LogInformation("Call hung up by user");
            OnCallStateChanged?.Invoke();
        }
    }

    public void Stop()
    {
        try
        {
            if (_userAgent is not null)
            {
                if (IsInCall) _userAgent.Hangup();
                _userAgent.Dispose();
                _userAgent = null;
            }

            if (_regAgent is not null)
            {
                _regAgent.Stop(sendZeroExpiryRegister: false);
                _regAgent = null;
            }

            if (_transport is not null)
            {
                _transport.Shutdown();
                _transport = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during SIP shutdown");
        }

        IsRegistered = false;
        RegisteredServer = null;
        RegisteredUser = null;
        LastError = null;
        LastRegistrationTime = null;
        IsInCall = false;
        ActiveCallFrom = null;
        CallStartTime = null;
        CallId = null;
        OnConnectionChanged?.Invoke();
    }

    public async Task TryRestoreRegistrationAsync()
    {
        if (!_configService.ConfigurationExists())
            return;

        try
        {
            var config = await _configService.LoadConfigurationAsync();
            if (config.SipPhone is not { Domain.Length: > 0 })
                return;

            _logger.LogInformation("Restoring SIP registration from saved config");
            await StartRegistrationAsync(config.SipPhone);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore SIP registration");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
