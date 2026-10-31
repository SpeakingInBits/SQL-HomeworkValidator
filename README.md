# SQL Homework Validator

A cross-platform desktop app that lets students check their SQL Server homework against the instructor's solutions. Students connect it to their own SQL Server (a local install on Windows, Docker on macOS and Linux), and it compares their query results against the reference solution on several test datasets.

Status: SELECT, INSERT/UPDATE/DELETE and database scripting (CREATE/ALTER TABLE, indexes, constraints) questions can be checked in the desktop app or with the command-line tool `hv`. See [PLAN.md](PLAN.md).

## Installing

Download the app from the [latest release](https://github.com/SpeakingInBits/SQL-HomeworkValidator/releases/latest), under **Assets**. You only download it once: after that the app keeps itself up to date (see [Updates](#updates)).

You also need SQL Server: a local install on Windows, or the Docker image on macOS and Linux.

The app isn't code-signed yet, so Windows and macOS warn you the first time you open it. That's expected.

### Windows

Download the Setup file. It's the one we recommend:

- `HomeworkValidator-win-x64-Setup.exe` for most PCs.
- `HomeworkValidator-win-arm64-Setup.exe` for PCs with an Arm processor, such as Snapdragon.

1. Double-click the Setup file. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.
2. The app installs for your Windows account (no administrator password needed) and opens. Next time, open **SQL Homework Validator** from the Start menu or the desktop.

If you can't run installers, download the `Portable.zip` for your PC instead, right-click it, choose **Extract All**, and open the app from the extracted folder. It updates itself too.

### macOS

- `HomeworkValidator-osx-arm64-Portable.zip` for Macs with Apple silicon (M1 or later).
- `HomeworkValidator-osx-x64-Portable.zip` for Intel Macs.

1. Double-click the zip file, then drag **SQL Homework Validator** into **Applications**.
2. Open the app. macOS says it can't verify it. Click **Done**.
3. Open **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to the message about SQL Homework Validator. Enter your password and confirm.

Instead of steps 2 and 3, you can run this in Terminal:

```bash
xattr -dr com.apple.quarantine "/Applications/SQL Homework Validator.app"
```

### Linux

Download `HomeworkValidator-linux-x64.AppImage`, then:

```bash
chmod +x HomeworkValidator-linux-x64.AppImage
./HomeworkValidator-linux-x64.AppImage
```

If it says FUSE is missing, install it (`sudo apt install libfuse2` on Ubuntu 22.04, `libfuse2t64` on 24.04 and later).

The other files in **Assets** (`.nupkg`, `.json`, `RELEASES`) are what installed apps download to update. You don't need them.

### Updates

Don't download new versions by hand. Each time you open the app it checks for a newer version and downloads it in the background. A banner then says the new version is ready: it installs the next time you open the app, or right away with **Restart now**. Your answers and settings are kept.

If you installed version 0.1.0, download the latest version once by hand; it updates itself from then on.

On macOS, if you saved your SQL Server password, macOS asks once after each update whether the app may use it. Click **Always Allow**.

## Building from source

Requires the .NET 10 SDK and a SQL Server you can create databases on.

### Desktop app

```bash
dotnet run --project src/HomeworkValidator.App
```

Connect to your server, pick a question, write a query and press F5 (or Check). The first connection builds the validator's databases, which takes a few seconds. Answers and pass/fail status are saved automatically in your user profile (`%APPDATA%\HomeworkValidator` on Windows, `~/.config/HomeworkValidator` elsewhere). With SQL Server authentication you can tick "Remember my password", which keeps it in Windows Credential Manager, the macOS keychain or your Linux keyring; otherwise it isn't saved.

### Command line

```bash
dotnet build
cd src/HomeworkValidator.Cli/bin/Debug/net10.0
./hv setup                              # creates HV_AP_base/_edge/_shifted, the same for MyGuitarShop, and HV_Membership_base
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

The desktop app's tests render windows headlessly and save screenshots to `tests/HomeworkValidator.App.Tests/bin/<config>/net10.0/screenshots/`.

The user journey tests (`UserJourneyTests`) use the app the way a student would, through `AppDriver`. They find controls by their on-screen text, click with the mouse and type with the keyboard, and they never call view-model code directly. Each step saves a numbered screenshot, such as `journey-03-wrong-answer.png`.

Tests marked `[Trait("Category", "NeedsSqlServer")]` run only when `HV_TEST_CONNECTION_STRING` is set, for example `Server=localhost;Integrated Security=true;TrustServerCertificate=true`. CI's `verify` job sets it. To run just those:

```bash
dotnet test tests/HomeworkValidator.App.Tests --filter "Category=NeedsSqlServer"
```

## Releases

Run the **Release** workflow from the repository's Actions tab, on `main`. Choose whether the release has bug fixes, new features or breaking changes, and it picks the next version number from the latest `vX.Y.Z` tag (or type a version to use instead). It builds the app for Windows, macOS and Linux and publishes a GitHub release with the packages and install instructions.

To build a package locally, from the repository root (macOS packages need a Mac, which signs them):

```bash
VERSION=0.0.1 packaging/package.sh win-x64
```

## Adding questions

Questions live in `content/assignments/<assignment>/`. See "Question format" in [PLAN.md](PLAN.md). A new assignment folder also goes in `content/assignments/order.yaml`, which sets the order the app shows assignments in. After adding or changing one, run `hv verify <assignment>`. If one of your known-wrong answers passes, add rows to the database's `datasets/edge.sql` that tell it apart.

If you change a database's `datasets/base.sql`, regenerate its shifted dataset from the repository root:

```bash
dotnet run --project src/HomeworkValidator.Cli -- make-shifted AP
```
