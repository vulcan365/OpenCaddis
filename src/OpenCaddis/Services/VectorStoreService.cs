using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenCaddis.Services;

public class VectorStoreService : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<VectorStoreService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public VectorStoreService(IWebHostEnvironment env, ILogger<VectorStoreService> logger)
    {
        _logger = logger;
        var dataDir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "opencaddis-vectors.db");
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        connection.LoadVector();
        return connection;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            using var connection = CreateConnection();

            using var createEmbeddings = connection.CreateCommand();
            createEmbeddings.CommandText = """
                CREATE TABLE IF NOT EXISTS embeddings (
                    id TEXT PRIMARY KEY,
                    source TEXT NOT NULL,
                    content TEXT NOT NULL,
                    content_type TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    metadata TEXT
                )
                """;
            await createEmbeddings.ExecuteNonQueryAsync();

            using var createVec = connection.CreateCommand();
            createVec.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS vec_embeddings USING vec0(
                    id TEXT PRIMARY KEY,
                    embedding float[1536]
                )
                """;
            await createVec.ExecuteNonQueryAsync();

            _initialized = true;
            _logger.LogInformation("Vector store initialized at {DbPath}", _dbPath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertAsync(EmbeddingRecord record)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        using var upsertCmd = connection.CreateCommand();
        upsertCmd.CommandText = """
            INSERT OR REPLACE INTO embeddings (id, source, content, content_type, created_at, metadata)
            VALUES (@id, @source, @content, @content_type, @created_at, @metadata)
            """;
        upsertCmd.Parameters.AddWithValue("@id", record.Id);
        upsertCmd.Parameters.AddWithValue("@source", record.Source);
        upsertCmd.Parameters.AddWithValue("@content", record.Content);
        upsertCmd.Parameters.AddWithValue("@content_type", record.ContentType);
        upsertCmd.Parameters.AddWithValue("@created_at", record.CreatedAt.ToString("o"));
        upsertCmd.Parameters.AddWithValue("@metadata", (object?)record.Metadata ?? DBNull.Value);
        await upsertCmd.ExecuteNonQueryAsync();

        if (record.Embedding is not null)
        {
            // vec0 doesn't support ON CONFLICT — delete then insert
            using var deleteVec = connection.CreateCommand();
            deleteVec.CommandText = "DELETE FROM vec_embeddings WHERE id = @id";
            deleteVec.Parameters.AddWithValue("@id", record.Id);
            await deleteVec.ExecuteNonQueryAsync();

            using var insertVec = connection.CreateCommand();
            insertVec.CommandText = """
                INSERT INTO vec_embeddings (id, embedding)
                VALUES (@id, @embedding)
                """;
            insertVec.Parameters.AddWithValue("@id", record.Id);
            insertVec.Parameters.AddWithValue("@embedding", JsonSerializer.Serialize(record.Embedding));
            await insertVec.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    public async Task<EmbeddingRecord?> GetAsync(string id)
    {
        using var connection = CreateConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, source, content, content_type, created_at, metadata FROM embeddings WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new EmbeddingRecord
        {
            Id = reader.GetString(0),
            Source = reader.GetString(1),
            Content = reader.GetString(2),
            ContentType = reader.GetString(3),
            CreatedAt = DateTime.Parse(reader.GetString(4)),
            Metadata = reader.IsDBNull(5) ? null : reader.GetString(5)
        };
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        using var deleteVec = connection.CreateCommand();
        deleteVec.CommandText = "DELETE FROM vec_embeddings WHERE id = @id";
        deleteVec.Parameters.AddWithValue("@id", id);
        await deleteVec.ExecuteNonQueryAsync();

        using var deleteEmb = connection.CreateCommand();
        deleteEmb.CommandText = "DELETE FROM embeddings WHERE id = @id";
        deleteEmb.Parameters.AddWithValue("@id", id);
        await deleteEmb.ExecuteNonQueryAsync();

        transaction.Commit();
    }

    public async Task<List<VectorSearchResult>> SearchAsync(float[] queryVector, int topK = 5)
    {
        using var connection = CreateConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.id, v.distance, e.source, e.content, e.content_type, e.created_at, e.metadata
            FROM vec_embeddings v
            INNER JOIN embeddings e ON e.id = v.id
            WHERE v.embedding MATCH @query AND k = @k
            ORDER BY v.distance
            """;
        cmd.Parameters.AddWithValue("@query", JsonSerializer.Serialize(queryVector));
        cmd.Parameters.AddWithValue("@k", topK);

        var results = new List<VectorSearchResult>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new VectorSearchResult
            {
                Record = new EmbeddingRecord
                {
                    Id = reader.GetString(0),
                    Source = reader.GetString(2),
                    Content = reader.GetString(3),
                    ContentType = reader.GetString(4),
                    CreatedAt = DateTime.Parse(reader.GetString(5)),
                    Metadata = reader.IsDBNull(6) ? null : reader.GetString(6)
                },
                Distance = reader.GetDouble(1)
            });
        }

        return results;
    }

    public async Task<List<VectorSearchResult>> SearchAsync(float[] queryVector, int topK, string sourceFilter)
    {
        using var connection = CreateConnection();

        // Over-fetch from vec0 since k is applied before our source filter
        var overFetchK = topK * 4;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT sub.id, sub.distance, e.source, e.content, e.content_type, e.created_at, e.metadata
            FROM (
                SELECT id, distance
                FROM vec_embeddings
                WHERE embedding MATCH @query AND k = @k
            ) sub
            INNER JOIN embeddings e ON e.id = sub.id
            WHERE e.source = @source
            ORDER BY sub.distance
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", JsonSerializer.Serialize(queryVector));
        cmd.Parameters.AddWithValue("@k", overFetchK);
        cmd.Parameters.AddWithValue("@source", sourceFilter);
        cmd.Parameters.AddWithValue("@limit", topK);

        var results = new List<VectorSearchResult>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new VectorSearchResult
            {
                Record = new EmbeddingRecord
                {
                    Id = reader.GetString(0),
                    Source = reader.GetString(2),
                    Content = reader.GetString(3),
                    ContentType = reader.GetString(4),
                    CreatedAt = DateTime.Parse(reader.GetString(5)),
                    Metadata = reader.IsDBNull(6) ? null : reader.GetString(6)
                },
                Distance = reader.GetDouble(1)
            });
        }

        return results;
    }

    public async Task<int> DeleteBySourceAsync(string source)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        // First get all IDs for this source
        var ids = new List<string>();
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT id FROM embeddings WHERE source = @source";
            selectCmd.Parameters.AddWithValue("@source", source);
            using var reader = await selectCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ids.Add(reader.GetString(0));
        }

        if (ids.Count == 0)
        {
            transaction.Commit();
            return 0;
        }

        // Delete from vec_embeddings one at a time (vec0 limitation)
        foreach (var id in ids)
        {
            using var deleteVec = connection.CreateCommand();
            deleteVec.CommandText = "DELETE FROM vec_embeddings WHERE id = @id";
            deleteVec.Parameters.AddWithValue("@id", id);
            await deleteVec.ExecuteNonQueryAsync();
        }

        // Batch delete from embeddings
        using var deleteEmb = connection.CreateCommand();
        deleteEmb.CommandText = "DELETE FROM embeddings WHERE source = @source";
        deleteEmb.Parameters.AddWithValue("@source", source);
        await deleteEmb.ExecuteNonQueryAsync();

        transaction.Commit();
        return ids.Count;
    }

    public async Task<List<(string Source, int Count)>> ListSourcesAsync()
    {
        using var connection = CreateConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT source, COUNT(*) FROM embeddings GROUP BY source";

        var results = new List<(string Source, int Count)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return results;
    }

    public async Task<string> VerifyAsync()
    {
        await InitializeAsync();

        // Get sqlite-vec version
        using var connection = CreateConnection();
        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "SELECT vec_version()";
        var version = (string?)await versionCmd.ExecuteScalarAsync() ?? "unknown";

        // Round-trip test
        var testId = "__vec_store_verify__";
        var testEmbedding = new float[1536];
        testEmbedding[0] = 1.0f;

        var testRecord = new EmbeddingRecord
        {
            Id = testId,
            Source = "verification",
            Content = "test",
            ContentType = "text/plain",
            Embedding = testEmbedding
        };

        await UpsertAsync(testRecord);

        var retrieved = await GetAsync(testId);
        if (retrieved is null)
            return $"FAIL: Could not read back test record (sqlite-vec v{version})";

        var searchResults = await SearchAsync(testEmbedding, 1);
        if (searchResults.Count == 0)
            return $"FAIL: Vector search returned no results (sqlite-vec v{version})";

        await DeleteAsync(testId);

        return $"OK: SQLite + sqlite-vec v{version} operational";
    }

    public void Dispose()
    {
        _initLock.Dispose();
    }
}
