using Avalonia.Headless.XUnit;
using Avalonia.Input;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.Core.Content;
using Microsoft.Data.SqlClient;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// A student using the app from start to finish, with only mouse clicks and key presses.
/// Each step saves a screenshot to bin/.../screenshots/ so a person can see what the student saw.
/// </summary>
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
