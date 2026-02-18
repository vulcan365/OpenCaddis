namespace OpenCaddis.Services;

public class EmbeddingRecord
{
    public required string Id { get; set; }
    public required string Source { get; set; }
    public required string Content { get; set; }
    public required string ContentType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Metadata { get; set; }
    public float[]? Embedding { get; set; }
}

public class VectorSearchResult
{
    public required EmbeddingRecord Record { get; set; }
    public double Distance { get; set; }
}
