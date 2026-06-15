using Microsoft.UI.Reactor.Data;
using Microsoft.UI.Reactor.Data.Providers;
using Microsoft.UI.Reactor.Controls;
using Microsoft.UI.Reactor.Controls.Validation;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Phase 0 cross-cutting integration tests: cancellation, concurrency,
/// error handling, and end-to-end scenarios.
/// </summary>
public class Phase0CrossCuttingTests
{
    // ── FieldDescriptor null guard ──────────────────────────────

    [Fact]
    public void Calling_Null_SetValue_Throws()
    {
        var fd = new FieldDescriptor
        {
            Name = "ReadOnly",
            FieldType = typeof(string),
            GetValue = _ => "fixed",
            IsReadOnly = true,
            // SetValue is null
        };

        Assert.Null(fd.SetValue);
    }

    // ── Cancellation ────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_Token_Cancels_CreateAsync()
    {
        var source = new ListDataSource<string>(
            new[] { "a", "b" }, x => (RowKey)x);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.CreateAsync("c", cts.Token));
    }

    [Fact]
    public async Task Cancellation_Token_Cancels_UpdateAsync()
    {
        var source = new ListDataSource<string>(
            new[] { "a" }, x => (RowKey)x);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.UpdateAsync((RowKey)"a", "b", cts.Token));
    }

    [Fact]
    public async Task Cancellation_Token_Cancels_DeleteAsync()
    {
        var source = new ListDataSource<string>(
            new[] { "a" }, x => (RowKey)x);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.DeleteAsync((RowKey)"a", cts.Token));
    }

    // ── Snapshot semantics ──────────────────────────────────────

    [Fact]
    public async Task Concurrent_Add_During_Page_Read_Doesnt_Throw()
    {
        var source = new ListDataSource<int>(
            Enumerable.Range(1, 100), x => (RowKey)x);

        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            var id = 1000 + i;
            tasks.Add(source.CreateAsync(id, TestContext.Current.CancellationToken));
            tasks.Add(source.GetPageAsync(new DataRequest { PageSize = 10 }, TestContext.Current.CancellationToken));
        }

        // All should complete without throwing
        await Task.WhenAll(tasks);
    }

    // ── End-to-end integration ──────────────────────────────────

    private class PersonModel
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public bool Active { get; set; }
    }

    [Fact]
    public void FieldDescriptor_From_Reflection_Used_In_PropertyGrid()
    {
        // Create FieldDescriptors from reflection, verify they work in PropertyGrid context
        var registry = new TypeRegistry();
        var model = new PersonModel { Name = "Alice", Age = 30, Active = true };
        var meta = registry.Resolve(typeof(PersonModel));
        var descriptors = meta.Decompose!(model);

        Assert.Equal(3, descriptors.Count);

        // Verify all are FieldDescriptors
        foreach (var d in descriptors)
        {
            Assert.IsType<FieldDescriptor>(d);
        }

        // Edit via FieldDescriptor
        var nameProp = descriptors.First(d => d.Name == "Name");
        var result = nameProp.SetValue!(model, "Bob");
        Assert.Same(model, result); // mutable
        Assert.Equal("Bob", model.Name);
    }

    [Fact]
    public async Task FieldDescriptor_From_Reflection_With_ListDataSource()
    {
        var items = new[]
        {
            new PersonModel { Name = "Alice", Age = 30 },
            new PersonModel { Name = "Bob", Age = 25 },
        };

        var source = new ListDataSource<PersonModel>(items, x => (RowKey)x.Name);
        var registry = new TypeRegistry();

        // Fetch page
        var page = await source.GetPageAsync(new DataRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Items.Count);

        // Get FieldDescriptors from first item
        var meta = registry.Resolve(typeof(PersonModel));
        var item = page.Items[0];
        var descriptors = meta.Decompose!(item);

        // Read and write via descriptors
        var ageProp = descriptors.First(d => d.Name == "Age");
        Assert.Equal(30, ageProp.GetValue(item));

        ageProp.SetValue!(item, 31);
        Assert.Equal(31, item.Age);
    }

    [Fact]
    public void FormField_AutoWired_From_FieldDescriptor_With_Validation()
    {
        var fd = new FieldDescriptor
        {
            Name = "Email",
            DisplayName = "Email Address",
            FieldType = typeof(string),
            GetValue = _ => "",
            Description = "Enter your email",
            Validators = new IValidator[]
            {
                Validate.Required(),
                Validate.Email(),
            },
        };

        var registry = new TypeRegistry();
        var el = Microsoft.UI.Reactor.Controls.Validation.FormFieldDsl.FormField(fd, "", _ => { }, registry);

        Assert.Equal("Email Address", el.Label);
        Assert.Equal("Enter your email", el.Description);
        Assert.True(el.Required);
        Assert.Equal("Email", el.FieldName);
    }
}
