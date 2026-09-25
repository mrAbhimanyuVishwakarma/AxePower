import logging
import requests
from config import Config

logger = logging.getLogger(__name__)


class LinkedInClient:
    def __init__(self):
        self.access_token = Config.LINKEDIN_ACCESS_TOKEN
        self.author_urn = Config.LINKEDIN_AUTHOR_URN  # e.g., urn:li:person:12345 or urn:li:organization:12345

    def post(self, text: str) -> bool:
        """Publishes a text/link post on LinkedIn using the UGC Post API."""
        if not self.access_token or not self.author_urn:
            logger.warning("LinkedIn access token or author URN is not configured.")
            return False

        url = "https://api.linkedin.com/v2/ugcPosts"
        headers = {
            "Authorization": f"Bearer {self.access_token}",
            "X-Restli-Protocol-Version": "2.0.0",
            "Content-Type": "application/json"
        }

        payload = {
            "author": self.author_urn,
            "lifecycleState": "PUBLISHED",
            "specificContent": {
                "com.linkedin.ugc.ShareContent": {
                    "shareCommentary": {
                        "text": text
                    },
                    "shareMediaCategory": "NONE"
                }
            },
            "visibility": {
                "com.linkedin.ugc.MemberNetworkVisibility": "PUBLIC"
            }
        }

        try:
            response = requests.post(url, headers=headers, json=payload, timeout=15)
            if response.status_code in (200, 201):
                post_urn = response.json().get("id", "unknown")
                logger.info(f"Successfully posted to LinkedIn (URN: {post_urn}).")
                return True
            else:
                logger.error(f"LinkedIn API error {response.status_code}: {response.text}")
                return False
        except Exception as e:
            logger.error(f"Failed to post to LinkedIn: {e}")
            return False
