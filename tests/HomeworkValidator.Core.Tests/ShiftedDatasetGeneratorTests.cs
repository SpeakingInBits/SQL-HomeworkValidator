using HomeworkValidator.Core.Content;

namespace HomeworkValidator.Core.Tests;

public class ShiftedDatasetGeneratorTests
{
    [Fact]
    public void Reverses_values_in_id_columns_only()
    {
        const string sql = """
            -- base comment
            INSERT INTO Orders (OrderID, CustomerID, Quantity, ShipAddressID) VALUES
            (1, 7, 3, NULL),
            (2, 8, 1, 12);
            """;

        var shifted = ShiftedDatasetGenerator.Generate(sql, "Test");

        Assert.Contains("(9999, 9993, 3, NULL)", shifted);
        Assert.Contains("(9998, 9992, 1, 9988)", shifted);
        Assert.DoesNotContain("base comment", shifted);
    }

    [Fact]
    public void Leaves_strings_that_look_like_numbers_alone()
    {
        var shifted = ShiftedDatasetGenerator.Generate("INSERT Vendors (VendorID, VendorZipCode) VALUES (5, '93711')", "Test");

        Assert.Contains("VALUES (9995, '93711')", shifted);
    }

    /// <summary>Fails when base.sql changed but shifted.sql wasn't regenerated with 'hv make-shifted'.</summary>
    [Theory]
    [InlineData("AP")]
    [InlineData("MyGuitarShop")]
    public void Committed_shifted_dataset_is_up_to_date(string database)
    {
        var dir = Path.Combine(ContentLibrary.FindRoot(AppContext.BaseDirectory), "databases", database, "datasets");

        var expected = ShiftedDatasetGenerator.Generate(File.ReadAllText(Path.Combine(dir, "base.sql")), database);

        Assert.Equal(expected.ReplaceLineEndings("\n"), File.ReadAllText(Path.Combine(dir, "shifted.sql")).ReplaceLineEndings("\n"));
    }
}
