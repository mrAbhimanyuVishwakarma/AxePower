using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels;

/// <summary>Which form the account dialog is showing.</summary>
public enum AccountDialogMode
{
    SignIn,
    SignUp,
    ForgotPassword,

    /// <summary>After sign-up or a reset request: "check your inbox".</summary>
    CheckEmail,

    /// <summary>Signed in without a @username yet.</summary>
    ChooseUsername,

    EditProfile,
    ConfirmDelete
}

/// <summary>
/// The account as the UI sees it: the sidebar item, the Profile page's hero and Account card, and
/// the sign-in dialog that opens over the whole window. One instance for the app.
///
/// The account is optional. Nothing here blocks the rest of AxePower, and every failure lands as a
/// message in the dialog rather than an exception.
/// </summary>
public partial class AccountViewModel : ObservableObject
{
    private readonly AccountService _account;
    private readonly IClock _clock;
    private CancellationTokenSource? _usernameCheck;

    public AccountViewModel(AccountService account, IClock clock)
    {
        _account = account;
        _clock = clock;
        _account.StateChanged += (_, _) => OnUi(Refresh);
        Refresh();
    }

    // -- State ------------------------------------------------------------------------------------

    public AccountState State => _account.State;
    public bool IsSignedIn => State.Status == AccountStatus.SignedIn;
    public bool IsSignedOut => State.Status == AccountStatus.SignedOut;
    public bool NeedsProfile => State.Status == AccountStatus.NeedsProfile;
    public bool HasAccount => !IsSignedOut;
    public bool IsOffline => State.IsOffline;

    public string DisplayName => State.Profile?.DisplayName ?? (NeedsProfile ? "Almost there" : "Not signed in");
    public string Handle => State.Profile is { } p ? "@" + p.Username : string.Empty;
    /// <summary>The email to show; Steam accounts have only a placeholder address, so none.</summary>
    public string AccountEmail => IsSteamAccount ? string.Empty : State.Email ?? string.Empty;
    public bool HasEmail => AccountEmail.Length > 0;
    public bool IsSteamAccount => string.Equals(State.Provider, "steam", StringComparison.OrdinalIgnoreCase);

    public string SignedInWith => State.Provider?.ToLowerInvariant() switch
    {
        "google" => "Signed in with Google",
        "github" => "Signed in with GitHub",
        "discord" => "Signed in with Discord",
        "steam" => "Signed in with Steam",
        _ => "Signed in with email"
    };
    public string Initials => string.IsNullOrEmpty(State.Initials) ? "?" : State.Initials;
    public string? AvatarUrl => State.Profile?.AvatarUrl;
    public bool HasAvatar => !string.IsNullOrEmpty(AvatarUrl);

    /// <summary>Two lines for the sidebar item.</summary>
    public string SidebarTitle => IsSignedOut ? "Sign in" : State.Profile?.DisplayName ?? "Choose a username";
    public string SidebarSubtitle => IsSignedOut ? "Profile & sync" : State.Profile is { } p ? "@" + p.Username : "Finish your profile";

    public string MemberSince => State.Profile is { } p && p.CreatedAtUtc > DateTime.MinValue
        ? $"Member since {p.CreatedAtUtc.ToLocalTime():MMM yyyy}"
        : string.Empty;

    public string StatusLine => IsOffline ? "Offline: showing your saved profile" : "Online";

    public string MachineName => string.IsNullOrWhiteSpace(Environment.MachineName) ? "This PC" : Environment.MachineName;

    /// <summary>Empty when the username may be changed now; otherwise when it can be.</summary>
    public string UsernameLockNote => State.UsernameLockedUntilUtc(_clock.UtcNow) is { } until
        ? $"You can change your username again on {until.ToLocalTime():d MMM yyyy}."
        : $"You can change your username once every {UsernameRules.ChangeIntervalDays} days.";

    // -- Dialog -----------------------------------------------------------------------------------

    [ObservableProperty] private bool _isDialogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DialogTitle), nameof(DialogSubtitle), nameof(IsSignInMode), nameof(IsSignUpMode),
        nameof(IsForgotMode), nameof(IsCheckEmailMode), nameof(IsChooseUsernameMode), nameof(IsEditMode),
        nameof(IsConfirmDeleteMode), nameof(ShowsUsernameField), nameof(ShowsPasswordField), nameof(ShowsProviderButtons))]
    private AccountDialogMode _mode;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _newDisplayName = string.Empty;
    [ObservableProperty] private string _deleteConfirmation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty] private string _checkEmailMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsernameHint))]
    private string _usernameHint = string.Empty;

    [ObservableProperty] private bool _isUsernameHintGood;

    /// <summary>A one-click sign-in is waiting for the browser to come back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotWaitingForBrowser))]
    private bool _isWaitingForBrowser;

    [ObservableProperty] private string _waitingMessage = string.Empty;

    public bool IsNotWaitingForBrowser => !IsWaitingForBrowser;
    public bool ShowsProviderButtons => Mode is AccountDialogMode.SignIn or AccountDialogMode.SignUp;

    private CancellationTokenSource? _browserSignIn;

    /// <summary>Opens a URL in the user's browser. Replaced in tests.</summary>
    public Action<Uri> OpenBrowser { get; set; } = OpenInUserBrowser;

    /// <summary>Set from the view's PasswordBox; a PasswordBox cannot be bound.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Raised when the dialog closes, so the view can wipe its PasswordBox.</summary>
    public event EventHandler? DialogClosed;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasUsernameHint => !string.IsNullOrEmpty(UsernameHint);
    public bool IsIdle => !IsBusy;

    public bool IsSignInMode => Mode == AccountDialogMode.SignIn;
    public bool IsSignUpMode => Mode == AccountDialogMode.SignUp;
    public bool IsForgotMode => Mode == AccountDialogMode.ForgotPassword;
    public bool IsCheckEmailMode => Mode == AccountDialogMode.CheckEmail;
    public bool IsChooseUsernameMode => Mode == AccountDialogMode.ChooseUsername;
    public bool IsEditMode => Mode == AccountDialogMode.EditProfile;
    public bool IsConfirmDeleteMode => Mode == AccountDialogMode.ConfirmDelete;
    public bool ShowsUsernameField => Mode is AccountDialogMode.SignUp or AccountDialogMode.ChooseUsername or AccountDialogMode.EditProfile;
    public bool ShowsPasswordField => Mode is AccountDialogMode.SignIn or AccountDialogMode.SignUp;

    public string DialogTitle => Mode switch
    {
        AccountDialogMode.SignIn => "Sign in to AxePower",
        AccountDialogMode.SignUp => "Create your AxePower account",
        AccountDialogMode.ForgotPassword => "Reset your password",
        AccountDialogMode.CheckEmail => "Check your inbox",
        AccountDialogMode.ChooseUsername => "Choose your username",
        AccountDialogMode.EditProfile => "Edit profile",
        AccountDialogMode.ConfirmDelete => "Delete your account?",
        _ => string.Empty
    };

    public string DialogSubtitle => Mode switch
    {
        AccountDialogMode.SignIn => "Optional. Everything in AxePower works without an account.",
        AccountDialogMode.SignUp => "Your @username is how friends find you.",
        AccountDialogMode.ForgotPassword => "We'll email you a link to set a new password.",
        AccountDialogMode.ChooseUsername => "One last step: pick the @username others will see.",
        AccountDialogMode.EditProfile => UsernameLockNote,
        AccountDialogMode.ConfirmDelete => "This deletes your profile and devices for good. Your settings on this PC stay.",
        _ => string.Empty
    };

    // -- Commands ---------------------------------------------------------------------------------

    [RelayCommand] public void OpenSignIn() => Open(AccountDialogMode.SignIn);
    [RelayCommand] public void OpenSignUp() => Open(AccountDialogMode.SignUp);
    [RelayCommand] private void OpenForgotPassword() => Open(AccountDialogMode.ForgotPassword);
    [RelayCommand] private void OpenChooseUsername() => Open(AccountDialogMode.ChooseUsername);
    [RelayCommand] private void OpenDelete() => Open(AccountDialogMode.ConfirmDelete);

    [RelayCommand]
    private void OpenEditProfile()
    {
        Open(AccountDialogMode.EditProfile);
        NewDisplayName = State.Profile?.DisplayName ?? string.Empty;
        Username = State.Profile?.Username ?? string.Empty;
        UsernameHint = string.Empty;
    }

    /// <summary>The close button, Esc and a click outside. Ignored mid-request so a reply is never lost.</summary>
    [RelayCommand]
    private void CloseDialog()
    {
        if (IsWaitingForBrowser) { _browserSignIn?.Cancel(); return; }
        if (!IsBusy) Close();
    }

    private void Close()
    {
        IsDialogOpen = false;
        Password = string.Empty;
        DeleteConfirmation = string.Empty;
        DialogClosed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task SubmitSignIn() => RunAsync(async () =>
    {
        if (!LooksLikeEmail(Email)) { ErrorMessage = "Enter your email address."; return; }
        if (Password.Length == 0) { ErrorMessage = "Enter your password."; return; }

        await _account.SignInWithPasswordAsync(Email, Password);
        if (NeedsProfile) Open(AccountDialogMode.ChooseUsername);
        else Close();
    });

    [RelayCommand]
    private Task SubmitSignUp() => RunAsync(async () =>
    {
        if (!LooksLikeEmail(Email)) { ErrorMessage = "Enter a valid email address."; return; }
        if (Password.Length < 8) { ErrorMessage = "Use at least 8 characters for your password."; return; }

        var name = UsernameRules.Normalize(Username);
        var display = string.IsNullOrWhiteSpace(NewDisplayName) ? name : NewDisplayName;
        var outcome = await _account.SignUpAsync(Email, Password, name, display);

        if (outcome == SignUpOutcome.ConfirmationEmailSent)
        {
            CheckEmailMessage = $"We sent a confirmation link to {Email.Trim()}. Open it, then come back and sign in.";
            Open(AccountDialogMode.CheckEmail);
        }
        else
        {
            Close();
        }
    });

    [RelayCommand]
    private Task SubmitForgotPassword() => RunAsync(async () =>
    {
        if (!LooksLikeEmail(Email)) { ErrorMessage = "Enter your email address."; return; }

        await _account.RequestPasswordResetAsync(Email);
        // Supabase answers the same whether or not the address has an account, and so do we.
        CheckEmailMessage = $"If {Email.Trim()} has an AxePower account, a reset link is on its way.";
        Open(AccountDialogMode.CheckEmail);
    });

    [RelayCommand]
    private Task SubmitChooseUsername() => RunAsync(async () =>
    {
        var name = UsernameRules.Normalize(Username);
        await _account.CreateProfileAsync(name, string.IsNullOrWhiteSpace(NewDisplayName) ? name : NewDisplayName);
        Close();
    });

    [RelayCommand]
    private Task SubmitEditProfile() => RunAsync(async () =>
    {
        var name = UsernameRules.Normalize(Username);
        await _account.UpdateProfileAsync(
            displayName: NewDisplayName,
            username: name == State.Profile?.Username ? null : name);
        Close();
    });

    [RelayCommand]
    private Task ConfirmDelete() => RunAsync(async () =>
    {
        if (!string.Equals(DeleteConfirmation.Trim(), "DELETE", StringComparison.Ordinal))
        {
            ErrorMessage = "Type DELETE to confirm.";
            return;
        }

        await _account.DeleteAccountAsync();
        Close();
    });

    /// <summary>Google, GitHub, Discord or Steam: finish in the browser, then come back here.</summary>
    [RelayCommand]
    private Task SignInWith(string providerName) => RunAsync(async () =>
    {
        if (!Enum.TryParse<AccountProvider>(providerName, ignoreCase: true, out var provider)) return;

        using var cts = _browserSignIn = new CancellationTokenSource();
        WaitingMessage = $"Finish signing in with {provider} in your browser, then come back here.";
        IsWaitingForBrowser = true;
        try
        {
            await _account.SignInWithProviderAsync(provider, OpenBrowser, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // the user pressed Cancel
        }
        finally
        {
            IsWaitingForBrowser = false;
            _browserSignIn = null;
        }

        if (NeedsProfile)
        {
            Open(AccountDialogMode.ChooseUsername);
            Username = _account.SuggestedUsername;
            NewDisplayName = _account.SuggestedDisplayName;
        }
        else
        {
            Close();
        }
    });

    [RelayCommand]
    private void CancelBrowserSignIn() => _browserSignIn?.Cancel();

    [RelayCommand]
    private Task SignOut() => RunAsync(() => _account.SignOutAsync());

    /// <summary>Enter in the dialog: whatever the visible form's main button does.</summary>
    [RelayCommand]
    private Task Submit()
    {
        switch (Mode)
        {
            case AccountDialogMode.SignIn: return SubmitSignIn();
            case AccountDialogMode.SignUp: return SubmitSignUp();
            case AccountDialogMode.ForgotPassword: return SubmitForgotPassword();
            case AccountDialogMode.ChooseUsername: return SubmitChooseUsername();
            case AccountDialogMode.EditProfile: return SubmitEditProfile();
            case AccountDialogMode.ConfirmDelete: return ConfirmDelete();
            case AccountDialogMode.CheckEmail: OpenSignIn(); return Task.CompletedTask;
            default: return Task.CompletedTask;
        }
    }

    // -- Username check as you type ---------------------------------------------------------------

    partial void OnUsernameChanged(string value)
    {
        if (!ShowsUsernameField) return;

        _usernameCheck?.Cancel();
        var name = UsernameRules.Normalize(value);

        if (name.Length == 0) { SetHint(string.Empty, false); return; }
        if (Mode == AccountDialogMode.EditProfile && name == State.Profile?.Username) { SetHint("This is your current username.", true); return; }
        if (!UsernameRules.IsValid(name))
        {
            SetHint($"{UsernameRules.MinLength}-{UsernameRules.MaxLength} characters: a-z, 0-9, '_' and '.' (not at the start or end).", false);
            return;
        }

        var cts = _usernameCheck = new CancellationTokenSource();
        SetHint("Checking...", false);
        _ = CheckUsernameAsync(name, cts.Token);
    }

    private async Task CheckUsernameAsync(string name, CancellationToken ct)
    {
        try
        {
            await Task.Delay(350, ct); // wait for typing to pause
            bool free = await _account.IsUsernameAvailableAsync(name, ct);
            if (ct.IsCancellationRequested) return;
            OnUi(() => SetHint(free ? $"@{name} is available." : $"@{name} is taken or reserved.", free));
        }
        catch (OperationCanceledException) { /* Cancelled: the name changed again before the check finished. */ }
        catch (AccountException)
        {
            if (!ct.IsCancellationRequested) OnUi(() => SetHint("Couldn't check right now; you can still try.", false));
        }
    }

    private void SetHint(string text, bool good)
    {
        UsernameHint = text;
        IsUsernameHintGood = good;
    }

    // -- Plumbing ---------------------------------------------------------------------------------

    private void Open(AccountDialogMode mode)
    {
        ErrorMessage = string.Empty;
        Mode = mode;
        if (mode is AccountDialogMode.SignUp or AccountDialogMode.ChooseUsername)
        {
            Username = string.Empty;
            NewDisplayName = string.Empty;
            UsernameHint = string.Empty;
        }
        IsDialogOpen = true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (AccountException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception)
        {
            ErrorMessage = "Something went wrong. Try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool LooksLikeEmail(string value)
    {
        var v = value.Trim();
        var at = v.IndexOf('@');
        return at > 0 && at < v.Length - 3 && v.IndexOf('.', at) > at + 1 && !v.Contains(' ');
    }

    private void Refresh()
    {
        OnPropertyChanged(string.Empty); // every state-derived property at once
    }

    /// <summary>
    /// Opens the page through Explorer rather than directly. AxePower usually runs elevated, and a
    /// browser started straight from it would run elevated too, without the user's profile or saved
    /// sign-ins; Explorer hands the URL to the browser in the user's normal session.
    /// </summary>
    private static void OpenInUserBrowser(Uri url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{url.AbsoluteUri}\"")
        {
            UseShellExecute = false
        });
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
