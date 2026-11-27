-- Preserve migration history and existing records while removing the client API.
-- Account deletion still cascades through the archived tables' foreign keys.
create schema axepower_retired;
revoke all on schema axepower_retired from public, anon, authenticated;

alter publication supabase_realtime drop table public.friendships, public.presence, public.friend_invites;

drop policy "See your own and your friends' presence" on public.presence;
drop policy "Invite a friend" on public.friend_invites;
drop trigger presence_touch on public.presence;
drop trigger friend_invites_guard on public.friend_invites;

drop function public.search_profiles(text);
drop function public.list_friends();
drop function public.send_friend_request(text);
drop function public.respond_friend_request(uuid, boolean);
drop function public.remove_friend(uuid);
drop function public.block_user(uuid);
drop function public.unblock_user(uuid);
drop function public.presence_touch();
drop function public.friend_invites_guard();
drop function public.are_friends(uuid, uuid);

revoke all on public.friendships, public.blocks, public.presence, public.friend_invites
  from public, anon, authenticated;
alter table public.friendships set schema axepower_retired;
alter table public.blocks set schema axepower_retired;
alter table public.presence set schema axepower_retired;
alter table public.friend_invites set schema axepower_retired;

drop index public.profiles_username_prefix_idx;
drop index public.profiles_display_name_prefix_idx;
drop policy "Signed-in users can read profiles" on public.profiles;
create policy "Users read their own profile" on public.profiles for select
  to authenticated using ((select auth.uid()) = id);
