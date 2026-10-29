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
  HomeworkValidator.App.Tests/   # headless UI tests with screenshots; one end-to-end test against SQL Server
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

The desktop app only collects input and shows results; Core does the work. That keeps the logic testable and gives the instructor a command-line tool from the same code. `content/` is copied next to both executables at build time.

## Connecting to SQL Server

- **Windows default:** `localhost`, Windows authentication. Offer a list of local instances for named installs such as `localhost\SQLEXPRESS`.
- **Mac/Linux default:** `localhost,1433`, SQL authentication as `sa`.
- **Trust server certificate:** on by default. SqlClient encrypts by default, and local installs and the Docker image use self-signed certificates.
- **Password storage:** not saved (milestone 3). Students on Windows use Windows authentication and don't need one; Docker users type the sa password each time. Saving it in the OS keychain (Windows Credential Manager, macOS Keychain, Secret Service on Linux) can come later if that's annoying.
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
- DML and DDL checks run inside a rolled-back transaction on these databases, so no throwaway databases are needed.

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

### `dml`: INSERT, UPDATE, DELETE, SELECT INTO, DROP TABLE

1. **Static checks.** Allowed statements:
   - INSERT, UPDATE, DELETE, MERGE and SELECT, including SELECT INTO
   - DROP TABLE, TRUNCATE TABLE and CREATE TABLE
   - IF and BEGIN…END blocks
   - variables, PRINT, SET options and SET IDENTITY_INSERT
   - the student's own BEGIN TRAN, COMMIT and ROLLBACK

   Anything that could reach outside the database is refused: EXEC, database statements, logins, and database names in object names. `USE` statements are blanked out, keeping line numbers. GO batches run separately.
2. For each dataset, on the validator's existing `HV_` database, inside a transaction that's always rolled back:
   1. **Reset every identity counter** to the table's current maximum. Rolled-back INSERTs still use up identity values, so without this the reference and the student would get different IDs.
   2. **Run the question's setup:** the reference solutions of the questions it runs `after` (recursively, with their own setup scripts), then its own `q<n>.setup.sql`.
   3. **Run the script.** Student scripts run as `hv_writer`, a database user that can read, write and create or drop tables in that database only.
   4. **Read every table,** then roll back.
3. **Compare every table** with the state after the reference solution, which is cached per dataset. Each table's rows are compared as a multiset, with columns matched by name. A missing table or one left behind fails. The first differing table is shown in detail and any others are named.

This is much faster than building fresh databases: all 18 DML questions and their mutants verify in a few seconds. If a student's own `COMMIT` or `ROLLBACK` ends the validator's transaction, their changes may have been saved, so the database is rebuilt afterwards and the student gets a note saying so.

**Questions that build on earlier exercises** (VendorCopy from exercise 1, the Brass row from exercise 1) list them in `after`. When the student's own database could give a row a different ID than the validator's copy (for example, if they ran exercise 1 twice), the question's `note` says which ID to use, and the question runs only on datasets where that ID holds.

### `ddl`: database scripting (CREATE DATABASE, CREATE/ALTER TABLE, constraints, indexes)

1. **Database-level statements are never run (safety-critical).** Statements that name the question's own database are recognized and blanked out, with a note to the student: `CREATE`, `ALTER` and `DROP DATABASE`, and an `IF` around them such as `IF DB_ID('Membership') IS NOT NULL DROP DATABASE Membership`. Every `USE` is blanked out too. A database statement naming any other database is refused, as are EXEC, logins and three-part names.

   This replaced the original plan to rename database names to a throwaway database. With nothing at the database level ever running, a student's `DROP DATABASE` can't touch their work, and there's no renaming to get wrong. `CREATE DATABASE Membership` itself is checked with the `CreateDatabase` rule.
2. **Run like DML:** in the validator's copy of the question's database (Membership starts empty), inside a rolled-back transaction, after the `after` questions' solutions. Table-level DDL can be rolled back in SQL Server. The restricted user also has `REFERENCES`, so it can create foreign keys.
3. **Schema comparison** from the system catalog:
   - tables and columns
   - types, including lengths (lengths can be ignored with `ignoreLengths` when the design doesn't give them)
   - nullability (can be ignored with `ignoreNullability` when the question leaves it to the student)
   - identity and whether a column has a default
   - the primary key
   - unique constraints and unique indexes, compared together
   - foreign keys, including cascade rules
   - other indexes, clustered or not
   - the number of check constraints per table

   Constraint and index names are ignored.
4. **Probes:** short scripts in the question, such as "add a group with negative dues", run against both designs, each inside its own savepoint. Each must succeed or fail the same way and return the same rows. This is how check constraints and default values are tested, since the same rule can be written many ways.
5. **The data** is compared as for DML, which catches a script that deleted rows to get a constraint added.

The first of these that differs is shown, so the student sees the most basic problem first.

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
- `select` questions run as a read-only database user, and `dml` and `ddl` questions as a read-write one that can also create and change tables (`EXECUTE AS USER`). Both are confined to that database.
- `dml` and `ddl` scripts run inside a transaction that's always rolled back.
- Statement types are limited per question kind by ScriptDom before anything runs.
- Database-level statements in `ddl` scripts are skipped, never run.

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

The reference solution sits next to it as `q<n>.sql`, and known-wrong answers go in `q<n>.mutants/*.sql`. Other correct answers, for questions that can be read more than one way, go in `q<n>.alternatives/*.sql`; an answer passes if it matches the solution or any alternative.

DML questions add:

```yaml
kind: dml
after: [q1]                      # run these questions' reference solutions first
note: >-                         # shown under the prompt
  In the validator's copy of the database, the Brass row from exercise 1 has CategoryID 5.
compare:
  presenceOnly: [Products.DateAdded]   # only check set vs NULL (dates from GETDATE() or typed by hand)
```

`q<n>.setup.sql` runs after the `after` questions and before the answer. For example, `ap-hw2/q1` uses it to create stale VendorCopy and InvoiceCopy tables, so the answer has to drop them.

DDL questions can also use `after`, `note` and setup scripts, and add:

```yaml
kind: ddl
database: AP                     # optional: a different database than the assignment's
compare:
  ignoreLengths: true            # varchar(50) and varchar(100) match
  ignoreNullability: true        # the question lets the student decide
probes:
  - name: Adding a group with negative dues
    sql: |
      INSERT Groups (GroupName, Dues) VALUES ('Debt Club', -5);
  - name: Adding a group without dues (they should default to 0)
    sql: |
      INSERT Groups (GroupName) VALUES ('Book Club');
      SELECT Dues FROM Groups WHERE GroupName = 'Book Club';
```

Probe names are shown to the student when a probe behaves differently, so write them as what's being tried. Probes should supply every column, since students may choose different nullability, and should look rows up by name rather than ID, since students may pick different identity seeds.

**Ordered comparison and ties.** Rows are first compared as a multiset. Then, for ordered questions, the sequence of sort-key values must match the reference's. Rows that tie on the sort keys can therefore come in any order, which is what SQL Server itself guarantees. (MyGuitarShop has two products at 799.99, for example.)

**Rules** available: `From`, `Where`, `Join` (including `FROM a, b`), `Subquery`, `Cte`, `OrderBy` (the statement's own, not `OVER (ORDER BY …)`), `GroupBy`, `Having`, `Distinct`, `Top`, `SelectStar`, `Union`, `AliasWithoutAs`, `SelectInto`, `InsertWithoutColumnList`, `CreateDatabase`. For UPDATE and DELETE, `From`, `Where` and `Join` look at the statement's own FROM and WHERE.

### Instructor commands

- `hv verify`: runs every reference solution on every dataset (catching broken solutions), then every mutant, and fails if any mutant passes. A passing mutant means the datasets are too weak for that question.
- `hv check <question> <file.sql>`: what the app does, from the command line (handy for batch-checking submissions).

## Screens

1. **Connection:** server, authentication, login and password. Connecting also checks that the login can create databases.
2. **Setup status:** a bar across the top. The `HV_` databases are built or updated automatically after connecting, with progress shown; a "Set up databases" button retries if that failed.
3. **Assignments:** a tree of assignments and questions, each marked passed, failed or not checked. Status is saved between sessions.
4. **Question:** prompt, SQL editor with syntax highlighting (AvaloniaEdit + TextMate), "Open .sql file", and Check (F5 or Ctrl+Enter). Answers are saved automatically. Reloading an opened file when it changes on disk is not done yet.
5. **Results:** a pass/fail banner, then per dataset: the message, tables of missing rows and rows that shouldn't be there (up to 25 each), and the hint. Also notes (such as an ignored USE), errors and rule violations. Not done yet: showing the student's full result next to the expected one, and a "Show solution" button (an instructor setting per assignment, e.g. after the due date).

## Distribution

- The **Release** workflow (`.github/workflows/release.yml`), run by hand from the Actions tab on `main`, builds every platform and publishes a GitHub release with install instructions (`packaging/release-notes.md`). It asks whether the release has bug fixes, new features or breaking changes and bumps the patch, minor or major number of the latest `vX.Y.Z` tag, unless a version is typed in.
- Self-contained single-file builds for `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, with single-file compression (`packaging/package.sh`). They aren't trimmed: YamlDotNet and SqlClient use reflection, and a trimmed build can't load the content.
- Packages: a zip with `HomeworkValidator.exe` and the `content` folder on Windows; a zipped `SQL Homework Validator.app` on macOS, with the content in `Contents/Resources`; a `.tar.gz` on Linux.
- **Signing:** none for now. The release notes tell students how to get past the warnings.
  - **macOS:** Gatekeeper blocks unsigned apps until the student clicks Open Anyway in Privacy & Security (or removes the quarantine flag with `xattr`). The bundle gets an ad-hoc signature, which is free and lets Apple silicon run it but doesn't satisfy Gatekeeper. Notarizing needs an Apple Developer account ($99/year).
  - **Windows:** SmartScreen warns until the student clicks More info → Run anyway. Code signing removes it.

## Milestones

1. **Core + CLI, SELECT only** (done): split both course scripts into schema and base data, build `HV_` databases, run a student query and the reference solution, compare, report. Question format, `hv verify` with mutants, and the first homework for each database (`ap-hw1`, `mgs-hw1`).
2. **Edge and shifted datasets** (done) for both databases. `hv verify` passes: all 43 mutants for `ap-hw1` and `mgs-hw1` are caught, including the four the base data couldn't catch. CI (GitHub Actions, added in its own PR) builds, runs the unit tests and runs `hv verify` against SQL Server in a container.
3. **Avalonia app for SELECT** (done): connection page, automatic database setup, question tree with saved status, SQL editor with saved answers, and results with differences and hints. Avalonia 12.
4. **DML questions** (done): `ap-hw2` and `mgs-hw2`, with setup chains, notes, alternatives and presence-only columns. AP exercise 5 means paying the invoice off (`PaymentTotal = InvoiceTotal - CreditTotal`); setting PaymentTotal to the balance itself is one of its mutants. The AP edge data gained an invoice that makes AP exercises 6 and 7 actually change something; in the course data they don't. The app shows which table differs, and a user journey test covers a DML question.
5. **DDL questions** (done): `scripting-hw1` on a new, empty Membership database, plus one AP exercise (a question can name its own `database`). Database-level statements are skipped rather than renamed; the design is compared from the catalog, and probes test constraints and defaults.
6. **Packaging:** builds per platform and install instructions for students (the Release workflow). Signing is postponed.

## Open questions

- **Assignments:** the next homework for each database, as it's ready.
- **"Use the CategoryID column to identify the row"** (MyGuitarShop homework 2, exercises 2 and 3) isn't enforced: `WHERE CategoryName = 'Brass'` gives the same tables and passes. A rule for "WHERE must use column X" could be added if that matters.
- **Content updates:** ship new assignments only with app releases, or let the app download content separately?
- **Grading use:** should `hv check` also batch-grade a folder of submissions?
- **Signing:** whether to pay for macOS and Windows code signing.
- **Repository visibility:** the repo is private. The course scripts and data come from the course textbook's materials; check their license before making it public.
