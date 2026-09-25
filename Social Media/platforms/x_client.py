import logging
from config import Config

logger = logging.getLogger(__name__)


class XClient:
    def __init__(self):
        self.api_key = Config.TWITTER_API_KEY
        self.api_secret = Config.TWITTER_API_SECRET
        self.access_token = Config.TWITTER_ACCESS_TOKEN
        self.access_token_secret = Config.TWITTER_ACCESS_TOKEN_SECRET
        self.bearer_token = Config.TWITTER_BEARER_TOKEN
        self.client = None

        if self.api_key and self.access_token:
            try:
                import tweepy
                self.client = tweepy.Client(
                    bearer_token=self.bearer_token or None,
                    consumer_key=self.api_key,
                    consumer_secret=self.api_secret,
                    access_token=self.access_token,
                    access_token_secret=self.access_token_secret
                )
            except ImportError:
                logger.warning("tweepy is not installed. Run 'pip install tweepy'.")

    def post_tweet(self, text: str) -> bool:
        """Posts a tweet to X using Twitter API v2."""
        if not self.client:
            logger.warning("X (Twitter) client not configured or missing API credentials.")
            return False

        try:
            response = self.client.create_tweet(text=text)
            tweet_id = response.data.get("id") if response.data else "unknown"
            logger.info(f"Successfully posted tweet to X (ID: {tweet_id}).")
            return True
        except Exception as e:
            logger.error(f"Failed to post tweet to X: {e}")
            return False
