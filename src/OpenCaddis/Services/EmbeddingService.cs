using Azure.AI.OpenAI;
using OpenAI;
using System.ClientModel;

namespace OpenCaddis.Services;

public class EmbeddingService
{
    private readonly FabrConfigService _configService;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private string? _provider;
    private string? _endpoint;
    private string? _model;
    private string? _apiKey;

    public EmbeddingService(
        FabrConfigService configService,
        ILogger<EmbeddingService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    private async Task EnsureConfigLoadedAsync()
    {
        if (_provider is not null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_provider is not null) return;

            var config = await _configService.LoadConfigurationAsync();

            var embeddingsModel = config.ModelConfigurations
                .FirstOrDefault(m => m.Name.Equals("embeddings", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    "No 'embeddings' model configuration found in fabr.json. " +
                    "Add a ModelConfiguration with Name='embeddings'.");

            var apiKey = config.ApiKeys
                .FirstOrDefault(k => k.Alias.Equals(embeddingsModel.ApiKeyAlias, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"API key alias '{embeddingsModel.ApiKeyAlias}' not found in fabr.json.");

            _provider = embeddingsModel.Provider;
            _endpoint = embeddingsModel.Uri.TrimEnd('/');
            _model = embeddingsModel.Model;
            _apiKey = apiKey.Value;

            _logger.LogInformation("Embedding service configured: provider={Provider}, model={Model}, endpoint={Endpoint}",
                _provider, _model, _endpoint);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        await EnsureConfigLoadedAsync();

#pragma warning disable OPENAI001 // OpenAIClientOptions.Endpoint is experimental
        var embeddingClient = _provider!.ToLowerInvariant() switch
        {
            "azure" => new AzureOpenAIClient(new Uri(_endpoint!), new ApiKeyCredential(_apiKey!))
                .GetEmbeddingClient(_model!),

            "openai" => new OpenAIClient(new ApiKeyCredential(_apiKey!))
                .GetEmbeddingClient(_model!),

            "openrouter" or "gemini" => new OpenAIClient(
                    new ApiKeyCredential(_apiKey!),
                    new OpenAIClientOptions { Endpoint = new Uri(_endpoint!) })
                .GetEmbeddingClient(_model!),

            "grok" => throw new NotSupportedException(
                "Grok (xAI) does not support embeddings. Use a different provider for your embeddings model."),

            _ => throw new NotSupportedException(
                $"Provider '{_provider}' is not supported for embeddings. Supported providers are: Azure, OpenAI, OpenRouter, Gemini.")
        };
#pragma warning restore OPENAI001

        var result = await embeddingClient.GenerateEmbeddingAsync(text);
        return result.Value.ToFloats().ToArray();
    }
}
