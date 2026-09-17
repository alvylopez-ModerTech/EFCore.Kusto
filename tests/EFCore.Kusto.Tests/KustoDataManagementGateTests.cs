using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFCore.Kusto.Data;
using Xunit;

namespace EFCore.Kusto.Tests;

/// <summary>
/// Covers the gate that keeps two data-management commands off the same table at once.
/// Kusto aborts the loser instead of queuing it, so the two behaviours that matter are:
/// recognising which commands target a table, and recognising which failures are worth retrying.
/// </summary>
public class KustoDataManagementGateTests
{
    [Theory]
    [InlineData(".update table Property delete D append A <| let U = datatable(...)", "Property")]
    [InlineData(".delete table Property records <| Property | where X == 1", "Property")]
    [InlineData(".ingest inline into table Property with (format='json') <| {}", "Property")]
    [InlineData(".set-or-append Member <| range i from 1 to 2 step 1", "Member")]
    [InlineData(".set-or-replace Media <| SomeQuery", "Media")]
    [InlineData("  .update table [Property] delete D append A <|", "Property")]
    [InlineData(".UPDATE TABLE Property delete D append A <|", "Property")]
    public void Recognises_the_table_a_data_management_command_targets(string command, string expected)
        => Assert.Equal(expected, KustoDataManagementGate.GetTargetTable(command));

    [Theory]
    [InlineData("Property | where ListingKey == \"x\" | project ListingKey")]   // a query
    [InlineData(".show commands | where StartedOn > ago(1h)")]                   // not a mutation
    [InlineData(".create table Property (A:string)")]                            // schema, not data
    [InlineData("")]
    [InlineData(null)]
    public void Leaves_everything_else_ungated(string? command)
        => Assert.Null(KustoDataManagementGate.GetTargetTable(command));

    [Theory]
    [InlineData("The operation was aborted because there is another operation currently working on the table")]
    [InlineData("Data update command failed: Failed to update metadata. Please try again")]
    [InlineData("The operation was aborted because database metadata was changed during the attempt")]
    public void Treats_a_concurrency_abort_as_retryable(string message)
        => Assert.True(KustoDataManagementGate.IsConcurrencyConflict(new InvalidOperationException(message)));

    [Fact]
    public void Finds_a_concurrency_abort_nested_in_an_inner_exception()
    {
        var inner = new InvalidOperationException("there is another operation currently working on it");
        Assert.True(KustoDataManagementGate.IsConcurrencyConflict(new Exception("wrapper", inner)));
    }

    [Theory]
    [InlineData("Syntax error: SYN0002")]
    [InlineData("Update data: unexpected schema found")]
    [InlineData("Caller is not authorized to perform this action")]
    public void Does_not_retry_failures_that_will_never_succeed(string message)
        => Assert.False(KustoDataManagementGate.IsConcurrencyConflict(new InvalidOperationException(message)));

    [Fact]
    public void Does_not_treat_a_missing_exception_as_a_conflict()
        => Assert.False(KustoDataManagementGate.IsConcurrencyConflict(null));

    [Fact]
    public async Task Serialises_commands_against_the_same_table()
    {
        var concurrent = 0;
        var peak = 0;
        var table = "SerialiseTest_" + Guid.NewGuid().ToString("N");

        async Task<int> Body()
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref concurrent);
            return 0;
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            KustoDataManagementGate.RunAsync("c", "db", table, retryCount: 0, Body)));

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Does_not_serialise_commands_against_different_tables()
    {
        var concurrent = 0;
        var peak = 0;
        var suffix = Guid.NewGuid().ToString("N");

        async Task<int> Body()
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(60);
            Interlocked.Decrement(ref concurrent);
            return 0;
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            KustoDataManagementGate.RunAsync("c", "db", $"Table{i}_{suffix}", retryCount: 0, Body)));

        Assert.True(peak > 1, $"expected tables to run in parallel, peak concurrency was {peak}");
    }

    [Fact]
    public async Task Retries_a_concurrency_abort_and_then_succeeds()
    {
        var attempts = 0;
        var table = "RetryTest_" + Guid.NewGuid().ToString("N");

        var result = await KustoDataManagementGate.RunAsync("c", "db", table, retryCount: 3, () =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("there is another operation currently working on it");
            return Task.FromResult("ok");
        });

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_number_of_retries()
    {
        var attempts = 0;
        var table = "GiveUpTest_" + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KustoDataManagementGate.RunAsync<string>("c", "db", table, retryCount: 2, () =>
            {
                attempts++;
                throw new InvalidOperationException("there is another operation currently working on it");
            }));

        Assert.Equal(3, attempts); // first attempt plus two retries
    }

    [Fact]
    public async Task Does_not_retry_a_failure_that_is_not_a_concurrency_abort()
    {
        var attempts = 0;
        var table = "NoRetryTest_" + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KustoDataManagementGate.RunAsync<string>("c", "db", table, retryCount: 5, () =>
            {
                attempts++;
                throw new InvalidOperationException("Syntax error: SYN0002");
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Releases_the_gate_when_a_command_fails()
    {
        var table = "ReleaseTest_" + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KustoDataManagementGate.RunAsync<string>("c", "db", table, retryCount: 0,
                () => throw new InvalidOperationException("boom")));

        // A second caller must not deadlock behind the failed one.
        var completed = await KustoDataManagementGate.RunAsync("c", "db", table, retryCount: 0,
            () => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
