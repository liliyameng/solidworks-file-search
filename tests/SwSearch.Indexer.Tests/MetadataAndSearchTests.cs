using SwSearch.Contracts;

namespace SwSearch.Indexer.Tests;

public sealed class MetadataAndSearchTests
{
    [Fact]
    public async Task DirectAndEffectiveProperties_AreSearchableInCorrectModes()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var model = new FileCandidate(@"C:\设计\泵体.SLDPRT", SolidWorksDocumentKind.Part, 100, now);
        var drawing = new FileCandidate(@"C:\设计\泵体图.SLDDRW", SolidWorksDocumentKind.Drawing, 200, now);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([model, drawing]), CancellationToken.None);

        await store.UpsertMetadataAsync(new DocumentMetadata(
            model,
            [
                new DocumentProperty(
                    "物料编码", "ABC-42", "ABC-42", PropertyScope.Document, null, MetadataSource.DocumentManager),
                new DocumentProperty(
                    "描述", "液压泵体", "液压泵体", PropertyScope.Configuration, "默认", MetadataSource.DocumentManager),
            ],
            [],
            MetadataSource.DocumentManager,
            now), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(
            drawing,
            [],
            [new DocumentReference(model.FullPath, null, true)],
            MetadataSource.DocumentManager,
            now), CancellationToken.None);

        Assert.Empty(await store.SearchAsync(
            new SearchQuery("ABC-42", SearchMode.FileName),
            CancellationToken.None));

        var propertyHits = await store.SearchAsync(
            new SearchQuery("ABC-42", SearchMode.Property),
            CancellationToken.None);
        Assert.Equal(2, propertyHits.Count);
        Assert.Contains(propertyHits, hit => hit.FullPath == model.FullPath && hit.OriginModelPath is null);
        var inherited = Assert.Single(propertyHits, hit => hit.FullPath == drawing.FullPath);
        Assert.Equal(model.FullPath, inherited.OriginModelPath);
        Assert.Equal("物料编码", inherited.MatchedProperty);
        Assert.Equal("ABC-42", inherited.MatchedValue);

        Assert.DoesNotContain(await store.SearchAsync(
            new SearchQuery("液压泵体", SearchMode.Property),
            CancellationToken.None), hit => hit.FullPath == drawing.FullPath);

        var all = await store.SearchAsync(new SearchQuery("泵体", SearchMode.All), CancellationToken.None);
        Assert.Contains(all, hit => hit.FullPath == model.FullPath);
        Assert.Contains(all, hit => hit.FullPath == drawing.FullPath);
    }

    [Fact]
    public async Task FileNameMode_DoesNotMatchAKeywordThatExistsOnlyInParentDirectory()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = new FileCandidate(
            @"C:\ParentOnlyKeyword\part.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);

        Assert.Empty(await store.SearchAsync(
            new SearchQuery("ParentOnlyKeyword", SearchMode.FileName),
            CancellationToken.None));
        Assert.Single(await store.SearchAsync(
            new SearchQuery("part", SearchMode.FileName),
            CancellationToken.None));
    }

    [Fact]
    public async Task ModelMetadataUpdate_RefreshesOnlyReferencingDrawingEffectiveValues()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var model = new FileCandidate(@"C:\m.SLDASM", SolidWorksDocumentKind.Assembly, 1, now);
        var drawing = new FileCandidate(@"C:\d.SLDDRW", SolidWorksDocumentKind.Drawing, 1, now);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([model, drawing]), CancellationToken.None);
        await StorePropertyAsync(store, model, "OLD", now);
        await store.UpsertMetadataAsync(new DocumentMetadata(
            drawing,
            [],
            [new DocumentReference(model.FullPath, null, true)],
            MetadataSource.DocumentManager,
            now), CancellationToken.None);

        Assert.Contains(await store.SearchAsync(
            new SearchQuery("OLD", SearchMode.Property),
            CancellationToken.None), hit => hit.FullPath == drawing.FullPath);
        var modifiedModel = model with { Size = 2, LastWriteTimeUtc = now.AddSeconds(1) };
        await store.ApplyDiscoverySnapshotAsync(
            AsyncFiles.From([modifiedModel, drawing]), CancellationToken.None);
        Assert.DoesNotContain(await store.SearchAsync(
            new SearchQuery("OLD", SearchMode.Property),
            CancellationToken.None), hit => hit.FullPath == drawing.FullPath);
        var pendingDrawing = Assert.IsType<DocumentMetadata>(
            await store.TryGetMetadataAsync(drawing.FullPath, CancellationToken.None));
        Assert.DoesNotContain(pendingDrawing.Properties,
            property => property.Scope == PropertyScope.ReferencedModel);

        await StorePropertyAsync(store, modifiedModel, "NEW", now.AddSeconds(1));
        Assert.DoesNotContain(await store.SearchAsync(
            new SearchQuery("OLD", SearchMode.Property),
            CancellationToken.None), hit => hit.FullPath == drawing.FullPath);
        Assert.Contains(await store.SearchAsync(
            new SearchQuery("NEW", SearchMode.Property),
            CancellationToken.None), hit => hit.FullPath == drawing.FullPath && hit.OriginModelPath == model.FullPath);
    }

    [Fact]
    public async Task SearchTextWithQuotesPercentUnderscoreAndAmpersand_IsDataNotSql()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var file = new FileCandidate(
            @"C:\中文 & 100%_\a.SLDPRT",
            SolidWorksDocumentKind.Part,
            1,
            DateTimeOffset.UtcNow);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([file]), CancellationToken.None);

        var exception = await Record.ExceptionAsync(async () =>
            await store.SearchAsync(new SearchQuery("\" & %_", SearchMode.All), CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ConfigurationCatalog_RoundTripsOrderAndRejectsPseudoNames()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var file = new FileCandidate(@"C:\配置\接头.SLDPRT", SolidWorksDocumentKind.Part, 10, now);
        await store.UpsertMetadataAsync(new DocumentMetadata(
            file,
            [
                new("规格", "DN40", "DN40", PropertyScope.Configuration, "默认", MetadataSource.NoApi),
                new("规格", "DN50", "DN50", PropertyScope.Configuration, "派生", MetadataSource.NoApi),
                new("伪属性", "BAD", "BAD", PropertyScope.Configuration, "Config-0", MetadataSource.NoApi),
            ],
            [], MetadataSource.NoApi, now,
            ConfigurationNames: ["默认", "派生", "Config-0"]), CancellationToken.None);

        var roundTrip = Assert.IsType<DocumentMetadata>(await store.TryGetMetadataAsync(file.FullPath, CancellationToken.None));
        Assert.Equal(new[] { "默认", "派生" }, roundTrip.ConfigurationNames);
        Assert.DoesNotContain(roundTrip.Properties, static property => property.ConfigurationName == "Config-0");
    }

    [Fact]
    public async Task ExtractorVersion4_RequeuesVersion3AndCompletesAtCurrentVersion()
    {
        await using var database = new TemporaryDatabase();
        var now = DateTimeOffset.UtcNow;
        var part = new FileCandidate(@"C:\Upgrade\P.SLDPRT", SolidWorksDocumentKind.Part, 7, now);
        await using (var store = new SqliteIndexStore(database.Path))
        {
            await store.InitializeAsync();
            await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([part]), CancellationToken.None);
            await store.UpsertMetadataAsync(new DocumentMetadata(
                part, [], [], MetadataSource.NoApi, now), CancellationToken.None);
        }
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE files SET extractor_version={ExtractorVersions.Current - 1},metadata_state=0; DELETE FROM index_queue;";
            await command.ExecuteNonQueryAsync();
        }

        await using (var store = new SqliteIndexStore(database.Path))
        {
            await store.InitializeAsync();
            await store.RequeueForExtractorVersionAsync(ExtractorVersions.Current);
            var lease = Assert.Single(await store.LeaseMetadataAsync(
                1, TimeSpan.FromSeconds(5), "extractor-v4-test"));
            Assert.Equal("ExtractorUpgrade", lease.Reason);
            await store.UpsertMetadataAsync(new DocumentMetadata(
                part, [], [], MetadataSource.NoApi, now.AddMinutes(1)), CancellationToken.None);
        }

        await using var verify = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={database.Path};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "SELECT extractor_version FROM files LIMIT 1;";
        Assert.Equal(ExtractorVersions.Current, Convert.ToInt32(await version.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task QualifiedDrawingHit_UsesSafeDisplayValueAndReportsOriginConfiguration()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var model = new FileCandidate(@"C:\Config\M.SLDPRT", SolidWorksDocumentKind.Part, 1, now);
        var drawing = new FileCandidate(@"C:\Config\D.SLDDRW", SolidWorksDocumentKind.Drawing, 1, now);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([model, drawing]), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(model,
            [new DocumentProperty("物料编码", "$PRP:\"Code\"", string.Empty,
                PropertyScope.Configuration, "Default", MetadataSource.DocumentManager,
                DisplayValue: "SAFE-42", CachedValue: "OLDER", IsLinked: true)],
            [], MetadataSource.DocumentManager, now), CancellationToken.None);
        await store.UpsertMetadataAsync(new DocumentMetadata(drawing, [],
            [new DocumentReference(model.FullPath, "Default", true)],
            MetadataSource.NoApi, now), CancellationToken.None);

        var hit = Assert.Single(await store.SearchAsync(
            new SearchQuery("SAFE-42", SearchMode.Property), CancellationToken.None),
            item => item.FullPath == drawing.FullPath);
        Assert.Equal("SAFE-42", hit.MatchedValue);
        Assert.Equal("Default", hit.OriginConfiguration);
        Assert.Equal(model.FullPath, hit.OriginModelPath);
        Assert.DoesNotContain(await store.SearchAsync(
            new SearchQuery("$PRP", SearchMode.Property), CancellationToken.None),
            item => item.FullPath == drawing.FullPath);
    }

    [Fact]
    public async Task ModelDiscoveryHint_BumpsRevisionHidesOldHitAndRestoresAfterExtraction()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var model = new FileCandidate(@"C:\Revision\M.SLDPRT", SolidWorksDocumentKind.Part, 1, now);
        var drawing = new FileCandidate(@"C:\Revision\D.SLDDRW", SolidWorksDocumentKind.Drawing, 1, now);
        await store.ApplyDiscoverySnapshotAsync(AsyncFiles.From([model, drawing]), CancellationToken.None);
        await StorePropertyAsync(store, model, "OLD-HINT", now);
        await store.UpsertMetadataAsync(new DocumentMetadata(drawing, [],
            [new DocumentReference(model.FullPath, null, true)], MetadataSource.NoApi, now),
            CancellationToken.None);
        var before = store.SearchContentVersion;
        var modified = model with { Size = 2, LastWriteTimeUtc = now.AddSeconds(1) };

        await store.ApplyDiscoveryHintAsync(new DiscoveredFile(
            modified, DiscoveryPath.NormalizeKey(modified.FullPath), @"C:\Revision", Identity: null),
            CancellationToken.None);

        Assert.True(store.SearchContentVersion > before);
        Assert.DoesNotContain(await store.SearchAsync(
            new SearchQuery("OLD-HINT", SearchMode.Property), CancellationToken.None),
            hit => hit.FullPath == drawing.FullPath);
        await StorePropertyAsync(store, modified, "NEW-HINT", now.AddSeconds(1));
        Assert.Contains(await store.SearchAsync(
            new SearchQuery("NEW-HINT", SearchMode.Property), CancellationToken.None),
            hit => hit.FullPath == drawing.FullPath);
    }

    private static ValueTask StorePropertyAsync(
        SqliteIndexStore store,
        FileCandidate file,
        string value,
        DateTimeOffset extracted) =>
        store.UpsertMetadataAsync(new DocumentMetadata(
            file,
            [new DocumentProperty(
                "代号", value, value, PropertyScope.Document, null, MetadataSource.DocumentManager)],
            [],
            MetadataSource.DocumentManager,
            extracted), CancellationToken.None);
}
