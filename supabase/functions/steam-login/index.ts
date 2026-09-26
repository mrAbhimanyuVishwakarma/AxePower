// Sign in with Steam for AxePower (ACCOUNTS_PROFILE_PLAN.md, A5).
//
// Steam speaks OpenID 2.0, which Supabase Auth does not, so this function does the Steam half and
// then signs the user in to Supabase as the account mapped to that Steam ID:
//
//   1. GET  ?redirect=<loopback>&challenge=<pkce>  -> 302 to Steam's login page
//   2. GET  ?challenge=...&openid.*                -> Steam came back: verify it with Steam,
//                                                     find or create the user, 302 to the loopback
//                                                     with a one-time code
//   3. POST {action:"exchange", code, verifier}    -> the app trades the code (plus the PKCE verifier
//                                                     only it holds) for a Supabase session
//
// Tokens never appear in a URL or the browser's history. Deployed with verify_jwt off, because
// steps 1 and 2 are plain browser navigations that carry no Supabase JWT.

import { createClient } from "npm:@supabase/supabase-js@2";

const LOOPBACK = "http://127.0.0.1:53682/auth/callback";
const STEAM_OPENID = "https://steamcommunity.com/openid/login";
const CODE_TTL_MS = 2 * 60 * 1000;
const CHALLENGE = /^[A-Za-z0-9_-]{43}$/;
const CLAIMED_ID = /^https:\/\/steamcommunity\.com\/openid\/id\/(\d{17})$/;

const projectUrl = Deno.env.get("SUPABASE_URL")!;
const functionUrl = `${projectUrl}/functions/v1/steam-login`;

function key(dictionaryVar: string, legacyVar: string): string {
  const dictionary = Deno.env.get(dictionaryVar);
  if (dictionary) {
    const parsed = JSON.parse(dictionary) as Record<string, string>;
    return parsed["default"] ?? Object.values(parsed)[0];
  }
  return Deno.env.get(legacyVar)!;
}

const admin = createClient(projectUrl, key("SUPABASE_SECRET_KEYS", "SUPABASE_SERVICE_ROLE_KEY"), {
  auth: { persistSession: false, autoRefreshToken: false },
});

function redirect(location: string): Response {
  return new Response(null, { status: 302, headers: { Location: location, "Cache-Control": "no-store" } });
}

function backToApp(params: Record<string, string>): Response {
  return redirect(`${LOOPBACK}?${new URLSearchParams(params)}`);
}

function fail(status: number, code: string, msg: string): Response {
  return new Response(JSON.stringify({ error_code: code, msg }), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

async function sha256(text: string): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)));
}

function base64Url(bytes: Uint8Array): string {
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function hex(bytes: Uint8Array): string {
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

// -- 1. Send the browser to Steam -----------------------------------------------------------------

function start(url: URL): Response {
  const back = url.searchParams.get("redirect");
  const challenge = url.searchParams.get("challenge") ?? "";
  if (back !== LOOPBACK || !CHALLENGE.test(challenge)) {
    return fail(400, "invalid_request", "This sign-in link is not from AxePower.");
  }

  const openid = new URLSearchParams({
    "openid.ns": "http://specs.openid.net/auth/2.0",
    "openid.mode": "checkid_setup",
    "openid.return_to": `${functionUrl}?challenge=${challenge}`,
    "openid.realm": projectUrl,
    "openid.identity": "http://specs.openid.net/auth/2.0/identifier_select",
    "openid.claimed_id": "http://specs.openid.net/auth/2.0/identifier_select",
  });
  return redirect(`${STEAM_OPENID}?${openid}`);
}

// -- 2. Steam came back ---------------------------------------------------------------------------

async function steamReturned(url: URL): Promise<Response> {
  const p = url.searchParams;
  if (p.get("openid.mode") === "cancel") {
    return backToApp({ error: "access_denied", error_description: "Steam sign-in was cancelled." });
  }

  // The challenge is read from the return_to Steam signed, not from anything the browser could add.
  const returnTo = p.get("openid.return_to") ?? "";
  const challenge = returnTo.startsWith(`${functionUrl}?challenge=`)
    ? returnTo.slice(`${functionUrl}?challenge=`.length)
    : "";
  if (!CHALLENGE.test(challenge) || p.get("openid.op_endpoint") !== STEAM_OPENID) {
    return backToApp({ error: "invalid_request", error_description: "Steam's reply did not match this sign-in." });
  }

  const steamId = (p.get("openid.claimed_id") ?? "").match(CLAIMED_ID)?.[1];
  if (!steamId) {
    return backToApp({ error: "invalid_request", error_description: "Steam did not say which account signed in." });
  }

  // Ask Steam whether it really issued this reply (it also refuses a replayed nonce).
  const check = new URLSearchParams();
  for (const [k, v] of p) if (k.startsWith("openid.")) check.set(k, v);
  check.set("openid.mode", "check_authentication");
  const verdict = await fetch(STEAM_OPENID, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: check,
  }).then((r) => r.text());
  if (!/is_valid\s*:\s*true/.test(verdict)) {
    return backToApp({ error: "access_denied", error_description: "Steam could not confirm the sign-in." });
  }

  const persona = await playerSummary(steamId);
  const metadata = {
    provider: "steam",
    steam_id: steamId,
    preferred_username: persona?.personaname ?? "",
    full_name: persona?.personaname ?? "",
    avatar_url: persona?.avatarfull ?? "",
  };

  const userId = await findOrCreateUser(steamId, metadata);
  if (!userId) {
    return backToApp({ error: "server_error", error_description: "Your AxePower account could not be set up. Try again." });
  }

  const code = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  await admin.from("steam_login_codes").delete().lt("expires_at", new Date().toISOString());
  const { error } = await admin.from("steam_login_codes").insert({
    code_hash: hex(await sha256(code)),
    challenge,
    user_id: userId,
    expires_at: new Date(Date.now() + CODE_TTL_MS).toISOString(),
  });
  if (error) {
    console.error("steam-login: storing the code failed", error.message);
    return backToApp({ error: "server_error", error_description: "Sign-in could not be completed. Try again." });
  }

  return backToApp({ code });
}

async function playerSummary(steamId: string): Promise<{ personaname?: string; avatarfull?: string } | undefined> {
  const apiKey = Deno.env.get("STEAM_WEB_API_KEY");
  if (!apiKey) return undefined;
  try {
    const r = await fetch(
      `https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key=${apiKey}&steamids=${steamId}`,
    );
    const json = await r.json();
    return json?.response?.players?.[0];
  } catch {
    return undefined; // the name and picture are nice to have; sign-in works without them
  }
}

async function findOrCreateUser(steamId: string, metadata: Record<string, string>): Promise<string | undefined> {
  const { data: existing } = await admin.from("steam_accounts").select("user_id").eq("steam_id", steamId).maybeSingle();
  if (existing?.user_id) {
    await admin.auth.admin.updateUserById(existing.user_id, { user_metadata: metadata });
    return existing.user_id;
  }

  // Steam gives no email, and Supabase sessions are minted through one. This address is on the
  // reserved .invalid domain, so nothing can ever be delivered to it.
  const { data, error } = await admin.auth.admin.createUser({
    email: `steam-${steamId}@steam.axepower.invalid`,
    email_confirm: true,
    user_metadata: metadata,
    app_metadata: { provider: "steam", providers: ["steam"], steam_id: steamId },
  });
  if (error || !data.user) {
    console.error("steam-login: creating the user failed", error?.message);
    return undefined;
  }

  const { error: mapError } = await admin.from("steam_accounts").insert({ steam_id: steamId, user_id: data.user.id });
  if (mapError) {
    console.error("steam-login: mapping the Steam account failed", mapError.message);
    return undefined;
  }
  return data.user.id;
}

// -- 3. The app trades the code for a session ------------------------------------------------------

async function exchange(req: Request): Promise<Response> {
  let body: { action?: string; code?: string; verifier?: string };
  try {
    body = await req.json();
  } catch {
    return fail(400, "invalid_request", "Expected JSON.");
  }
  if (body.action !== "exchange" || !body.code || !body.verifier) {
    return fail(400, "invalid_request", "Missing code or verifier.");
  }

  // Deleting while reading makes the code single-use even if two requests race.
  const { data: rows } = await admin
    .from("steam_login_codes")
    .delete()
    .eq("code_hash", hex(await sha256(body.code)))
    .select("challenge, user_id, expires_at");
  const row = rows?.[0];

  if (!row || new Date(row.expires_at).getTime() < Date.now()) {
    return fail(400, "invalid_code", "This Steam sign-in has expired. Try again.");
  }
  if (base64Url(await sha256(body.verifier)) !== row.challenge) {
    return fail(400, "invalid_code", "This Steam sign-in did not start in this copy of AxePower.");
  }

  const { data: user } = await admin.auth.admin.getUserById(row.user_id);
  const email = user?.user?.email;
  if (!email) return fail(500, "server_error", "Your account could not be loaded.");

  // Mint a normal Supabase session: a magic link generated here (never emailed), redeemed at once.
  const { data: link, error: linkError } = await admin.auth.admin.generateLink({ type: "magiclink", email });
  if (linkError || !link?.properties?.hashed_token) {
    console.error("steam-login: generateLink failed", linkError?.message);
    return fail(500, "server_error", "Sign-in could not be completed.");
  }

  const publicClient = createClient(projectUrl, key("SUPABASE_PUBLISHABLE_KEYS", "SUPABASE_ANON_KEY"), {
    auth: { persistSession: false, autoRefreshToken: false },
  });
  const { data: verified, error: verifyError } = await publicClient.auth.verifyOtp({
    token_hash: link.properties.hashed_token,
    type: "magiclink",
  });
  if (verifyError || !verified.session) {
    console.error("steam-login: verifyOtp failed", verifyError?.message);
    return fail(500, "server_error", "Sign-in could not be completed.");
  }

  return new Response(JSON.stringify(verified.session), {
    status: 200,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
  });
}

Deno.serve(async (req) => {
  const url = new URL(req.url);
  try {
    if (req.method === "POST") return await exchange(req);
    if (req.method !== "GET") return fail(405, "method_not_allowed", "GET or POST only.");
    return url.searchParams.has("openid.mode") ? await steamReturned(url) : start(url);
  } catch (e) {
    console.error("steam-login: unexpected failure", e instanceof Error ? e.message : e);
    return fail(500, "server_error", "Steam sign-in failed. Try again.");
  }
});
