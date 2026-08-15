using FileOp.Core.Indexing;
using FileOp.Windows.IndexingService;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexRebuildPublicationQuiescenceLeaseTests
{
    [TestMethod]
    public async Task ReadyWalDatabasesHoldBothGatesAndCrossExistingSwapBarrier()
    {
        using var fixture = new QuiescenceFixture();
        fixture.CreateWalDatabase(fixture.LivePath);
        fixture.CreateWalDatabase(fixture.ShadowPath);
        var publicationGate = new IndexingVolumeFileGate(fixture.LivePath);
        var provider = new WindowsIndexRebuildPublicationQuiescenceProvider();

        await using var lease = await provider.TryAcquireAsync(
            publicationGate,
            fixture.LivePath,
            fixture.ShadowPath);

        Assert.IsNotNull(lease);
        Assert.IsTrue(lease.Evidence.CanStartFilesystemSwap);
        Assert.IsFalse(lease.Evidence.GrantsFilesystemMutationAuthority);
        Assert.IsFalse(lease.GrantsFilesystemMutationAuthority);
        Assert.IsFalse(await publicationGate.OperationGate.WaitAsync(0));
        Assert.IsNull(publicationGate.TryAcquireRead());
        Assert.IsFalse(File.Exists(fixture.LivePath + "-wal"));
        Assert.IsFalse(File.Exists(fixture.LivePath + "-shm"));
        Assert.IsFalse(File.Exists(fixture.ShadowPath + "-wal"));
        Assert.IsFalse(File.Exists(fixture.ShadowPath + "-shm"));

        var publicationState = IndexRebuildPublicationPolicy.MarkVerified(
            IndexRebuildPublicationPolicy.Begin(fixture.LivePath, fixture.ShadowPath),
            exclusivePublicationLeaseHeld: true);
        var attempt = IndexRebuildPublicationTransactionPolicy.Prepare(
            Guid.NewGuid(),
            publicationState);
        var started = lease.MarkSwapStarted(attempt, publicationState);

        Assert.AreEqual(IndexRebuildPublicationAttemptState.SwapStarted, started.State);
        Assert.AreSame(lease.Evidence, started.QuiescenceAtSwapStart);
        Assert.IsTrue(started.SwapMayHaveChangedLiveSnapshot);
    }

    [TestMethod]
    public async Task DisposeReleasesBothGatesExactlyOnceAndLeaseCannotBeReused()
    {
        using var fixture = new QuiescenceFixture();
        fixture.CreateWalDatabase(fixture.LivePath);
        fixture.CreateWalDatabase(fixture.ShadowPath);
        var publicationGate = new IndexingVolumeFileGate(fixture.LivePath);
        var provider = new WindowsIndexRebuildPublicationQuiescenceProvider();
        var lease = await provider.TryAcquireAsync(
            publicationGate,
            fixture.LivePath,
            fixture.ShadowPath);
        Assert.IsNotNull(lease);

        var publicationState = IndexRebuildPublicationPolicy.MarkVerified(
            IndexRebuildPublicationPolicy.Begin(fixture.LivePath, fixture.ShadowPath),
            exclusivePublicationLeaseHeld: true);
        var attempt = IndexRebuildPublicationTransactionPolicy.Prepare(
            Guid.NewGuid(),
            publicationState);

        lease.Dispose();
        lease.Dispose();

        Assert.IsTrue(lease.IsDisposed);
        Assert.IsTrue(await publicationGate.OperationGate.WaitAsync(0));
        publicationGate.OperationGate.Release();
        using var readLease = publicationGate.TryAcquireRead();
        Assert.IsNotNull(readLease);
        Assert.Throws<ObjectDisposedException>(() =>
            lease.MarkSwapStarted(attempt, publicationState));
    }

    [TestMethod]
    public async Task BusyLocalGateReturnsNullWithoutTakingProcessLease()
    {
        using var fixture = new QuiescenceFixture();
        fixture.CreateWalDatabase(fixture.LivePath);
        fixture.CreateWalDatabase(fixture.ShadowPath);
        var publicationGate = new IndexingVolumeFileGate(fixture.LivePath);
        Assert.IsTrue(await publicationGate.OperationGate.WaitAsync(0));
        var provider = new WindowsIndexRebuildPublicationQuiescenceProvider();

        var lease = await provider.TryAcquireAsync(
            publicationGate,
            fixture.LivePath,
            fixture.ShadowPath);

        Assert.IsNull(lease);
        using var readLease = publicationGate.TryAcquireRead();
        Assert.IsNotNull(readLease);
        publicationGate.OperationGate.Release();
    }

    [TestMethod]
    public async Task BusyProcessGateReturnsNullAndReleasesLocalGate()
    {
        using var fixture = new QuiescenceFixture();
        fixture.CreateWalDatabase(fixture.LivePath);
        fixture.CreateWalDatabase(fixture.ShadowPath);
        var publicationGate = new IndexingVolumeFileGate(fixture.LivePath);
        using var competingReadLease = publicationGate.TryAcquireRead();
        Assert.IsNotNull(competingReadLease);
        var provider = new WindowsIndexRebuildPublicationQuiescenceProvider();

        var lease = await provider.TryAcquireAsync(
            publicationGate,
            fixture.LivePath,
            fixture.ShadowPath);

        Assert.IsNull(lease);
        Assert.IsTrue(await publicationGate.OperationGate.WaitAsync(0));
        publicationGate.OperationGate.Release();
    }

    [TestMethod]
    public async Task PublicationGateForDifferentLiveDatabaseIsRejectedBeforeAcquisition()
    {
        using var fixture = new QuiescenceFixture();
        fixture.CreateWalDatabase(fixture.LivePath);
        fixture.CreateWalDatabase(fixture.ShadowPath);
        var otherPath = Path.Combine(fixture.DirectoryPath, "other.sqlite");
        fixture.CreateWalDatabase(otherPath);
        var wrongGate = new IndexingVolumeFileGate(otherPath);
        var provider = new WindowsIndexRebuildPublicationQuiescenceProvider();

        await AssertThrowsAsync<ArgumentException>(async () =>
            await provider.TryAcquireAsync(
                wrongGate,
                fixture.LivePath,
                fixture.ShadowPath));

        Assert.IsTrue(await wrongGate.OperationGate.WaitAsync(0));
        wrongGate.OperationGate.Release();
    }

    [TestMethod]
    public async Task NonSiblingShadowDatabaseIsRejectedBeforeAcquisition()
    {
        using var fixture = new QuiescenceFixture();
        fixture.CreateWalDatabase(fixture.LivePath);
        var nestedDirectory = Path.Combine(fixture.DirectoryPath, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var nestedShadow = Path.Combine(nestedDirectory, "shadow.sqlite");
        fixture.CreateWalDatabase(nestedShadow);
        var publicationGate = new IndexingVolumeFileGate(fixture.LivePath);
        var provider = new WindowsIndexRebuildPublicationQuiescenceProvider();

        await AssertThrowsAsync<ArgumentException>(async () =>
            await provider.TryAcquireAsync(
                publicationGate,
                fixture.LivePath,
                nestedShadow));

        Assert.IsTrue(await publicationGate.OperationGate.WaitAsync(0));
        publicationGate.OperationGate.Release();
    }

    [TestMethod]
    public async Task NonWalDatabaseFailsClosedAndReleasesBothGates()
    {
        using var fixture = new QuiescenceFixture();
        fixture.CreateRollbackJournalDatabase(fixture.LivePath);
        fixture.CreateWalDatabase(fixture.ShadowPath);
        var publicationGate = new IndexingVolumeFileGate(fixture.LivePath);
        var provider = new WindowsIndexRebuildPublicationQuiescenceProvider();

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await provider.TryAcquireAsync(
                publicationGate,
                fixture.LivePath,
                fixture.ShadowPath));

        Assert.IsTrue(await publicationGate.OperationGate.WaitAsync(0));
        publicationGate.OperationGate.Release();
        using var readLease = publicationGate.TryAcquireRead();
        Assert.IsNotNull(readLease);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class QuiescenceFixture : IDisposable
    {
        public QuiescenceFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.IndexPublicationQuiescence.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            LivePath = Path.Combine(DirectoryPath, "live.sqlite");
            ShadowPath = Path.Combine(DirectoryPath, "shadow.sqlite");
        }

        public string DirectoryPath { get; }
        public string LivePath { get; }
        public string ShadowPath { get; }

        public void CreateWalDatabase(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS test_items(id INTEGER NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO test_items(value) VALUES('ready');
                """;
            command.ExecuteNonQuery();
        }

        public void CreateRollbackJournalDatabase(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                CREATE TABLE IF NOT EXISTS test_items(id INTEGER NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO test_items(value) VALUES('not-wal');
                """;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }
    }
}
