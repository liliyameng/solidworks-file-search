using Microsoft.Data.Sqlite;

namespace SwSearch.Indexer.Tests;

public sealed class StorageProbeTests
{
    [Fact]
    public async Task ConnectionFactories_UseCreateOnlyForInitializationAndReadWriteForAllOperationalPaths()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);

        Assert.Equal(
            SqliteOpenMode.ReadWriteCreate,
            new SqliteConnectionStringBuilder(store.InitializationConnectionString).Mode);
        Assert.Equal(
            SqliteOpenMode.ReadWrite,
            new SqliteConnectionStringBuilder(store.OperationalConnectionString).Mode);
        var notInitialized = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.OpenConnectionAsync(CancellationToken.None).AsTask());
        Assert.Equal("INDEX_STORAGE_NOT_INITIALIZED", notInitialized.Message);

        await store.InitializeAsync();
        await using var operational = await store.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(SqliteOpenMode.ReadWrite, new SqliteConnectionStringBuilder(operational.ConnectionString).Mode);
    }

    [Fact]
    public async Task PostInitializationOpen_DatabaseMovedAfterProbe_FailsWithoutRecreatingEmptyDatabase()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        var movedPath = database.Path + ".moved";
        try
        {
            store.BeforePostInitializationOpenForTestingAsync = _ =>
            {
                store.BeforePostInitializationOpenForTestingAsync = null;
                SqliteConnection.ClearAllPools();
                File.Move(database.Path, movedPath);
                return ValueTask.CompletedTask;
            };

            var fault = await Assert.ThrowsAsync<IndexStorageFaultException>(
                () => store.GetStatusAsync().AsTask());

            Assert.Equal("INDEX_DATABASE_MISSING", fault.Code);
            Assert.False(File.Exists(database.Path));
            Assert.True(File.Exists(movedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(movedPath))
            {
                File.Delete(movedPath);
            }
        }
    }

    [Fact]
    public void VolatileWal_DisappearingBetweenExistsAndLength_ReturnsZero()
    {
        Assert.Equal(0, SqliteIndexStore.ReadVolatileWalLength(
            @"C:\cache\index.db-wal",
            _ => true,
            _ => throw new FileNotFoundException("checkpoint race")));
        Assert.Equal(0, SqliteIndexStore.ReadVolatileWalLength(
            @"C:\cache\index.db-wal",
            _ => true,
            _ => throw new DirectoryNotFoundException("checkpoint race")));
    }

    [Fact]
    public void StorageProbes_DoNotSwallowUnauthorizedOrOtherIoFailures()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            SqliteIndexStore.ReadRequiredDatabaseLength("index.db", _ => throw new UnauthorizedAccessException()));
        Assert.Throws<IOException>(() =>
            SqliteIndexStore.ReadRequiredDatabaseLength("index.db", _ => throw new IOException("device fault")));
        Assert.Throws<UnauthorizedAccessException>(() =>
            SqliteIndexStore.ReadVolatileWalLength(
                "index.db-wal",
                _ => true,
                _ => throw new UnauthorizedAccessException()));
        Assert.Throws<IOException>(() =>
            SqliteIndexStore.ReadVolatileWalLength(
                "index.db-wal",
                _ => true,
                _ => throw new IOException("device fault")));
    }

    [Fact]
    public void RequiredDatabase_MissingFileOrDirectory_BecomesExplicitStorageFault()
    {
        foreach (var exception in new Exception[]
                 {
                     new FileNotFoundException(),
                     new DirectoryNotFoundException(),
                 })
        {
            var fault = Assert.Throws<IndexStorageFaultException>(() =>
                SqliteIndexStore.ReadRequiredDatabaseLength("missing.db", _ => throw exception));
            Assert.Equal("INDEX_DATABASE_MISSING", fault.Code);
            Assert.Equal("missing.db", fault.DatabasePath);
            Assert.Same(exception, fault.InnerException);
        }
    }

    [Fact]
    public async Task InitializedStore_DeletedPrimaryDatabase_ReportsFaultInsteadOfZeroBytes()
    {
        await using var database = new TemporaryDatabase();
        await using var store = new SqliteIndexStore(database.Path);
        await store.InitializeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(database.Path);

        var fault = await Assert.ThrowsAsync<IndexStorageFaultException>(
            () => store.GetStorageStatusAsync().AsTask());

        Assert.Equal("INDEX_DATABASE_MISSING", fault.Code);
        var readFault = await Assert.ThrowsAsync<IndexStorageFaultException>(
            () => store.GetStatusAsync().AsTask());
        Assert.Equal("INDEX_DATABASE_MISSING", readFault.Code);
        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public async Task InitializedStore_DeletedDatabaseDirectory_ReportsFaultInsteadOfHealthyState()
    {
        var root = Path.Combine(Path.GetTempPath(), "swsearch-storage-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "cache", "index.db");
        await using var store = new SqliteIndexStore(databasePath);
        try
        {
            await store.InitializeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);

            var fault = await Assert.ThrowsAsync<IndexStorageFaultException>(
                () => store.GetStorageStatusAsync().AsTask());

            Assert.Equal("INDEX_DATABASE_MISSING", fault.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExistingDatabase_UsesRealLengthForHardLimitCalculation()
    {
        await using var database = new TemporaryDatabase();
        var policy = new IndexStoragePolicy(
            TargetDatabaseBytes: 1,
            MaximumDatabaseBytes: 1,
            PassiveCheckpointWalBytes: 1024 * 1024,
            PauseWalBytes: 2 * 1024 * 1024);
        await using var store = new SqliteIndexStore(database.Path, policy);
        await store.InitializeAsync();

        var status = await store.GetStorageStatusAsync();

        Assert.True(status.DatabaseBytes > 1);
        Assert.True(status.MetadataWritesPaused);
        Assert.Equal("DATABASE_HARD_LIMIT", status.PauseReason);
    }
}
