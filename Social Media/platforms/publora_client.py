import logging
import os
import requests
from config import Config

logger = logging.getLogger(__name__)


class PubloraClient:
    """Publora Unified Multi-Platform Social Media Client.
    Supports publishing to LinkedIn, X/Twitter, Threads, Instagram, YouTube, TikTok, Facebook, Mastodon, Bluesky, and Telegram.
    """

    BASE_URL = "https://api.publora.com/api/v1"

    def __init__(self, api_key: str = None):
        self.api_key = api_key or os.getenv("PUBLORA_API_KEY", "")
        self._session = requests.Session()
        if self.api_key:
            self._session.headers.update({
                "x-publora-key": self.api_key,
                "Content-Type": "application/json"
            })

    def is_configured(self) -> bool:
        return bool(self.api_key)

    def get_connected_channels(self) -> list:
        """Returns the list of connected social media channels in Publora."""
        if not self.is_configured():
            logger.warning("PUBLORA_API_KEY is not configured.")
            return []

        try:
            resp = self._session.get(f"{self.BASE_URL}/platform-connections", timeout=15)
            if resp.status_code == 200:
                return resp.json().get("connections", [])
            else:
                logger.error(f"Publora error ({resp.status_code}): {resp.text}")
                return []
        except Exception as e:
            logger.error(f"Failed to fetch Publora connections: {e}")
            return []

    def create_post(self, content: str, platforms: list = None, media_urls: list = None, schedule_time: str = None) -> dict:
        """Publishes or schedules a post across connected platforms via Publora.
        
        Args:
            content: The text copy of the post.
            platforms: List of platform connection IDs or names (optional, defaults to all active).
            media_urls: Optional list of image/video URLs to attach.
            schedule_time: ISO-8601 string or None for immediate post.
        """
        if not self.is_configured():
            raise RuntimeError("PUBLORA_API_KEY is not set. Add it to .env.")

        payload = {
            "content": content,
        }

        if platforms:
            payload["platforms"] = platforms
        if media_urls:
            payload["mediaUrls"] = media_urls
        if schedule_time:
            payload["scheduledTime"] = schedule_time

        try:
            resp = self._session.post(f"{self.BASE_URL}/create-post", json=payload, timeout=20)
            if resp.status_code in (200, 201):
                data = resp.json()
                logger.info(f"Successfully created post via Publora (ID: {data.get('id', 'unknown')}).")
                return data
            else:
                logger.error(f"Publora post creation failed ({resp.status_code}): {resp.text}")
                return {"error": resp.text, "status_code": resp.status_code}
        except Exception as e:
            logger.error(f"Publora request exception: {e}")
            return {"error": str(e)}
