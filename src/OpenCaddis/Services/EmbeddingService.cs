using System.Text.Json;

namespace OpenCaddis.Services;

public class EmbeddingService
{
    private readonly FabrConfigService _configService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private string? _endpoint;
    private string? _model;
    private string? _apiKey;

    public EmbeddingService(
        FabrConfigService configService,
        IHttpClientFactory httpClientFactory,
        ILogger<EmbeddingService> logger)
    {
        _configService = configService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private async Task EnsureConfigLoadedAsync()
    {
        if (_endpoint is not null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_endpoint is not null) return;

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

            _endpoint = embeddingsModel.Uri.TrimEnd('/');
            _model = embeddingsModel.Model;
            _apiKey = apiKey.Value;

            _logger.LogInformation("Embedding service configured: model={Model}, endpoint={Endpoint}", _model, _endpoint);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        await EnsureConfigLoadedAsync();

        var url = $"{_endpoint}/openai/deployments/{_model}/embeddings?api-version=2024-06-01";

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", _apiKey);

        var requestBody = JsonSerializer.Serialize(new { input = text });
        using var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

        var embedding = new float[embeddingArray.GetArrayLength()];
        var i = 0;
        foreach (var element in embeddingArray.EnumerateArray())
        {
            embedding[i++] = element.GetSingle();
        }

        return embedding;
    }
}
