# Remote access (AxePower Mobile over the cloud): turned off, and the plan to fix it

Found 2026-10-06 during Prompt 25. The owner decided to turn it off until it is fixed properly.

## What was wrong

`CloudBridgeService` (added in `385b0d9`) connected to Supabase Realtime whenever you were signed in
and Remote Access was on. Remote Access was on by default.

- **Public topics.** It joined broadcast topics named `realtime:axepower:user_<user id>`,
  `user_<@username>` and `user_<email address before the @>`. It used only the publishable key,
  which is public and ships in the app. The channels were not private, and the user's access token
  was never sent.
- **Outbound.** Every second it broadcast the computer name, CPU/GPU load and temperatures, RAM
  use, power mode, battery state and charging settings, plus the playing song.
- **Inbound.** It obeyed `set_mode`, `set_battery_tuning` (conservation mode, rapid charge),
  `update_settings` and the music commands (`music_play`, `music_pause`, `music_next`,
  `music_seek`, `music_volume`, and others) from anything that broadcast on those topics.

So anyone who knew or guessed a user's @username (usernames are public: friend search shows them)
could watch that PC's status and change its power mode, battery charging and some settings.

## What changed (Prompt 25)

- `CloudBridgeService.IsAvailable` is `false`. The worker loop never connects while it is false,
  whatever `settings.json` says, so existing installs with `remoteAccess.enabled: true` are covered.
- `RemoteAccessSettings.Enabled` now defaults to `false`.
- Settings › AxePower Mobile shows the reason, and its toggle is disabled. Trust & Permissions lists
  remote access as "Turned off in this version".
- AxePower Mobile's remote features over the cloud stop working. Phone Hub on the same network
  (LAN Drop, with its access link) is not affected.

Tests: `TrustInventoryTests.RemoteAccess_StaysOff_UntilItsChannelIsPrivate`.

## The fix, before turning it back on

1. **Private channels.** Join with `config.private = true` and the user's access token (Realtime
   Authorization). Add RLS policies on `realtime.messages` so that only the authenticated owner of
   `user_<auth.uid()>` can send to or receive from their topic.
2. **One topic per user, by ID only.** Drop the @username and email-prefix topics. The phone signs
   in to the same account and uses the same ID.
3. **Device pairing.** Use the `devices` table (already in the schema, with a public key per
   device). A command must carry the sending device's signature, and the PC accepts commands only
   from devices the user approved.
4. **Command allow-list and limits.** Validate every payload, and apply each existing per-feature
   switch (`AllowPowerModeControl`, `AllowBatteryTuning`, `AllowSystemPower`) on the PC side.
   Rate-limit commands and log refusals.
5. **Less telemetry.** Send no computer name, unless the user names the PC for the phone. Send at
   most once every few seconds, and only while a phone is viewing.
6. **Tests**, including an end-to-end test against a local Supabase that proves an unauthenticated
   client can neither read nor send on another user's topic.

AxePower Mobile has to change at the same time (steps 1–3), so ship both together.
