using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.App.Views;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Takes the screenshots for the website in site/. It only runs when asked, as it writes into the repo: set HV_WEBSITE_IMAGES to the
/// folder (see "Website" in the README). The website is public, so none of these queries are homework answers.
/// </summary>
[Trait("Category", "WebsiteScreenshots")]
[Collection(SqlServerCollection.Name)]
public class WebsiteScreenshots
{
    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    [AvaloniaFact]
    public async Task Take_the_websites_screenshots()
    {
        var folder = Environment.GetEnvironmentVariable("HV_WEBSITE_IMAGES");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), "HV_WEBSITE_IMAGES isn't set.");
        Directory.CreateDirectory(folder!);

        void Save(Window window, string name)
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
            frame.Save(Path.Combine(folder!, name + ".png"), PngBitmapEncoderOptions.Default);
        }

        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "website");
        await UserJourneyTests.ConnectAsync(app);

        // A check that fails: wrong rows, and no sort.
        app.Click(app.TreeItem("3. Products priced between 500"));
        app.WriteAnswer("SELECT ProductName, ListPrice, DateAdded\nFROM Products\nWHERE ListPrice > 1000;");
        app.Press(PhysicalKey.F5);
        await app.WaitUntil(() => app.Shows("Not quite"), "the result of the check");
        Save(app.Window, "check-result");

        async Task<(VisualizerWindow Window, VisualizerViewModel Animation)> VisualizeAsync(string sql, string title)
        {
            app.WriteAnswer(sql);
            app.Click(app.Button("Visualize"));
            await app.WaitUntil(() => app.Visualizer is not null && app.VisualizerShows(title), $"the animation '{title}'");
            return (app.Visualizer!, (VisualizerViewModel)app.Visualizer!.DataContext!);
        }

        void GoTo(VisualizerViewModel animation, string phaseStart)
        {
            animation.Step = animation.Scene.Frames.ToList().FindIndex(f => f.Phase.StartsWith(phaseStart, StringComparison.Ordinal));
            Assert.True(animation.Step >= 0, phaseStart);
        }

        var (window, join) = await VisualizeAsync(
            "SELECT c.CategoryName, p.ProductName, p.ListPrice\nFROM Categories c\nLEFT JOIN Products p ON c.CategoryID = p.CategoryID\nWHERE p.ListPrice > 700;",
            "Categories LEFT JOIN Products → WHERE");
        join.InDepth = true;
        join.Step = 1;
        Save(window, "visualizer-join");

        var (_, groups) = await VisualizeAsync(
            "SELECT CategoryName, COUNT(*) AS Products, AVG(ListPrice) AS AveragePrice\nFROM Categories c\nJOIN Products p ON c.CategoryID = p.CategoryID\nGROUP BY CategoryName\nHAVING COUNT(*) > 2;",
            "Categories ⋈ Products → GROUP BY → HAVING");
        GoTo(groups, "Guitars: 6 rows become one row");
        Save(window, "visualizer-group-by");

        var (_, union) = await VisualizeAsync(
            "SELECT ProductName AS Name, 'Product' AS Kind\nFROM Products\nWHERE ListPrice > 1000\nUNION\nSELECT CategoryName, 'Category'\nFROM Categories;",
            "Query 1: Products → WHERE");
        union.Scene = union.Scenes[^1];
        GoTo(union, "✓ Query 2 row 2 of 4");
        Save(window, "visualizer-union");

        var (_, row) = await VisualizeAsync("SELECT 250 AS Price,\n.05 AS Rate,\n250 * .05 AS Discount,\n250 - (250 * .05) AS Total;", "SELECT without FROM");
        GoTo(row, "Discount:");
        Save(window, "visualizer-no-from");

        // Scripts run in a transaction that's rolled back, so this changes nothing.
        app.Click(app.TreeItem("6. Delete category 4"));
        var (_, update) = await VisualizeAsync("UPDATE Products\nSET DiscountPercent = 50\nWHERE ListPrice > 1000;", "UPDATE Products");
        GoTo(update, "✓ Updated: DiscountPercent 30 → 50");
        Save(window, "visualizer-update");
    }
}
