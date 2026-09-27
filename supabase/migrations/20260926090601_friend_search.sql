-- Suggestions while typing in "Add a friend by @username" (owner request, 2026-09-26).
--
-- Matches @usernames or display names that start with what was typed (2+ characters, up to 8
-- results). Anyone can opt out with "Let people find me by name": they then appear only for an
-- exact @username, which already works for sending a request.

alter table public.profiles
  add column discoverable boolean not null default true;

comment on column public.profiles.discoverable is
  'Shown in name-search suggestions. Off: found only by exact @username.';

-- Starts-with searches on both names can use an index.
create index profiles_username_prefix_idx on public.profiles (username text_pattern_ops);
create index profiles_display_name_prefix_idx on public.profiles (lower(display_name) text_pattern_ops);

create function public.search_profiles(query text)
returns table (
  user_id      uuid,
  username     text,
  display_name text,
  avatar_url   text,
  state        text)          -- 'none', 'friend', 'incoming', 'outgoing' or 'blocked' (you blocked them)
language plpgsql
stable
security definer              -- to leave out people who blocked you, whose blocks you can't read
set search_path = ''
as $$
declare
  me uuid := auth.uid();
  q text := lower(ltrim(btrim(coalesce(query, '')), '@'));
  pattern text;
begin
  if me is null or char_length(q) < 2 or char_length(q) > 32 then
    return;
  end if;

  -- '_' is common in usernames and would otherwise be a LIKE wildcard.
  pattern := replace(replace(replace(q, '\', '\\'), '%', '\%'), '_', '\_') || '%';

  return query
  select p.id, p.username, p.display_name, p.avatar_url,
         case
           when exists (select 1 from public.blocks b where b.blocker = me and b.blocked = p.id) then 'blocked'
           when f.status = 'accepted' then 'friend'
           when f.status = 'pending' and f.addressee = me then 'incoming'
           when f.status = 'pending' then 'outgoing'
           else 'none'
         end
  from public.profiles p
  left join public.friendships f
    on least(f.requester, f.addressee) = least(me, p.id)
   and greatest(f.requester, f.addressee) = greatest(me, p.id)
  where p.id <> me
    and (p.username like pattern or lower(p.display_name) like pattern)
    and (p.discoverable or p.username = q)
    -- People who blocked you don't appear; people you blocked do, marked 'blocked'
    and not exists (select 1 from public.blocks b where b.blocker = p.id and b.blocked = me)
  order by (p.username = q) desc,
           (p.username like pattern) desc,
           p.username
  limit 8;
end;
$$;

revoke execute on function public.search_profiles(text) from public, anon;
grant execute on function public.search_profiles(text) to authenticated;
