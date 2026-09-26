---
name: accounts-profile-plan
description: AxePower accounts on Supabase (email, Google, Discord, Steam) and the desktop Profile page that replaces About; shared with AxePower Mobile and, later, Remote Play.
---

# AxePower Accounts & Profile — Plan

> **Status:** planned, nothing built · **Written:** 2026-09-26 · **Desktop:** v2.0.8 (branch `v2.0.8`) · **Mobile:** v0.0.1

Steam-style: sign in once, and your name ("mrabhimanyu") shows at the bottom of the sidebar. The
Profile page replaces About. Remote Play ([REMOTE_PLAY_PLAN.md](REMOTE_PLAY_PLAN.md) phase 7)
uses the same accounts to decide which phone may reach which PC.

## Decisions (owner, 2026-09-26)

| Question | Decision |
|---|---|
| When | **Real login now**, not a local-only profile first. |
| Required? | **Optional.** Everything works signed out, as today. Signing in adds Remote Play from anywhere and, later, sync. |
| Backend | **Supabase** (hosted auth + Postgres). One project for desktop and mobile. |
| Sign-in methods | **Email + password, Google, Discord, Steam.** Apple skipped for now (owner, 2026-09-26). |
| Names | **Unique username + display name**, like Steam/Discord. `@mrabhimanyu` is unique and used to find you. The display name is what the sidebar shows. |
| Sidebar | **Profile replaces About at the bottom.** Avatar + display name; the 72 px rail shows just the avatar. |
| About content | **Moves into a card on the Profile page**: version, Check for updates, release notes, license, support, Privacy Policy and Terms. |
| UI | **I design, owner reviews.** Layout previews go to the owner before anything is built. There is no reference image in `ui reference/` yet. |

## What the owner has to create (I cannot do these)

1. A **Supabase project** (free tier). Send me the project URL and the *publishable (anon)* key.
   The publishable key is safe to ship in the apps. The **service-role key never goes in either
   app**; it lives only in Edge Function secrets.
2. A **Google OAuth client** in Google Cloud Console, with redirect URLs from Supabase.
3. A **Discord application** in the Discord Developer Portal, with the Supabase redirect URL.
4. A **Steam Web API key** (steamcommunity.com/dev/apikey), for the Steam name and avatar.
5. A **sender for account emails** (verification, reset). Supabase's built-in sender is
   rate-limited and meant for testing only. Production needs SMTP (e.g. Resend, Brevo) on a domain
   we control.

## Architecture

```
 AxePower desktop (.NET, elevated)            Supabase project
 ┌──────────────────────────────┐            ┌───────────────────────────────────────┐
 │ AccountService (Core)        │── HTTPS ──►│ Auth: email+password, Google, Discord │
 │  Supabase.Gotrue, PKCE       │            │ Edge Function: steam-login (OpenID 2) │
 │  session: DPAPI file         │            │ Postgres + RLS:                       │
 │ Profile page · sidebar item  │            │   profiles (username, display, avatar)│
 └──────────────────────────────┘            │   devices  (Remote Play, later)       │
 AxePower Mobile (Flutter, v0.0.1)           │ Realtime: Remote Play rendezvous      │
  supabase_flutter ─────────────── HTTPS ───►│   candidate (decided in RP phase 7)   │
                                             └───────────────────────────────────────┘
```

## Phases (each ends with build, full tests, `--self-test`, settings.json compare, pause for review)

### A0 — Layout previews (decided 2026-09-26)
- **Sidebar:** Discord-style two lines at the bottom: avatar + display name + `@username`, with a
  green online dot. Signed out: a grey avatar, "Sign in" and "Profile & sync". The 72 px rail shows the
  avatar only.
- **Profile page:** a Steam-style hero banner (skin-accent glow, big avatar, display name,
  @username, "Member since", This PC, Edit). Below it, two columns: **Account** (Email / Google / Discord /
  Steam, linked or [Link]; Sign out; Delete) and **AxePower** (version + Check for updates, release
  notes, website and support, MIT License, Privacy and Terms).
- **Sign in / Sign up:** a centred glass modal over the dimmed app. Social buttons (Google, Discord,
  Steam) come first, then "or", then the email form. "Forgot?" and "Create account" switch the same
  dialog.
- **Avatar:** the provider picture (Google/Discord/Steam), otherwise coloured initials. No uploads yet.
- Every skin must produce any new resource keys.

### A1 — Supabase schema (in repo, `supabase/` via the Supabase CLI)
> **Status 2026-09-26:** migration `20260926044918_accounts_profiles_devices` **pushed to the live
> project**. 26/26 behaviour checks passed on PGlite (Postgres 18 with a stub `auth` schema). Live
> checks with the publishable key: `username_available` answers correctly, anon is denied on
> `profiles` and `devices`, `reserved_usernames` returns nothing, and the guard trigger is not callable.
> **Not yet run live:** signed-in insert and update, the 30-day rule, and the delete cascade. They need a
> real signed-in user (A3). The delete-account Edge Function is **not built**; it moves to A2.

- `profiles`:
  - `id` → `auth.users`
  - `username`: citext, unique, `^[a-z0-9_.]{3,20}$`, with a reserved-name list
  - `display_name`, `avatar_url`, `created_at`
- RLS: anyone signed in may read `username`, `display_name` and `avatar_url`; only the owner may update
  their row. Username changes are rate-limited (e.g. once per 30 days, like Discord).
- `devices` (Remote Play later): `user_id`, `kind` (pc/phone), `name`, `public_key`, `last_seen`.
  Created now so the schema does not churn; nothing writes to it until Remote Play.
- Account deletion: an Edge Function that deletes the auth user; the profile row cascades.
  Required by Google Play and the App Store for the mobile app.

### A2 — Desktop account core
- `AccountService` in Core on `Supabase.Gotrue` (the maintained supabase-csharp monorepo; the old
  gotrue-csharp repo is archived).
- The session (refresh token) is stored **DPAPI-encrypted in its own file** under
  `%LOCALAPPDATA%\AxePower\`, **not** in settings.json. The token refreshes on start.
- Offline-safe: when Supabase cannot be reached, the cached profile is still shown and nothing
  blocks the app.
- Tests use a fake `HttpMessageHandler` and a temp directory, and must leave settings.json
  byte-identical.
- Self-test line: `Account: signed out` / `Account: @username (cached)`. It reads the cache only,
  with no network call and no writes.

### A3 — Email + password
Sign up (email, password, username, display name), verification email, sign in, forgot/reset
password, and a live "username available" check.

### A4 — Google and Discord
- OAuth with **PKCE in the system browser**, redirecting to a loopback address the app listens on.
  Tokens never pass through an embedded webview.
- AxePower runs elevated, so the browser must be **started un-elevated** (through the shell,
  not a direct `Process.Start` from the elevated process). Otherwise the user's browser opens as
  admin without their profile.
- First social sign-in → a "Choose a username" step, suggesting one from the provider's name.

### A5 — Steam (the risky one)
- Steam is **OpenID 2.0**, not OAuth/OIDC. Supabase has no Steam provider, and its new *custom
  OIDC providers* feature cannot use Steam either.
- Plan: an Edge Function `steam-login` does the following.
  1. It verifies Steam's OpenID assertion with `check_authentication`.
  2. It finds or creates the user by SteamID64 and issues a Supabase session.
  3. It fetches the persona name and avatar with the Steam Web API key.
- Steam gives **no email**. A Steam-only account has no email reset until the user adds one on
  the Profile page.
- If A5 turns out to need the service-role key in a way we are not comfortable with, stop and ask.

### A6 — Desktop UI
- **Sidebar (bottom):** avatar + display name + `@username`. Signed out, it shows a generic avatar
  and "Sign in". Clicking opens Profile.
- **Profile page:**
  - Identity card: avatar, display name, @username, edit.
  - Linked sign-in methods: link or unlink Google, Discord and Steam; add an email.
  - This PC.
  - Sign out.
  - Delete account, with confirmation.
  - The **AxePower card** moved from About.
- `About` navigation keys and tray/menu links go to Profile, so nothing that links to About breaks.
- Friends: when signed in, `FriendsProfile.GamerTag` shows `@username` instead of the Windows user
  name.

### A7 — Privacy Policy, Terms, What's New
- Privacy Policy: replace "AxePower has no accounts" with "accounts are optional". Say what an
  account stores (email, username, display name, avatar, linked provider IDs), that Supabase
  processes it, how to delete it, and that signed-out use sends nothing, as before.
- Terms: account rules (usernames, impersonation, deletion).
- Ask the owner which What's New entries v2.0.8 keeps.

### A8 — Mobile (v0.0.1)
- `supabase_flutter`, with the same sign-in methods and the same profile.
- **Apple is skipped for now.** Before an **iOS** build ships, App Store guideline 4.8 will require
  Sign in with Apple (or an equivalent privacy-focused login) because Google, Discord and Steam are
  offered. Android and desktop are not affected. It needs a paid Apple Developer account.

## Security rules
- Never ship or log the service-role key, refresh tokens or passwords. The log shows `@username` at most.
- RLS on every table from the first migration. There is no table without a policy.
- Signed out is a first-class state: no feature that works today may start requiring an account.

## Open decisions (ask before the phase that needs them)
- **A0:** the layouts (previews to pick from).
- **A7:** the domain and SMTP provider for account emails.
- **Before any iOS release:** Sign in with Apple (skipped for now).
- **Later:** whether Friends moves from account-free room links to finding friends by `@username`.
  That changes the product decision in [SHARE_SOCIAL_PLAN.md](SHARE_SOCIAL_PLAN.md).
