using Microsoft.Data.Sqlite;
using SwSearch.Contracts;

namespace SwSearch.Indexer;

public sealed partial class SqliteIndexStore
{
    // The dirty set is a freshness overlay, not another search corpus.  Keep
    // it deliberately bounded so a projector lag never turns an interactive
    // request into a scan of the core database.
    private const int SearchDirtyDeltaMaximumFiles = 192;
    private const int SearchFtsCandidateFloor = 8_000;
    // This is not a result cap.  It only bounds the temporary leading window
    // used while a ready sidecar has a small, unprojected dirty overlay.  Deep
    // pages retain their exact result set through the core fallback until the
    // projector catches up.
    private const int SearchDirtyMergeMaximumWindow = 20_000;

    private const string CoreSearchCtes = """
        WITH direct_documents AS (
            SELECT p.file_id,1 AS source_kind,p.configuration_name,
                   '' AS file_name,'' AS path_norm,
                   group_concat(p.name_norm,char(31)) AS names_text,
                   group_concat(p.value_norm,char(31)) AS values_text,
                   NULL AS origin_path
              FROM properties p
              JOIN files owner ON owner.id=p.file_id AND owner.state=0
             WHERE p.scope IN (@documentScope,@configurationScope)
             GROUP BY p.file_id,coalesce(p.configuration_name,'')
        ), documents AS (
            SELECT f.id AS file_id,0 AS source_kind,NULL AS configuration_name,
                   f.file_name_norm AS file_name,f.path_norm,
                   '' AS names_text,'' AS values_text,NULL AS origin_path
              FROM files f WHERE f.state=0 AND f.kind IN (0,1)
            UNION ALL
            SELECT file_id,source_kind,configuration_name,file_name,path_norm,
                   names_text,values_text,origin_path
               FROM direct_documents
        ), property_values AS (
            SELECT p.id AS property_id,p.file_id,1 AS source_kind,p.configuration_name,
                   NULL AS origin_path,p.name AS property_name,p.name_norm,
                   p.raw_value AS raw_value,
                   p.resolved_value AS resolved_value,
                   p.display_value AS display_value,
                   p.cached_value AS cached_value,
                   p.is_linked AS is_linked,
                   p.value_norm
              FROM properties p
              JOIN files owner ON owner.id=p.file_id AND owner.state=0
             WHERE p.scope IN (@documentScope,@configurationScope)
        )
        """;

    public async ValueTask<long> CountSearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var term = IndexNormalization.Text(query.Text);
        if (term.Length == 0)
        {
            return await CountSearchCoreAsync(query, term, cancellationToken).ConfigureAwait(false);
        }

        await using var lease = await TryOpenReadySearchConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        // A count has no paging budget to bound an outbox merge.  Keep it
        // authoritative until the sidecar watermark is current; results
        // themselves still use the fast stale-while-revalidate path below.
        if (lease is { IsCurrent: true })
        {
            if (AfterSearchReadinessAcceptedForTestingAsync is { } hook)
            {
                await hook(cancellationToken).ConfigureAwait(false);
            }
            try
            {
                // FTS5 trigram indexing cannot match one or two character
                // terms, so those short terms use a direct LIKE over the
                // sidecar document/value tables instead of search_fts.
                return await CountSearchSidecarAsync(
                        lease,
                        query,
                        term,
                        requireFts: term.Length >= 3,
                        cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception)
            {
                RecordSearchSidecarQueryFallback(exception);
            }
        }

        return await CountSearchCoreAsync(query, term, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Offset/Limit 超出范围。");
        }

        var term = IndexNormalization.Text(query.Text);
        if (term.Length == 0)
        {
            return await SearchCoreAsync(query, term, cancellationToken).ConfigureAwait(false);
        }

        await using var lease = await TryOpenReadySearchConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lease is not null)
        {
            if (AfterSearchReadinessAcceptedForTestingAsync is { } hook)
            {
                await hook(cancellationToken).ConfigureAwait(false);
            }
            try
            {
                var dirtyFileIds = lease.IsCurrent
                    ? Array.Empty<long>()
                    : await ReadSearchProjectionDirtyFileIdsAsync(
                            SearchDirtyDeltaMaximumFiles,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (dirtyFileIds.Count == 0)
                {
                    return await SearchSidecarAsync(
                        lease,
                        query,
                        term,
                        Array.Empty<long>(),
                        requireFts: term.Length >= 3,
                        cancellationToken).ConfigureAwait(false);
                }

                // The fast freshness merge deliberately materializes only
                // a bounded leading window.  Do not silently return a
                // truncated later page while projection is catching up.
                // Normal interactive searches are page zero; a deep page
                // keeps the authoritative core semantics until the
                // sidecar reaches its watermark.
                if ((long)query.Offset + query.Limit > SearchDirtyMergeMaximumWindow)
                {
                    return await SearchCoreAsync(query, term, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Fetch the leading window from both sources, then merge
                // in memory. The sidecar excludes the dirty ids and the
                // core evaluates only that small coalesced delta.
                var windowQuery = CreateSearchMergeWindow(query);
                var sidecarHits = await SearchSidecarAsync(
                    lease,
                    windowQuery,
                    term,
                    dirtyFileIds,
                    requireFts: term.Length >= 3,
                    cancellationToken).ConfigureAwait(false);
                var dirtyHits = await SearchDirtyCoreAsync(
                        windowQuery,
                        term,
                        dirtyFileIds,
                        cancellationToken)
                    .ConfigureAwait(false);
                return MergeSearchHits(query, sidecarHits, dirtyHits);
            }
            catch (SqliteException exception)
            {
                RecordSearchSidecarQueryFallback(exception);
            }
        }

        return await SearchCoreAsync(query, term, cancellationToken).ConfigureAwait(false);
    }

    private void RecordSearchSidecarQueryFallback(SqliteException exception)
    {
        // No partial sidecar result escapes: callers restart the complete
        // operation as one authoritative core statement. Retain the SQLite
        // failure class in status so a lock/corruption regression is not
        // silently disguised as an ordinary lag fallback.
        Volatile.Write(
            ref _searchProjectionHealth,
            $"query-fallback:{exception.SqliteErrorCode}:{exception.SqliteExtendedErrorCode}");
        StartSearchProjection();
    }

    private async ValueTask<long> CountSearchCoreAsync(
        SearchQuery query,
        string term,
        CancellationToken cancellationToken)
    {
        var context = BuildSearchContext(query);
        if (term.Length == 0)
        {
            await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $$"""
                SELECT count(*) FROM files f
                 WHERE f.state=0 AND f.kind IN ({{context.KindFilter}})
                   AND instr(upper(replace(f.path,'/','\')),@excluded_system_segment)=0
                   AND (@scope_kind=0 OR f.path_norm=@scope_root
                        OR f.path_norm LIKE @scope_prefix ESCAPE '\')
                    AND ({{BuildLibraryFilter(query.Mode, context.LibraryRoots, "f.path_norm")}});
                """;
            AddSearchParameters(command, query, term, context, includePaging: false);
            return await ExecuteCountAsync(connection, command, cancellationToken).ConfigureAwait(false);
        }

        await using var coreConnection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var coreCommand = coreConnection.CreateCommand();
        coreCommand.CommandText = CoreSearchCtes + $$"""
            , eligible_documents AS (
                SELECT d.*
                  FROM documents d
                 WHERE ((@mode=0 AND d.source_kind=0
                          AND d.file_name LIKE @contains ESCAPE '\')
                     OR (@mode IN (2,3) AND d.source_kind=0
                          AND (d.file_name LIKE @contains ESCAPE '\'
                               OR d.path_norm LIKE @contains ESCAPE '\'))
                     OR (@mode IN (1,2,3) AND d.source_kind=1
                          AND (d.names_text LIKE @contains ESCAPE '\'
                               OR d.values_text LIKE @contains ESCAPE '\')))
            )
            SELECT count(DISTINCT f.id)
              FROM eligible_documents d
              JOIN files f ON f.id=d.file_id AND f.state=0
             WHERE f.kind IN ({{context.KindFilter}})
               AND instr(upper(replace(f.path,'/','\')),@excluded_system_segment)=0
               AND (@scope_kind=0 OR f.path_norm=@scope_root
                    OR f.path_norm LIKE @scope_prefix ESCAPE '\')
               AND ({{BuildLibraryFilter(query.Mode, context.LibraryRoots, "f.path_norm")}})
               AND (d.source_kind=0 OR EXISTS(
                   SELECT 1 FROM property_values v
                    WHERE v.file_id=d.file_id AND v.source_kind=d.source_kind
                      AND coalesce(v.configuration_name,'')=coalesce(d.configuration_name,'')
                      AND coalesce(v.origin_path,'')=coalesce(d.origin_path,'')
                   AND (v.name_norm LIKE @contains ESCAPE '\'
                           OR v.value_norm LIKE @contains ESCAPE '\')));
            """;
        AddSearchParameters(coreCommand, query, term, context, includePaging: false);
        return await ExecuteCountAsync(coreConnection, coreCommand, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> CountSearchSidecarAsync(
        SearchReadLease lease,
        SearchQuery query,
        string term,
        bool requireFts,
        CancellationToken cancellationToken)
    {
        var context = BuildSearchContext(query);
        await using var command = lease.Connection.CreateCommand();
        command.Transaction = lease.Transaction;
        command.CommandText = $$"""
            WITH eligible_documents AS (
                SELECT d.*
                  FROM search_documents d
                 {{(requireFts ? "WHERE d.document_id IN (SELECT document_id FROM search_fts WHERE search_fts MATCH @match)" : string.Empty)}}
                 {{(requireFts ? "AND" : "WHERE")}} ((@mode=0 AND d.source_kind=0
                          AND d.file_name LIKE @contains ESCAPE '\')
                     OR (@mode IN (2,3) AND d.source_kind=0
                          AND (d.file_name LIKE @contains ESCAPE '\'
                               OR d.path_norm LIKE @contains ESCAPE '\'))
                     OR (@mode IN (1,2,3) AND d.source_kind=1
                          AND (d.names_text LIKE @contains ESCAPE '\'
                               OR d.values_text LIKE @contains ESCAPE '\')))
            )
            SELECT count(DISTINCT f.file_id)
              FROM eligible_documents d
              JOIN search_files f ON f.file_id=d.file_id
             WHERE f.kind IN ({{context.KindFilter}})
               AND instr(upper(replace(f.path,'/','\')),@excluded_system_segment)=0
               AND (@scope_kind=0 OR f.path_norm=@scope_root
                    OR f.path_norm LIKE @scope_prefix ESCAPE '\')
               AND ({{BuildLibraryFilter(query.Mode, context.LibraryRoots, "f.path_norm")}})
               AND (d.source_kind=0 OR EXISTS(
                   SELECT 1 FROM search_property_values v
                    WHERE v.file_id=d.file_id AND v.source_kind=d.source_kind
                      AND coalesce(v.configuration_name,'')=coalesce(d.configuration_name,'')
                      AND coalesce(v.origin_path,'')=coalesce(d.origin_path,'')
                      AND (v.name_norm LIKE @contains ESCAPE '\'
                           OR v.value_norm LIKE @contains ESCAPE '\')));
            """;
        AddSearchParameters(command, query, term, context, includePaging: false);
        return await ExecuteCountAsync(lease.Connection, command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<SearchHit>> SearchCoreAsync(
        SearchQuery query,
        string term,
        CancellationToken cancellationToken)
    {
        var context = BuildSearchContext(query);
        if (term.Length == 0)
        {
            return await SearchAllCoreAsync(query, context, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await OpenSearchReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildRankedSearchSql(
            query,
            context,
            documentsTable: "documents",
            valuesTable: "property_values",
            filesTable: "files",
            fileIdColumn: "id",
            requireFts: false,
            applySafeValue: true,
            prefix: CoreSearchCtes);
        AddSearchParameters(command, query, term, context, includePaging: true);
        return await ReadSearchHitsAsync(connection, command, status, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<SearchHit>> SearchSidecarAsync(
        SearchReadLease lease,
        SearchQuery query,
        string term,
        IReadOnlyList<long> excludedFileIds,
        bool requireFts,
        CancellationToken cancellationToken)
    {
        var context = BuildSearchContext(query);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await using var command = lease.Connection.CreateCommand();
        command.Transaction = lease.Transaction;
        command.CommandText = BuildRankedSearchSql(
            query,
            context,
            documentsTable: "search_documents",
            valuesTable: "search_property_values",
            filesTable: "search_files",
            fileIdColumn: "file_id",
            requireFts: requireFts,
            applySafeValue: false,
            prefix: null,
            excludedFileIds: excludedFileIds);
        AddSearchParameters(command, query, term, context, includePaging: true);
        AddExcludedFileParameters(command, excludedFileIds);
        return await ReadSearchHitsAsync(
            lease.Connection,
            command,
            status,
            cancellationToken).ConfigureAwait(false);
    }

    private static SearchQuery CreateSearchMergeWindow(SearchQuery query)
    {
        // The public API deliberately caps a page at 500.  Fetch only the
        // leading result window needed to merge a bounded freshness overlay;
        // normal UI requests are page zero and therefore never materialize the
        // entire result set.
        var required = Math.Min(
            SearchDirtyMergeMaximumWindow,
            Math.Max((long)query.Limit, (long)query.Offset + query.Limit));
        return query with { Offset = 0, Limit = (int)required };
    }

    private async ValueTask<IReadOnlyList<SearchHit>> SearchDirtyCoreAsync(
        SearchQuery query,
        string term,
        IReadOnlyList<long> dirtyFileIds,
        CancellationToken cancellationToken)
    {
        if (dirtyFileIds.Count == 0)
        {
            return Array.Empty<SearchHit>();
        }

        var context = BuildSearchContext(query);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var hits = new List<SearchHit>(dirtyFileIds.Count);
        foreach (var fileId in dirtyFileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await ReadSearchFileSnapshotAsync(
                    connection,
                    fileId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null || !IsDirtySearchFileEligible(snapshot, query, context))
            {
                continue;
            }

            var hit = CreateDirtySearchHit(snapshot, query.Mode, term, status.CacheMayBeStale);
            if (hit is not null)
            {
                hits.Add(hit);
            }
        }

        return hits;
    }

    private static bool IsDirtySearchFileEligible(
        SearchFileSnapshot snapshot,
        SearchQuery query,
        SearchContext context)
    {
        if (snapshot.PathNorm.Contains(
                IndexNormalization.ExcludedRecycleBinSegment,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (query.Kinds is { Count: > 0 } &&
            !query.Kinds.Contains((SolidWorksDocumentKind)snapshot.Kind))
        {
            return false;
        }

        if (context.Scope.Kind == SearchScopeKind.Folder &&
            !SearchPath.Contains(context.Scope.RootPath!, snapshot.Path))
        {
            return false;
        }

        return query.Mode != SearchMode.Library ||
               context.LibraryRoots.Any(root => SearchPath.Contains(root, snapshot.Path));
    }

    private static SearchHit? CreateDirtySearchHit(
        SearchFileSnapshot snapshot,
        SearchMode mode,
        string term,
        bool cacheMayBeStale)
    {
        SearchProjectedValue? bestValue = null;
        SearchProjectedDocument? bestDocument = null;
        var bestRank = double.MinValue;

        foreach (var document in snapshot.Documents)
        {
            if (document.SourceKind == 0)
            {
                if (mode == SearchMode.Property ||
                    (!document.FileName.Contains(term, StringComparison.Ordinal) &&
                     !snapshot.PathNorm.Contains(term, StringComparison.Ordinal)))
                {
                    continue;
                }

                // FileName mode intentionally does not search the parent path;
                // All/Library preserve the Everything-style file-or-path match.
                if (mode == SearchMode.FileName &&
                    !document.FileName.Contains(term, StringComparison.Ordinal))
                {
                    continue;
                }

                var rank = document.FileName.Equals(term, StringComparison.Ordinal)
                    ? 900.0
                    : document.FileName.StartsWith(term, StringComparison.Ordinal)
                        ? 800.0
                        : 500.0;
                if (rank > bestRank)
                {
                    bestDocument = document;
                    bestValue = null;
                    bestRank = rank;
                }
                continue;
            }

            if (mode == SearchMode.FileName)
            {
                continue;
            }

            foreach (var value in snapshot.Values)
            {
                if (value.SourceKind != document.SourceKind ||
                    !string.Equals(value.ConfigurationName, document.ConfigurationName, StringComparison.Ordinal) ||
                    !string.Equals(value.OriginPath, document.OriginPath, StringComparison.Ordinal) ||
                    (!value.NameNorm.Contains(term, StringComparison.Ordinal) &&
                     !value.ValueNorm.Contains(term, StringComparison.Ordinal)))
                {
                    continue;
                }

                var rank = GetPropertyMatchRank(document.SourceKind, value, term);
                if (rank <= bestRank)
                {
                    continue;
                }

                bestDocument = document;
                bestValue = value;
                bestRank = rank;
            }
        }

        if (bestDocument is null)
        {
            return null;
        }

        var displayValue = bestValue?.DisplayValue;
        if (SolidWorksPropertyValue.IsUnresolvedExpression(displayValue))
        {
            displayValue = null;
        }

        return new SearchHit(
            snapshot.Path,
            (SolidWorksDocumentKind)snapshot.Kind,
            Path.GetFileName(snapshot.Path),
            bestValue?.PropertyName,
            displayValue,
            bestDocument.OriginPath,
            bestRank,
            cacheMayBeStale,
            bestDocument.ConfigurationName);
    }

    private static double GetPropertyMatchRank(
        int sourceKind,
        SearchProjectedValue value,
        string term)
    {
        var valueMatchesExactly = value.ValueNorm.Equals(term, StringComparison.Ordinal);
        var nameMatchesExactly = value.NameNorm.Equals(term, StringComparison.Ordinal);
        return sourceKind switch
        {
            2 when valueMatchesExactly => 725.0,
            1 when valueMatchesExactly => 700.0,
            2 when nameMatchesExactly => 675.0,
            1 when nameMatchesExactly => 650.0,
            2 => 425.0,
            1 => 400.0,
            _ => 200.0,
        };
    }

    private static IReadOnlyList<SearchHit> MergeSearchHits(
        SearchQuery query,
        IReadOnlyList<SearchHit> sidecarHits,
        IReadOnlyList<SearchHit> dirtyHits)
    {
        var merged = new Dictionary<string, SearchHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in sidecarHits.Concat(dirtyHits))
        {
            if (!merged.TryGetValue(hit.FullPath, out var existing) ||
                hit.Rank > existing.Rank ||
                (hit.Rank == existing.Rank && !hit.CacheMayBeStale && existing.CacheMayBeStale))
            {
                merged[hit.FullPath] = hit;
            }
        }

        return OrderMergedSearchHits(query, merged.Values)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
    }

    private static IOrderedEnumerable<SearchHit> OrderMergedSearchHits(
        SearchQuery query,
        IEnumerable<SearchHit> hits)
    {
        if (query.SortColumn is null)
        {
            return hits
                .OrderByDescending(static hit => hit.Rank)
                .ThenBy(static hit => hit.FullPath, StringComparer.OrdinalIgnoreCase);
        }

        return (query.SortColumn switch
        {
            "fileName" => query.SortDescending
                ? hits.OrderByDescending(static hit => hit.FileName, StringComparer.OrdinalIgnoreCase)
                : hits.OrderBy(static hit => hit.FileName, StringComparer.OrdinalIgnoreCase),
            "kind" => query.SortDescending
                ? hits.OrderByDescending(static hit => hit.Kind)
                : hits.OrderBy(static hit => hit.Kind),
            "matchedProperty" => query.SortDescending
                ? hits.OrderByDescending(static hit => hit.MatchedProperty ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : hits.OrderBy(static hit => hit.MatchedProperty ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "matchedValue" => query.SortDescending
                ? hits.OrderByDescending(static hit => hit.MatchedValue ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : hits.OrderBy(static hit => hit.MatchedValue ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "source" => query.SortDescending
                ? hits.OrderByDescending(static hit => hit.OriginModelPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : hits.OrderBy(static hit => hit.OriginModelPath ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            _ => query.SortDescending
                ? hits.OrderByDescending(static hit => hit.FullPath, StringComparer.OrdinalIgnoreCase)
                : hits.OrderBy(static hit => hit.FullPath, StringComparer.OrdinalIgnoreCase),
        }).ThenBy(static hit => hit.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildRankedSearchSql(
        SearchQuery query,
        SearchContext context,
        string documentsTable,
        string valuesTable,
        string filesTable,
        string fileIdColumn,
        bool requireFts,
        bool applySafeValue,
        string? prefix = null,
        IReadOnlyList<long>? excludedFileIds = null)
    {
        // Core exposes the raw value columns and must resolve them with the
        // registered sw_safe_value UDF.  The sidecar already persists the
        // resolved display value, so it continues to read that column directly.
        var matchedValueExpression = applySafeValue
            ? "sw_safe_value(mv.raw_value,mv.resolved_value,mv.display_value,mv.cached_value,mv.is_linked)"
            : "mv.display_value";
        // FTS is the candidate generator, not merely an extra predicate on a
        // later whole-table query.  In particular, keep property-value lookup
        // joined to the small eligible-document set below.  The old shape
        // scanned every search_property_values row for each query, which made
        // property search visibly slower as the cache grew.
        var candidateExcludedFileFilter = BuildExcludedFileFilter(
            "candidate_file." + fileIdColumn,
            excludedFileIds);
        var ftsCte = requireFts
            ? $$"""
              fts_candidates AS (
                  SELECT d.document_id
                    FROM search_fts
                    JOIN {{documentsTable}} d ON d.document_id=search_fts.document_id
                    JOIN {{filesTable}} candidate_file
                      ON candidate_file.{{fileIdColumn}}=d.file_id
                   WHERE search_fts MATCH @match
                     AND candidate_file.kind IN ({{context.KindFilter}})
                     -- Scope eligibility must precede the candidate cap.  If
                     -- the global FTS corpus is capped first, a common
                     -- property term can fill the entire window with files
                     -- outside the requested library/folder and hide valid
                     -- in-scope matches that occur later in the index.
                     AND instr(upper(replace(candidate_file.path,'/','\')),@excluded_system_segment)=0
                     AND (@scope_kind=0 OR candidate_file.path_norm=@scope_root
                          OR candidate_file.path_norm LIKE @scope_prefix ESCAPE '\')
                     AND ({{BuildLibraryFilter(query.Mode, context.LibraryRoots, "candidate_file.path_norm")}})
                     AND {{candidateExcludedFileFilter}}
                   LIMIT @candidate_limit
              )
              """
            : string.Empty;
        var ctes = new List<string>();
        if (requireFts)
        {
            ctes.Add(ftsCte);
        }
        ctes.Add($$"""
            eligible_documents AS (
                SELECT d.*
                  FROM {{documentsTable}} d
                 {{(requireFts ? "JOIN fts_candidates candidate ON candidate.document_id=d.document_id" : string.Empty)}}
                 WHERE (
                       (@mode=0 AND d.source_kind=0
                        AND d.file_name LIKE @contains ESCAPE '\')
                    OR (@mode IN (2,3) AND d.source_kind=0
                        AND (d.file_name LIKE @contains ESCAPE '\'
                             OR d.path_norm LIKE @contains ESCAPE '\'))
                    OR (@mode IN (1,2,3) AND d.source_kind=1
                        AND (d.names_text LIKE @contains ESCAPE '\'
                             OR d.values_text LIKE @contains ESCAPE '\')))
            )
            """);
        var withBlock = string.IsNullOrEmpty(prefix)
            ? "WITH " + string.Join(",", ctes)
            : prefix + "," + string.Join(",", ctes);
        var excludedFileFilter = BuildExcludedFileFilter(
            "f." + fileIdColumn,
            excludedFileIds);
        return $$"""
            {{withBlock}}, matching_values AS (
                -- Attribute the hit from the actual value row that matched.
                -- Do not first pick a matching group and then re-join every
                -- property in that group: that leaks an unrelated lowest-id
                -- property into the result columns.
                SELECT v.*,
                       CASE
                         WHEN v.source_kind=2 AND v.value_norm=@term THEN 725.0
                         WHEN v.source_kind=1 AND v.value_norm=@term THEN 700.0
                         WHEN v.source_kind=2 AND v.name_norm=@term THEN 675.0
                         WHEN v.source_kind=1 AND v.name_norm=@term THEN 650.0
                         WHEN v.source_kind=2 THEN 425.0
                         WHEN v.source_kind=1 THEN 400.0
                         ELSE 200.0
                       END AS match_rank
                  FROM {{valuesTable}} v
                  JOIN eligible_documents d
                    ON d.file_id=v.file_id AND d.source_kind=v.source_kind
                   AND coalesce(d.configuration_name,'')=coalesce(v.configuration_name,'')
                   AND coalesce(d.origin_path,'')=coalesce(v.origin_path,'')
                 WHERE v.name_norm LIKE @contains ESCAPE char(92)
                    OR v.value_norm LIKE @contains ESCAPE char(92)
            ), matched_values AS (
                SELECT matching_values.*,
                       row_number() OVER(
                           PARTITION BY file_id,source_kind,
                                        coalesce(configuration_name,''),
                                        coalesce(origin_path,'')
                           -- Keep the existing semantic priority and make a
                           -- same-rank multi-hit deterministic.
                           ORDER BY match_rank DESC,property_id ASC) AS match_number
                  FROM matching_values
            ), raw_matches AS (
                SELECT f.{{fileIdColumn}} AS id,f.path,f.path_norm,f.kind,
                       d.file_name,d.source_kind,d.origin_path,d.configuration_name,
                       mv.property_name AS matched_name,{{matchedValueExpression}} AS matched_value,
                       CASE
                         WHEN d.source_kind=0 AND d.file_name=@term THEN 900.0
                         WHEN d.source_kind=0 AND d.file_name LIKE @prefix ESCAPE '\' THEN 800.0
                         WHEN d.source_kind=0 THEN 500.0
                         ELSE mv.match_rank END AS rank_value
                  FROM eligible_documents d
                  JOIN {{filesTable}} f ON f.{{fileIdColumn}}=d.file_id
                  LEFT JOIN matched_values mv
                    ON mv.file_id=d.file_id AND mv.source_kind=d.source_kind
                   AND coalesce(mv.configuration_name,'')=coalesce(d.configuration_name,'')
                   AND coalesce(mv.origin_path,'')=coalesce(d.origin_path,'')
                   AND mv.match_number=1
                 WHERE f.kind IN ({{context.KindFilter}})
                   AND instr(upper(replace(f.path,'/','\')),@excluded_system_segment)=0
                    AND (@scope_kind=0 OR f.path_norm=@scope_root
                         OR f.path_norm LIKE @scope_prefix ESCAPE '\')
                    AND ({{BuildLibraryFilter(query.Mode, context.LibraryRoots, "f.path_norm")}})
                    AND {{excludedFileFilter}}
                    AND (d.source_kind=0 OR mv.property_id IS NOT NULL)
            ), ranked AS (
                SELECT *,row_number() OVER(
                    PARTITION BY id
                    ORDER BY rank_value DESC,matched_name,matched_value) AS row_number
                  FROM raw_matches
            )
            SELECT path,kind,matched_name,matched_value,origin_path,
                   rank_value,configuration_name
              FROM ranked WHERE row_number=1
             ORDER BY {{BuildRankedOrderBy(query)}} LIMIT @limit OFFSET @offset;
            """;
    }

    private async ValueTask<IReadOnlyList<SearchHit>> SearchAllCoreAsync(
        SearchQuery query,
        SearchContext context,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT f.path,f.kind
              FROM files f
             WHERE f.state=0 AND f.kind IN ({{context.KindFilter}})
               AND instr(upper(replace(f.path,'/','\')),@excluded_system_segment)=0
               AND (@scope_kind=0 OR f.path_norm=@scope_root
                    OR f.path_norm LIKE @scope_prefix ESCAPE '\')
               AND ({{BuildLibraryFilter(query.Mode, context.LibraryRoots, "f.path_norm")}})
             ORDER BY {{BuildSimpleOrderBy(query, "f.file_name_norm", "f.path", "f.kind", "f.path COLLATE NOCASE")}}
             LIMIT @limit OFFSET @offset;
            """;
        AddSearchParameters(command, query, string.Empty, context, includePaging: true);
        var hits = new List<SearchHit>();
        using var registration = RegisterSqliteInterrupt(connection, cancellationToken);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                hits.Add(new SearchHit(
                    path,
                    (SolidWorksDocumentKind)reader.GetInt32(1),
                    Path.GetFileName(path),
                    null,
                    null,
                    null,
                    0,
                    status.CacheMayBeStale));
            }
        }
        catch (SqliteException exception) when (IsCancelledInterrupt(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return hits;
    }

    private static async ValueTask<long> ExecuteCountAsync(
        SqliteConnection connection,
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        using var registration = RegisterSqliteInterrupt(connection, cancellationToken);
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException exception) when (IsCancelledInterrupt(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static async ValueTask<IReadOnlyList<SearchHit>> ReadSearchHitsAsync(
        SqliteConnection connection,
        SqliteCommand command,
        IndexStatus status,
        CancellationToken cancellationToken)
    {
        var hits = new List<SearchHit>();
        using var registration = RegisterSqliteInterrupt(connection, cancellationToken);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                var matchedValue = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (SolidWorksPropertyValue.IsUnresolvedExpression(matchedValue))
                {
                    matchedValue = null;
                }
                hits.Add(new SearchHit(
                    path,
                    (SolidWorksDocumentKind)reader.GetInt32(1),
                    Path.GetFileName(path),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    matchedValue,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetDouble(5),
                    status.CacheMayBeStale,
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }
        catch (SqliteException exception) when (IsCancelledInterrupt(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return hits;
    }

    private sealed record SearchContext(
        string KindFilter,
        SearchScope Scope,
        string ScopeRoot,
        string ScopePrefix,
        IReadOnlyList<string> LibraryRoots);

    private static SearchContext BuildSearchContext(SearchQuery query)
    {
        var requestedKinds = query.Kinds?.Distinct().ToArray() ?? [];
        var supportedKinds = requestedKinds
            .Where(SupportedSolidWorksDocumentKinds.IsSupported)
            .ToArray();
        var kindFilter = requestedKinds.Length == 0
            ? "0,1"
            : supportedKinds.Length == 0
                ? "-1"
                : string.Join(",", supportedKinds.Select(static kind =>
                    ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var scope = query.EffectiveScope;
        return new SearchContext(
            kindFilter,
            scope,
            scope.Kind == SearchScopeKind.Folder
                ? SearchPath.NormalizeKey(scope.RootPath!)
                : string.Empty,
            scope.Kind == SearchScopeKind.Folder
                ? EscapeLike(SearchPath.DescendantPrefixKey(scope.RootPath!)) + "%"
                : string.Empty,
            NormalizeLibraryRoots(query.LibraryRoots));
    }

    private static void AddSearchParameters(
        SqliteCommand command,
        SearchQuery query,
        string term,
        SearchContext context,
        bool includePaging)
    {
        var escaped = EscapeLike(term);
        command.Parameters.AddWithValue("@term", term);
        command.Parameters.AddWithValue("@prefix", escaped + "%");
        command.Parameters.AddWithValue("@contains", "%" + escaped + "%");
        command.Parameters.AddWithValue(
            "@match",
            '"' + term.Replace("\"", "\"\"", StringComparison.Ordinal) + '"');
        // Keep the FTS candidate budget ahead of the requested page.  A normal
        // first page stays compact; scrolling remains pageable instead of
        // inheriting a fixed 8k result ceiling.
        var requestedWindow = (long)query.Offset + query.Limit;
        var candidateLimit = Math.Max(
            SearchFtsCandidateFloor,
            Math.Min(100_000L, checked(requestedWindow * 4L + 1_024L)));
        command.Parameters.AddWithValue("@candidate_limit", candidateLimit);
        command.Parameters.AddWithValue("@mode", (int)query.Mode);
        command.Parameters.AddWithValue("@scope_kind", (int)context.Scope.Kind);
        command.Parameters.AddWithValue("@scope_root", context.ScopeRoot);
        command.Parameters.AddWithValue("@scope_prefix", context.ScopePrefix);
        command.Parameters.AddWithValue(
            "@excluded_system_segment",
            IndexNormalization.ExcludedRecycleBinSegment);
        command.Parameters.AddWithValue("@documentScope", (int)PropertyScope.Document);
        command.Parameters.AddWithValue("@configurationScope", (int)PropertyScope.Configuration);
        if (includePaging)
        {
            command.Parameters.AddWithValue("@limit", query.Limit);
            command.Parameters.AddWithValue("@offset", query.Offset);
        }
        AddLibraryParameters(command, context.LibraryRoots);
    }

    private static string BuildExcludedFileFilter(
        string fileIdColumn,
        IReadOnlyList<long>? excludedFileIds)
    {
        if (excludedFileIds is not { Count: > 0 })
        {
            return "1=1";
        }

        return fileIdColumn + " NOT IN (" + string.Join(",", Enumerable.Range(0, excludedFileIds.Count)
            .Select(static index => "@excluded_file_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")";
    }

    private static void AddExcludedFileParameters(
        SqliteCommand command,
        IReadOnlyList<long>? excludedFileIds)
    {
        if (excludedFileIds is not { Count: > 0 })
        {
            return;
        }

        for (var index = 0; index < excludedFileIds.Count; index++)
        {
            command.Parameters.AddWithValue(
                "@excluded_file_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                excludedFileIds[index]);
        }
    }

    private static IReadOnlyList<string> NormalizeLibraryRoots(IReadOnlyCollection<string>? roots) =>
        (roots ?? Array.Empty<string>())
        .Where(static root => !string.IsNullOrWhiteSpace(root))
        .Select(SearchPath.NormalizeFolder)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string BuildLibraryFilter(
        SearchMode mode,
        IReadOnlyList<string> roots,
        string pathColumn)
    {
        if (mode != SearchMode.Library)
        {
            return "1=1";
        }
        if (roots.Count == 0)
        {
            return "0=1";
        }
        return string.Join(" OR ", Enumerable.Range(0, roots.Count)
            .Select(index =>
                $"{pathColumn}=@library_root_{index} OR " +
                $"{pathColumn} LIKE @library_prefix_{index} ESCAPE '\\'"));
    }

    private static void AddLibraryParameters(SqliteCommand command, IReadOnlyList<string> roots)
    {
        for (var index = 0; index < roots.Count; index++)
        {
            command.Parameters.AddWithValue(
                $"@library_root_{index}",
                SearchPath.NormalizeKey(roots[index]));
            command.Parameters.AddWithValue(
                $"@library_prefix_{index}",
                EscapeLike(SearchPath.DescendantPrefixKey(roots[index])) + "%");
        }
    }

    private static string BuildRankedOrderBy(SearchQuery query)
    {
        if (query.SortColumn is null)
        {
            return "rank_value DESC,path COLLATE NOCASE";
        }
        var column = query.SortColumn switch
        {
            "fileName" => "file_name COLLATE NOCASE",
            "kind" => "kind",
            "matchedProperty" => "coalesce(matched_name,'') COLLATE NOCASE",
            "matchedValue" => "coalesce(matched_value,'') COLLATE NOCASE",
            "source" => "coalesce(origin_path,'') COLLATE NOCASE",
            "directory" => "path COLLATE NOCASE",
            _ => "path COLLATE NOCASE",
        };
        return column + (query.SortDescending ? " DESC" : " ASC") + ",path COLLATE NOCASE";
    }

    private static string BuildSimpleOrderBy(
        SearchQuery query,
        string fileNameColumn,
        string pathColumn,
        string kindColumn,
        string fallback)
    {
        if (query.SortColumn is null)
        {
            return fallback;
        }
        var column = query.SortColumn switch
        {
            "fileName" => fileNameColumn + " COLLATE NOCASE",
            "kind" => kindColumn,
            _ => pathColumn + " COLLATE NOCASE",
        };
        return column + (query.SortDescending ? " DESC" : " ASC") +
               "," + pathColumn + " COLLATE NOCASE";
    }

    private static CancellationTokenRegistration RegisterSqliteInterrupt(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static state =>
            {
                try
                {
                    var connection = (SqliteConnection)state!;
                    if (connection.Handle is { } handle)
                    {
                        SQLitePCL.raw.sqlite3_interrupt(handle);
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }, connection)
            : default;

    private static bool IsCancelledInterrupt(
        SqliteException exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && exception.SqliteErrorCode == 9;

}
