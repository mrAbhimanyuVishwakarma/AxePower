import logging
from config import Config

logger = logging.getLogger(__name__)


class RedditClient:
    def __init__(self):
        self.client_id = Config.REDDIT_CLIENT_ID
        self.client_secret = Config.REDDIT_CLIENT_SECRET
        self.username = Config.REDDIT_USERNAME
        self.password = Config.REDDIT_PASSWORD
        self.user_agent = Config.REDDIT_USER_AGENT
        self.reddit = None

        if self.client_id and self.client_secret and self.username and self.password:
            try:
                import praw
                self.reddit = praw.Reddit(
                    client_id=self.client_id,
                    client_secret=self.client_secret,
                    username=self.username,
                    password=self.password,
                    user_agent=self.user_agent
                )
            except ImportError:
                logger.warning("praw is not installed. Run 'pip install praw'.")

    def submit_post(self, title: str, body: str, subreddit_name: str = None) -> bool:
        """Submits a self/text post to the designated subreddit."""
        if not self.reddit:
            logger.warning("Reddit client is not configured or missing credentials.")
            return False

        target_sub = subreddit_name or Config.REDDIT_DEFAULT_SUBREDDIT

        try:
            subreddit = self.reddit.subreddit(target_sub)
            submission = subreddit.submit(title=title, selftext=body)
            logger.info(f"Successfully posted to Reddit r/{target_sub} (URL: {submission.url}).")
            return True
        except Exception as e:
            logger.error(f"Failed to submit to Reddit r/{target_sub}: {e}")
            return False
