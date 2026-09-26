// Deletes the calling user's AxePower account (ACCOUNTS_PROFILE_PLAN.md, A1/A2).
//
// The caller proves who they are with their own access token; the secret key is used only to
// delete that same user, never anyone named in the request. Their profile and devices go with
// them through `on delete cascade`.
//
// The gateway's verify_jwt (on by default) already rejects calls without a valid user JWT; the
// token is checked again here with Supabase Auth, so a revoked session cannot delete anything.

import { createClient } from "npm:@supabase/supabase-js@2";

function json(status: number, body: Record<string, unknown>): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

// New projects expose a JSON dictionary of secret keys; older ones the legacy service_role key.
function secretKey(): string | undefined {
  const keys = Deno.env.get("SUPABASE_SECRET_KEYS");
  if (keys) {
    const parsed = JSON.parse(keys) as Record<string, string>;
    return parsed["default"] ?? Object.values(parsed)[0];
  }
  return Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? undefined;
}

Deno.serve(async (req) => {
  if (req.method !== "POST") return json(405, { error: "method_not_allowed" });

  const token = (req.headers.get("Authorization") ?? "").replace(/^Bearer\s+/i, "");
  if (!token) return json(401, { error: "not_signed_in" });

  const url = Deno.env.get("SUPABASE_URL");
  const key = secretKey();
  if (!url || !key) return json(500, { error: "function_not_configured" });

  const admin = createClient(url, key, {
    auth: { persistSession: false, autoRefreshToken: false },
  });

  const { data, error } = await admin.auth.getUser(token);
  if (error || !data.user) return json(401, { error: "session_expired" });

  const { error: deleteError } = await admin.auth.admin.deleteUser(data.user.id);
  if (deleteError) {
    console.error("delete-account failed", deleteError.message);
    return json(500, { error: "delete_failed" });
  }

  return json(200, { deleted: true });
});
