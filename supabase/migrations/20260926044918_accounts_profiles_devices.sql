-- AxePower accounts, step A1 (skillsMD/ACCOUNTS_PROFILE_PLAN.md).
--
-- profiles: the public face of an account, a unique @username plus a display name.
-- devices:  the PCs and phones an account owns. Nothing writes here until Remote Play; the table
--           exists now so the schema does not churn later.
--
-- Every table has Row Level Security on. A table without a policy is unreadable, so nothing is
-- exposed by accident. Deleting the auth user deletes their profile and devices (on delete cascade).

-- -----------------------------------------------------------------------------------------------
-- Usernames
-- -----------------------------------------------------------------------------------------------

-- 3-20 characters of a-z, 0-9, '_' and '.', not starting or ending with '.', and no '..'.
-- Lowercase only, so uniqueness cannot be dodged with capitals.
create function public.is_valid_username(name text)
returns boolean
language sql
immutable
set search_path = ''
as $$
  select coalesce(
    name ~ '^[a-z0-9_][a-z0-9_.]{1,18}[a-z0-9_]$' and position('..' in name) = 0,
    false);
$$;

-- Names nobody may take. Clients cannot read this table (RLS on, no policies); the checks below run
-- as the function owner.
create table public.reserved_usernames (
  username text primary key
);

alter table public.reserved_usernames enable row level security;

insert into public.reserved_usernames (username) values
  ('admin'), ('administrator'), ('root'), ('system'), ('sysadmin'),
  ('axepower'), ('axe_power'), ('axepower_official'), ('official'), ('team'), ('staff'),
  ('support'), ('help'), ('helpdesk'), ('security'), ('moderator'), ('mod'), ('mods'),
  ('owner'), ('developer'), ('dev'), ('api'), ('www'), ('mail'), ('email'), ('noreply'),
  ('no_reply'), ('null'), ('undefined'), ('anonymous'), ('guest'), ('me'), ('you'),
  ('profile'), ('settings'), ('account'), ('accounts'), ('login'), ('signin'), ('signup'),
  ('register'), ('logout'), ('about'), ('privacy'), ('terms'), ('steam'), ('valve'),
  ('discord'), ('google'), ('apple'), ('microsoft'), ('windows'), ('supabase');

-- -----------------------------------------------------------------------------------------------
-- Profiles
-- -----------------------------------------------------------------------------------------------

create table public.profiles (
  id                  uuid        primary key references auth.users (id) on delete cascade,
  username            text        not null unique
                                  constraint profiles_username_format
                                  check (public.is_valid_username(username)),
  display_name        text        not null
                                  constraint profiles_display_name_length
                                  check (display_name = btrim(display_name)
                                         and char_length(display_name) between 1 and 32),
  avatar_url          text
                                  constraint profiles_avatar_url_https
                                  check (avatar_url is null
                                         or (avatar_url ~ '^https://' and char_length(avatar_url) <= 1024)),
  -- Null until the first change after sign-up; the name picked at sign-up is free.
  username_changed_at timestamptz,
  created_at          timestamptz not null default now(),
  updated_at          timestamptz not null default now()
);

comment on table public.profiles is
  'Public AxePower profile: unique @username, display name, avatar. One row per auth user.';

-- Enforces what a check constraint cannot: reserved names, the 30-day username change limit, and
-- server-owned timestamps. Security definer so it can read reserved_usernames, which clients cannot.
create function public.profiles_guard()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  if tg_op = 'UPDATE' then
    if new.id <> old.id then
      raise exception 'A profile cannot change owner.';
    end if;

    new.created_at := old.created_at;

    if new.username <> old.username then
      if old.username_changed_at is not null
         and old.username_changed_at > now() - interval '30 days' then
        raise exception 'USERNAME_CHANGE_TOO_SOON'
          using hint = 'A username can be changed once every 30 days.';
      end if;
      new.username_changed_at := now();
    else
      new.username_changed_at := old.username_changed_at;
    end if;
  else
    new.created_at := now();
    new.username_changed_at := null;
  end if;

  if (tg_op = 'INSERT' or new.username <> old.username)
     and exists (select 1 from public.reserved_usernames r where r.username = new.username) then
    raise exception 'USERNAME_RESERVED'
      using hint = 'That username is reserved.';
  end if;

  new.updated_at := now();
  return new;
end;
$$;

revoke execute on function public.profiles_guard() from public, anon, authenticated;

create trigger profiles_guard
  before insert or update on public.profiles
  for each row execute function public.profiles_guard();

alter table public.profiles enable row level security;

-- Profiles are public to signed-in users, like Steam profiles: that is how people find each other.
create policy "Signed-in users can read profiles"
  on public.profiles for select
  to authenticated
  using (true);

create policy "Users create their own profile"
  on public.profiles for insert
  to authenticated
  with check ((select auth.uid()) = id);

create policy "Users update their own profile"
  on public.profiles for update
  to authenticated
  using ((select auth.uid()) = id)
  with check ((select auth.uid()) = id);

-- No delete policy: a profile goes away only with its account.
revoke all on public.profiles from anon;

-- Live "is this username free?" for the sign-up form, which runs before an account exists, so anon
-- may call it. It answers yes/no only and never returns other users' data.
create function public.username_available(name text)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
  select public.is_valid_username(name)
     and not exists (select 1 from public.reserved_usernames r where r.username = name)
     and not exists (select 1 from public.profiles p where p.username = name);
$$;

revoke execute on function public.username_available(text) from public;
grant execute on function public.username_available(text) to anon, authenticated;

-- -----------------------------------------------------------------------------------------------
-- Devices (Remote Play, later)
-- -----------------------------------------------------------------------------------------------

create table public.devices (
  id           uuid        primary key default gen_random_uuid(),
  user_id      uuid        not null references auth.users (id) on delete cascade,
  kind         text        not null
                           constraint devices_kind check (kind in ('pc', 'phone')),
  name         text        not null
                           constraint devices_name_length
                           check (char_length(btrim(name)) between 1 and 64),
  -- The device's public key for Remote Play pairing. Never a private key or a token.
  public_key   text
                           constraint devices_public_key_length
                           check (public_key is null or char_length(public_key) <= 512),
  created_at   timestamptz not null default now(),
  last_seen_at timestamptz not null default now()
);

comment on table public.devices is
  'PCs and phones owned by an account. Used by Remote Play; visible only to the owner.';

create index devices_user_id_idx on public.devices (user_id);

alter table public.devices enable row level security;

create policy "Users see their own devices"
  on public.devices for select
  to authenticated
  using ((select auth.uid()) = user_id);

create policy "Users add their own devices"
  on public.devices for insert
  to authenticated
  with check ((select auth.uid()) = user_id);

create policy "Users update their own devices"
  on public.devices for update
  to authenticated
  using ((select auth.uid()) = user_id)
  with check ((select auth.uid()) = user_id);

create policy "Users remove their own devices"
  on public.devices for delete
  to authenticated
  using ((select auth.uid()) = user_id);

revoke all on public.devices from anon;
