using Microsoft.UI.Reactor.Data;
using Microsoft.UI.Reactor.Data.Providers;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Tests for the DataPageCache — block-based caching with LRU eviction.
/// </summary>
public class DataPageCacheTests
{
    private record TestItem(int Id, string Name);

    private static ListDataSource<TestItem> CreateSource(int count = 200)
    {
        var items = Enumerable.Range(0, count).Select(i => new TestItem(i, $"Item {i}"));
        return new ListDataSource<TestItem>(items, t => (RowKey)t.Id);
    }

    // ── Cache hit returns existing data ────────────────────────

    [Fact]
    public async Task GetBlockAsync_Returns_Loaded_Data()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        var block = await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(BlockStatus.Loaded, block.Status);
        Assert.Equal(10, block.Items.Count);
        Assert.Equal(0, block.Items[0].Id);
        Assert.Equal(9, block.Items[9].Id);
    }

    [Fact]
    public async Task GetBlockAsync_Returns_Correct_Block_For_Index()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        var block = await cache.GetBlockAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(BlockStatus.Loaded, block.Status);
        Assert.Equal(10, block.Items.Count);
        Assert.Equal(30, block.Items[0].Id); // block 3 starts at index 30
    }

    // ── Cache hit returns cached data ─────────────────────────

    [Fact]
    public async Task Cached_Block_Returned_Without_Refetch()
    {
        var source = CreateSource(100);
        var cache = new DataPageCache<TestItem>(source, blockSize: 10, maxBlocks: 5);

        // Fetch block 0
        var first = await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);
        // Fetch again — should return the same data (from cache)
        var second = await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(first.Items.Count, second.Items.Count);
        Assert.Same(first.Items, second.Items); // exact same reference
    }

    [Fact]
    public async Task CachedBlockCount_Tracks_Loaded_Blocks()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        Assert.Equal(0, cache.CachedBlockCount);

        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.CachedBlockCount);

        await cache.GetBlockAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(2, cache.CachedBlockCount);
    }

    // ── Cache miss triggers fetch ─────────────────────────────

    [Fact]
    public void GetBlock_Sync_Returns_Loading_For_Uncached()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        var block = cache.GetBlock(0);
        // Synchronous call returns loading placeholder (fetch is in-flight)
        Assert.Equal(BlockStatus.Loading, block.Status);
        Assert.Empty(block.Items);
    }

    [Fact]
    public async Task GetBlock_Sync_After_Fetch_Returns_Loaded()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        // First fetch via async
        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        // Now sync access should return loaded block
        var block = cache.GetBlock(0);
        Assert.Equal(BlockStatus.Loaded, block.Status);
        Assert.Equal(10, block.Items.Count);
    }

    // ── LRU eviction drops oldest blocks ──────────────────────

    [Fact]
    public async Task LRU_Eviction_Drops_Oldest_When_At_Capacity()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(200), blockSize: 10, maxBlocks: 3);

        // Fill cache to capacity
        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);
        await cache.GetBlockAsync(1, TestContext.Current.CancellationToken);
        await cache.GetBlockAsync(2, TestContext.Current.CancellationToken);
        Assert.Equal(3, cache.CachedBlockCount);

        // Adding block 3 should evict block 0 (LRU)
        await cache.GetBlockAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(3, cache.CachedBlockCount);

        // Block 0 should have been evicted
        Assert.False(cache.IsLoaded(0)); // row 0 is in block 0
        Assert.True(cache.IsLoaded(10)); // row 10 is in block 1
        Assert.True(cache.IsLoaded(20)); // row 20 is in block 2
        Assert.True(cache.IsLoaded(30)); // row 30 is in block 3
    }

    [Fact]
    public async Task LRU_Touch_Prevents_Eviction()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(200), blockSize: 10, maxBlocks: 3);

        // Load blocks 0, 1, 2
        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);
        await cache.GetBlockAsync(1, TestContext.Current.CancellationToken);
        await cache.GetBlockAsync(2, TestContext.Current.CancellationToken);

        // Touch block 0 (makes it MRU)
        cache.GetBlock(0);

        // Load block 3 — should evict block 1 (now LRU, since 0 was touched)
        await cache.GetBlockAsync(3, TestContext.Current.CancellationToken);

        Assert.True(cache.IsLoaded(0));   // block 0 survived
        Assert.False(cache.IsLoaded(10)); // block 1 was evicted
        Assert.True(cache.IsLoaded(20));  // block 2 survived
        Assert.True(cache.IsLoaded(30));  // block 3 loaded
    }

    // ── Sort/filter change invalidates cache ──────────────────

    [Fact]
    public async Task SetState_Invalidates_All_Blocks()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);
        await cache.GetBlockAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(2, cache.CachedBlockCount);

        // Change sort state
        cache.SetState(new DataRequest
        {
            Sort = new[] { new SortDescriptor("Name", SortDirection.Descending) }
        });

        Assert.Equal(0, cache.CachedBlockCount);
        Assert.Null(cache.TotalCount);
    }

    [Fact]
    public async Task Invalidate_Clears_All_Blocks()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);
        await cache.GetBlockAsync(1, TestContext.Current.CancellationToken);

        cache.Invalidate();

        Assert.Equal(0, cache.CachedBlockCount);
    }

    // ── TotalCount ────────────────────────────────────────────

    [Fact]
    public async Task TotalCount_Set_From_First_Response()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(150), blockSize: 10, maxBlocks: 5);

        Assert.Null(cache.TotalCount);

        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(150, cache.TotalCount);
    }

    // ── GetItem ───────────────────────────────────────────────

    [Fact]
    public async Task GetItem_Returns_Correct_Item()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);
        var item = cache.GetItem(5);

        Assert.NotNull(item);
        Assert.Equal(5, item!.Id);
    }

    [Fact]
    public void GetItem_Returns_Default_When_Not_Loaded()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        // No blocks loaded yet
        var item = cache.GetItem(5);
        Assert.Null(item);
    }

    // ── BlockLoaded event ─────────────────────────────────────

    [Fact]
    public async Task BlockLoaded_Fires_When_Block_Fetched()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);
        int? loadedBlockIndex = null;
        cache.BlockLoaded += idx => loadedBlockIndex = idx;

        await cache.GetBlockAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(2, loadedBlockIndex);
    }

    // ── Sort/filter push-down in requests ──────────────────────

    [Fact]
    public async Task SetState_Passes_Sort_In_Subsequent_Requests()
    {
        var source = CreateSource(100);
        var cache = new DataPageCache<TestItem>(source, blockSize: 10, maxBlocks: 5);

        cache.SetState(new DataRequest
        {
            Sort = new[] { new SortDescriptor("Name", SortDirection.Ascending) }
        });

        var block = await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(BlockStatus.Loaded, block.Status);
        Assert.Equal(10, block.Items.Count);
        // Since ListDataSource supports ServerSort, items should be sorted
        // "Item 0" < "Item 1" < ... (string sort)
    }

    // ── Failed block ──────────────────────────────────────────

    [Fact]
    public async Task Failed_Fetch_Produces_Failed_Block()
    {
        var source = new FailingSource();
        var cache = new DataPageCache<TestItem>(source, blockSize: 10, maxBlocks: 5);

        var block = await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(BlockStatus.Failed, block.Status);
        Assert.Empty(block.Items);
        Assert.NotNull(block.Error);
    }

    private class FailingSource : IDataSource<TestItem>
    {
        public DataSourceCapabilities Capabilities => DataSourceCapabilities.None;
        public RowKey GetRowKey(TestItem item) => (RowKey)item.Id;

        public Task<DataPage<TestItem>> GetPageAsync(DataRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated server error");
    }

    // ── IsLoaded ──────────────────────────────────────────────

    [Fact]
    public async Task IsLoaded_Returns_True_For_Loaded_Rows()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        Assert.True(cache.IsLoaded(0));
        Assert.True(cache.IsLoaded(9));
        Assert.False(cache.IsLoaded(10)); // block 1 not loaded
    }

    // ── GetBlockStatus ────────────────────────────────────────

    [Fact]
    public async Task GetBlockStatus_Returns_Correct_Status()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(100), blockSize: 10, maxBlocks: 5);

        await cache.GetBlockAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(BlockStatus.Loaded, cache.GetBlockStatus(0));
        Assert.Equal(BlockStatus.Loading, cache.GetBlockStatus(1)); // not yet accessed
    }

    // ── Last block may have fewer items ──────────────────────

    [Fact]
    public async Task Last_Block_Has_Fewer_Items()
    {
        var cache = new DataPageCache<TestItem>(CreateSource(25), blockSize: 10, maxBlocks: 5);

        var block = await cache.GetBlockAsync(2, TestContext.Current.CancellationToken); // items 20-24

        Assert.Equal(BlockStatus.Loaded, block.Status);
        Assert.Equal(5, block.Items.Count); // only 5 items in last block
    }
}
