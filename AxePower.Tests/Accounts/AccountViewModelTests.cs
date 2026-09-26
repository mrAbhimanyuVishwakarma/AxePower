using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AxePower.App.ViewModels;
using AxePower.App.Views;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Accounts;

/// <summary>
/// The account dialog and sidebar state, driven through the real <see cref="AccountService"/> over a
/// fake Supabase. The views are also loaded in every state, so a broken binding or a missing style
/// fails here rather than on the owner's screen. What they look like still needs the owner's eyes.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AccountViewModelTests : IDisposable
{
    private readonly WpfApplicationFixture _wpf;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerAccountTests", Guid.NewGuid().ToString("N"));
    private readonly FakeAccountApi _api = new();
    private readonly AccountService _service;
    private readonly AccountViewModel _vm;

    public AccountViewModelTests(WpfApplicationFixture wpf)
    {
        _wpf = wpf;
        _service = new AccountService(_api, new AccountSessionStore(new LogService(), _dir), new SystemClock(), new LogService());
        _vm = new AccountViewModel(_service, new SystemClock());
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AuthSession Session(Dictionary<string, string>? metadata = null) =>
        new("acc", "ref", DateTime.UtcNow.AddHours(1), "u-1", "me@example.com", "email", metadata ?? new Dictionary<string, string>());

    [Fact]
    public void SignedOut_TheSidebarInvitesYouToSignIn()
    {
        Assert.True(_vm.IsSignedOut);
        Assert.Equal("Sign in", _vm.SidebarTitle);
        Assert.Equal("Profile & sync", _vm.SidebarSubtitle);
        Assert.False(_vm.IsDialogOpen);
    }

    [Fact]
    public async Task SignIn_Succeeds_ClosesTheDialog_AndShowsYourName()
    {
        _api.SignIn = () => Session();
        _api.GetProfile = (_, _) => FakeAccountApi.Profile();

        _vm.OpenSignIn();
        _vm.Email = "me@example.com";
        _vm.Password = "pw123456";
        await _vm.SubmitCommand.ExecuteAsync(null);

        Assert.False(_vm.IsDialogOpen);
        Assert.True(_vm.IsSignedIn);
        Assert.Equal("Abhimanyu", _vm.SidebarTitle);
        Assert.Equal("@mrabhimanyu", _vm.SidebarSubtitle);
        Assert.Equal("A", _vm.Initials);
    }

    [Fact]
    public async Task SignIn_WithAWrongPassword_StaysOpen_WithTheReason()
    {
        _api.SignIn = () => throw new AccountException(AccountError.InvalidCredentials, "Wrong email or password.");

        _vm.OpenSignIn();
        _vm.Email = "me@example.com";
        _vm.Password = "nope1234";
        await _vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(_vm.IsDialogOpen);
        Assert.Equal("Wrong email or password.", _vm.ErrorMessage);
        Assert.True(_vm.IsSignedOut);
    }

    [Fact]
    public async Task SignIn_ChecksTheFormBeforeCallingSupabase()
    {
        _vm.OpenSignIn();
        _vm.Email = "not-an-email";
        await _vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Enter your email address.", _vm.ErrorMessage);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task SignUp_ShowsCheckYourInbox()
    {
        _vm.OpenSignUp();
        _vm.Email = "me@example.com";
        _vm.Password = "pw123456";
        _vm.Username = "mrabhimanyu";
        await _vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(AccountDialogMode.CheckEmail, _vm.Mode);
        Assert.Contains("me@example.com", _vm.CheckEmailMessage);
        Assert.True(_vm.IsDialogOpen);
    }

    [Fact]
    public async Task SignUp_WithAShortPassword_IsStoppedLocally()
    {
        _vm.OpenSignUp();
        _vm.Email = "me@example.com";
        _vm.Password = "short";
        _vm.Username = "mrabhimanyu";
        await _vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Use at least 8 characters for your password.", _vm.ErrorMessage);
        Assert.DoesNotContain("SignUp", _api.Calls);
    }

    [Fact]
    public async Task SignIn_WithoutAProfileYet_MovesToChooseUsername()
    {
        _api.SignIn = () => Session();
        _api.GetProfile = (_, _) => null;

        _vm.OpenSignIn();
        _vm.Email = "me@example.com";
        _vm.Password = "pw123456";
        await _vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(_vm.NeedsProfile);
        Assert.Equal(AccountDialogMode.ChooseUsername, _vm.Mode);
        Assert.True(_vm.IsDialogOpen);
    }

    [Fact]
    public void UsernameHint_ExplainsTheRules_ForAnInvalidName()
    {
        _vm.OpenSignUp();
        _vm.Username = "a..b";

        Assert.False(_vm.IsUsernameHintGood);
        Assert.Contains("a-z, 0-9", _vm.UsernameHint);
    }

    [Fact]
    public async Task DeleteAccount_NeedsTheWordDelete()
    {
        _api.SignIn = () => Session();
        _api.GetProfile = (_, _) => FakeAccountApi.Profile();
        await _service.SignInWithPasswordAsync("me@example.com", "pw123456");

        _vm.OpenDeleteCommand.Execute(null);
        _vm.DeleteConfirmation = "delete";
        await _vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("Type DELETE to confirm.", _vm.ErrorMessage);
        Assert.DoesNotContain("Delete", _api.Calls);

        _vm.DeleteConfirmation = "DELETE";
        await _vm.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("Delete", _api.Calls);
        Assert.True(_vm.IsSignedOut);
        Assert.False(_vm.IsDialogOpen);
    }

    // -- The views load in every state ------------------------------------------------------------

    [Theory]
    [InlineData(AccountDialogMode.SignIn, "Sign in to AxePower")]
    [InlineData(AccountDialogMode.SignUp, "Create your AxePower account")]
    [InlineData(AccountDialogMode.ForgotPassword, "Reset your password")]
    [InlineData(AccountDialogMode.ConfirmDelete, "Delete your account?")]
    public void TheDialog_LoadsEachForm(AccountDialogMode mode, string title) =>
        _wpf.Invoke(() =>
        {
            var vm = new AccountViewModel(_service, new SystemClock());
            vm.Mode = mode;
            var view = new AccountDialogView { DataContext = vm };
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.UpdateLayout();

            Assert.Equal(title, vm.DialogTitle);
            Assert.Contains(Texts(view), t => t == "Email" || t == "Type DELETE to confirm");
        });

    [Fact]
    public void TheProfilePage_LoadsSignedOut_WithSignInOffered() =>
        _wpf.Invoke(() =>
        {
            var page = new AboutView { DataContext = new AboutViewModel { Account = new AccountViewModel(_service, new SystemClock()) } };
            page.Measure(new Size(1200, 800));
            page.Arrange(new Rect(0, 0, 1200, 800));
            page.UpdateLayout();

            var text = string.Join("\n", Texts(page));
            Assert.Contains("Profile", text);
            Assert.Contains("No account on this PC.", text);
            Assert.Contains("Release notes", ButtonContents(page));
            Assert.Contains("Privacy Policy & Credits", text); // the About dialogs moved with the page
        });

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock block) yield return block.Text;
            foreach (var nested in Texts(child)) yield return nested;
        }
    }

    private static IEnumerable<string> ButtonContents(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button { Content: string content }) yield return content;
            foreach (var nested in ButtonContents(child)) yield return nested;
        }
    }
}
