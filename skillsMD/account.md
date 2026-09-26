---
name: account
description: AxePower Account — optional sign-in (email, Google, GitHub, Discord, Steam) on Supabase, the Profile page that replaced About, the sidebar profile item, session storage, the database behind it, and how to set up and work on it.
---

# AxePower Account

An **optional** account. Everything in AxePower works without one. Signing in gives you a profile
(unique `@username`, display name, picture) that Friends uses, and later AxePower Mobile and Remote
Play. The account lives on **Supabase** (hosted auth + Postgres). There is no AxePower server of our
own.

The build plan and its status per step are in [ACCOUNTS_PROFILE_PLAN.md](ACCOUNTS_PROFILE_PLAN.md).
Friends on the account are covered in [SHARE_SOCIAL_PLAN.md](SHARE_SOCIAL_PLAN.md) and [friends.md](friends.md).

## Where it lives

| Piece | File |
|---|---|
| Project URL + publishable key | `SupabaseProject.AxePower` in [AccountModels.cs](../AxePower.Core/Services/Accounts/AccountModels.cs) |
| State, errors, providers | [AccountModels.cs](../AxePower.Core/Services/Accounts/AccountModels.cs) |
| Username / display name / avatar rules | [UsernameRules.cs](../AxePower.Core/Services/Accounts/UsernameRules.cs) |
| HTTP client (Auth, PostgREST, Edge Functions) | [SupabaseAccountApi.cs](../AxePower.Core/Services/Accounts/SupabaseAccountApi.cs), Friends calls in [SupabaseAccountApi.Friends.cs](../AxePower.Core/Services/Accounts/SupabaseAccountApi.Friends.cs) |
| Browser sign-in (PKCE + loopback listener) | [OAuthLoopback.cs](../AxePower.Core/Services/Accounts/OAuthLoopback.cs) |
| Saved sign-in (DPAPI) | [AccountSessionStore.cs](../AxePower.Core/Services/Accounts/AccountSessionStore.cs) |
| The account itself | [AccountService.cs](../AxePower.Core/Services/Accounts/AccountService.cs) |
| Sidebar item, dialog, Profile state | [AccountViewModel.cs](../AxePower.App/ViewModels/AccountViewModel.cs) |
| Sign-in / sign-up dialog | [AccountDialogView.xaml](../AxePower.App/Views/AccountDialogView.xaml) |
| Profile page (was About) | [AboutView.xaml](../AxePower.App/Views/AboutView.xaml) + [AboutViewModel.cs](../AxePower.App/ViewModels/AboutViewModel.cs) (`Account` property) |
| Sidebar item + dialog overlay | [MainWindow.xaml](../AxePower.App/MainWindow.xaml) (search "Profile (bottom of the list" / "Account dialog") |
| Database | [supabase/migrations/](../supabase/migrations/) |
| Edge Functions | [supabase/functions/delete-account](../supabase/functions/delete-account/index.ts), [supabase/functions/steam-login](../supabase/functions/steam-login/index.ts) |
| Tests | [AxePower.Tests/Accounts/](../AxePower.Tests/Accounts/), database rules in [supabase/tests/](../supabase/tests/) |

The class names `AboutView` / `AboutViewModel` were kept because the update, patch-notes, privacy and
terms dialogs bind to them in about 100 places. The page the user sees is **Profile**.

## Sign-in methods

| Method | How it works |
|---|---|
| Email + password | `POST /auth/v1/signup` with `username` and `display_name` in the metadata. Supabase emails a confirmation link, which lands on the Site URL. The first sign-in afterwards creates the profile from that metadata. Forgot password: `POST /auth/v1/recover`. |
| Google, GitHub, Discord | Supabase OAuth with **PKCE**. AxePower opens `…/auth/v1/authorize?provider=…&redirect_to=http://127.0.0.1:53682/auth/callback&code_challenge=…` in the user's browser. The one-shot listener on that port receives `?code=…`. `POST /auth/v1/token?grant_type=pkce` trades it, with the verifier that never left the app, for a session. |
| Steam | Steam speaks OpenID 2.0, which Supabase does not, so the `steam-login` Edge Function does it. See [Steam](#steam) below. |
| Apple | Not offered (the owner skipped it). **An iOS release will need it** (App Store guideline 4.8), because other social sign-ins are offered. |

- **Browser from an elevated app:** AxePower usually runs as admin. It opens the browser through
  `explorer.exe "<url>"`, so the browser runs un-elevated with the user's profile.
- **The loopback** listens on `127.0.0.1` only, with a fixed port (`53682`), because Supabase only
  redirects to allow-listed URLs. It closes after one callback or 5 minutes. If the port is taken, the
  user gets a clear error.
- **After the first one-click sign-in** there is no `@username` yet (state `NeedsProfile`). The dialog
  switches to *Choose your username*, prefilled from the provider's name (`UsernameRules.Suggest`).

### Steam
1. `GET functions/v1/steam-login?redirect=<loopback>&challenge=<pkce>` checks the redirect is exactly
   the loopback and redirects to Steam's login page.
2. Steam returns to the function. The function reads the challenge from the **signed** `return_to`,
   checks the reply with Steam (`check_authentication`), and takes the SteamID64 from `claimed_id`.
3. It maps the SteamID to one Supabase user (`steam_accounts`), creating one if needed. The user gets
   a placeholder email `steam-<id>@steam.axepower.invalid`; nothing can be delivered to `.invalid`,
   and the UI hides it. It fetches the Steam name and picture with the Web API key.
4. It stores a 2-minute single-use code (SHA-256 only, in `steam_login_codes`) and redirects to the
   loopback with `?code=…`.
5. The app `POST`s `{action:"exchange", code, verifier}`. The function checks the verifier against the
   challenge, deletes the code, and mints a normal Supabase session (generateLink + verifyOtp, never
   emailed).

The function is deployed with **verify_jwt off** (`[functions.steam-login]` in
[config.toml](../supabase/config.toml)), because steps 1-2 are plain browser navigations.

## Session

- **Stored** in `%LOCALAPPDATA%\AxePower\Account\session.dat`, encrypted with **DPAPI** for the
  current Windows user. It is never in `settings.json` and never logged; the log shows `@username` at
  most. The file holds the access and refresh tokens, expiry, user id, email, provider, and the last
  profile seen.
- **Start-up** (`AccountService.InitializeAsync`): the cached profile shows at once, then the session
  is refreshed. **Offline or server trouble keeps you signed in** (with an "offline" flag). Only
  Supabase refusing the refresh token signs you out; any refused refresh counts, including a damaged
  token, which comes back as `validation_failed`.
- **Tokens:** refreshed 2 minutes before expiry, and once more if the server calls a token expired.
  All account operations are serialized, because a refresh token is single-use.
- **Other services** (Friends) call `AccountService.GetAccessTokenAsync()`.
- **Sign out** uses `POST /auth/v1/logout?scope=local`: only this PC; other devices stay signed in.
  It always succeeds locally.
- **Delete account:** the dialog asks the user to type `DELETE`, then calls the `delete-account` Edge
  Function as the user. The function verifies the caller with Supabase Auth and deletes only that user.
  The profile, devices, friends, presence and invites go with them (`on delete cascade`).

## Profile rules

| Rule | Where it is enforced |
|---|---|
| `@username`: 3-20 characters, `a-z 0-9 _ .`, not starting or ending with `.`, no `..`, lowercase, unique | the DB check `is_valid_username` + unique index; mirrored in `UsernameRules.IsValid` |
| Reserved names (`admin`, `axepower`, `steam`, `support`, …) | the `reserved_usernames` table (unreadable by apps) and the `profiles_guard` trigger |
| Username change at most once every 30 days (the sign-up pick is free) | the `profiles_guard` trigger; the UI shows the date it unlocks |
| Display name: 1-32 characters, trimmed, no control or invisible/bidirectional characters | DB checks; `UsernameRules.IsValidDisplayName` |
| Avatar only from Google, GitHub, Discord or Steam picture servers | DB check `profiles_avatar_url_provider`; `UsernameRules.IsAllowedAvatarUrl`. Friends' apps load each other's avatars, so an arbitrary URL would log viewers' IPs. |

`username_available(name)` is callable signed out, for the live "available" check while signing up.
It answers only yes or no.

**Finding people:**
- `search_profiles(query)` (signed in only) powers the suggestions in "Add a friend". It returns up to
  8 people whose @username or display name starts with what was typed (2+ characters, `_` taken
  literally). Each comes with its state: `none`, `incoming`, `outgoing`, `friend` or `blocked` (you
  blocked them). People who blocked you never appear.
- `profiles.discoverable` ("Let people find me by name" in Settings → Friends) hides you from
  suggestions. An exact @username still finds you.
- Declined requests disappear from the sender's list, and they can ask again (the owner's choice).

## UI

- **Sidebar** (bottom, replacing About): avatar (provider picture, otherwise initials), display name,
  `@username`, and a green (online) or grey (offline) dot. Signed out it reads **Sign in / Profile &
  sync**. The 72 px rail shows only the avatar.
- **Navigation:** the key `Profile` opens the page, and **also opens the sign-in dialog when signed
  out**. `About` still works as an alias (used by Settings for the update dialog).
- **Profile page:**
  - A hero with the avatar, name, `@username`, "Member since", this PC, online/offline, and Edit profile.
  - An **Account** card: "Signed in with …", email (hidden for Steam), username, display name, the
    30-day note, Sign out, Delete account.
  - An **AxePower** card: version, Check for updates, release notes, website, support, developer,
    licence, Privacy Policy and Terms.
- **Dialog** (a glass card over the dimmed window; Enter submits, Esc closes):
  - A Google / GitHub / Discord / Steam icon row. While the browser is out, a waiting panel with
    Cancel replaces the form.
  - Email + password, sign up (with the live username check), forgot password, check your inbox,
    choose username, edit profile, and delete.

## Supabase project

| Item | Value / where |
|---|---|
| Project | `kjnqsgdlcytpooueymdt` (`https://kjnqsgdlcytpooueymdt.supabase.co`) |
| Publishable key | in the code (`SupabaseProject.AxePower`). Public by design: it only reaches what RLS allows. **The secret key must never be in the repo or the apps.** |
| Providers | Email (confirmation on), Google, Discord, GitHub (on since 2026-09-26). Steam is through the function. |
| URL Configuration | Site URL `https://axepower.vercel.app`; Redirect URLs must include `http://127.0.0.1:53682/auth/callback` |
| Function secrets | `STEAM_WEB_API_KEY` (added by the owner in the dashboard). `SUPABASE_URL` and the keys are provided by Supabase. |
| Functions | `delete-account` (verify_jwt on), `steam-login` (verify_jwt off) |

### Setting up a sign-in provider (owner)
1. Create the provider's OAuth app with the callback
   `https://kjnqsgdlcytpooueymdt.supabase.co/auth/v1/callback`.
2. Paste its client ID and secret into **Supabase → Authentication → Sign In / Providers → <provider>**,
   turn it on, and save. Secrets go only there, never into chat or the repo.
3. Check: `GET /auth/v1/settings` with the publishable key as `apikey` lists which providers are on.

## Working on it

- **Database changes:** add a migration with `npx supabase migration new <name>`.
  - Test it with `cd supabase/tests && npm install && npm test`. This runs every migration on PGlite
    over a stub `auth` schema and checks the Friends rules as three users.
  - `npx supabase db push` applies it to the live project; ask the owner first.
  - Afterwards run `npx supabase db lint --linked`.
- **Functions:** `npx supabase functions deploy <name> --use-api` (no Docker needed). Add
  `--no-verify-jwt` for `steam-login`.
- **The CLI** is logged in and linked on the owner's PC (`npx supabase login`, `npx supabase link
  --project-ref kjnqsgdlcytpooueymdt`).
- **Tests:** `AxePower.Tests/Accounts` runs against a stub network (`SupabaseAccountApi`) and a fake
  API (`AccountService`, the view models). The real loopback is tested on its real port. None of it
  proves Supabase answers the same way: check live calls with the publishable key, and leave anything
  signed-in to the owner.
- **Self-test:** `AxePower.exe --self-test` prints
  `Account: signed out | @user (saved session, not refreshed); backend <host>`. It decrypts the saved
  session only; it makes no network call and writes nothing.
- **Settings:** the account does not use `settings.json`. The Friends privacy switches do
  (`shareScreen.shareActivityWithFriends`, `shareScreen.appearOffline`).

## Requirements

- Internet, for signing in and anything live. Offline, the last profile stays on screen.
- Port **53682** free on 127.0.0.1 during a one-click sign-in.
- No admin rights needed. It works elevated too, and opens the browser un-elevated.

## Known gaps

1. **Account emails** go through Supabase's built-in sender, which is rate-limited (a few an hour) and
   meant for testing. Production needs SMTP on a domain we control.
2. **The password policy** is enforced by the app only (8+ characters). Set the same minimum in
   Supabase → Authentication → Email so the server enforces it too.
3. **Not yet run live:**
   - account deletion as a real user
   - a Steam sign-in (the `.invalid` placeholder email in particular)
   - GitHub (the provider is on and redirects to GitHub; nobody has signed in through it yet)
4. **Apple** sign-in is missing; it is needed before any iOS release.
5. **The tests write to the real log:** they use the real `LogService`, so
   `%APPDATA%\AxePower\AxePower.log` gets test lines under the name `@mrabhimanyu`. Harmless, but
   confusing when reading the log.
