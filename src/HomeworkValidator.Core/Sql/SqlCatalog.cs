using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HomeworkValidator.Core.Sql;

public sealed record CatalogColumn(string Name, string Type);

public sealed record CatalogTable(string Name, IReadOnlyList<CatalogColumn> Columns);

/// <summary>
/// The tables and columns a script creates, for suggesting names in the editor. Read from a course database's
/// schema.sql rather than from SQL Server, so suggestions work before the databases are set up.
/// </summary>
public sealed class SqlCatalog
{
    public static SqlCatalog Empty { get; } = new([]);

    private readonly Dictionary<string, CatalogTable> _tables;

    public SqlCatalog(IEnumerable<CatalogTable> tables)
    {
        _tables = new Dictionary<string, CatalogTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
            _tables[table.Name] = table;
    }

    public IEnumerable<CatalogTable> Tables => _tables.Values;

    public CatalogTable? Find(string name) => _tables.GetValueOrDefault(name);

    /// <summary>Reads CREATE TABLE and ALTER TABLE ... ADD statements. Anything else in the script is ignored.</summary>
    public static SqlCatalog FromScript(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out _);
        var reader = new TableReader();
        fragment?.Accept(reader);
        return new SqlCatalog(reader.Tables.Select(t => new CatalogTable(t.Key, t.Value)));
    }

    private sealed class TableReader : TSqlFragmentVisitor
    {
        public Dictionary<string, List<CatalogColumn>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(CreateTableStatement node) =>
            Add(node.SchemaObjectName, node.Definition?.ColumnDefinitions);

        public override void ExplicitVisit(AlterTableAddTableElementStatement node) =>
            Add(node.SchemaObjectName, node.Definition?.ColumnDefinitions);

        private void Add(SchemaObjectName name, IList<ColumnDefinition>? columns)
        {
            if (name.BaseIdentifier?.Value is not { } table)
                return;
            if (!Tables.TryGetValue(table, out var list))
                Tables[table] = list = [];
            foreach (var column in columns ?? [])
                list.Add(new CatalogColumn(column.ColumnIdentifier.Value, TypeName(column)));
        }

        private static string TypeName(ColumnDefinition column)
        {
            if (column.DataType is null)
                return "computed";
            var name = column.DataType.Name.BaseIdentifier.Value.ToLowerInvariant();
            return column.DataType is ParameterizedDataTypeReference { Parameters.Count: > 0 } sized
                ? $"{name}({string.Join(",", sized.Parameters.Select(p => p.Value))})"
                : name;
        }
    }
}
