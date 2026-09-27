-- Friends on AxePower accounts, phase F1 (skillsMD/SHARE_SOCIAL_PLAN.md).
--
-- friendships    a request (pending) or a friendship (accepted) between two users
-- blocks         who blocked whom; a block also ends any friendship or request
-- presence       what a user lets friends see: online/away/offline plus an activity
-- friend_invites "join my call" / "watch my screen", for ten minutes
--
-- Every change to friendships and blocks goes through the functions below, so the rules live in
-- one place; the tables themselves accept no writes from the apps. Everything is readable only by
-- the people it involves. Deleting an account removes all of it (on delete cascade).

-- -----------------------------------------------------------------------------------------------
-- Tables
-- -----------------------------------------------------------------------------------------------

create table public.friendships (
  id         uuid        primary key default gen_random_uuid(),
  requester  uuid        not null references auth.users (id) on delete cascade,
  addressee  uuid        not null references auth.users (id) on delete cascade,
  status     text        not null constraint friendships_status check (status in ('pending', 'accepted')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint friendships_not_self check (requester <> addressee)
);

-- One row per pair, whichever way round the request went.
create unique index friendships_pair_idx
  on public.friendships (least(requester, addressee), greatest(requester, addressee));
create index friendships_addressee_idx on public.friendships (addressee);

create table public.blocks (
  blocker    uuid        not null references auth.users (id) on delete cascade,
  blocked    uuid        not null references auth.users (id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (blocker, blocked),
  constraint blocks_not_self check (blocker <> blocked)
);

create table public.presence (
  user_id       uuid        primary key references auth.users (id) on delete cascade,
  status        text        not null default 'offline'
                            constraint presence_status check (status in ('online', 'away', 'offline')),
  -- Written already filtered by the user's privacy switch: friends see exactly this row.
  activity_kind text        constraint presence_activity_kind check (activity_kind in ('game', 'call', 'sharing')),
  activity      text        constraint presence_activity check (
                              activity is null
                              or (char_length(activity) between 1 and 64
                                  and activity !~ '[\u0001-\u001F\u007F-\u009F​-‏‪-‮⁠-⁩﻿]')),
  updated_at    timestamptz not null default now(),
  constraint presence_offline_has_no_activity check (status <> 'offline' or (activity is null and activity_kind is null))
);

create table public.friend_invites (
  id         uuid        primary key default gen_random_uuid(),
  from_user  uuid        not null default auth.uid() references auth.users (id) on delete cascade,
  to_user    uuid        not null references auth.users (id) on delete cascade,
  kind       text        not null constraint friend_invites_kind check (kind in ('call', 'watch')),
  -- The host's room link (LAN or Tailscale address). Readable only by the two people involved.
  room_link  text        not null constraint friend_invites_link check (
                           char_length(room_link) <= 512 and room_link ~ '^https?://[^\s]+$'),
  created_at timestamptz not null default now(),
  expires_at timestamptz not null default now() + interval '10 minutes',
  constraint friend_invites_not_self check (from_user <> to_user)
);

create index friend_invites_to_idx on public.friend_invites (to_user, expires_at);

-- -----------------------------------------------------------------------------------------------
-- Helpers
-- -----------------------------------------------------------------------------------------------

create function public.are_friends(a uuid, b uuid)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
  select exists (
    select 1 from public.friendships f
    where f.status = 'accepted'
      and least(f.requester, f.addressee) = least(a, b)
      and greatest(f.requester, f.addressee) = greatest(a, b));
$$;

revoke execute on function public.are_friends(uuid, uuid) from public, anon;
grant execute on function public.are_friends(uuid, uuid) to authenticated;

-- -----------------------------------------------------------------------------------------------
-- Actions (the only way to change friendships and blocks)
-- -----------------------------------------------------------------------------------------------

-- Returns REQUESTED, ACCEPTED (they had already asked you), ALREADY_FRIENDS or ALREADY_REQUESTED.
-- Someone who has blocked you gets the same REQUESTED as anyone else, and nothing happens, so a
-- block can't be detected from outside.
create function public.send_friend_request(target_username text)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
  me uuid := auth.uid();
  them uuid;
  existing public.friendships;
begin
  if me is null then raise exception 'NOT_SIGNED_IN'; end if;

  select p.id into them from public.profiles p where p.username = lower(ltrim(btrim(target_username), '@'));
  if them is null then raise exception 'USER_NOT_FOUND'; end if;
  if them = me then raise exception 'CANNOT_ADD_SELF'; end if;

  if exists (select 1 from public.blocks b where b.blocker = me and b.blocked = them) then
    raise exception 'YOU_BLOCKED_THEM';
  end if;
  if exists (select 1 from public.blocks b where b.blocker = them and b.blocked = me) then
    return 'REQUESTED';
  end if;

  select * into existing from public.friendships f
  where least(f.requester, f.addressee) = least(me, them)
    and greatest(f.requester, f.addressee) = greatest(me, them);

  if found then
    if existing.status = 'accepted' then return 'ALREADY_FRIENDS'; end if;
    if existing.requester = me then return 'ALREADY_REQUESTED'; end if;
    update public.friendships set status = 'accepted', updated_at = now() where id = existing.id;
    return 'ACCEPTED';
  end if;

  if (select count(*) from public.friendships f where f.requester = me and f.status = 'pending') >= 100 then
    raise exception 'TOO_MANY_REQUESTS';
  end if;

  insert into public.friendships (requester, addressee, status) values (me, them, 'pending');
  return 'REQUESTED';
end;
$$;

-- Accept or decline a request someone sent you. Declining deletes it quietly.
create function public.respond_friend_request(request_id uuid, accept boolean)
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
  if auth.uid() is null then raise exception 'NOT_SIGNED_IN'; end if;

  if accept then
    update public.friendships set status = 'accepted', updated_at = now()
    where id = request_id and addressee = auth.uid() and status = 'pending';
  else
    delete from public.friendships
    where id = request_id and addressee = auth.uid() and status = 'pending';
  end if;

  if not found then raise exception 'REQUEST_NOT_FOUND'; end if;
end;
$$;

-- Unfriend, or cancel a request you sent (or decline one you received).
create function public.remove_friend(other uuid)
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
  if auth.uid() is null then raise exception 'NOT_SIGNED_IN'; end if;
  delete from public.friendships f
  where least(f.requester, f.addressee) = least(auth.uid(), other)
    and greatest(f.requester, f.addressee) = greatest(auth.uid(), other);
  delete from public.friend_invites i
  where (i.from_user = auth.uid() and i.to_user = other) or (i.from_user = other and i.to_user = auth.uid());
end;
$$;

-- Block: ends any friendship, request and invite between you, and stops new ones.
create function public.block_user(other uuid)
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
  if auth.uid() is null then raise exception 'NOT_SIGNED_IN'; end if;
  if other = auth.uid() then raise exception 'CANNOT_BLOCK_SELF'; end if;
  perform public.remove_friend(other);
  insert into public.blocks (blocker, blocked) values (auth.uid(), other) on conflict do nothing;
end;
$$;

create function public.unblock_user(other uuid)
returns void
language sql
security definer
set search_path = ''
as $$
  delete from public.blocks where blocker = auth.uid() and blocked = other;
$$;

revoke execute on function public.send_friend_request(text) from public, anon;
revoke execute on function public.respond_friend_request(uuid, boolean) from public, anon;
revoke execute on function public.remove_friend(uuid) from public, anon;
revoke execute on function public.block_user(uuid) from public, anon;
revoke execute on function public.unblock_user(uuid) from public, anon;
grant execute on function public.send_friend_request(text) to authenticated;
grant execute on function public.respond_friend_request(uuid, boolean) to authenticated;
grant execute on function public.remove_friend(uuid) to authenticated;
grant execute on function public.block_user(uuid) to authenticated;
grant execute on function public.unblock_user(uuid) to authenticated;

-- Friends, incoming and outgoing requests, each with the other person's profile.
create function public.list_friends()
returns table (
  friendship_id uuid,
  user_id       uuid,
  username      text,
  display_name  text,
  avatar_url    text,
  state         text,          -- 'friend', 'incoming' or 'outgoing'
  since         timestamptz)
language sql
stable
security invoker
set search_path = ''
as $$
  select f.id,
         p.id, p.username, p.display_name, p.avatar_url,
         case when f.status = 'accepted' then 'friend'
              when f.addressee = auth.uid() then 'incoming'
              else 'outgoing' end,
         f.updated_at
  from public.friendships f
  join public.profiles p on p.id = case when f.requester = auth.uid() then f.addressee else f.requester end
  where auth.uid() in (f.requester, f.addressee)
  order by p.username;
$$;

revoke execute on function public.list_friends() from public, anon;
grant execute on function public.list_friends() to authenticated;

-- -----------------------------------------------------------------------------------------------
-- Row Level Security
-- -----------------------------------------------------------------------------------------------

alter table public.friendships enable row level security;
alter table public.blocks enable row level security;
alter table public.presence enable row level security;
alter table public.friend_invites enable row level security;

revoke all on public.friendships, public.blocks, public.presence, public.friend_invites from anon;
-- Writes to friendships and blocks only through the functions above.
revoke insert, update, delete on public.friendships, public.blocks from authenticated;

create policy "See your own friendships and requests"
  on public.friendships for select to authenticated
  using ((select auth.uid()) in (requester, addressee));

create policy "See whom you blocked"
  on public.blocks for select to authenticated
  using ((select auth.uid()) = blocker);

create policy "See your own and your friends' presence"
  on public.presence for select to authenticated
  using ((select auth.uid()) = user_id or public.are_friends((select auth.uid()), user_id));

create policy "Publish your own presence"
  on public.presence for insert to authenticated
  with check ((select auth.uid()) = user_id);

create policy "Update your own presence"
  on public.presence for update to authenticated
  using ((select auth.uid()) = user_id)
  with check ((select auth.uid()) = user_id);

create policy "See invites you sent or received"
  on public.friend_invites for select to authenticated
  using ((select auth.uid()) in (from_user, to_user) and expires_at > now());

create policy "Invite a friend"
  on public.friend_invites for insert to authenticated
  with check (
    (select auth.uid()) = from_user
    and public.are_friends(from_user, to_user)
    and expires_at <= now() + interval '15 minutes');

create policy "Dismiss or cancel an invite"
  on public.friend_invites for delete to authenticated
  using ((select auth.uid()) in (from_user, to_user));

revoke update on public.friend_invites from authenticated;

-- Server-owned timestamps on presence, and a cap on invites so nobody can flood a friend.
create function public.presence_touch()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  new.updated_at := now();
  return new;
end;
$$;

create trigger presence_touch before insert or update on public.presence
  for each row execute function public.presence_touch();

create function public.friend_invites_guard()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  delete from public.friend_invites where expires_at < now() - interval '1 hour';
  if (select count(*) from public.friend_invites
      where from_user = new.from_user and created_at > now() - interval '1 hour') >= 30 then
    raise exception 'TOO_MANY_INVITES';
  end if;
  new.created_at := now();
  return new;
end;
$$;

create trigger friend_invites_guard before insert on public.friend_invites
  for each row execute function public.friend_invites_guard();

revoke execute on function public.presence_touch() from public, anon, authenticated;
revoke execute on function public.friend_invites_guard() from public, anon, authenticated;

-- -----------------------------------------------------------------------------------------------
-- Realtime: friends' presence, new requests and invites arrive live (RLS still applies)
-- -----------------------------------------------------------------------------------------------

alter publication supabase_realtime add table public.friendships, public.presence, public.friend_invites;
