import { PGlite } from '@electric-sql/pglite';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

// Runs every migration on PGlite (Postgres in WebAssembly) over a stand-in for Supabase's auth
// schema, then checks the Friends rules as three users. Not the real Supabase: a pass here means
// the SQL and its rules behave; the live project still has to be checked after a push.
//   cd supabase/tests && npm install && npm test
const here = dirname(fileURLToPath(import.meta.url));
const migrations = join(here, '..', 'migrations');

const db = new PGlite();
await db.exec(readFileSync(join(here, 'auth_stub.sql'), 'utf8'));
await db.exec('create publication supabase_realtime;');
for (const m of readdirSync(migrations).filter((f) => f.endsWith('.sql')).sort())
  await db.exec(readFileSync(join(migrations, m), 'utf8'));
console.log('MIGRATIONS-OK');

const A = '11111111-1111-1111-1111-111111111111', B = '22222222-2222-2222-2222-222222222222', C = '33333333-3333-3333-3333-333333333333';
await db.exec(`insert into auth.users values ('${A}'),('${B}'),('${C}');
  insert into public.profiles (id, username, display_name) values
    ('${A}','mrabhimanyu','Abhimanyu'),('${B}','player_two','Two'),('${C}','third_one','Three');`);

let fails = 0;
const results = [];
const check = (name, ok) => { results.push([ok, name]); if (!ok) fails++; };
const as = async (who, fn) => { await db.exec(`set role authenticated; set request.jwt.claim.sub = '${who}'`); try { return await fn(); } finally { await db.exec('reset role'); } };
const val = async (sql) => (await db.query(sql)).rows[0];
const err = async (sql) => { try { await db.query(sql); return null; } catch (e) { return e.message; } };

// Requests
await as(A, async () => {
  check('A requests @player_two', (await val(`select public.send_friend_request(' @Player_Two') r`)).r === 'REQUESTED');
  check('again: already requested', (await val(`select public.send_friend_request('player_two') r`)).r === 'ALREADY_REQUESTED');
  check('self: refused', (await err(`select public.send_friend_request('mrabhimanyu')`))?.includes('CANNOT_ADD_SELF'));
  check('unknown user: refused', (await err(`select public.send_friend_request('nobody_here')`))?.includes('USER_NOT_FOUND'));
  check('direct insert into friendships: denied', (await err(`insert into public.friendships (requester, addressee, status) values ('${A}','${C}','accepted')`)) !== null);
  check('A sees outgoing', (await val(`select state from public.list_friends() where username='player_two'`))?.state === 'outgoing');
});
await as(C, async () => check('C sees no one else\'s requests', (await val(`select count(*)::int n from public.friendships`)).n === 0));
await as(B, async () => {
  const row = await val(`select friendship_id, state from public.list_friends() where username='mrabhimanyu'`);
  check('B sees incoming', row?.state === 'incoming');
  await db.query(`select public.respond_friend_request('${row.friendship_id}', true)`);
  check('B accepted: friends', (await val(`select public.are_friends('${A}','${B}') f`)).f === true);
});

// Mutual request becomes a friendship
await as(C, async () => check('C requests A', (await val(`select public.send_friend_request('mrabhimanyu') r`)).r === 'REQUESTED'));
await as(A, async () => check('A asks C back: accepted', (await val(`select public.send_friend_request('third_one') r`)).r === 'ACCEPTED'));
await as(A, async () => check('A has two friends', (await val(`select count(*)::int n from public.list_friends() where state='friend'`)).n === 2));
await as(C, async () => await db.query(`select public.remove_friend('${A}')`));
await as(A, async () => check('C unfriended A', (await val(`select count(*)::int n from public.list_friends() where state='friend'`)).n === 1));

// Presence
await as(A, async () => {
  await db.query(`insert into public.presence (user_id, status, activity_kind, activity) values ('${A}','online','game','Valorant')`);
  check('activity with hidden chars: refused', (await err(`update public.presence set activity = E'Val\\u202Eorant' where user_id='${A}'`)) !== null);
  check('offline with an activity: refused', (await err(`update public.presence set status='offline' where user_id='${A}'`)) !== null);
  check('writing someone else\'s presence: refused', (await err(`insert into public.presence (user_id, status) values ('${B}','online')`)) !== null);
});
await as(B, async () => check('friend B sees A playing', (await val(`select activity from public.presence where user_id='${A}'`))?.activity === 'Valorant'));
await as(C, async () => check('non-friend C sees nothing', (await val(`select count(*)::int n from public.presence`)).n === 0));

// Invites
await as(A, async () => {
  check('A invites friend B', (await err(`insert into public.friend_invites (to_user, kind, room_link) values ('${B}','watch','http://100.64.0.2:8890/abc#v=secret')`)) === null);
  check('A invites non-friend C: refused', (await err(`insert into public.friend_invites (to_user, kind, room_link) values ('${C}','call','http://x/y')`)) !== null);
  check('bad link: refused', (await err(`insert into public.friend_invites (to_user, kind, room_link) values ('${B}','call','javascript:alert(1)')`)) !== null);
  check('expired invite: hidden', (await err(`insert into public.friend_invites (to_user, kind, room_link, expires_at) values ('${B}','call','http://x/old', now() - interval '1 minute')`)) === null);
});
await as(B, async () => check('B sees one live invite', (await val(`select count(*)::int n from public.friend_invites`)).n === 1));
await as(C, async () => check('C sees no invites', (await val(`select count(*)::int n from public.friend_invites`)).n === 0));
await as(B, async () => check('B cannot rewrite an invite', (await err(`update public.friend_invites set room_link='http://evil/'`)) !== null));

// Blocking
await as(B, async () => await db.query(`select public.block_user('${A}')`));
await as(A, async () => {
  check('block ended the friendship', (await val(`select count(*)::int n from public.list_friends()`)).n === 0);
  check('blocked user\'s request looks normal', (await val(`select public.send_friend_request('player_two') r`)).r === 'REQUESTED');
  check('...but nothing was created', (await val(`select count(*)::int n from public.friendships`)).n === 0);
  check('A cannot see the block', (await val(`select count(*)::int n from public.blocks`)).n === 0);
  check('A cannot invite B any more', (await err(`insert into public.friend_invites (to_user, kind, room_link) values ('${B}','call','http://x/y')`)) !== null);
});
await as(B, async () => {
  check('B sees whom they blocked', (await val(`select count(*)::int n from public.blocks`)).n === 1);
  check('B no longer sees A\'s presence', (await val(`select count(*)::int n from public.presence where user_id='${A}'`)).n === 0);
  check('B blocked A: B cannot request A', (await err(`select public.send_friend_request('mrabhimanyu')`))?.includes('YOU_BLOCKED_THEM'));
  await db.query(`select public.unblock_user('${A}')`);
  check('after unblock B can request A', (await val(`select public.send_friend_request('mrabhimanyu') r`)).r === 'REQUESTED');
});

// Anonymous callers and account deletion
await db.exec('set role anon');
check('anon cannot send requests', (await err(`select public.send_friend_request('mrabhimanyu')`)) !== null);
check('anon cannot read presence', (await err(`select * from public.presence`)) !== null);
await db.exec('reset role');
await db.exec(`delete from auth.users where id='${A}'`);
check('deleting A removes everything of A', (await val(`select (select count(*) from public.friendships) + (select count(*) from public.presence) + (select count(*) from public.friend_invites where from_user='${A}') n`)).n == 0);

for (const [ok, name] of results) console.log(ok ? 'PASS' : 'FAIL', name);
console.log(`${results.length - fails}/${results.length} passed`);
process.exitCode = fails ? 1 : 0;
