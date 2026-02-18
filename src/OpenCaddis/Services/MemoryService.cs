using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenCaddis.Services;

public class MemoryService
{
    private readonly VectorStoreService _vectorStore;
    private readonly EmbeddingService _embeddingService;
    private readonly ILogger<MemoryService> _logger;

    private const int MaxSingleChunkLength = 2000;
    private const int TargetChunkLength = 500;

    public MemoryService(
        VectorStoreService vectorStore,
        EmbeddingService embeddingService,
        ILogger<MemoryService> logger)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<int> SaveAsync(string content, string source, string? title = null)
    {
        var chunks = ChunkContent(content);
        var baseId = GenerateId(source, content);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkId = chunks.Count == 1 ? baseId : $"{baseId}_{i}";
            var chunk = chunks[i];

            var embedding = await _embeddingService.GenerateEmbeddingAsync(chunk);

            var metadata = new Dictionary<string, object?>();
            if (title is not null) metadata["title"] = title;
            if (chunks.Count > 1)
            {
                metadata["chunk"] = i;
                metadata["totalChunks"] = chunks.Count;
            }

            var record = new EmbeddingRecord
            {
                Id = chunkId,
                Source = source,
                Content = chunk,
                ContentType = "memory",
                CreatedAt = DateTime.UtcNow,
                Metadata = metadata.Count > 0 ? JsonSerializer.Serialize(metadata) : null,
                Embedding = embedding
            };

            await _vectorStore.UpsertAsync(record);
        }

        _logger.LogInformation("Saved {ChunkCount} chunk(s) for source={Source}, title={Title}", chunks.Count, source, title);
        return chunks.Count;
    }

    public async Task<List<VectorSearchResult>> SearchAsync(string query, int maxResults, string? sourceFilter = null)
    {
        var embedding = await _embeddingService.GenerateEmbeddingAsync(query);

        if (sourceFilter is not null)
            return await _vectorStore.SearchAsync(embedding, maxResults, sourceFilter);

        return await _vectorStore.SearchAsync(embedding, maxResults);
    }

    public async Task<int> DeleteBySourceAsync(string source)
    {
        var count = await _vectorStore.DeleteBySourceAsync(source);
        _logger.LogInformation("Deleted {Count} memory record(s) for source={Source}", count, source);
        return count;
    }

    public async Task<List<(string Source, int Count)>> ListSourcesAsync()
    {
        return await _vectorStore.ListSourcesAsync();
    }

    private static List<string> ChunkContent(string content)
    {
        if (content.Length <= MaxSingleChunkLength)
            return [content];

        var paragraphs = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim();
            if (trimmed.Length == 0) continue;

            if (current.Length > 0 && current.Length + trimmed.Length + 2 > TargetChunkLength)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            if (current.Length > 0) current.Append("\n\n");
            current.Append(trimmed);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        return chunks.Count > 0 ? chunks : [content];
    }

    private static string GenerateId(string source, string content)
    {
        var input = $"{source}|{content}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
