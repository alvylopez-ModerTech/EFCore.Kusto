using Azure.Core;
using EFCore.Kusto.Infrastructure;
using Microsoft.EntityFrameworkCore;
using EFCore.Kusto.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFCore.Kusto.Extensions;

public static class KustoDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Configures the current <see cref="DbContextOptionsBuilder"/> to use the Kusto provider.
    /// </summary>
    /// <param name="builder">The options builder being configured.</param>
    /// <param name="clusterUrl">The Kusto cluster URL.</param>
    /// <param name="database">The database name within the cluster.</param>
    /// <returns>The same options builder instance for chaining.</returns>
    public static DbContextOptionsBuilder UseKusto(
        this DbContextOptionsBuilder builder,
        string clusterUrl,
        string database,
        Action<KustoDbContextOptionsBuilder>? kustoOptionsAction = null)
    {
        if (string.IsNullOrWhiteSpace(clusterUrl))
        {
            throw new ArgumentException("Cluster URL must be provided.", nameof(clusterUrl));
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            throw new ArgumentException("Database name must be provided.", nameof(database));
        }

        var ext = builder.Options.FindExtension<KustoOptionsExtension>()
                  ?? new KustoOptionsExtension();

        ext = ext.WithCluster(clusterUrl).WithDatabase(database);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(ext);

        kustoOptionsAction?.Invoke(new KustoDbContextOptionsBuilder(builder));

        return builder;
    }

    /// <summary>
    /// Configures the current <see cref="DbContextOptionsBuilder{TContext}"/> to use the Kusto provider.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext"/> type being configured.</typeparam>
    /// <param name="builder">The options builder being configured.</param>
    /// <param name="clusterUrl">The Kusto cluster URL.</param>
    /// <param name="database">The database name within the cluster.</param>
    /// <returns>The same options builder instance for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseKusto<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string clusterUrl,
        string database,
        Action<KustoDbContextOptionsBuilder>? kustoOptionsAction = null)
        where TContext : DbContext
    {
        UseKusto((DbContextOptionsBuilder)builder, clusterUrl, database, kustoOptionsAction);

        return builder;
    }

    /// <summary>
    /// Configures the provider to use managed identity authentication.
    /// </summary>
    public static KustoDbContextOptionsBuilder UseManagedIdentity(
        this KustoDbContextOptionsBuilder builder,
        string? clientId = null)
    {
        var ext = builder.OptionsBuilder.Options.FindExtension<KustoOptionsExtension>()
                  ?? new KustoOptionsExtension();

        ext = ext.WithManagedIdentity(clientId);
        ((IDbContextOptionsBuilderInfrastructure)builder.OptionsBuilder).AddOrUpdateExtension(ext);

        return builder;
    }

    /// <summary>
    /// Configures the provider to use client secret authentication for an app registration.
    /// </summary>
    public static KustoDbContextOptionsBuilder UseApplicationAuthentication(
        this KustoDbContextOptionsBuilder builder,
        string tenantId,
        string clientId,
        string clientSecret)
    {
        var ext = builder.OptionsBuilder.Options.FindExtension<KustoOptionsExtension>()
                  ?? new KustoOptionsExtension();

        ext = ext.WithApplicationAuthentication(tenantId, clientId, clientSecret);
        ((IDbContextOptionsBuilderInfrastructure)builder.OptionsBuilder).AddOrUpdateExtension(ext);

        return builder;
    }

    /// <summary>
    /// Opts into treating a string column's null check as an empty-string check: once enabled,
    /// <c>x.Field == null</c> / <c>!= null</c> on a string-typed operand is generated as
    /// <c>isempty()</c>/<c>isnotempty()</c> instead of <c>isnull()</c>/<c>isnotnull()</c>. 
    /// </summary>
    /// <param name="builder">The Kusto options builder being configured.</param>
    /// <param name="enabled">Whether the rewrite is enabled.</param>
    /// <summary>
    /// Controls how data-management commands (<c>.update</c>, <c>.delete</c>, <c>.ingest</c>, ...)
    /// that target the same table behave when they overlap.
    /// </summary>
    /// <remarks>
    /// Kusto allows only one data-management operation per table at a time and aborts the loser
    /// rather than queuing it. With this enabled (the default) such commands are serialised within
    /// the process and retried with backoff when a writer in <i>another</i> process aborts them.
    /// Disable it only if the application already guarantees a single writer per table.
    /// </remarks>
    /// <param name="builder">The options builder.</param>
    /// <param name="enabled">Whether to serialise and retry. Default <see langword="true"/>.</param>
    /// <param name="retryCount">Retries after an abort. Default 4; 0 serialises without retrying.</param>
    public static KustoDbContextOptionsBuilder SerializeDataManagementCommands(
        this KustoDbContextOptionsBuilder builder,
        bool enabled = true,
        int retryCount = 4)
    {
        var ext = builder.OptionsBuilder.Options.FindExtension<KustoOptionsExtension>()
                  ?? new KustoOptionsExtension();

        ext = ext.WithDataManagementConcurrency(enabled, retryCount);
        ((IDbContextOptionsBuilderInfrastructure)builder.OptionsBuilder).AddOrUpdateExtension(ext);

        return builder;
    }

    public static KustoDbContextOptionsBuilder UseIsEmptyForStringIsNull(
        this KustoDbContextOptionsBuilder builder,
        bool enabled = true)
    {
        var ext = builder.OptionsBuilder.Options.FindExtension<KustoOptionsExtension>()
                  ?? new KustoOptionsExtension();

        ext = ext.WithTreatNullAsEmpty(enabled);
        ((IDbContextOptionsBuilderInfrastructure)builder.OptionsBuilder).AddOrUpdateExtension(ext);

        return builder;
    }

    /// <summary>
    /// Configures the provider to use an explicitly supplied <see cref="TokenCredential"/>.
    /// </summary>
    public static KustoDbContextOptionsBuilder UseTokenCredential(
        this KustoDbContextOptionsBuilder builder,
        TokenCredential credential)
    {
        var ext = builder.OptionsBuilder.Options.FindExtension<KustoOptionsExtension>()
                  ?? new KustoOptionsExtension();

        ext = ext.WithTokenCredential(credential);
        ((IDbContextOptionsBuilderInfrastructure)builder.OptionsBuilder).AddOrUpdateExtension(ext);

        return builder;
    }
}