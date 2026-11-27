import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { test } from 'node:test';
import { PGlite } from '@electric-sql/pglite';

const migrations = new URL('../migrations/', import.meta.url);
const retirement = '20261127000000_retire_social_endpoints.sql';
const owner = '11111111-1111-1111-1111-111111111111';
const other = '22222222-2222-2222-2222-222222222222';

test('the account API survives the upgrade and retired data is private', async () => {
  const db = new PGlite();
  const migrate = name => db.exec(readFileSync(new URL(name, migrations), 'utf8'));
  try {
    await db.exec(readFileSync(new URL('auth_stub.sql', import.meta.url), 'utf8'));
    await db.exec('create publication supabase_realtime;');
    for (const name of readdirSync(migrations).filter(name => name.endsWith('.sql')).sort()) {
      if (name === retirement) {
        await db.exec(`insert into auth.users values ('${owner}'), ('${other}');
          insert into public.profiles (id, username, display_name) values
            ('${owner}', 'sample_one', 'Sample One'), ('${other}', 'sample_two', 'Sample Two');
          insert into public.friendships (requester, addressee, status) values ('${owner}', '${other}', 'accepted');`);
      }
      await migrate(name);
    }

    assert.deepEqual((await db.query('select status from axepower_retired.friendships')).rows, [{ status: 'accepted' }]);
    assert.deepEqual((await db.query("select tablename from pg_publication_tables where pubname = 'supabase_realtime'")).rows, []);
    await db.exec(`set role authenticated; set request.jwt.claim.sub = '${owner}';`);
    try {
      assert.deepEqual((await db.query('select username from public.profiles')).rows, [{ username: 'sample_one' }]);
      await db.query("update public.profiles set display_name = 'Updated' where username = 'sample_one'");
      assert.deepEqual((await db.query('select display_name from public.profiles')).rows, [{ display_name: 'Updated' }]);
      assert.deepEqual((await db.query("select public.username_available('sample_two') as available")).rows, [{ available: false }]);
      await assert.rejects(db.query('select * from axepower_retired.friendships'), error => error.code === '42501');
      await assert.rejects(db.query('select * from public.list_friends()'), error => error.code === '42883');
    } finally {
      await db.exec('reset role');
    }

    await db.query('delete from auth.users where id = $1', [owner]);
    assert.deepEqual((await db.query('select id from public.profiles')).rows, [{ id: other }]);
    assert.deepEqual((await db.query('select * from axepower_retired.friendships')).rows, []);
  } finally {
    await db.close();
  }
});
