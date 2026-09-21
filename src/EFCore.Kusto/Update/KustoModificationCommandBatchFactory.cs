using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFCore.Kusto.Update;

public class KustoModificationCommandBatchFactory(ModificationCommandBatchFactoryDependencies dependencies)
    : IModificationCommandBatchFactory
{
    public ModificationCommandBatch Create()
    {
        return new KustoModificationCommandBatch(dependencies);
    }
}

public class KustoModificationCommandBatch(
    ModificationCommandBatchFactoryDependencies dependencies,
    int? maxBatchSize = null)
    : AffectedCountModificationCommandBatch(dependencies, maxBatchSize)
{
    private string? _table;
    private EntityState? _operation;

    public override bool TryAddCommand(IReadOnlyModificationCommand command)
    {
        if (_table == null)
        {
            _table = command.TableName;
            _operation = command.EntityState;
        }
        else if (!string.Equals(_table, command.TableName, StringComparison.Ordinal))
            return false;
        else if (_operation != command.EntityState)
            return false;

        return base.TryAddCommand(command);
    }

    public override void Complete(bool moreBatchesExpected)
    {
        if (SqlBuilder.ToString().StartsWith(".update"))
        {
            AppendUpdateTail();
        }

        base.Complete(moreBatchesExpected);
    }

    private void AppendUpdateTail()
    {
        var table = ModificationCommands[0].TableName;
        var keyColumns = string.Join(", ", ModificationCommands[0].ColumnModifications
            .Where(c => c.IsKey)
            .Select(c => c.ColumnName));

        var matchesAnyKey = string.Join(" or ", ModificationCommands
            .Select(KustoUpdateSqlGenerator.BuildPredicate)
            .Distinct());

        SqlBuilder.AppendLine();
        SqlBuilder.AppendLine("];");
        SqlBuilder.AppendLine($"let D = {table} | where {matchesAnyKey};");
        SqlBuilder.AppendLine($"let A = {table} | where {matchesAnyKey}");
        SqlBuilder.AppendLine($"  | lookup kind=inner (U) on {keyColumns}");
        SqlBuilder.AppendLine($"  | extend {KustoUpdateSqlGenerator.AssignChangedColumns(ModificationCommands)}");
        SqlBuilder.Append("  | project-away changes;");
    }
}