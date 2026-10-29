# SQL Homework Validator

A cross-platform desktop app that lets students check their SQL Server homework against the instructor's solutions. Students connect it to their own SQL Server (a local install on Windows, Docker on macOS and Linux), and it compares their query results against the reference solution on several test datasets.

Status: the command-line tool `hv` checks SELECT questions. The desktop app comes later. See [PLAN.md](PLAN.md).

## Trying it

Requires the .NET 10 SDK and a SQL Server you can create databases on.

```bash
dotnet build
cd src/HomeworkValidator.Cli/bin/Debug/net10.0
./hv setup                              # creates HV_AP_base and HV_MyGuitarShop_base
./hv list                               # assignments and question IDs
./hv show ap-hw1/q2                     # print a question
./hv check ap-hw1/q2 my-answer.sql      # check an answer
./hv verify                             # instructor: check every solution and mutant
./hv clean                              # drop the validator's databases
```

By default `hv` connects to `localhost` with Windows authentication on Windows, and to `localhost,1433` as `sa` elsewhere. Use `--server`, `--user` and `--password` (or `HV_PASSWORD`), or a full `--connection` string (or `HV_CONNECTION_STRING`).

The validator only creates and drops databases whose names start with `HV_` and that it marked as its own. It never runs the original course scripts, which drop the `AP` and `MyGuitarShop` databases students work in.

## Adding questions

Questions live in `content/assignments/<assignment>/`. See "Question format" in [PLAN.md](PLAN.md). After adding or changing one, run `hv verify <assignment>`.
