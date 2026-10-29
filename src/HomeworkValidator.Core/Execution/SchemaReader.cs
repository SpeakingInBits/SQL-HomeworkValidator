using Microsoft.Data.SqlClient;

namespace HomeworkValidator.Core.Execution;

/// <param name="Type">The type with its length or precision, e.g. varchar(50), decimal(9,2), money.</param>
/// <param name="BaseType">The type without length or precision, e.g. varchar.</param>
public sealed record ColumnSchema(string Name, string Type, string BaseType, bool Nullable, bool Identity, bool HasDefault);

/// <summary>An index, including the ones behind primary keys and unique constraints. Columns are in key order.</summary>
public sealed record IndexSchema(IReadOnlyList<string> Columns, bool Clustered, bool Unique, bool PrimaryKey);

public sealed record ForeignKeySchema(
    IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns, string OnDelete, string OnUpdate);

public sealed record TableSchema(
    string Name,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<IndexSchema> Indexes,
    IReadOnlyList<ForeignKeySchema> ForeignKeys,
    int CheckConstraints);

/// <summary>A database's design: its tables, columns, keys, indexes and constraints. Constraint and index names aren't kept.</summary>
public sealed record SchemaSnapshot(IReadOnlyDictionary<string, TableSchema> Tables);

/// <summary>Reads a <see cref="SchemaSnapshot"/> from the system catalog.</summary>
public static class SchemaReader
{
    public static async Task<SchemaSnapshot> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        var columns = new Dictionary<string, List<ColumnSchema>>(StringComparer.OrdinalIgnoreCase);
        await ForEachRowAsync(connection, """
            SELECT t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
            ORDER BY t.name, c.column_id
            """, r =>
        {
            var baseType = r.GetString(2);
            var type = TypeName(baseType, r.GetInt16(3), r.GetByte(4), r.GetByte(5));
            List(columns, r.GetString(0)).Add(new ColumnSchema(r.GetString(1), type, baseType, r.GetBoolean(6), r.GetBoolean(7), r.GetInt32(8) == 1));
        }, ct);

        var indexes = new Dictionary<string, List<IndexSchema>>(StringComparer.OrdinalIgnoreCase);
        await ForEachRowAsync(connection, """
            SELECT t.name, i.type, i.is_unique, i.is_primary_key,
                   STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE t.is_ms_shipped = 0 AND i.type IN (1, 2)
            GROUP BY t.name, i.index_id, i.type, i.is_unique, i.is_primary_key
            """, r => List(indexes, r.GetString(0)).Add(
                new IndexSchema(r.GetString(4).Split(','), r.GetByte(1) == 1, r.GetBoolean(2), r.GetBoolean(3))), ct);

        var foreignKeys = new Dictionary<string, List<ForeignKeySchema>>(StringComparer.OrdinalIgnoreCase);
        await ForEachRowAsync(connection, """
            SELECT t.name, rt.name, fk.delete_referential_action_desc, fk.update_referential_action_desc,
                   STRING_AGG(pc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id),
                   STRING_AGG(rc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            GROUP BY fk.object_id, t.name, rt.name, fk.delete_referential_action_desc, fk.update_referential_action_desc
            """, r => List(foreignKeys, r.GetString(0)).Add(new ForeignKeySchema(
                r.GetString(4).Split(','), r.GetString(1), r.GetString(5).Split(','), r.GetString(2), r.GetString(3))), ct);

        var checks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await ForEachRowAsync(connection, """
            SELECT t.name, COUNT(*)
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            GROUP BY t.name
            """, r => checks[r.GetString(0)] = r.GetInt32(1), ct);

        var tables = columns.ToDictionary(
            entry => entry.Key,
            entry => new TableSchema(
                entry.Key,
                entry.Value,
                indexes.GetValueOrDefault(entry.Key) ?? [],
                foreignKeys.GetValueOrDefault(entry.Key) ?? [],
                checks.GetValueOrDefault(entry.Key)),
            StringComparer.OrdinalIgnoreCase);
        return new SchemaSnapshot(tables);
    }

    /// <summary>The type as it would be written in CREATE TABLE: varchar(50), nvarchar(max), decimal(9,2), datetime2(7).</summary>
    public static string TypeName(string type, int maxLength, int precision, int scale) => type switch
    {
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength)})",
        "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : maxLength / 2)})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        "datetime2" or "time" or "datetimeoffset" => $"{type}({scale})",
        _ => type,
    };

    private static List<T> List<T>(Dictionary<string, List<T>> map, string key) =>
        map.TryGetValue(key, out var list) ? list : map[key] = [];

    private static async Task ForEachRowAsync(SqlConnection connection, string sql, Action<SqlDataReader> read, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            read(reader);
    }
}
