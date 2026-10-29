# SQL Homework Validator

A cross-platform desktop app that lets students check their SQL Server homework against the instructor's solutions. Students connect it to their own SQL Server (a local install on Windows, Docker on macOS and Linux), and it compares their query results against the reference solution on several test datasets.

Status: SELECT questions can be checked in the desktop app or with the command-line tool `hv`. See [PLAN.md](PLAN.md).

## Trying it

Requires the .NET 10 SDK and a SQL Server you can create databases on.

### Desktop app

```bash
dotnet run --project src/HomeworkValidator.App
```

Connect to your server, pick a question, write a query and press F5 (or Check). The first connection builds the validator's databases, which takes a few seconds. Answers and pass/fail status are saved automatically in your user profile (`%APPDATA%\HomeworkValidator` on Windows, `~/.config/HomeworkValidator` elsewhere); the password is never saved.

### Command line

```bash
dotnet build
cd src/HomeworkValidator.Cli/bin/Debug/net10.0
./hv setup                              # creates HV_AP_base, HV_AP_edge, HV_AP_shifted and the same for MyGuitarShop
./hv list                               # assignments and question IDs
./hv show ap-hw1/q2                     # print a question
./hv check ap-hw1/q2 my-answer.sql      # check an answer
./hv verify                             # instructor: check every solution and mutant
./hv clean                              # drop the validator's databases
```

By default `hv` connects to `localhost` with Windows authentication on Windows, and to `localhost,1433` as `sa` elsewhere. Use `--server`, `--user` and `--password` (or `HV_PASSWORD`), or a full `--connection` string (or `HV_CONNECTION_STRING`).

The validator only creates and drops databases whose names start with `HV_` and that it marked as its own. It never runs the original course scripts, which drop the `AP` and `MyGuitarShop` databases students work in.

## Tests

```bash
dotnet test
```

The desktop app's tests render windows headlessly and save screenshots to `tests/HomeworkValidator.App.Tests/bin/<config>/net10.0/screenshots/`. Its end-to-end test runs only when `HV_TEST_CONNECTION_STRING` is set, for example `Server=localhost;Integrated Security=true;TrustServerCertificate=true`.

## Adding questions

Questions live in `content/assignments/<assignment>/`. See "Question format" in [PLAN.md](PLAN.md). After adding or changing one, run `hv verify <assignment>`. If one of your known-wrong answers passes, add rows to the database's `datasets/edge.sql` that tell it apart.

If you change a database's `datasets/base.sql`, regenerate its shifted dataset from the repository root:

```bash
dotnet run --project src/HomeworkValidator.Cli -- make-shifted AP
```
