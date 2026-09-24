import http.server
import logging
import os
from pathlib import Path
import urllib.parse
import webbrowser
import requests
from dotenv import load_dotenv

logging.basicConfig(level=logging.INFO, format="%(message)s")
logger = logging.getLogger(__name__)

ENV_FILE = Path(__file__).parent / ".env"
load_dotenv(ENV_FILE)

CLIENT_ID = os.getenv("LINKEDIN_CLIENT_ID", "").strip()
CLIENT_SECRET = os.getenv("LINKEDIN_CLIENT_SECRET", "").strip()
REDIRECT_URI = "http://localhost:8080"
SCOPE = "w_member_social openid profile email"


def save_to_env(key: str, value: str):
    if not ENV_FILE.exists():
        return

    content = ENV_FILE.read_text(encoding="utf-8")
    lines = content.splitlines()
    updated = False
    new_lines = []

    for line in lines:
        if line.startswith(f"{key}="):
            new_lines.append(f"{key}={value}")
            updated = True
        else:
            new_lines.append(line)

    if not updated:
        new_lines.append(f"{key}={value}")

    ENV_FILE.write_text("\n".join(new_lines) + "\n", encoding="utf-8")


class CallbackHandler(http.server.BaseHTTPRequestHandler):
    auth_code = None

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        params = urllib.parse.parse_qs(parsed.query)

        if "code" in params:
            CallbackHandler.auth_code = params["code"][0]
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(b"""
                <html>
                <body style="font-family:sans-serif; text-align:center; padding:50px; background:#0f172a; color:#f8fafc;">
                    <h1 style="color:#00ff88;">&#9989; LinkedIn Authentication Successful!</h1>
                    <p>You can close this tab and return to your terminal.</p>
                </body>
                </html>
            """)
        else:
            error = params.get("error_description", ["Unknown error"])[0]
            self.send_response(400)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(f"<h3>Authentication failed: {error}</h3>".encode("utf-8"))

    def log_message(self, format, *args):
        pass  # Suppress server request logging


def main():
    global CLIENT_ID, CLIENT_SECRET

    if not CLIENT_ID or not CLIENT_SECRET:
        print("\n⚠️ LINKEDIN_CLIENT_ID or LINKEDIN_CLIENT_SECRET missing in .env.")
        CLIENT_ID = input("Enter LinkedIn Client ID: ").strip()
        CLIENT_SECRET = input("Enter LinkedIn Client Secret: ").strip()
        save_to_env("LINKEDIN_CLIENT_ID", CLIENT_ID)
        save_to_env("LINKEDIN_CLIENT_SECRET", CLIENT_SECRET)

    auth_url = (
        f"https://www.linkedin.com/oauth/v2/authorization?"
        f"response_type=code&client_id={CLIENT_ID}&redirect_uri={urllib.parse.quote(REDIRECT_URI)}"
        f"&scope={urllib.parse.quote(SCOPE)}&state=axepower_login_state"
    )

    print("\n" + "=" * 60)
    print(" 💼 AxePower LinkedIn OAuth Helper")
    print("=" * 60)
    print("1. Opening LinkedIn login in your default browser...")
    print(f"   (If it does not open, visit:\n   {auth_url} )\n")
    print("2. Waiting for authorization on http://localhost:8080 ...")

    webbrowser.open(auth_url)

    server = http.server.HTTPServer(("localhost", 8080), CallbackHandler)
    while not CallbackHandler.auth_code:
        server.handle_request()

    code = CallbackHandler.auth_code
    print("\n✅ Authorization code received! Exchanging for 60-day Access Token...")

    # Exchange code for access token
    token_url = "https://www.linkedin.com/oauth/v2/accessToken"
    payload = {
        "grant_type": "authorization_code",
        "code": code,
        "redirect_uri": REDIRECT_URI,
        "client_id": CLIENT_ID,
        "client_secret": CLIENT_SECRET,
    }

    resp = requests.post(token_url, data=payload, timeout=15)
    if resp.status_code != 200:
        print(f"❌ Failed to obtain access token ({resp.status_code}): {resp.text}")
        return

    data = resp.json()
    access_token = data.get("access_token")
    save_to_env("LINKEDIN_ACCESS_TOKEN", access_token)
    print(f"✅ Access Token obtained and saved to .env!")

    # Fetch User Profile URN
    userinfo_url = "https://api.linkedin.com/v2/userinfo"
    headers = {"Authorization": f"Bearer {access_token}"}
    user_resp = requests.get(userinfo_url, headers=headers, timeout=10)

    if user_resp.status_code == 200:
        user_data = user_resp.json()
        sub = user_data.get("sub")  # user ID
        person_urn = f"urn:li:person:{sub}"
        save_to_env("LINKEDIN_AUTHOR_URN", person_urn)
        print(f"✅ Author URN found: {person_urn}")
        print(f"   Logged in as: {user_data.get('name', 'Unknown')}")
    else:
        print("⚠️ Could not auto-detect profile URN, check your permissions.")

    print("\n🎉 LinkedIn configuration complete! Check with:")
    print("   python post_orchestrator.py --status\n")


if __name__ == "__main__":
    main()
