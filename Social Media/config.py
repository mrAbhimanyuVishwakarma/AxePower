import os
from pathlib import Path
from dotenv import load_dotenv

# Load .env file from the current directory or parent
env_path = Path(__file__).parent / ".env"
load_dotenv(dotenv_path=env_path)


class Config:
    GEMINI_API_KEY = os.getenv("GEMINI_API_KEY", "")

    # Discord
    DISCORD_BOT_TOKEN = os.getenv("DISCORD_BOT_TOKEN", "")
    DISCORD_REVIEW_CHANNEL_ID = os.getenv("DISCORD_REVIEW_CHANNEL_ID", "")
    DISCORD_ANNOUNCEMENTS_WEBHOOK_URL = os.getenv("DISCORD_ANNOUNCEMENTS_WEBHOOK_URL", "")

    # X / Twitter
    TWITTER_API_KEY = os.getenv("TWITTER_API_KEY", "")
    TWITTER_API_SECRET = os.getenv("TWITTER_API_SECRET", "")
    TWITTER_ACCESS_TOKEN = os.getenv("TWITTER_ACCESS_TOKEN", "")
    TWITTER_ACCESS_TOKEN_SECRET = os.getenv("TWITTER_ACCESS_TOKEN_SECRET", "")
    TWITTER_BEARER_TOKEN = os.getenv("TWITTER_BEARER_TOKEN", "")

    # LinkedIn
    LINKEDIN_CLIENT_ID = os.getenv("LINKEDIN_CLIENT_ID", "")
    LINKEDIN_CLIENT_SECRET = os.getenv("LINKEDIN_CLIENT_SECRET", "")
    LINKEDIN_ACCESS_TOKEN = os.getenv("LINKEDIN_ACCESS_TOKEN", "")
    LINKEDIN_AUTHOR_URN = os.getenv("LINKEDIN_AUTHOR_URN", "")

    # Reddit
    REDDIT_CLIENT_ID = os.getenv("REDDIT_CLIENT_ID", "")
    REDDIT_CLIENT_SECRET = os.getenv("REDDIT_CLIENT_SECRET", "")
    REDDIT_USERNAME = os.getenv("REDDIT_USERNAME", "")
    REDDIT_PASSWORD = os.getenv("REDDIT_PASSWORD", "")
    REDDIT_USER_AGENT = os.getenv("REDDIT_USER_AGENT", "windows:axepower.social:v1.0")
    REDDIT_DEFAULT_SUBREDDIT = os.getenv("REDDIT_DEFAULT_SUBREDDIT", "windows")

    # Publora (Unified Multi-Platform: LinkedIn, X, Threads, Instagram, YouTube, TikTok, etc.)
    PUBLORA_API_KEY = os.getenv("PUBLORA_API_KEY", "")
    LINKEDIN_PLATFORM_ID = os.getenv("LINKEDIN_PLATFORM_ID", "linkedin-SJ3_6mcfX2")
    X_PLATFORM_ID = os.getenv("X_PLATFORM_ID", "twitter-1205009531173924900")
    APIFY_TOKEN = os.getenv("APIFY_TOKEN", "")
    PIXFARO_TOKEN = os.getenv("PIXFARO_TOKEN", "")

    # Links
    AXEPOWER_WEBSITE_URL = os.getenv("AXEPOWER_WEBSITE_URL", "https://axepower.app")
    AXEPOWER_DOWNLOAD_URL = os.getenv("AXEPOWER_DOWNLOAD_URL", "https://axepower.app/download.html")
    AXEPOWER_GITHUB_URL = os.getenv("AXEPOWER_GITHUB_URL", "https://github.com/mrAbhimanyuVishwakarma/AxePower")

    @classmethod
    def get_status(cls):
        """Returns a dict of which services are configured with active keys."""
        return {
            "publora_unified": bool(cls.PUBLORA_API_KEY),
            "gemini": bool(cls.GEMINI_API_KEY),
            "discord_bot": bool(cls.DISCORD_BOT_TOKEN),
            "discord_webhook": bool(cls.DISCORD_ANNOUNCEMENTS_WEBHOOK_URL),
            "twitter_direct": bool(
                cls.TWITTER_API_KEY
                and cls.TWITTER_API_SECRET
                and cls.TWITTER_ACCESS_TOKEN
                and cls.TWITTER_ACCESS_TOKEN_SECRET
            ),
            "linkedin_direct": bool(cls.LINKEDIN_ACCESS_TOKEN and cls.LINKEDIN_AUTHOR_URN),
            "reddit_direct": bool(
                cls.REDDIT_CLIENT_ID
                and cls.REDDIT_CLIENT_SECRET
                and cls.REDDIT_USERNAME
                and cls.REDDIT_PASSWORD
            ),
        }
