import json
import logging
from config import Config

logger = logging.getLogger(__name__)

AXEPOWER_SYSTEM_PROMPT = """You are the Lead Engineer and Creator of AxePower (mrAbhimanyuVishwakarma/AxePower).
AxePower is an open-source, modern Windows power management, battery telemetry, and automated performance profile utility for Windows 10/11 laptops and PCs.
Key features:
- Smart battery charge threshold limiting (stop charging at 80% to protect Li-ion battery health from continuous high-voltage degradation).
- Dynamic performance/efficiency profiles (Gaming, Quiet, Extreme Battery Saver, Balanced).
- Modern Fluent / Windows 11 dark UI with hardware telemetry (wattage draw, discharge rate, thermals).
- Safe, non-invasive Windows power plan switching and background process throttler.
- 100% free and open source.

TONE & VOICE GUIDELINES (Inspired by Serge Bulaev's Content Engineering & Humanizer Rules):
1. NO AI CLICHÉS OR TELLS: Strictly avoid words like "delve", "game-changer", "revolutionize", "testament", "unleash", "in today's fast-paced world", "tapestry", "plethora".
2. SOUND LIKE A REAL ENGINEER / FOUNDER: Write with conviction, clear tradeoffs, real developer anecdotes, and authentic problem-solving.
3. CONCISE & PUNCHY: Every sentence must earn its place. Use strong opening hooks.

PLATFORM FORMATTING:
1. X (Twitter):
   - Hook formula: Problem statement, surprising stat, or build-in-public lesson.
   - Max 260 characters or short 2-bullet format.
   - 2-3 focused hashtags (#Windows11, #PCGaming, #BatteryLife).
2. LinkedIn:
   - Founder / Systems Engineering perspective (why laptops degrade when plugged in 24/7).
   - Problem -> Engineering Solution -> Results / Metrics.
   - White space between paragraphs for readability, call to action.
3. Threads:
   - Conversational, behind-the-scenes indie dev tone.
4. Reddit:
   - Community-first, zero hype or sales jargon.
   - Explains the problem, how AxePower solves it, link to GitHub release / download, asks for honest technical feedback.
5. Discord:
   - Rich announcement embed format with highlights and release notes.

Return your response strictly in valid JSON matching this schema:
{
  "x": "...",
  "linkedin": "...",
  "threads": "...",
  "reddit": {
    "title": "...",
    "body": "...",
    "suggested_subreddit": "windows"
  },
  "discord": {
    "title": "...",
    "description": "...",
    "fields": [
      {"name": "...", "value": "..."}
    ]
  }
"""


class ContentGenerator:
    def __init__(self):
        self.api_key = Config.GEMINI_API_KEY
        self.client = None
        if self.api_key:
            try:
                from google import genai
                self.client = genai.Client(api_key=self.api_key)
            except ImportError:
                try:
                    import google.generativeai as genai
                    genai.configure(api_key=self.api_key)
                    self.client = genai.GenerativeModel("gemini-1.5-flash")
                except ImportError:
                    logger.warning("google-genai not installed. Using fallback template generator.")

    def generate(self, topic: str, context: str = "") -> dict:
        """Generates platform-specific social posts for the given topic."""
        if self.client:
            try:
                return self._generate_with_ai(topic, context)
            except Exception as e:
                logger.error(f"AI generation failed: {e}. Falling back to template generation.")

        return self._generate_fallback(topic, context)

    def _generate_with_ai(self, topic: str, context: str = "") -> dict:
        prompt = f"Topic / Announcement: {topic}\nAdditional Context: {context}\nWebsite: {Config.AXEPOWER_WEBSITE_URL}\nDownload: {Config.AXEPOWER_DOWNLOAD_URL}\nGitHub: {Config.AXEPOWER_GITHUB_URL}"
        
        # New Google GenAI SDK
        if hasattr(self.client, "models"):
            response = self.client.models.generate_content(
                model="gemini-3.6-flash",
                contents=[AXEPOWER_SYSTEM_PROMPT, prompt],
            )
            raw_text = response.text
        else:
            # Legacy GenerativeModel fallback
            chat = self.client.start_chat()
            response = chat.send_message(f"{AXEPOWER_SYSTEM_PROMPT}\n\n{prompt}")
            raw_text = response.text

        # Clean JSON markdown if wrapped
        raw_text = raw_text.strip()
        if raw_text.startswith("```json"):
            raw_text = raw_text[7:]
        if raw_text.startswith("```"):
            raw_text = raw_text[3:]
        if raw_text.endswith("```"):
            raw_text = raw_text[:-3]

        return json.loads(raw_text.strip())

    def _generate_fallback(self, topic: str, context: str = "") -> dict:
        """High quality deterministic fallback when API key is not present."""
        download_url = Config.AXEPOWER_DOWNLOAD_URL
        github_url = Config.AXEPOWER_GITHUB_URL

        x_post = (
            f"⚡ {topic}\n\n"
            f"Take complete control of your Windows power & battery health with AxePower:\n"
            f"• Real-time discharge wattage telemetry\n"
            f"• Smart 80% charge threshold limiter\n"
            f"• Instant gaming & quiet presets\n\n"
            f"Free & Open Source 👇\n"
            f"{download_url}\n\n"
            f"#Windows11 #LaptopBattery #AxePower #OpenSource"
        )

        linkedin_post = (
            f"🔋 {topic}\n\n"
            f"One of the biggest culprits of premature laptop battery degradation is holding modern lithium-ion cells at 100% capacity while constantly plugged into wall power.\n\n"
            f"With AxePower, we've engineered a lightweight, non-invasive power management utility for Windows 10 & 11 that puts control back into your hands:\n\n"
            f"✅ Smart battery health protection (configurable charge thresholds)\n"
            f"✅ Sub-second hardware power telemetry & real-time discharge curves\n"
            f"✅ Seamless automated power scheme switching between gaming and extreme battery saver\n"
            f"✅ Native Fluent UI built for Windows\n\n"
            f"AxePower is completely free and open-source.\n\n"
            f"🔗 Try it here: {download_url}\n"
            f"⭐ Star on GitHub: {github_url}\n\n"
            f"#SoftwareEngineering #OpenSource #Windows11 #BatteryOptimization #TechTools"
        )

        reddit_title = f"[Release / Update] AxePower - {topic}"
        reddit_body = (
            f"Hey everyone,\n\n"
            f"We just pushed a new update/feature for **AxePower**: **{topic}**.\n\n"
            f"For those who haven't seen it, AxePower is a free and open-source utility for Windows 10/11 designed to solve the annoying power management and battery health issues without heavy manufacturer bloatware.\n\n"
            f"### Highlights:\n"
            f"- **Smart Charging Limiter:** Prevent high-voltage degradation by capping charging at 80%.\n"
            f"- **Power Telemetry:** View live discharge rates (watts), battery health wear level, and temperatures.\n"
            f"- **Automated Profiles:** Auto-toggle between High Performance, Balanced, and Extreme Battery Saver.\n\n"
            f"{context if context else ''}\n\n"
            f"**Download & Info:**\n"
            f"- Website: [{download_url}]({download_url})\n"
            f"- GitHub Repository: [{github_url}]({github_url})\n\n"
            f"I'd love to hear your thoughts, bug reports, and feature requests below!"
        )

        discord_data = {
            "title": f"⚡ AxePower Update: {topic}",
            "description": f"A new update is available for AxePower!\n\n{context if context else 'Optimizations, power profile updates, and telemetry improvements.'}",
            "fields": [
                {
                    "name": "🚀 Features & Highlights",
                    "value": "• Smart Battery Charge Protection\n• Live Telemetry & Wattage Draw\n• Auto Profile Switching"
                },
                {
                    "name": "📥 Download & Source",
                    "value": f"[Download Installer]({download_url}) | [GitHub Repo]({github_url})"
                }
            ]
        }

        return {
            "x": x_post,
            "linkedin": linkedin_post,
            "reddit": {
                "title": reddit_title,
                "body": reddit_body,
                "suggested_subreddit": "windows"
            },
            "discord": discord_data
        }
