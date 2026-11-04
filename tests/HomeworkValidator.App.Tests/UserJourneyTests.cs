using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.Core.Content;
using Microsoft.Data.SqlClient;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// A student using the app from start to finish, with only mouse clicks and key presses.
/// Each step saves a screenshot to bin/.../screenshots/ so a person can see what the student saw.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class UserJourneyTests
{
    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    private const string WrongAnswer = """
        SELECT ProductName, ListPrice, DateAdded
        FROM Products
        WHERE ListPrice >= 500 AND ListPrice <= 2000
        ORDER BY DateAdded DESC;
        """;

    private const string RightAnswer = """
        SELECT ProductName, ListPrice, DateAdded
        FROM Products
        WHERE ListPrice > 500 AND ListPrice < 2000
        ORDER BY DateAdded DESC;
        """;

    // ----- Without a SQL Server -----

    [AvaloniaFact]
    public async Task A_server_that_isnt_there_shows_an_error_and_stays_on_the_connection_page()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "unreachable");

        // Port 1 on this machine: nothing listens there, so the connection is refused straight away.
        app.Fill(app.TextBox("Server"), "127.0.0.1,1");
        UseSqlAuthentication(app, "sa", "not-a-real-password");
        app.Click(app.Button("Connect"));

        await app.WaitUntil(() => app.ShowsTextStartingWith("Couldn't reach 127.0.0.1,1."), "the connection error", timeoutSeconds: 90);
        app.Screenshot("error");
        Assert.True(app.ShowsAsFailure("Couldn't reach 127.0.0.1,1."));
        Assert.True(app.ShowsTextStartingWith("Details: "));
        Assert.True(app.Shows("Connect to your SQL Server"));
    }

    [AvaloniaFact]
    public void Connect_is_disabled_until_there_is_a_server()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "empty-server");

        app.Fill(app.TextBox("Server"), "");
        Assert.False(app.Button("Connect").IsEffectivelyEnabled);

        app.Type("localhost");
        Assert.True(app.Button("Connect").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Login_and_password_appear_only_for_SQL_authentication()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "auth-options");

        if (OperatingSystem.IsWindows())
        {
            // Windows authentication is the default on Windows, so there's no login to type.
            Assert.False(app.Shows("Password"));
            app.Click(app.RadioButton("SQL Server authentication"));
        }

        // Everywhere else SQL authentication is the only choice, so the fields are always there.
        Assert.True(app.Shows("Login"));
        Assert.True(app.Shows("Password"));
        Assert.Equal("sa", app.TextBox("Login").Text);
    }

    // ----- Against a real SQL Server (HV_TEST_CONNECTION_STRING) -----

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_works_through_a_question()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "journey");
        app.Screenshot("connection");

        await ConnectAsync(app);
        app.Screenshot("connected");

        // Pick a question and get it wrong: >= and <= include the edge data's products priced exactly 500 and 2000.
        app.Click(app.TreeItem("3. Products priced between 500"));
        Assert.True(app.Shows("Question 3: Products priced between 500 and 2000"));
        app.WriteAnswer(WrongAnswer);
        app.Press(PhysicalKey.F5);

        await app.WaitUntil(() => app.Shows("Not quite"), "the result of checking the wrong answer");
        app.Screenshot("wrong-answer");
        Assert.True(app.Shows("Your query matches the expected result on 2 of 3 datasets."));
        Assert.True(app.Shows("Edge Guitar 500"));
        Assert.True(app.ShowsTextStartingWith("Hint: Check products priced exactly 500"));
        Assert.Equal("✗", app.TreeMark("3. Products priced between 500"));

        // Fix it and check with the button this time.
        app.WriteAnswer(RightAnswer);
        app.Click(app.Button("Check (F5)"));

        await app.WaitUntil(() => app.Shows("Correct!"), "the result of checking the right answer");
        app.Screenshot("right-answer");
        Assert.True(app.Shows("Your query matches the expected result on all 3 datasets."));
        Assert.Equal("✓", app.TreeMark("3. Products priced between 500"));

        // Another question, with a syntax error, checked with Ctrl+Enter.
        app.Click(app.TreeItem("1. Products sorted by list price"));
        Assert.Equal("", app.Editor.Text);
        app.WriteAnswer("SELECT ProductCode, ProductName\nFROM Products\nORDER BY ListPrice DESC,;");
        app.Press(PhysicalKey.Enter, RawInputModifiers.Control);

        await app.WaitUntil(() => app.Shows("Not quite"), "the result of checking a syntax error");
        app.Screenshot("syntax-error");
        Assert.True(app.ShowsAsFailure("Syntax error on line 3"));

        // Going back to question 3 shows the answer and result the student left there.
        app.Click(app.TreeItem("3. Products priced between 500"));
        Assert.Equal(RightAnswer.ReplaceLineEndings("\n"), app.Editor.Text.ReplaceLineEndings("\n"));
        Assert.True(app.Shows("Correct!"));
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_scrolls_sideways_to_the_last_columns_of_a_wide_result()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "wide-result");
        await ConnectAsync(app);

        // SELECT * returns all eight Products columns, more than fit in the student's half of the side-by-side view.
        app.Click(app.TreeItem("3. Products priced between 500"));
        app.WriteAnswer("SELECT * FROM Products\nWHERE ListPrice > 500 AND ListPrice < 2000\nORDER BY DateAdded DESC;");
        app.Press(PhysicalKey.F5);
        await app.WaitUntil(() => app.Shows("Not quite"), "the result of checking SELECT *");

        app.Click(app.SectionHeader("Show your whole result on course data next to the expected one"));
        app.Screenshot("side-by-side");
        var column = app.Text("DiscountPercent"); // only the student's result has it
        Assert.False(app.IsScrolledIntoView(column));

        app.ScrollRight(column);
        app.Screenshot("scrolled-right");
        Assert.True(app.IsScrolledIntoView(column));
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_watches_a_query_run()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "visualizer");
        await ConnectAsync(app);

        app.Click(app.TreeItem("3. Products priced between 500"));
        app.WriteAnswer(RightAnswer);
        app.Click(app.Button("Visualize"));
        var visualizer = await app.WaitForVisualizerAsync();
        Assert.True(app.VisualizerShows("Products → WHERE → ORDER BY"));
        Assert.True(app.VisualizerShows("Start scan"));
        app.VisualizerScreenshot(visualizer, "start");

        // Step by step: the first row is tested against each condition.
        var next = app.VisualizerButton("Next");
        app.Click(next);
        Assert.True(app.VisualizerShows("WHERE: test row 1 of 10"));
        app.Click(next);
        Assert.True(app.VisualizerShows("Check: ListPrice > 500"));
        app.VisualizerScreenshot(visualizer, "first-condition");

        // The arrow keys step too.
        app.PressIn(visualizer, PhysicalKey.ArrowLeft);
        Assert.True(app.VisualizerShows("WHERE: test row 1 of 10"));

        var animation = (VisualizerViewModel)visualizer.DataContext!;
        animation.Step = animation.Scene.Frames.Count / 2;
        Dispatcher.UIThread.RunJobs();
        app.VisualizerScreenshot(visualizer, "halfway");
        animation.Step = animation.Scene.Frames.Count - 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(app.VisualizerShows("Sorted"));
        Assert.False(next.IsEffectivelyEnabled);
        app.VisualizerScreenshot(visualizer, "sorted");

        // A join, in the same window.
        app.WriteAnswer("SELECT c.CategoryName, p.ProductName, p.ListPrice\nFROM Categories c\nLEFT JOIN Products p ON c.CategoryID = p.CategoryID\nWHERE c.CategoryName <> 'Drums'\nORDER BY p.ListPrice DESC;");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.VisualizerShows("Categories LEFT JOIN Products → WHERE → ORDER BY"), "the join's animation");
        Assert.Same(visualizer, app.Visualizer);
        app.VisualizerScreenshot(visualizer, "join-start");

        var join = (VisualizerViewModel)visualizer.DataContext!;
        join.Step = 9;
        Dispatcher.UIThread.RunJobs();
        app.VisualizerScreenshot(visualizer, "join-forming");

        app.Click(app.VisualizerCheckBox("In-depth"));
        Assert.True(app.VisualizerShows("Ready: test each Categories row against every Products row (4 × 10 comparisons)"));
        app.Click(app.VisualizerButton("Next"));
        Assert.True(app.VisualizerShows("Compare Categories row 1 with each Products row: ON is true for 6, false for 4"));
        app.VisualizerScreenshot(visualizer, "join-in-depth");

        // Esc closes it.
        app.PressIn(visualizer, PhysicalKey.Escape);
        Assert.Null(app.Visualizer);

        // Queries it can't show say why, next to the button.
        app.WriteAnswer("WITH p AS (SELECT * FROM Products) SELECT * FROM p;");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.ShowsSelectableTextStartingWith("The visualizer can't show common table expressions"), "the message about WITH");
        Assert.Null(app.Visualizer);
        app.Screenshot("cant-show");

        // So do mistakes, and a query that works clears the message.
        app.WriteAnswer("SELECT Nope FROM Products;");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.ShowsSelectableTextStartingWith("Line 1: Invalid column name 'Nope'."), "the SQL error");
        app.WriteAnswer(RightAnswer);
        app.Click(app.Button("Visualize"));
        await app.WaitForVisualizerAsync();
        Assert.False(app.ShowsSelectableTextStartingWith("Line 1:"));

        // Scripts that change the database's design have no rows to watch.
        app.Click(app.TreeItem("1. Create the Membership database"));
        Assert.False(app.VisibleButton("Visualize"));
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_watches_rows_go_into_groups()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "visualizer-groups");
        await ConnectAsync(app);

        app.Click(app.TreeItem("2. Product count and top price"));
        app.WriteAnswer("SELECT CategoryName, COUNT(*) AS ProductCount, MAX(ListPrice) AS MostExpensive\nFROM Categories c\nJOIN Products p ON c.CategoryID = p.CategoryID\nGROUP BY CategoryName\nHAVING COUNT(*) > 2\nORDER BY ProductCount DESC;");
        app.Click(app.Button("Visualize"));
        var visualizer = await app.WaitForVisualizerAsync();
        Assert.True(app.VisualizerShows("Categories ⋈ Products → GROUP BY → HAVING → ORDER BY"));

        var animation = (VisualizerViewModel)visualizer.DataContext!;
        var frames = animation.Scene.Frames.ToList();
        void GoTo(string phaseStart, string screenshot)
        {
            animation.Step = frames.FindIndex(f => f.Phase.StartsWith(phaseStart, StringComparison.Ordinal));
            Assert.True(animation.Step >= 0, phaseStart);
            Dispatcher.UIThread.RunJobs();
            app.VisualizerScreenshot(visualizer, screenshot);
        }

        GoTo("Row 5 of 10 goes into the group", "into-groups");
        GoTo("Guitars: 6 rows become one row: ProductCount = 6, MostExpensive = 2517", "summarize");
        GoTo("HAVING: test row 2", "having");
        GoTo("Sorted", "sorted");
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_watches_two_queries_combine()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "visualizer-union");
        await ConnectAsync(app);

        app.Click(app.TreeItem("7. Shipped and unshipped orders"));
        app.WriteAnswer("SELECT 'SHIPPED' AS ShipStatus, OrderID, OrderDate\nFROM Orders\nWHERE ShipDate IS NOT NULL\nUNION\nSELECT 'NOT SHIPPED', OrderID, OrderDate\nFROM Orders\nWHERE ShipDate IS NULL\nORDER BY OrderDate;");
        app.Click(app.Button("Visualize"));
        var visualizer = await app.WaitForVisualizerAsync();

        // Each query plays on its own first.
        var animation = (VisualizerViewModel)visualizer.DataContext!;
        Assert.Equal(["Query 1: Orders → WHERE", "Query 2: Orders → WHERE", "Combine: UNION → ORDER BY"], animation.Scenes.Select(s => s.Title));
        Assert.True(app.VisualizerShows("Query 1: Orders → WHERE"));
        Assert.True(app.VisualizerShows("This runs each query on its own, then combines their rows. Choose a step from the list at the top."));
        app.VisualizerScreenshot(visualizer, "first-query");

        // Then the rows come together.
        animation.Scene = animation.Scenes[^1];
        Dispatcher.UIThread.RunJobs();
        Assert.True(app.VisualizerShows("Combine: UNION → ORDER BY"));
        Assert.Equal(0, animation.Step);
        app.Click(app.VisualizerButton("Next"));
        app.Click(app.VisualizerButton("Next"));
        app.VisualizerScreenshot(visualizer, "combining");

        animation.Step = animation.Scene.Frames.Count - 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(app.VisualizerShows("Sorted"));
        app.VisualizerScreenshot(visualizer, "sorted");

        // EXCEPT looks rows up instead of bringing them across.
        app.WriteAnswer("SELECT CategoryID FROM Categories\nEXCEPT\nSELECT CategoryID FROM Products;");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.VisualizerShows("Query 1: Categories"), "the EXCEPT's animation");
        var except = (VisualizerViewModel)visualizer.DataContext!;
        except.Scene = except.Scenes[^1];
        Dispatcher.UIThread.RunJobs();
        Assert.True(app.VisualizerShows("Combine: EXCEPT"));
        except.Step = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("✗ Query 1 row 1 of 4 is also in Query 2", except.Phase);
        app.VisualizerScreenshot(visualizer, "except");
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_watches_rows_change()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "visualizer-changes");
        await ConnectAsync(app);

        // An UPDATE, on the table an earlier question's solution set up. Only the visualizer's copy changes, and only until it finishes.
        app.Click(app.TreeItem("4. Change default account 400"));
        app.WriteAnswer("UPDATE VendorCopy\nSET DefaultAccountNo = 403\nWHERE DefaultAccountNo = 400;");
        app.Click(app.Button("Visualize"));
        var visualizer = await app.WaitForVisualizerAsync();
        Assert.True(app.VisualizerShows("UPDATE VendorCopy"));

        var animation = (VisualizerViewModel)visualizer.DataContext!;
        var updated = animation.Scene.Frames.ToList().FindIndex(f => f.Phase.StartsWith("✓ Updated: DefaultAccountNo 400 → 403", StringComparison.Ordinal));
        Assert.True(updated > 0);
        animation.Step = updated;
        Dispatcher.UIThread.RunJobs();
        app.VisualizerScreenshot(visualizer, "update");
        animation.Step = animation.Scene.Frames.Count - 1;
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("UPDATE complete:", animation.Phase);
        app.VisualizerScreenshot(visualizer, "update-done");

        // A script with two statements gets a choice of statement.
        app.Click(app.TreeItem("6. Delete category 4"));
        app.WriteAnswer("DELETE Products\nWHERE CategoryID = 4;\n\nDELETE Categories\nWHERE CategoryID = 4;");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.VisualizerShows("DELETE Products"), "the first DELETE's animation");
        var deletes = (VisualizerViewModel)visualizer.DataContext!;
        Assert.Equal(2, deletes.Scenes.Count);
        deletes.Step = deletes.Scene.Frames.Count - 1;
        Dispatcher.UIThread.RunJobs();
        app.VisualizerScreenshot(visualizer, "delete");

        deletes.Scene = deletes.Scenes[1];
        Dispatcher.UIThread.RunJobs();
        Assert.True(app.VisualizerShows("DELETE Categories"));
        Assert.Equal(0, deletes.Step);

        // An INSERT shows the values SQL Server fills in.
        app.Click(app.TreeItem("1. Add the Brass category"));
        app.WriteAnswer("INSERT INTO Categories (CategoryName)\nVALUES ('Brass');");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.VisualizerShows("INSERT Categories"), "the INSERT's animation");
        var insert = (VisualizerViewModel)visualizer.DataContext!;
        Assert.Equal("Add row 1 of 1. SQL Server fills in CategoryID", insert.Scene.Frames[1].Phase);
        insert.Step = 1;
        Dispatcher.UIThread.RunJobs();
        app.VisualizerScreenshot(visualizer, "insert");

        // A mistake is reported the way a check reports it.
        app.WriteAnswer("DELETE Categories WHERE CategoryID = 1;");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.ShowsSelectableTextStartingWith("Line 1:"), "the foreign key error");

        // Scripts with nothing to show say so.
        app.WriteAnswer("SELECT * INTO CategoryCopy FROM Categories;");
        app.Click(app.Button("Visualize"));
        await app.WaitUntil(() => app.ShowsSelectableTextStartingWith("The visualizer shows INSERT, UPDATE and DELETE"), "the message about SELECT INTO");
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Long_tables_keep_the_current_row_in_view()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "visualizer-long");
        await ConnectAsync(app);

        app.Click(app.TreeItem("1. Vendors joined with their invoices"));
        app.WriteAnswer("SELECT v.VendorName, i.InvoiceNumber, i.InvoiceTotal\nFROM Vendors v\nJOIN Invoices i ON v.VendorID = i.VendorID\nWHERE i.InvoiceTotal > 500\nORDER BY v.VendorName;");
        app.Click(app.Button("Visualize"));
        var visualizer = await app.WaitForVisualizerAsync();
        app.VisualizerScreenshot(visualizer, "start");

        var animation = (VisualizerViewModel)visualizer.DataContext!;
        animation.Step = animation.Scene.Frames.Count / 3;
        Dispatcher.UIThread.RunJobs();
        app.VisualizerScreenshot(visualizer, "a-third-in");
        animation.Step = animation.Scene.Frames.Count * 3 / 4;
        Dispatcher.UIThread.RunJobs();
        app.VisualizerScreenshot(visualizer, "three-quarters-in");

        app.Click(app.VisualizerButton("Fit"));
        app.VisualizerScreenshot(visualizer, "fit");
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_works_through_DML_questions()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "dml-journey");
        await ConnectAsync(app);

        // Exercise 6 as the book expects students to first try it: the DELETE fails because products still use category 4.
        app.Click(app.TreeItem("6. Delete category 4"));
        app.WriteAnswer("DELETE Categories\nWHERE CategoryID = 4;");
        app.Press(PhysicalKey.F5);

        await app.WaitUntil(() => app.Shows("Not quite"), "the result of deleting the category alone");
        app.Screenshot("foreign-key-error");
        Assert.True(app.ShowsTextStartingWith("Your script failed to run: Line 1: The DELETE statement conflicted with the REFERENCE constraint"));

        // Delete the products first, as the exercise says.
        app.WriteAnswer("DELETE Products\nWHERE CategoryID = 4;\n\nDELETE Categories\nWHERE CategoryID = 4;");
        app.Click(app.Button("Check (F5)"));

        await app.WaitUntil(() => app.Shows("Correct!"), "the result of the fixed script");
        app.Screenshot("fixed");
        Assert.True(app.Shows("Your script leaves every table as expected on all 2 datasets."));
        Assert.Equal("✓", app.TreeMark("6. Delete category 4"));

        // Exercise 2 tells the student which ID the earlier exercise's row has, and shows table differences when wrong.
        app.Click(app.TreeItem("2. Rename Brass to Woodwinds"));
        Assert.True(app.ShowsTextStartingWith("Note: In the validator's copy of the database, the Brass row from exercise 1 has CategoryID 5."));
        app.WriteAnswer("UPDATE Categories\nSET CategoryName = 'Woodwinds'\nWHERE CategoryID = 4;");
        app.Press(PhysicalKey.F5);

        await app.WaitUntil(() => app.Shows("Not quite"), "the result of renaming the wrong category");
        app.Screenshot("wrong-row");
        Assert.True(app.ShowsTextStartingWith("Missing from Categories (2)"));
        Assert.True(app.ShowsTextStartingWith("In Categories but not expected (2)"));
        Assert.True(app.Shows("Woodwinds"));
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Student_works_through_a_database_scripting_question()
    {
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "ddl-journey");
        await ConnectAsync(app);

        // The whole script a student usually writes, including the database statements the validator skips.
        // Dues > 0 contradicts the default of 0, which one of the question's probes shows.
        const string withMistake = """
            USE master;
            GO
            CREATE DATABASE Membership;
            GO
            USE Membership;
            CREATE TABLE Individuals (IndividualID int IDENTITY PRIMARY KEY, FirstName varchar(50), LastName varchar(50), Address varchar(100), Phone varchar(20));
            CREATE TABLE Groups (GroupID int IDENTITY PRIMARY KEY, GroupName varchar(50), Dues money NOT NULL DEFAULT 0 CHECK (Dues > 0));
            CREATE TABLE GroupMembership (GroupID int REFERENCES Groups (GroupID), IndividualID int REFERENCES Individuals (IndividualID));
            """;

        app.Click(app.TreeItem("2. Create the Membership tables"));
        app.WriteAnswer(withMistake);
        app.Press(PhysicalKey.F5);

        await app.WaitUntil(() => app.Shows("Not quite"), "the result of the script with Dues > 0");
        app.Screenshot("check-contradicts-default");
        Assert.True(app.ShowsTextStartingWith("Note: Skipped 'CREATE DATABASE Membership'"));
        Assert.True(app.ShowsTextStartingWith("Adding a group without dues (they should default to 0): this should work, but with your design it fails"));

        app.WriteAnswer(withMistake.Replace("CHECK (Dues > 0)", "CHECK (Dues >= 0)"));
        app.Press(PhysicalKey.F5);

        await app.WaitUntil(() => app.Shows("Correct!"), "the result of the fixed script");
        app.Screenshot("fixed");
        Assert.True(app.Shows("Your design matches the expected one."));
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task A_wrong_password_says_the_login_was_rejected()
    {
        var server = TestServer();
        var app = AppDriver.Launch(new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder()), "wrong-password");

        app.Fill(app.TextBox("Server"), server.DataSource);
        UseSqlAuthentication(app, "hv_no_such_login", "wrong");
        app.Press(PhysicalKey.Enter); // Connect is the default button

        await app.WaitUntil(() => app.ShowsTextStartingWith($"{server.DataSource} rejected the login."), "the login error");
        app.Screenshot("error");
        Assert.True(app.ShowsAsFailure($"{server.DataSource} rejected the login."));
    }

    [AvaloniaFact]
    [Trait("Category", "NeedsSqlServer")]
    public async Task Answers_and_progress_are_still_there_after_restarting_the_app()
    {
        var folder = TestAppBuilder.NewTempFolder();

        // First session: answer question 3 correctly, then close the app.
        var first = AppDriver.Launch(new MainViewModel(Content, appDataFolder: folder), "restart-before");
        await ConnectAsync(first);
        first.Click(first.TreeItem("3. Products priced between 500"));
        first.WriteAnswer(RightAnswer);
        first.Press(PhysicalKey.F5);
        await first.WaitUntil(() => first.Shows("Correct!"), "the first session's check");
        first.Window.Close();

        // Second session: the server is remembered, and question 3 still has its mark and answer.
        var second = AppDriver.Launch(new MainViewModel(Content, appDataFolder: folder), "restart-after");
        Assert.Equal(TestServer().DataSource, second.TextBox("Server").Text);
        await ConnectAsync(second);
        Assert.Equal("✓", second.TreeMark("3. Products priced between 500"));

        second.Click(second.TreeItem("3. Products priced between 500"));
        second.Screenshot("reopened");
        Assert.Equal(RightAnswer.ReplaceLineEndings("\n"), second.Editor.Text.ReplaceLineEndings("\n"));

        // Change connection goes back to the connection page with the server filled in.
        second.Click(second.Button("Change connection"));
        Assert.True(second.Shows("Connect to your SQL Server"));
        Assert.Equal(TestServer().DataSource, second.TextBox("Server").Text);
    }

    /// <summary>Fills in the connection page from HV_TEST_CONNECTION_STRING, connects, and waits for the databases.</summary>
    private static async Task ConnectAsync(AppDriver app)
    {
        var server = TestServer();
        app.Fill(app.TextBox("Server"), server.DataSource);
        if (server.IntegratedSecurity)
            app.Click(app.RadioButton("Windows authentication"));
        else
            UseSqlAuthentication(app, server.UserID, server.Password);

        app.Click(app.Button("Connect"));
        await app.WaitUntil(() => app.Shows("Databases ready"), "the workspace to open and the databases to be ready", timeoutSeconds: 180);
    }

    private static void UseSqlAuthentication(AppDriver app, string user, string password)
    {
        if (OperatingSystem.IsWindows())
            app.Click(app.RadioButton("SQL Server authentication"));
        app.Fill(app.TextBox("Login"), user);
        app.Fill(app.TextBox("Password"), password);
    }

    private static SqlConnectionStringBuilder TestServer()
    {
        var connectionString = Environment.GetEnvironmentVariable("HV_TEST_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "HV_TEST_CONNECTION_STRING isn't set.");
        return new SqlConnectionStringBuilder(connectionString);
    }
}
