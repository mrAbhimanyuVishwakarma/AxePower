-- Profile hardening before Friends (ACCOUNTS_PROFILE_PLAN.md, security review of 2026-09-26).
--
-- 1. Avatars may only come from the sign-in providers' picture servers. Friends' apps load each
--    other's avatars, so an arbitrary https URL would let anyone collect the IP address of
--    everyone who sees their profile.
-- 2. Display names may not contain control or invisible/bidirectional formatting characters,
--    which can make one name render as another.

alter table public.profiles drop constraint profiles_avatar_url_https;

alter table public.profiles add constraint profiles_avatar_url_provider check (
  avatar_url is null
  or (char_length(avatar_url) <= 1024
      and (avatar_url ~ '^https://([a-z0-9-]+\.)*(googleusercontent\.com|githubusercontent\.com|discordapp\.com|steamstatic\.com)/'
           or avatar_url ~ '^https://steamcdn-a\.akamaihd\.net/'))
);

alter table public.profiles add constraint profiles_display_name_printable check (
  display_name !~ '[\u0001-\u001F\u007F-\u009F\u200B-\u200F\u202A-\u202E\u2060-\u2069\uFEFF]'
);
