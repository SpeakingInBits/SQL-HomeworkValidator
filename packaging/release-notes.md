## Installing

Download the file for your computer from **Assets** below. You also need SQL Server: a local install on Windows, or the Docker image on macOS and Linux.

The app isn't code-signed yet, so Windows and macOS warn you the first time you open it. That's expected.

After that, the app keeps itself up to date. When a new version is out, it downloads it in the background and installs it the next time you open the app (or right away with **Restart now**). If you installed version 0.1.0, download this version once by hand; it updates itself from then on.

### Windows

- `HomeworkValidator-win-x64-Setup.exe` for most PCs.
- `HomeworkValidator-win-arm64-Setup.exe` for PCs with an Arm processor, such as Snapdragon.

1. Double-click the Setup file. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.
2. The app installs for your Windows account (no administrator password needed) and opens. Next time, open **SQL Homework Validator** from the Start menu or the desktop.

If you can't run installers, download the `Portable.zip` for your PC instead, right-click it, choose **Extract All**, and double-click `SQL Homework Validator.exe` in the extracted folder.

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

If you saved your SQL Server password, macOS asks once after each update whether the app may use it. Click **Always Allow**.

### Linux

Download `HomeworkValidator-linux-x64.AppImage`, then:

```bash
chmod +x HomeworkValidator-linux-x64.AppImage
./HomeworkValidator-linux-x64.AppImage
```

If it says FUSE is missing, install it (`sudo apt install libfuse2` on Ubuntu 22.04, `libfuse2t64` on 24.04 and later).

The other files in **Assets** (`.nupkg`, `.json`, `RELEASES`) are what installed apps download to update. You don't need them.
