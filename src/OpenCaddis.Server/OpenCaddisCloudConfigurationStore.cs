using FabrCore.Core;
using FabrCore.Core.CloudServer;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenCaddis.Server;

public sealed class OpenCaddisCloudConfigurationStore
{
    public const string ServerClusterId = "opencaddis-server";
    public const string ServerBuilderClusterId = "opencaddis-server-builder";

    private const string ServerFileName = "server.fabrcore.json";
    private const string ServerBuilderFileName = "server-builder.fabrcore.json";
    private const string AuthenticationFileName = "cloud-server-auth.json";

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerOptions.Web)
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly AuthenticationState authentication;

    public OpenCaddisCloudConfigurationStore(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        StorageDirectory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(StorageDirectory);
        authentication = LoadOrCreateAuthentication();
    }

    public event EventHandler? ConfigurationChanged;

    public string StorageDirectory { get; }

    public bool IsConfigured(OpenCaddisCloudTarget target) =>
        TryGetPublishedConfiguration(target, out _, out _);

    public string GetConfigurationJson(OpenCaddisCloudTarget target)
    {
        var path = GetConfigurationPath(target);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : CreateConfigurationTemplate();
    }

    public string GetValidationMessage(OpenCaddisCloudTarget target)
    {
        if (TryGetPublishedConfiguration(target, out var envelope, out var error))
        {
            return $"Ready: {envelope!.Configuration.ModelConfigurations.Count} model(s) and " +
                $"{envelope.Configuration.ApiKeys.Count} API key(s).";
        }

        return error;
    }

    public async Task SaveConfigurationAsync(
        OpenCaddisCloudTarget target,
        string json,
        CancellationToken cancellationToken = default)
    {
        var configuration = DeserializeAndValidate(json);
        var normalizedJson = JsonSerializer.Serialize(configuration, WriteOptions);
        var path = GetConfigurationPath(target);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, normalizedJson, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            writeLock.Release();
        }

        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public OpenCaddisCloudServerConnection CreateConnection(
        OpenCaddisCloudTarget target,
        Uri cloudServerUri)
    {
        ArgumentNullException.ThrowIfNull(cloudServerUri);
        return new OpenCaddisCloudServerConnection(
            cloudServerUri,
            target == OpenCaddisCloudTarget.Server
                ? authentication.ServerApiKey
                : authentication.ServerBuilderApiKey,
            GetClusterId(target));
    }

    internal bool IsAuthorized(string clusterId, string? authorizationHeader)
    {
        var target = GetTarget(clusterId);
        if (target is null || string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suppliedKey = authorizationHeader["Bearer ".Length..].Trim();
        var expectedKey = target == OpenCaddisCloudTarget.Server
            ? authentication.ServerApiKey
            : authentication.ServerBuilderApiKey;
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        return suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    internal bool TryGetPublishedConfiguration(
        string clusterId,
        out CloudConfigurationEnvelope? envelope,
        out string error)
    {
        var target = GetTarget(clusterId);
        if (target is null)
        {
            envelope = null;
            error = "The requested OpenCaddis cluster is unknown.";
            return false;
        }

        return TryGetPublishedConfiguration(target.Value, out envelope, out error);
    }

    private bool TryGetPublishedConfiguration(
        OpenCaddisCloudTarget target,
        out CloudConfigurationEnvelope? envelope,
        out string error)
    {
        var path = GetConfigurationPath(target);
        if (!File.Exists(path))
        {
            envelope = null;
            error = "Configuration is required before this server can start.";
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var configuration = DeserializeAndValidate(json);
            var canonicalJson = JsonSerializer.Serialize(configuration, WriteOptions);
            envelope = new CloudConfigurationEnvelope
            {
                SchemaVersion = CloudServerProtocol.CurrentSchemaVersion,
                ConfigurationVersion = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant(),
                IssuedAt = File.GetLastWriteTimeUtc(path),
                Configuration = configuration
            };
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or IOException)
        {
            envelope = null;
            error = $"Configuration is not ready: {exception.Message}";
            return false;
        }
    }

    private AuthenticationState LoadOrCreateAuthentication()
    {
        var path = Path.Combine(StorageDirectory, AuthenticationFileName);
        if (File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<AuthenticationState>(
                    File.ReadAllText(path), ReadOptions);
                if (saved is not null &&
                    !string.IsNullOrWhiteSpace(saved.ServerApiKey) &&
                    !string.IsNullOrWhiteSpace(saved.ServerBuilderApiKey))
                {
                    return saved;
                }
            }
            catch (JsonException)
            {
            }
        }

        var authenticationState = new AuthenticationState
        {
            ServerApiKey = CreateApiKey(),
            ServerBuilderApiKey = CreateApiKey()
        };
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(authenticationState, WriteOptions));
        File.Move(temporaryPath, path, overwrite: true);
        return authenticationState;
    }

    private static FabrCoreConfiguration DeserializeAndValidate(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var configuration = JsonSerializer.Deserialize<FabrCoreConfiguration>(json, ReadOptions)
            ?? throw new InvalidOperationException("The fabrcore.json document is empty.");
        if (configuration.ModelConfigurations.Count == 0)
        {
            throw new InvalidOperationException("Add at least one model configuration.");
        }

        if (configuration.ApiKeys.Count == 0)
        {
            throw new InvalidOperationException("Add at least one API key.");
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var apiKey in configuration.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(apiKey.Alias) || string.IsNullOrWhiteSpace(apiKey.Value))
            {
                throw new InvalidOperationException("Every API key needs a non-empty alias and value.");
            }

            if (!aliases.Add(apiKey.Alias))
            {
                throw new InvalidOperationException($"API key alias '{apiKey.Alias}' is duplicated.");
            }
        }

        var modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in configuration.ModelConfigurations)
        {
            if (string.IsNullOrWhiteSpace(model.Name) ||
                string.IsNullOrWhiteSpace(model.Provider) ||
                string.IsNullOrWhiteSpace(model.Model) ||
                string.IsNullOrWhiteSpace(model.ApiKeyAlias))
            {
                throw new InvalidOperationException(
                    "Every model needs a name, provider, model identifier, and API key alias.");
            }

            if (!modelNames.Add(model.Name))
            {
                throw new InvalidOperationException($"Model name '{model.Name}' is duplicated.");
            }

            if (!aliases.Contains(model.ApiKeyAlias))
            {
                throw new InvalidOperationException(
                    $"Model '{model.Name}' refers to missing API key alias '{model.ApiKeyAlias}'.");
            }
        }

        return configuration;
    }

    private string GetConfigurationPath(OpenCaddisCloudTarget target) =>
        Path.Combine(
            StorageDirectory,
            target == OpenCaddisCloudTarget.Server ? ServerFileName : ServerBuilderFileName);

    private static string GetClusterId(OpenCaddisCloudTarget target) =>
        target == OpenCaddisCloudTarget.Server ? ServerClusterId : ServerBuilderClusterId;

    private static OpenCaddisCloudTarget? GetTarget(string? clusterId) => clusterId switch
    {
        ServerClusterId => OpenCaddisCloudTarget.Server,
        ServerBuilderClusterId => OpenCaddisCloudTarget.ServerBuilder,
        _ => null
    };

    private static string CreateApiKey() => $"occ_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

    private static string CreateConfigurationTemplate() =>
        """
        {
          "modelConfigurations": [
            {
              "name": "default",
              "provider": "OpenAI",
              "uri": "",
              "model": "",
              "apiKeyAlias": "openai"
            }
          ],
          "apiKeys": [
            {
              "alias": "openai",
              "value": ""
            }
          ]
        }
        """;

    private sealed class AuthenticationState
    {
        public string ServerApiKey { get; set; } = string.Empty;
        public string ServerBuilderApiKey { get; set; } = string.Empty;
    }
}
