using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Tests;

public class SchemaComparerTests
{
    private static ColumnSchema Column(string name, string type = "int", bool nullable = false, bool identity = false, bool hasDefault = false) =>
        new(name, type, type.Split('(')[0], nullable, identity, hasDefault);

    private static TableSchema Groups(
        string groupNameType = "varchar(50)", bool nameNullable = false, int checks = 1, IReadOnlyList<IndexSchema>? indexes = null) =>
        new("Groups",
            [Column("GroupID", identity: true), Column("GroupName", groupNameType, nameNullable), Column("Dues", "money", hasDefault: true)],
            indexes ?? [new IndexSchema(["GroupID"], Clustered: true, Unique: true, PrimaryKey: true)],
            [],
            checks);

    private static TableSchema Membership(IReadOnlyList<IndexSchema> indexes, IReadOnlyList<ForeignKeySchema>? keys = null) =>
        new("GroupMembership", [Column("GroupID"), Column("IndividualID")], indexes,
            keys ?? [new ForeignKeySchema(["GroupID"], "Groups", ["GroupID"], "NO_ACTION", "NO_ACTION")], 0);

    private static SchemaSnapshot Schema(params TableSchema[] tables) =>
        new(tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Same_design_passes()
    {
        Assert.True(SchemaComparer.Compare(Schema(Groups()), Schema(Groups()), CompareOptions.Default).Passed);
    }

    [Fact]
    public void Lengths_and_nullability_can_be_ignored()
    {
        var expected = Schema(Groups());
        var actual = Schema(Groups(groupNameType: "varchar(100)", nameNullable: true));

        var strict = SchemaComparer.Compare(expected, actual, CompareOptions.Default);
        var lenient = SchemaComparer.Compare(expected, actual, CompareOptions.Default with { IgnoreLengths = true, IgnoreNullability = true });

        Assert.Contains("Groups.GroupName should be varchar(50), but it's varchar(100).", strict.Message);
        Assert.Contains("Groups.GroupName should not allow NULLs.", strict.Message);
        Assert.True(lenient.Passed);
    }

    [Fact]
    public void Check_constraints_are_counted()
    {
        var result = SchemaComparer.Compare(Schema(Groups(checks: 2)), Schema(Groups(checks: 1)), CompareOptions.Default);

        Assert.Contains("Groups should have 2 check constraints, but it has 1.", result.Message);
    }

    [Fact]
    public void A_primary_key_where_none_is_expected_is_reported()
    {
        var expected = Schema(Membership([]));
        var actual = Schema(Membership([new IndexSchema(["GroupID", "IndividualID"], true, true, PrimaryKey: true)]));

        var result = SchemaComparer.Compare(expected, actual, CompareOptions.Default);

        Assert.Contains("GroupMembership shouldn't have a primary key, but it has one on (GroupID, IndividualID).", result.Message);
    }

    [Fact]
    public void Unique_constraints_match_regardless_of_column_order_or_index_name()
    {
        var expected = Schema(Membership([new IndexSchema(["GroupID", "IndividualID"], false, Unique: true, false)]));
        var actual = Schema(Membership([new IndexSchema(["IndividualID", "GroupID"], false, Unique: true, false)]));

        Assert.True(SchemaComparer.Compare(expected, actual, CompareOptions.Default).Passed);
    }

    [Fact]
    public void Clustered_and_nonclustered_indexes_are_told_apart()
    {
        var expected = Schema(Membership([new IndexSchema(["GroupID"], Clustered: true, false, false)]));
        var actual = Schema(Membership([new IndexSchema(["GroupID"], Clustered: false, false, false)]));

        var result = SchemaComparer.Compare(expected, actual, CompareOptions.Default);

        Assert.Contains("GroupMembership needs a clustered index on (GroupID).", result.Message);
        Assert.Contains("GroupMembership has a nonclustered index on (GroupID) that isn't expected.", result.Message);
    }

    [Fact]
    public void Missing_foreign_keys_are_named()
    {
        var expected = Schema(Membership([]));
        var actual = Schema(Membership([], keys: []));

        var result = SchemaComparer.Compare(expected, actual, CompareOptions.Default);

        Assert.Contains("GroupMembership needs a foreign key (GroupID) referencing Groups(GroupID).", result.Message);
    }

    [Fact]
    public void Missing_and_extra_tables_are_reported()
    {
        var result = SchemaComparer.Compare(Schema(Groups()), Schema(Membership([])), CompareOptions.Default);

        Assert.Contains("Table Groups is missing.", result.Message);
        Assert.Contains("Table GroupMembership isn't expected.", result.Message);
        Assert.True(result.Structural);
    }

    [Fact]
    public void Probes_must_succeed_or_fail_the_same_way()
    {
        var expected = new[]
        {
            new ProbeResult("Adding a group with negative dues", "The INSERT statement conflicted with the CHECK constraint.", null),
        };
        var actual = new[] { new ProbeResult("Adding a group with negative dues", null, null) };

        var result = ProbeComparer.Compare(expected, actual, 5);

        Assert.False(result.Passed);
        Assert.StartsWith("Adding a group with negative dues: this should fail", result.Message);
    }

    [Fact]
    public void Probe_results_must_match()
    {
        ResultTable Dues(decimal value) => new(["Dues"], [[value]]);
        var expected = new[] { new ProbeResult("Adding a group without dues", null, Dues(0m)) };
        var same = new[] { new ProbeResult("Adding a group without dues", null, Dues(0.00m)) };
        var different = new[] { new ProbeResult("Adding a group without dues", null, Dues(10m)) };

        Assert.True(ProbeComparer.Compare(expected, same, 5).Passed);
        Assert.False(ProbeComparer.Compare(expected, different, 5).Passed);
    }

    [Fact]
    public void Type_names_are_written_as_in_create_table()
    {
        Assert.Equal("varchar(50)", SchemaReader.TypeName("varchar", 50, 0, 0));
        Assert.Equal("nvarchar(50)", SchemaReader.TypeName("nvarchar", 100, 0, 0));
        Assert.Equal("varchar(max)", SchemaReader.TypeName("varchar", -1, 0, 0));
        Assert.Equal("decimal(9,2)", SchemaReader.TypeName("decimal", 5, 9, 2));
        Assert.Equal("money", SchemaReader.TypeName("money", 8, 19, 4));
    }
}
