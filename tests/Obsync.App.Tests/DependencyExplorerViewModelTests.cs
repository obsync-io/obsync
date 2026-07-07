using NSubstitute;
using Obsync.App.ViewModels;
using Obsync.Data.Repositories;
using Obsync.Metadata;
using Obsync.Shared;
using Obsync.Shared.Abstractions;
using Obsync.Shared.Models;
using Obsync.Shared.Objects;
using Obsync.Shared.Results;

namespace Obsync.App.Tests;

/// <summary>The Dependencies tab: picker search over the local index, live lookups, and drill-through.</summary>
public sealed class DependencyExplorerViewModelTests
{
    private readonly IObjectStateRepository _states = Substitute.For<IObjectStateRepository>();
    private readonly ISqlServerProbe _probe = Substitute.For<ISqlServerProbe>();
    private readonly ICredentialStore _credentials = Substitute.For<ICredentialStore>();

    private readonly SyncJob _job = new() { Name = "Sales sync", ConnectionProfileId = Guid.NewGuid() };
    private readonly SqlConnectionProfile _connection = new() { Name = "prod", ServerName = "SQL01" };

    private DependencyExplorerViewModel NewVm() => new(_states, _probe, _credentials);

    private static TrackedObjectState State(string schema, string name) => new()
    {
        DatabaseName = "SalesDB", SchemaName = schema, ObjectName = name, ObjectType = SqlObjectType.Table,
    };

    [Fact]
    public async Task Initialize_LoadsDatabases_SelectsTheFirst_AndRunsTheInitialSearch()
    {
        _states.GetDatabasesForJobAsync(_job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["CRM", "SalesDB"]));
        _states.SearchAsync(_job.Id, "CRM", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TrackedObjectState>>([State("dbo", "Customers")]));

        var vm = NewVm();
        await vm.InitializeAsync(_job, _connection);

        Assert.Equal(["CRM", "SalesDB"], vm.Databases);
        Assert.Equal("CRM", vm.SelectedDatabase);
        var result = Assert.Single(vm.SearchResults);
        Assert.Equal("Customers", result.ObjectName);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task Initialize_WithNoIndexedObjects_ExplainsThatARunIsNeeded()
    {
        _states.GetDatabasesForJobAsync(_job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var vm = NewVm();
        await vm.InitializeAsync(_job, _connection);

        Assert.Contains("Run this job once", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectingAnObject_QueriesTheLiveGraph_AndSummarizesBothDirections()
    {
        _states.GetDatabasesForJobAsync(_job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["SalesDB"]));
        _probe.GetDependenciesAsync(_connection, Arg.Any<string?>(), "SalesDB", "dbo", "Customers", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SqlObjectDependencies
            {
                UsedBy =
                [
                    new SqlDependencyItem { Schema = "dbo", Name = "vw_A", TypeLabel = "View" },
                    new SqlDependencyItem { Schema = "dbo", Name = "vw_B", TypeLabel = "View" },
                    new SqlDependencyItem { Schema = "dbo", Name = "usp_C", TypeLabel = "Stored procedure" },
                ],
                Uses = [new SqlDependencyItem { Schema = "dbo", Name = "Regions", TypeLabel = "Table (foreign key)" }],
            }));

        var vm = NewVm();
        await vm.InitializeAsync(_job, _connection);
        vm.SelectedObject = State("dbo", "Customers");
        await WaitForLookupAsync(vm);

        Assert.True(vm.HasResults);
        Assert.Equal("dbo.Customers", vm.CurrentObjectLabel);
        Assert.Equal(3, vm.UsedBy.Count);
        Assert.Equal("2 views · 1 stored procedure", vm.UsedBySummary);
        Assert.Equal("1 table (foreign key)", vm.UsesSummary);
    }

    [Fact]
    public async Task AFailedLookup_ClearsResults_AndShowsTheError()
    {
        _states.GetDatabasesForJobAsync(_job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["SalesDB"]));
        _probe.GetDependenciesAsync(
                Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SqlObjectDependencies>("dbo.Ghost was not found in SalesDB — it may have been dropped."));

        var vm = NewVm();
        await vm.InitializeAsync(_job, _connection);
        vm.SelectedObject = State("dbo", "Ghost");
        await WaitForLookupAsync(vm);

        Assert.False(vm.HasResults);
        Assert.Contains("not found", vm.StatusMessage);
    }

    [Fact]
    public async Task DrillInto_ReanalyzesTheClickedObject_ButIgnoresUnresolvedReferences()
    {
        _states.GetDatabasesForJobAsync(_job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["SalesDB"]));
        _probe.GetDependenciesAsync(
                Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SqlObjectDependencies()));

        var vm = NewVm();
        await vm.InitializeAsync(_job, _connection);

        await vm.DrillIntoCommand.ExecuteAsync(new SqlDependencyItem { Schema = "dbo", Name = "vw_A", TypeLabel = "View" });
        Assert.Equal("dbo.vw_A", vm.CurrentObjectLabel);

        await vm.DrillIntoCommand.ExecuteAsync(
            new SqlDependencyItem { Name = "OtherDb.dbo.T", TypeLabel = "Cross-database reference", IsDrillable = false });
        Assert.Equal("dbo.vw_A", vm.CurrentObjectLabel); // unchanged
        await _probe.Received(1).GetDependenciesAsync(
            Arg.Any<SqlConnectionProfile>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Summarize_OrdersByCount_AndPluralizes()
    {
        var summary = DependencyExplorerViewModel.Summarize(
        [
            new SqlDependencyItem { Name = "a", TypeLabel = "Trigger" },
            new SqlDependencyItem { Name = "b", TypeLabel = "View" },
            new SqlDependencyItem { Name = "c", TypeLabel = "View" },
        ]);

        Assert.Equal("2 views · 1 trigger", summary);
        Assert.Equal("none", DependencyExplorerViewModel.Summarize([]));
    }

    // Selection kicks the lookup off fire-and-forget; with substituted (synchronous) dependencies it
    // finishes within a few scheduler hops, so poll briefly instead of hard-coding a delay.
    private static async Task WaitForLookupAsync(DependencyExplorerViewModel vm)
    {
        for (var i = 0; i < 50 && vm.IsLoading; i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(20);
    }
}
