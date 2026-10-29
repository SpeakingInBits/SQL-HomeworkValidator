using System.Globalization;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HomeworkValidator.Core.Content;

/// <summary>
/// Builds the "shifted" dataset from a base dataset: every integer value inserted into a column whose name ends in "ID"
/// becomes <see cref="Pivot"/> minus that value. Keys and foreign keys stay consistent, but hardcoded IDs stop working,
/// and because the order of IDs is reversed, rows no longer come back in the course data's order by accident.
/// </summary>
public static class ShiftedDatasetGenerator
{
    public const int Pivot = 10_000;

    public static string Generate(string baseSql, string databaseName)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(baseSql), out var errors);
        if (errors.Count > 0)
            throw new ContentException($"Couldn't parse the base dataset: line {errors[0].Line}: {errors[0].Message}");

        var visitor = new IdLiteralVisitor();
        fragment.Accept(visitor);

        // Replace from the end so earlier offsets stay valid.
        var text = new StringBuilder(baseSql);
        foreach (var literal in visitor.Literals.OrderByDescending(l => l.StartOffset))
        {
            var value = int.Parse(literal.Value, CultureInfo.InvariantCulture);
            if (value >= Pivot)
                throw new ContentException($"ID {value} is too large to shift; raise {nameof(ShiftedDatasetGenerator)}.{nameof(Pivot)}.");
            text.Remove(literal.StartOffset, literal.FragmentLength)
                .Insert(literal.StartOffset, (Pivot - value).ToString(CultureInfo.InvariantCulture));
        }

        var header = $"""
            -- {databaseName} shifted dataset: the base data with every ID reversed (new ID = {Pivot} - old ID).
            -- Generated from base.sql by 'hv make-shifted {databaseName}'. Don't edit this file; edit base.sql and regenerate.

            """;
        return header + StripLeadingComments(text.ToString()).ReplaceLineEndings("\n");
    }

    private static string StripLeadingComments(string sql)
    {
        var lines = sql.ReplaceLineEndings("\n").Split('\n');
        var skip = lines.TakeWhile(l => l.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(l)).Count();
        return string.Join('\n', lines.Skip(skip));
    }

    private sealed class IdLiteralVisitor : TSqlFragmentVisitor
    {
        public List<IntegerLiteral> Literals { get; } = [];

        public override void Visit(InsertSpecification node)
        {
            if (node.InsertSource is not ValuesInsertSource values)
                return;

            var idColumns = node.Columns
                .Select((column, index) => (Name: column.MultiPartIdentifier.Identifiers[^1].Value, Index: index))
                .Where(c => c.Name.EndsWith("ID", StringComparison.Ordinal))
                .Select(c => c.Index)
                .ToList();

            foreach (var row in values.RowValues)
            {
                foreach (var index in idColumns)
                {
                    if (row.ColumnValues[index] is IntegerLiteral literal)
                        Literals.Add(literal);
                }
            }
        }
    }
}
