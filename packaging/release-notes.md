## Installing

Download the file for your computer from **Assets** below. You also need SQL Server: a local install on Windows, or the Docker image on macOS and Linux.

The app isn't code-signed yet, so Windows and macOS warn you the first time you open it. That's expected.

### Windows

- `HomeworkValidator-{{VERSION}}-win-x64.zip` for most PCs.
- `HomeworkValidator-{{VERSION}}-win-arm64.zip` for PCs with an Arm processor, such as Snapdragon.

1. Right-click the zip file, choose **Extract All**, and open the extracted `HomeworkValidator` folder.
2. Double-click `HomeworkValidator.exe`.
3. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.

Keep the `content` folder next to `HomeworkValidator.exe`. The app needs it.

### macOS

- `HomeworkValidator-{{VERSION}}-osx-arm64.zip` for Macs with Apple silicon (M1 or later).
- `HomeworkValidator-{{VERSION}}-osx-x64.zip` for Intel Macs.

1. Double-click the zip file, then drag **SQL Homework Validator** into **Applications**.
2. Open the app. macOS says it can't verify it. Click **Done**.
3. Open **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to the message about SQL Homework Validator. Enter your password and confirm.

Instead of steps 2 and 3, you can run this in Terminal:

```bash
xattr -dr com.apple.quarantine "/Applications/SQL Homework Validator.app"
```

### Linux

```bash
tar -xzf HomeworkValidator-{{VERSION}}-linux-x64.tar.gz
./HomeworkValidator/HomeworkValidator
```

