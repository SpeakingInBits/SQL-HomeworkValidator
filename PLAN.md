# SQL Homework Validator: Plan

A cross-platform desktop app that lets students check their SQL homework against the instructor's solutions before submitting. The course targets SQL Server.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| App type | Desktop app | Students already run their own SQL Server; no hosting needed. |
| Language | C# on .NET 10 | Instructor's main language. Only .NET has Microsoft's official SQL Server driver and T-SQL parser. |
| UI framework | Avalonia | Mature, WPF-style XAML, solid on Windows, macOS and Linux. |
| Database engine | Student's own SQL Server, chosen in a connection dialog | SQL Server can't be embedded and has no native macOS build. Windows students already install SQL Server Developer on `localhost`; Mac/Linux students run the Docker image. |
| Checking method | Compare result sets against several datasets with the same schema | Queries can be written many ways; one dataset lets wrong queries pass by coincidence. |
| Reference solutions | Shipped inside the app, in the same repo | Students are beginners and answers are available online and from AI anyway, so hiding them isn't worth the complexity. |
| Expected results | Computed live by running the reference solution next to the student's | No build step, no stored results to keep in sync, and questions using `GETDATE()` work because both queries run at the same moment. |
| T-SQL parsing | `Microsoft.SqlServer.TransactSql.ScriptDom` | Microsoft's official parser. Needed for the database-rename safety step and for rules like "must use a JOIN". |
| SQL Server driver | `Microsoft.Data.SqlClient` | Official, cross-platform. |
| SQL editor | AvaloniaEdit | SQL syntax highlighting, line numbers. |

## Repository layout

```
src/
  HomeworkValidator.Core/        # all checking logic; no UI
  HomeworkValidator.Cli/         # hv: instructor tooling + batch checking
  HomeworkValidator.App/         # Avalonia desktop app (thin layer over Core)
tests/
  HomeworkValidator.Core.Tests/  # unit tests: comparison, analysis, content
                                 # (CI runs `hv verify` against a real SQL Server for the rest)
content/
  databases/
    MyGuitarShop/
      original.sql        # course script as students get it (reference only; the app never runs it)
      schema.sql          # tables and constraints only, no database-level statements
      datasets/
        base.sql          # the course data
        edge.sql          # :r base.sql, then rows that catch common mistakes
        shifted.sql       # generated from base.sql by hv make-shifted
        hints.yaml        # default hint per dataset
    AP/
      ...same layout
  assignments/
    ap-hw1/
      assignment.yaml     # title and database
      q5.yaml             # prompt, compare options, rules, hints
      q5.sql              # reference solution
      q5.mutants/         # known-wrong answers that must fail
        exclusive-range.sql
        no-filter.sql
```

The desktop app only collects input and shows results; Core does the work. That keeps the logic testable and gives the instructor a command-line tool from the same code. `content/` is embedded in the app at build time.

## Connecting to SQL Server

- **Windows default:** `localhost`, Windows authentication. Offer a list of local instances for named installs such as `localhost\SQLEXPRESS`.
- **Mac/Linux default:** `localhost,1433`, SQL authentication as `sa`.
- **Trust server certificate:** on by default. SqlClient encrypts by default, and local installs and the Docker image use self-signed certificates.
- **Password storage:** the OS keychain (Windows Credential Manager, macOS Keychain, Secret Service on Linux), or not saved.
- **Test connection:** also confirms the login can create databases.

## Course databases

Both scripts start with `USE master; DROP DATABASE …; CREATE DATABASE …`. Students already have databases with these exact names, so **the app never runs the original scripts**. Each is split into `schema.sql` (tables, keys, defaults, foreign keys) and `datasets/base.sql` (the data), with no database-level statements, and the app runs them inside databases it created.

### MyGuitarShop

- Tables: Categories, Products, Customers, Addresses, Orders, OrderItems, Administrators.
- Base data: 4 categories, 10 products, about 485 customers and 510 addresses, 41 orders, 47 order items, 3 administrators. Order dates are in 2016.
- Already useful for catching mistakes: the Keyboards category has no products, most customers have no orders, and 5 orders have no ship date.
- Notes: `Products.Description` uses the deprecated `TEXT` type (can't be compared with `=` or used in `GROUP BY`/`DISTINCT`). Some product descriptions contain `?` where the original had dashes; the data is kept as is. The script ends with a commented-out MySQL `GRANT`, which is ignored.

### AP (Accounts Payable)

- Tables: Vendors, Invoices, InvoiceLineItems, Terms, GLAccounts, ContactUpdates, InvoiceArchive.
- Base data: 122 vendors, 114 invoices, 118 line items, 5 terms, 75 GL accounts, 8 contact updates. InvoiceArchive is empty. Invoice dates are in 2022 and 2023; 11 invoices are unpaid.
- Already useful for catching mistakes: many vendors have no invoices; unpaid invoices have a NULL `PaymentDate`.
- Foreign keys: `InvoiceLineItems → Invoices` cascades on delete, which matters for DELETE questions.
- The original script was UTF-16; the repo copy is UTF-8.

## Databases the app manages

The app creates and owns its own databases. It never touches the student's working databases.

- Name pattern: `HV_<CourseDb>_<dataset>`, e.g. `HV_MyGuitarShop_base`, `HV_AP_edge`.
- Built from `schema.sql` plus one dataset script.
- Each database records the content version in an extended property, so the app rebuilds it when an app update changes the schema or data.
- Throwaway databases for DML/DDL checks use `HV_scratch_<random>` and are dropped after each check (and cleaned up at startup if a previous run crashed).

### Datasets

Every course database gets three datasets with the same schema. Students can see all of them, and every check shows full differences, since hiding answers isn't a goal.

- **base:** the course data, unchanged.
- **edge:** base data plus rows designed to catch common mistakes. It starts with `:r base.sql` (sqlcmd's include syntax), so it never goes out of date with base.
- **shifted:** base data with every value in a column whose name ends in `ID` reversed (new ID = 10000 − old ID). Keys and foreign keys still line up, but hardcoded IDs fail, and rows no longer come back in the course data's order by accident. It's generated by `hv make-shifted <database>`; a unit test fails if base changes without regenerating it. A question that names specific IDs should leave it out of its `datasets`.

What **edge** adds so far:

| Mistake it catches | MyGuitarShop | AP |
|---|---|---|
| Boundary values (`>` vs `>=`, `BETWEEN`) | Products priced exactly 500 and 2000; an item whose total is exactly 500; a customer whose last name is exactly "M" | Invoices totaling exactly 500, 10,000, 499.99 and 10,000.01; a balance due of exactly 1,000 |
| Logic in compound conditions | — | Both kinds of invalid payment date; an invoice paid entirely by a credit |
| Sorting ties | Two Nguyens, inserted Zed before Amy | Two Ellis contacts, inserted Zoe before Adam |
| NULL handling | A product with no date added | A vendor with no address line 2 or phone |
| `INNER` vs `LEFT JOIN` | A product with no category | Terms that no invoice uses |
| Dates | Orders at 23:59:59 on New Year's Eve and at midnight on New Year's Day | — |
| Strings | O'Brien | — |

Add rows as new assignments need them (for example invoices with several line items for `COUNT` vs `SUM`, or InvoiceArchive rows for INSERT questions). Keep every row in edge consistent with the course's foreign keys, and avoid NULLs where the question doesn't say how to handle them (a NULL contact name would make `+` and `CONCAT` give different answers, and both would be reasonable).

Each question lists which datasets it runs on and can give a hint per dataset. `datasets/hints.yaml` holds a default hint per dataset for questions that don't have their own.

Note for DML questions: `Vendors.DefaultTermsID` defaults to 3, which doesn't exist in shifted (it's 9997 there), so an INSERT that relies on the default fails on shifted.

## Question kinds and how each is checked

Each question declares `kind: select | dml | ddl`. In every case the student's SQL and the reference solution run the same way, and their outcomes are compared.

### `select`: queries, and calls to views/procedures/functions

1. Static checks with ScriptDom (allowed statement types, required/forbidden constructs).
2. Run the student's query and the reference solution on each dataset's shared database, read-only, with a timeout and row cap.
3. Compare the two result sets.

### `dml`: INSERT, UPDATE, DELETE

1. Static checks.
2. For each dataset, build two fresh throwaway databases (or restore from a snapshot; all students have Developer edition): one for the student's script, one for the reference solution.
3. Run each script as written, including any `BEGIN TRAN`/`COMMIT` of their own.
4. Compare the final contents of the tables the question targets, confirm all other tables match too, and optionally compare rows affected.
5. Drop the throwaway databases.

Fresh databases are used instead of wrapping the script in a transaction, because students' own transaction statements interfere with an outer one. The reference solution's result can be cached per dataset and content version so it isn't rebuilt every time.

### `ddl`: database scripting (CREATE/ALTER DATABASE, TABLE, constraints, views, procedures)

1. **Rename step (safety-critical).** Parse with ScriptDom and replace every database name with the throwaway name: `CREATE`/`DROP`/`ALTER DATABASE`, `USE`, three-part names, and string literals in `DB_ID('…')`, `DATABASEPROPERTYEX('…')` and similar. If the script contains anything that can't be renamed reliably (dynamic SQL via `EXEC('…')` or `sp_executesql`, unknown functions taking a database name), **refuse to run it** and explain why. Without this step, a typical `IF DB_ID('X') IS NOT NULL DROP DATABASE X` would delete the student's own work.
2. Run the rewritten student script and the rewritten reference solution in separate throwaway databases.
3. **Schema comparison** from the system catalog: tables, columns, types and lengths, nullability, identity, primary keys, foreign keys (including cascade rules), unique and check constraints, defaults. Strictness is configurable per question (constraint names ignored by default; column order optional).
4. **Behavior tests:** instructor-written inserts that must succeed or must fail, run against both databases. These catch constraints that exist but are written wrong.
5. Run any `select`-style checks the question defines against the new objects (views, procedures, functions).
6. Drop the throwaway databases.

## Comparing result sets

Per-question options:

- `ordered`: compare row order only when the question asks for an `ORDER BY`. Otherwise compare as a multiset.
- `columns`: `by-position` (default) or `by-name` (when the question specifies aliases).
- `distinct`: whether duplicate rows matter.
- `decimals`: round numbers to N places before comparing.
- Dates normalized to a single format; strings compared with a configurable case/trailing-space policy.

`TOP` without `ORDER BY` and `NEWID()` are non-deterministic and should be avoided in questions. `GETDATE()` is fine because both queries run at the same moment.

## Running student SQL safely

- Only against databases the app created.
- Query timeout and row cap on every run.
- `select` questions run as a low-privilege database user (`EXECUTE AS USER`), which confines them to that database.
- Statement types are limited per question kind by ScriptDom before anything runs.
- The rename step above for `ddl`.

## Question format

Each assignment folder has an `assignment.yaml` (`title`, `database`) and one `q<n>.yaml` per question. Question IDs are `<assignment>/q<n>`, e.g. `mgs-hw1/q2`.

```yaml
title: Customer full names, M to Z
prompt: |
  Write a SELECT statement that returns one column ...
kind: select                     # default
datasets: [base, edge, shifted]  # default: all of the database's datasets
compare:
  columns: by-name               # default; or by-position, or row-count when the question doesn't name columns
  decimals: 2                    # round numbers before comparing
  order:                         # sort keys, by result column; omit for unordered questions
    - column: FullName
      keyPattern: "^[^,]*"       # optional: only this part of the value is the key (here, the last name)
rules:
  require: [Where]
  forbid: [From, AliasWithoutAs]
hints:
  edge: Check how your query treats products with no category.
```

The reference solution sits next to it as `q<n>.sql`, and known-wrong answers go in `q<n>.mutants/*.sql`.

**Ordered comparison and ties.** Rows are first compared as a multiset. Then, for ordered questions, the sequence of sort-key values must match the reference's. Rows that tie on the sort keys can therefore come in any order, which is what SQL Server itself guarantees. (MyGuitarShop has two products at 799.99, for example.)

**Rules** available: `From`, `Where`, `Join` (including `FROM a, b`), `Subquery`, `Cte`, `OrderBy` (the statement's own, not `OVER (ORDER BY …)`), `GroupBy`, `Having`, `Distinct`, `Top`, `SelectStar`, `Union`, `AliasWithoutAs`.

### Instructor commands

- `hv verify`: runs every reference solution on every dataset (catching broken solutions), then every mutant, and fails if any mutant passes. A passing mutant means the datasets are too weak for that question.
- `hv check <question> <file.sql>`: what the app does, from the command line (handy for batch-checking submissions).

## Screens

1. **Connection:** server, authentication, test button.
2. **Setup status:** which `HV_` databases exist and are current; build/rebuild button with progress.
3. **Assignments:** list of assignments and questions with pass/fail status.
4. **Question:** prompt, SQL editor (paste, or open a `.sql` file and reload on change), Check button.
5. **Results:** pass/fail per dataset; side-by-side expected vs. actual with differing rows highlighted; hints; rule violations. Optionally a "Show solution" button (an instructor setting per assignment, e.g. after the due date).

## Distribution

- Self-contained single-file builds for `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, with trimming and single-file compression.
- **macOS:** unsigned apps are blocked by Gatekeeper. Either sign and notarize (Apple Developer account, $99/year) or give students instructions to allow the app.
- **Windows:** unsigned apps show a SmartScreen warning. Code signing removes it.

## Milestones

1. **Core + CLI, SELECT only** (done): split both course scripts into schema and base data, build `HV_` databases, run a student query and the reference solution, compare, report. Question format, `hv verify` with mutants, and the first homework for each database (`ap-hw1`, `mgs-hw1`).
2. **Edge and shifted datasets** (done) for both databases. `hv verify` passes: all 43 mutants for `ap-hw1` and `mgs-hw1` are caught, including the four the base data couldn't catch. CI (GitHub Actions, added in its own PR) builds, runs the unit tests and runs `hv verify` against SQL Server in a container.
3. **Avalonia app for SELECT:** connection dialog, setup status, question list, editor, results with differences.
4. **DML questions:** throwaway databases, table-state comparison.
5. **DDL questions:** rename step, schema comparison, behavior tests.
6. **Packaging:** builds per platform, signing, install instructions for students.

## Open questions

- **Assignments:** the next homework for each database, as it's ready.
- **Content updates:** ship new assignments only with app releases, or let the app download content separately?
- **Grading use:** should `hv check` also batch-grade a folder of submissions?
- **Signing:** whether to pay for macOS and Windows code signing.
- **Repository visibility:** the repo is private. The course scripts and data come from the course textbook's materials; check their license before making it public.
