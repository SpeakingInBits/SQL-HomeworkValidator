using HomeworkValidator.App.Services;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Saves, reads and deletes a password in this computer's real password store. Skipped where there isn't one (CI's Linux job
/// has no keyring) unless HV_EXPECT_PASSWORD_STORE is set, as the Password store workflow does on Windows, macOS and Linux.
/// </summary>
[Trait("Category", "PasswordStore")]
public sealed class PasswordStoreTests : IDisposable
{
    private readonly IPasswordStore _store = PasswordStore.ForThisComputer();

    // A server name no real entry uses, so the test never touches a student's or developer's saved password.
    private readonly string _server = "hv-test-" + Guid.NewGuid().ToString("N");

    public PasswordStoreTests()
    {
        var expected = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HV_EXPECT_PASSWORD_STORE"));
        Assert.SkipUnless(_store.IsAvailable || expected, "This computer has no password store the app can use.");
        Assert.True(_store.IsAvailable, $"{_store.GetType().Name} isn't available.");
    }

    public void Dispose() => _store.Delete(_server, "sa");

    [Fact]
    public void A_saved_password_can_be_read_back()
    {
        Assert.True(_store.Save(_server, "sa", "Pässw0rd with spaces & symbols!"));

        Assert.Equal("Pässw0rd with spaces & symbols!", _store.Load(_server, "sa"));
    }

    [Fact]
    public void Saving_again_replaces_the_password()
    {
        Assert.True(_store.Save(_server, "sa", "first"));
        Assert.True(_store.Save(_server, "sa", "second"));

        Assert.Equal("second", _store.Load(_server, "sa"));
    }

    [Fact]
    public void Each_login_has_its_own_password()
    {
        Assert.True(_store.Save(_server, "sa", "for sa"));

        Assert.Null(_store.Load(_server, "student"));
    }

    [Fact]
    public void A_deleted_password_is_gone()
    {
        Assert.True(_store.Save(_server, "sa", "secret"));

        _store.Delete(_server, "sa");

        Assert.Null(_store.Load(_server, "sa"));
    }

    [Fact]
    public void Deleting_a_password_that_was_never_saved_is_fine()
    {
        _store.Delete(_server, "nobody");

        Assert.Null(_store.Load(_server, "nobody"));
    }
}
