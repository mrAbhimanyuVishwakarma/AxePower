import logging
import requests
from config import Config

logger = logging.getLogger(__name__)


class DiscordClient:
    def __init__(self):
        self.webhook_url = Config.DISCORD_ANNOUNCEMENTS_WEBHOOK_URL
        self.bot_token = Config.DISCORD_BOT_TOKEN
        self.review_channel_id = Config.DISCORD_REVIEW_CHANNEL_ID

    def post_announcement_webhook(self, discord_data: dict) -> bool:
        """Sends a rich announcement embed via Discord Webhook."""
        if not self.webhook_url:
            logger.warning("DISCORD_ANNOUNCEMENTS_WEBHOOK_URL not configured.")
            return False

        embed = {
            "title": discord_data.get("title", "⚡ AxePower Update"),
            "description": discord_data.get("description", ""),
            "color": 0x00FF88,  # AxePower Electric Green/Cyan accent
            "fields": discord_data.get("fields", []),
            "footer": {
                "text": "AxePower Social Automation • axepower.app"
            }
        }

        payload = {
            "username": "AxePower Bot",
            "avatar_url": "https://raw.githubusercontent.com/mrAbhimanyuVishwakarma/AxePower/main/AxePower.App/Assets/icon.ico",
            "embeds": [embed]
        }

        try:
            response = requests.post(self.webhook_url, json=payload, timeout=10)
            if response.status_code in (200, 204):
                logger.info("Successfully posted announcement to Discord Webhook.")
                return True
            else:
                logger.error(f"Discord Webhook error {response.status_code}: {response.text}")
                return False
        except Exception as e:
            logger.error(f"Failed to post to Discord Webhook: {e}")
            return False

    def post_review_drafts(self, drafts: dict) -> bool:
        """Posts drafted posts for all platforms into the private Discord review channel."""
        if not self.bot_token or not self.review_channel_id:
            logger.warning("DISCORD_BOT_TOKEN or DISCORD_REVIEW_CHANNEL_ID not configured for review bot.")
            return False

        headers = {
            "Authorization": f"Bot {self.bot_token}",
            "Content-Type": "application/json"
        }

        url = f"https://discord.com/api/v10/channels/{self.review_channel_id}/messages"

        embeds = [
            {
                "title": "📋 Social Automation: Pending Review Drafts",
                "description": "Review the generated drafts for **AxePower** across platforms below.",
                "color": 0x5865F2,
            },
            {
                "title": "🐦 X (Twitter) Draft",
                "description": drafts.get("x", "N/A"),
                "color": 0x1DA1F2,
            },
            {
                "title": "💼 LinkedIn Draft",
                "description": (drafts.get("linkedin", "N/A")[:2000]),
                "color": 0x0077B5,
            },
            {
                "title": f"👽 Reddit Draft ({drafts.get('reddit', {}).get('suggested_subreddit', 'windows')})",
                "description": f"**Title:** {drafts.get('reddit', {}).get('title', '')}\n\n**Body Preview:**\n{drafts.get('reddit', {}).get('body', '')[:1000]}...",
                "color": 0xFF4500,
            }
        ]

        payload = {
            "embeds": embeds
        }

        try:
            response = requests.post(url, headers=headers, json=payload, timeout=10)
            if response.status_code in (200, 201):
                logger.info("Successfully sent preview embeds to Discord review channel.")
                return True
            else:
                logger.error(f"Discord Review Bot error {response.status_code}: {response.text}")
                return False
        except Exception as e:
            logger.error(f"Failed to send to Discord Review Bot: {e}")
            return False
