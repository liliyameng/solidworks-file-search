using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

/// <summary>Short-lived, query-only reader for latency-sensitive property projection.</summary>
public sealed class SqliteMetadataSnapshotReader(string? databasePath = null)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath ?? SwSearchPaths.DatabasePath);

    public async ValueTask<DocumentMetadata?> TryReadAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath)) return null;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=50;";
            await queryOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var pathNorm = IndexNormalization.PathKey(fullPath);
        long fileId;
        FileCandidate file;
        DateTimeOffset extracted;
        string? warning;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, path, kind, size, modified_utc, extracted_utc, error_code
                  FROM files WHERE path_norm=@path AND state=0 AND kind IN (0,1);
                """;
            command.Parameters.AddWithValue("@path", pathNorm);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(5)) return null;
            fileId = reader.GetInt64(0);
            file = new FileCandidate(
                reader.GetString(1),
                (SolidWorksDocumentKind)reader.GetInt32(2),
                reader.GetInt64(3),
                FromUtcTicks(reader.GetInt64(4)));
            extracted = FromUtcTicks(reader.GetInt64(5));
            warning = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        var properties = new List<DocumentProperty>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name, raw_value, resolved_value, scope, configuration_name, source,
                       display_value, cached_value, is_linked
                  FROM properties WHERE file_id=@file ORDER BY scope, configuration_name, name;
                """;
            command.Parameters.AddWithValue("@file", fileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                properties.Add(new DocumentProperty(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    (PropertyScope)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    (MetadataSource)reader.GetInt32(5),
                    DisplayValue: reader.IsDBNull(6) ? null : reader.GetString(6),
                    CachedValue: reader.IsDBNull(7) ? null : reader.GetString(7),
                    IsLinked: reader.GetInt32(8) != 0));
            }
        }

        var configurations = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT configuration_name FROM configurations WHERE file_id=@file ORDER BY ordinal;";
            command.Parameters.AddWithValue("@file", fileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) configurations.Add(reader.GetString(0));
        }

        return new DocumentMetadata(
            file, properties, Array.Empty<DocumentReference>(), MetadataSource.PersistentCache,
            extracted, warning, configurations);
    }

    private static DateTimeOffset FromUtcTicks(long value) =>
        new(value, TimeSpan.Zero);
}
