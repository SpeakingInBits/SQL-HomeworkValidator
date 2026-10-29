namespace HomeworkValidator.App.Services;

/// <summary>
/// Keeps SQL Server passwords in the operating system's own password store, encrypted and tied to the student's login,
/// so students who use SQL authentication (Docker on macOS and Linux) don't type the password every time.
/// The app never writes the password to its own files.
/// </summary>
public interface IPasswordStore
{
    /// <summary>False where there's nowhere safe to keep a password, such as Linux without a keyring. The app then doesn't offer to.</summary>
    bool IsAvailable { get; }

    /// <summary>Where the password goes, in words a student recognizes, e.g. "Windows Credential Manager".</summary>
    string Description { get; }

    string? Load(string server, string user);

    /// <returns>False if the password couldn't be saved.</returns>
    bool Save(string server, string user, string password);

    void Delete(string server, string user);
}

public static class PasswordStore
{
    /// <summary>The name the passwords are filed under, which students see in Credential Manager or Keychain Access.</summary>
    internal const string Service = "SQL Homework Validator";

    public static IPasswordStore ForThisComputer() =>
        OperatingSystem.IsWindows() ? new WindowsPasswordStore()
        : OperatingSystem.IsMacOS() ? new MacPasswordStore()
        : OperatingSystem.IsLinux() ? new LinuxPasswordStore()
        : new NoPasswordStore();

    /// <summary>One saved password per login on each server.</summary>
    internal static string Account(string server, string user) => $"{user}@{server}";
}

/// <summary>For platforms without a password store: nothing is saved.</summary>
public sealed class NoPasswordStore : IPasswordStore
{
    public bool IsAvailable => false;
    public string Description => "";
    public string? Load(string server, string user) => null;
    public bool Save(string server, string user, string password) => false;
    public void Delete(string server, string user) { }
}
