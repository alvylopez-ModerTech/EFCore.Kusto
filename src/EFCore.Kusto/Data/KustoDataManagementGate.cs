using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace EFCore.Kusto.Data;

/// <summary>
/// Serialises data-management commands that target the same table, and retries the ones Kusto
/// aborts because another such command was already running.
/// </summary>
/// <remarks>
/// <para>
/// Kusto permits only one data-management operation per table at a time. It does not queue the
/// second one - it <i>aborts</i> it, with a message such as
/// <c>The operation was aborted because there is another operation currently working on ...</c> or
/// <c>database metadata was changed during the attempt</c>. Microsoft's own guidance for
/// <c>.update table</c> is that "running many .update commands at once is considered a
/// resource-intensive action that should be avoided".
/// </para>
/// <para>
/// Measured on a two-node dev cluster: four concurrent writers against one table failed
/// <b>14 of 35</b> batches (40%), while the same load spread across separate tables failed none.
/// A single <c>.delete</c> issued against a table with an <c>.update</c> in flight was enough to
/// abort the update - the cheap command displaces the expensive one.
/// </para>
/// <para>
/// This type addresses both halves of that problem:
/// <list type="number">
/// <item><b>Prevention, in-process.</b> A per-table asynchronous gate means one process never
/// collides with itself. This is the common case for a replication feed running several workers in
/// one host, and it costs a semaphore.</item>
/// <item><b>Recovery, cross-process.</b> An in-process gate cannot see other instances, so a
/// command aborted by a writer elsewhere is retried with exponential backoff and jitter. The abort
/// is genuinely transient: the same command usually succeeds once the other operation commits.</item>
/// </list>
/// </para>
/// <para>
/// The gate is deliberately <i>not</i> a distributed lock. A durable cross-process lock needs a
/// store this provider does not own; applications that need strict ordering should serialise
/// writers themselves (one writer per table, fed by a queue) and treat this as a safety net.
/// </para>
/// </remarks>
public static class KustoDataManagementGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    // `.update table T ...`, `.delete table T ...`, `.ingest inline into table T ...`,
    // `.set-or-append T ...`, `.set-or-replace T ...`, `.append T ...`, `.set T ...`
    private static readonly Regex TableTarget = new(
        @"^\s*\.(?:update\s+table|delete\s+table|ingest\s+inline\s+into\s+table|ingest\s+into\s+table|set-or-append|set-or-replace|append|set)\s+(?<table>\[?[A-Za-z_][A-Za-z0-9_\-]*\]?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Kusto's wording for "someone else is already mutating this table". Matched on the message
    // rather than an exception type, because the client surfaces these as generic service errors.
    private static readonly string[] ConflictMarkers =
    {
        "another operation currently working on",
        "database metadata was changed during the attempt",
        "Failed to update metadata",
        "cannot be executed due to an invalid state"
    };

    /// <summary>
    /// Returns the table a data-management command targets, or <see langword="null"/> when the
    /// command is not one that mutates a single table (a query, or a schema command).
    /// </summary>
    public static string? GetTargetTable(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return null;

        var match = TableTarget.Match(commandText);
        return match.Success ? match.Groups["table"].Value.Trim('[', ']') : null;
    }

    /// <summary>
    /// True when <paramref name="exception"/> is Kusto rejecting a command because another
    /// data-management operation held the table. These are worth retrying; other failures are not.
    /// </summary>
    public static bool IsConcurrencyConflict(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e.Message is { } message && ConflictMarkers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> while holding the gate for
    /// <paramref name="cluster"/>/<paramref name="database"/>/<paramref name="table"/>, retrying
    /// when Kusto aborts it because another writer held the table.
    /// </summary>
    /// <param name="retryCount">
    /// Attempts after the first. Zero disables retries but still serialises in-process.
    /// </param>
    public static async Task<T> RunAsync<T>(
        string cluster,
        string database,
        string table,
        int retryCount,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd($"{cluster}|{database}|{table}", _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < retryCount && IsConcurrencyConflict(ex))
                {
                    // Exponential backoff with jitter, so instances that collided once do not
                    // line up and collide again on the retry.
                    var delayMs = (int)(Math.Pow(2, attempt) * 250) + Random.Shared.Next(0, 250);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
