using Microsoft.UI.Reactor.Data;
using Microsoft.UI.Reactor.Data.Providers;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Tests for ListDataSource: paging, sorting, filtering, search, and CRUD.
/// </summary>
public class ListDataSourceTests
{
    private record TestItem(int Id, string Name, int Age, string? Email = null);

    private static ListDataSource<TestItem> CreateSource(params TestItem[] items)
        => new(items, x => (RowKey)x.Id);

    private static TestItem[] SampleItems =>
    [
        new(1, "Alice", 30, "alice@test.com"),
        new(2, "Bob", 25, "bob@test.com"),
        new(3, "Charlie", 35, "charlie@test.com"),
        new(4, "Diana", 28, null),
        new(5, "Eve", 30, "eve@test.com"),
    ];

    // ── Basic ───────────────────────────────────────────────────

    [Fact]
    public async Task Empty_Source_Returns_Empty_Page()
    {
        var source = CreateSource();
        var page = await source.GetPageAsync(new DataRequest(), TestContext.Current.CancellationToken);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task Paging_Through_Items()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest { PageSize = 2 };

        var page1 = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page1.TotalCount);
        Assert.NotNull(page1.ContinuationToken);

        var page2 = await source.GetPageAsync(req with { ContinuationToken = page1.ContinuationToken }, TestContext.Current.CancellationToken);
        Assert.Equal(2, page2.Items.Count);
        Assert.NotNull(page2.ContinuationToken);

        var page3 = await source.GetPageAsync(req with { ContinuationToken = page2.ContinuationToken }, TestContext.Current.CancellationToken);
        Assert.Single(page3.Items);
        Assert.Null(page3.ContinuationToken); // last page
    }

    [Fact]
    public async Task TotalCount_Reflects_Filtered_Count()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Age", FilterOperator.Equals, 30) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.TotalCount); // Alice(30) and Eve(30)
        Assert.Equal(2, page.Items.Count);
    }

    // ── Sort ────────────────────────────────────────────────────

    [Fact]
    public async Task Sort_Ascending_String()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Sort = new[] { new SortDescriptor("Name", SortDirection.Ascending) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal("Alice", page.Items[0].Name);
        Assert.Equal("Bob", page.Items[1].Name);
        Assert.Equal("Charlie", page.Items[2].Name);
    }

    [Fact]
    public async Task Sort_Descending_String()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Sort = new[] { new SortDescriptor("Name", SortDirection.Descending) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal("Eve", page.Items[0].Name);
        Assert.Equal("Diana", page.Items[1].Name);
    }

    [Fact]
    public async Task Sort_Ascending_Numeric()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Sort = new[] { new SortDescriptor("Age", SortDirection.Ascending) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(25, page.Items[0].Age); // Bob
        Assert.Equal(28, page.Items[1].Age); // Diana
    }

    [Fact]
    public async Task Sort_Descending_Numeric()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Sort = new[] { new SortDescriptor("Age", SortDirection.Descending) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(35, page.Items[0].Age); // Charlie
    }

    [Fact]
    public async Task Sort_MultiField()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Sort = new[]
            {
                new SortDescriptor("Age", SortDirection.Ascending),
                new SortDescriptor("Name", SortDirection.Descending),
            },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal("Bob", page.Items[0].Name); // Age 25
        Assert.Equal("Diana", page.Items[1].Name); // Age 28
        // Age 30: Eve before Alice (descending Name)
        Assert.Equal("Eve", page.Items[2].Name);
        Assert.Equal("Alice", page.Items[3].Name);
    }

    // ── Filter ──────────────────────────────────────────────────

    [Fact]
    public async Task Filter_Equals_String()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Name", FilterOperator.Equals, "Alice") },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Single(page.Items);
        Assert.Equal("Alice", page.Items[0].Name);
    }

    [Fact]
    public async Task Filter_NotEquals_String()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Name", FilterOperator.NotEquals, "Alice") },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(4, page.Items.Count);
        Assert.DoesNotContain(page.Items, x => x.Name == "Alice");
    }

    [Fact]
    public async Task Filter_Contains_String()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Name", FilterOperator.Contains, "li") },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Items.Count); // Alice, Charlie
    }

    [Fact]
    public async Task Filter_StartsWith_String()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Name", FilterOperator.StartsWith, "Ch") },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Single(page.Items);
        Assert.Equal("Charlie", page.Items[0].Name);
    }

    [Fact]
    public async Task Filter_EndsWith_String()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Name", FilterOperator.EndsWith, "e") },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(3, page.Items.Count); // Alice, Charlie, Eve
    }

    [Fact]
    public async Task Filter_GreaterThan_Numeric()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Age", FilterOperator.GreaterThan, 30) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Single(page.Items); // Charlie(35)
    }

    [Fact]
    public async Task Filter_LessThan_Numeric()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Age", FilterOperator.LessThan, 28) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Single(page.Items); // Bob(25)
    }

    [Fact]
    public async Task Filter_Between_Numeric()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Age", FilterOperator.Between, 28, 30) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(3, page.Items.Count); // Diana(28), Alice(30), Eve(30)
    }

    [Fact]
    public async Task Filter_In()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Name", FilterOperator.In, new[] { "Alice", "Eve" }) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task Filter_IsNull()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Email", FilterOperator.IsNull) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Single(page.Items); // Diana has null email
    }

    [Fact]
    public async Task Filter_IsNotNull()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[] { new FilterDescriptor("Email", FilterOperator.IsNotNull) },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(4, page.Items.Count);
    }

    [Fact]
    public async Task Multiple_Filters_ANDed()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest
        {
            Filters = new[]
            {
                new FilterDescriptor("Age", FilterOperator.GreaterThanOrEqual, 30),
                new FilterDescriptor("Name", FilterOperator.Contains, "li"),
            },
        };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Items.Count); // Alice(30, has "li") and Charlie(35, has "li")
    }

    // ── Search ──────────────────────────────────────────────────

    [Fact]
    public async Task Search_Matches_Across_String_Fields()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest { SearchQuery = "bob" };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Single(page.Items); // Bob matches on both Name and Email
        Assert.Equal("Bob", page.Items[0].Name);
    }

    [Fact]
    public async Task Search_Matches_Email_Field()
    {
        var source = CreateSource(SampleItems);
        var req = new DataRequest { SearchQuery = "test.com" };

        var page = await source.GetPageAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(4, page.Items.Count); // All non-null email items
    }

    // ── CRUD ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Adds_Item()
    {
        var source = CreateSource(SampleItems);
        var newItem = new TestItem(6, "Frank", 40, "frank@test.com");
        await source.CreateAsync(newItem, TestContext.Current.CancellationToken);

        var page = await source.GetPageAsync(new DataRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(6, page.Items.Count);
        Assert.Contains(page.Items, x => x.Name == "Frank");
    }

    [Fact]
    public async Task UpdateAsync_Modifies_Item()
    {
        var source = CreateSource(SampleItems);
        var updated = new TestItem(1, "Alice Updated", 31, "alice@new.com");
        await source.UpdateAsync((RowKey)1, updated, TestContext.Current.CancellationToken);

        var page = await source.GetPageAsync(new DataRequest(), TestContext.Current.CancellationToken);
        Assert.Contains(page.Items, x => x.Name == "Alice Updated" && x.Age == 31);
    }

    [Fact]
    public async Task DeleteAsync_Removes_Item()
    {
        var source = CreateSource(SampleItems);
        await source.DeleteAsync((RowKey)1, TestContext.Current.CancellationToken);

        var page = await source.GetPageAsync(new DataRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(4, page.Items.Count);
        Assert.DoesNotContain(page.Items, x => x.Name == "Alice");
    }

    [Fact]
    public async Task GetRowKey_Returns_Stable_Key()
    {
        var item = new TestItem(42, "Test", 20);
        var source = CreateSource(item);
        Assert.Equal(new RowKey("42"), source.GetRowKey(item));
    }

    // ── Cancellation ────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_Token_Cancels_GetPageAsync()
    {
        var source = CreateSource(SampleItems);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.GetPageAsync(new DataRequest(), cts.Token));
    }

    // ── Concurrency ─────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_Add_During_PageRead_Does_Not_Throw()
    {
        var source = CreateSource(SampleItems);

        // Run concurrent reads and writes
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var id = 100 + i;
            tasks.Add(source.CreateAsync(new TestItem(id, $"Item{id}", 20), TestContext.Current.CancellationToken));
            tasks.Add(source.GetPageAsync(new DataRequest(), TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks); // Should not throw
    }
}
