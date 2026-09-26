-- Sign in with Steam (ACCOUNTS_PROFILE_PLAN.md, A5), used only by the steam-login Edge Function.
--
-- Supabase Auth has no Steam provider, so the function verifies Steam's OpenID 2.0 reply itself and
-- maps each Steam account to one Supabase user. Both tables are for that function alone: RLS is on
-- with no policies, so the apps can neither read nor write them.

-- Which Supabase user a Steam account signs in as.
create table public.steam_accounts (
  steam_id   text        primary key
                         constraint steam_accounts_id_format check (steam_id ~ '^[0-9]{17}$'),
  user_id    uuid        not null unique references auth.users (id) on delete cascade,
  created_at timestamptz not null default now()
);

alter table public.steam_accounts enable row level security;
revoke all on public.steam_accounts from anon, authenticated;

-- One-time codes handed to AxePower after a Steam sign-in (PKCE-style: only the app that holds the
-- verifier for `challenge` can redeem one). Only the SHA-256 of a code is kept, and each lives for
-- two minutes.
create table public.steam_login_codes (
  code_hash  text        primary key,
  challenge  text        not null
                         constraint steam_login_codes_challenge_format check (challenge ~ '^[A-Za-z0-9_-]{43}$'),
  user_id    uuid        not null references auth.users (id) on delete cascade,
  expires_at timestamptz not null
);

create index steam_login_codes_expires_idx on public.steam_login_codes (expires_at);

alter table public.steam_login_codes enable row level security;
revoke all on public.steam_login_codes from anon, authenticated;
