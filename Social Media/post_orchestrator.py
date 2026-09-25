import argparse
import json
import logging
import sys

# Ensure UTF-8 output encoding on Windows consoles
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

from config import Config
from generator import ContentGenerator
from platforms.discord_client import DiscordClient
from platforms.x_client import XClient
from platforms.linkedin_client import LinkedInClient
from platforms.reddit_client import RedditClient
from platforms.publora_client import PubloraClient

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("AxePowerSocial")


def show_status():
    status = Config.get_status()
    print("\n" + "=" * 55)
    print(" ⚡ AxePower Social Automation - Platform Status")
    print("=" * 55)
    for service, configured in status.items():
        icon = "✅ Ready" if configured else "❌ Missing Credentials"
        print(f" • {service.replace('_', ' ').title():<22}: {icon}")

    if status.get("publora_unified"):
        publora = PubloraClient()
        channels = publora.get_connected_channels()
        print("-" * 55)
        print(f" 🌐 Publora Connected Channels ({len(channels)}):")
        for ch in channels:
            platform = ch.get("platformId", "").split("-")[0].capitalize()
            username = ch.get("username", "N/A")
            token_status = ch.get("tokenStatus", "unknown")
            icon = "✅" if token_status == "valid" else "⚠️"
            print(f"   {icon} {platform:<12} : @{username}")
    print("=" * 55 + "\n")


def main():
    parser = argparse.ArgumentParser(description="AxePower Social Media Automation Engine")
    parser.add_argument("--status", action="store_true", help="Check status of configured platform credentials")
    parser.add_argument("--topic", type=str, default="", help="Feature, tip, or release topic to announce")
    parser.add_argument("--context", type=str, default="", help="Extra context, changelog notes, or details")
    parser.add_argument(
        "--mode",
        choices=["preview", "review", "direct"],
        default="preview",
        help="preview (terminal only), review (send to Discord review channel), direct (publish to platforms)",
    )
    parser.add_argument(
        "--platforms",
        type=str,
        default="x,linkedin,reddit,discord",
        help="Comma-separated platforms to publish to in direct mode",
    )

    args = parser.parse_args()

    if args.status:
        show_status()
        return

    topic = args.topic.strip()
    if not topic:
        topic = "AxePower v1.2: Smart Battery Threshold Limiter (80%) and Live Discharge Telemetry"

    print(f"\n⚡ Generating social copy for: '{topic}'...")
    generator = ContentGenerator()
    drafts = generator.generate(topic=topic, context=args.context)

    if args.mode == "preview":
        print("\n" + "=" * 60)
        print(" 🐦 X (Twitter) Preview")
        print("=" * 60)
        print(drafts.get("x", ""))

        print("\n" + "=" * 60)
        print(" 💼 LinkedIn Preview")
        print("=" * 60)
        print(drafts.get("linkedin", ""))

        print("\n" + "=" * 60)
        print(f" 👽 Reddit Preview (Target: r/{drafts.get('reddit', {}).get('suggested_subreddit', 'windows')})")
        print("=" * 60)
        print(f"Title: {drafts.get('reddit', {}).get('title', '')}\n")
        print(drafts.get("reddit", {}).get("body", ""))

        print("\n" + "=" * 60)
        print(" 🎮 Discord Preview")
        print("=" * 60)
        print(json.dumps(drafts.get("discord", {}), indent=2))
        print("=" * 60)
        print("\n💡 Run with '--mode review' to send these drafts to your Discord review channel,")
        print("   or '--mode direct --platforms x,discord' to publish immediately.\n")

    elif args.mode == "review":
        discord = DiscordClient()
        print("\n📤 Sending drafts to Discord Review Channel...")
        success = discord.post_review_drafts(drafts)
        if success:
            print("✅ Drafts posted to Discord for approval.")
        else:
            print("⚠️ Could not send to Discord review channel. Check DISCORD_BOT_TOKEN and DISCORD_REVIEW_CHANNEL_ID.")

    elif args.mode == "direct":
        selected = [p.strip().lower() for p in args.platforms.split(",")]
        print(f"\n🚀 Direct publishing to platforms: {', '.join(selected)}...")

        publora = PubloraClient()

        # If Publora is configured, route LinkedIn and X directly through Publora
        if publora.is_configured():
            if "publora" in selected:
                primary_content = drafts.get("linkedin") or drafts.get("x", "")
                print("📡 Dispatching unified post to all connected channels via Publora API...")
                publora.create_post(content=primary_content)

            elif "linkedin" in selected:
                li_text = drafts.get("linkedin", "")
                print(f"💼 Publishing directly to LinkedIn ({Config.LINKEDIN_PLATFORM_ID}) via Publora...")
                publora.create_post(content=li_text, platforms=[Config.LINKEDIN_PLATFORM_ID])

            if ("x" in selected or "twitter" in selected) and not Config.TWITTER_API_KEY:
                x_text = drafts.get("x", "")
                print(f"🐦 Publishing to Twitter/X ({Config.X_PLATFORM_ID}) via Publora...")
                publora.create_post(content=x_text, platforms=[Config.X_PLATFORM_ID])
        else:
            if "linkedin" in selected:
                li_client = LinkedInClient()
                li_client.post(drafts.get("linkedin", ""))

            if "x" in selected or "twitter" in selected:
                x_client = XClient()
                x_client.post_tweet(drafts.get("x", ""))

        if "reddit" in selected:
            reddit_client = RedditClient()
            r_data = drafts.get("reddit", {})
            reddit_client.submit_post(
                title=r_data.get("title", topic),
                body=r_data.get("body", ""),
                subreddit_name=r_data.get("suggested_subreddit"),
            )

        if "discord" in selected:
            discord = DiscordClient()
            discord.post_announcement_webhook(drafts.get("discord", {}))


if __name__ == "__main__":
    main()
