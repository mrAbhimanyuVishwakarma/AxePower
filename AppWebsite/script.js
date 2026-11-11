// ==========================================
// Google Analytics 4 (GA4) Configuration (100% Free)
// Replace 'G-XXXXXXXXXX' with your GA4 Measurement ID
// ==========================================
const GA_MEASUREMENT_ID = window.GA_MEASUREMENT_ID || 'G-DXTX507FC9';

function initGoogleAnalytics() {
  if (!GA_MEASUREMENT_ID || GA_MEASUREMENT_ID === 'G-XXXXXXXXXX') {
    // Analytics is disabled until a valid GA4 Measurement ID is provided
    return;
  }

  // Prevent duplicate script injection
  if (document.getElementById('ga-gtag-script')) return;

  const script = document.createElement('script');
  script.id = 'ga-gtag-script';
  script.async = true;
  script.src = `https://www.googletagmanager.com/gtag/js?id=${GA_MEASUREMENT_ID}`;
  document.head.appendChild(script);

  window.dataLayer = window.dataLayer || [];
  window.gtag = function() {
    window.dataLayer.push(arguments);
  };

  window.gtag('js', new Date());
  window.gtag('config', GA_MEASUREMENT_ID, {
    send_page_view: true
  });
}

/**
 * Custom Event Tracker Helper
 */
function trackAnalyticsEvent(eventName, params = {}) {
  if (typeof window.gtag === 'function') {
    window.gtag('event', eventName, params);
  }
}

document.addEventListener('DOMContentLoaded', () => {
  initSiteFooter();
  initGoogleAnalytics();
  initThemeToggle();
  initMobileMenu();
  initFaqAccordion();
  initMockupModeSwitcher();
  initScrollScrubVideo();
  initSmoothScroll();
  initLatestRelease();
  initReleasePage();
  initLegalModals();
});

/**
 * Render one consistent footer across every static page.
 */
function initSiteFooter() {
  const footer = document.querySelector('.site-footer');
  if (!footer) return;

  footer.innerHTML = `
    <section class="footer-cta" aria-label="Download AxePower">
      <div class="container footer-cta-inner">
        <p>One app. Every side of Windows.</p>
        <a href="https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases/latest" class="btn btn-primary js-direct-download">
          Download AxePower
        </a>
      </div>
    </section>
    <div class="footer-mega">
      <div class="container footer-mega-grid">
        <nav class="footer-group" aria-labelledby="footerProductTitle">
          <h2 id="footerProductTitle">Product</h2>
          <a href="download.html">Download</a>
          <a href="features.html">Features</a>
          <a href="whats-new.html">What’s New</a>
          <a href="store.html">Store</a>
          <a href="index.html#dashboard">Dashboard</a>
        </nav>
        <nav class="footer-group" aria-labelledby="footerSupportTitle">
          <h2 id="footerSupportTitle">Support</h2>
          <a href="help.html">Help &amp; Guides</a>
          <a href="support.html">Connect</a>
          <a href="index.html#faq">FAQ</a>
          <a href="https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues" target="_blank" rel="noopener noreferrer">Report an Issue</a>
          <a href="https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases" target="_blank" rel="noopener noreferrer">Previous Releases</a>
        </nav>
        <div class="footer-group">
          <h2>Company</h2>
          <a href="about.html">About</a>
          <a href="jobs.html">Jobs</a>
          <a href="get-involved.html">Get Involved</a>
          <a href="privacy.html">Privacy Policy</a>
          <a href="terms.html">Terms</a>
          <a href="#" data-cookie-settings>Cookie Settings</a>
        </div>
        <div class="footer-community">
          <h2>Follow AxePower</h2>
          <div class="footer-socials">
            <a href="https://github.com/mrAbhimanyuVishwakarma" target="_blank" rel="noopener noreferrer" aria-label="AxePower on GitHub">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2a10 10 0 0 0-3.16 19.49c.5.09.68-.22.68-.48v-1.69c-2.78.6-3.37-1.18-3.37-1.18-.45-1.16-1.11-1.47-1.11-1.47-.91-.62.07-.61.07-.61 1 .07 1.53 1.03 1.53 1.03.9 1.53 2.35 1.09 2.92.83.09-.65.35-1.09.64-1.34-2.22-.25-4.56-1.11-4.56-4.94 0-1.09.39-1.98 1.03-2.68-.1-.25-.45-1.27.1-2.64 0 0 .84-.27 2.75 1.02A9.56 9.56 0 0 1 12 7.7a9.5 9.5 0 0 1 2.5.34c1.9-1.29 2.74-1.02 2.74-1.02.55 1.37.2 2.39.1 2.64.64.7 1.02 1.59 1.02 2.68 0 3.84-2.34 4.68-4.57 4.93.36.31.68.92.68 1.86V21c0 .27.18.58.69.48A10 10 0 0 0 12 2Z"/></svg>
            </a>
            <a href="https://discord.gg/WfD4wZFJ8d" target="_blank" rel="noopener noreferrer" aria-label="AxePower Discord community">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19.5 5.34A16.3 16.3 0 0 0 15.44 4l-.5 1.02a15.2 15.2 0 0 0-5.87 0L8.56 4A16.5 16.5 0 0 0 4.5 5.35C1.93 9.17 1.23 12.9 1.58 16.58a16.7 16.7 0 0 0 4.98 2.5l1.2-1.63a10.8 10.8 0 0 1-1.88-.9l.46-.35c3.63 1.67 7.56 1.67 11.15 0l.47.35c-.6.35-1.23.65-1.89.9l1.2 1.63a16.6 16.6 0 0 0 4.98-2.5c.42-4.27-.72-7.96-2.75-11.24ZM8.35 14.32c-1.09 0-1.99-1-1.99-2.23s.88-2.24 1.99-2.24c1.11 0 2.01 1.01 1.99 2.24 0 1.23-.88 2.23-1.99 2.23Zm7.3 0c-1.1 0-1.99-1-1.99-2.23s.88-2.24 1.99-2.24c1.11 0 2 1.01 1.99 2.24 0 1.23-.88 2.23-1.99 2.23Z"/></svg>
            </a>
          </div>
          <h2>Support AxePower</h2>
          <a href="donate.html" class="btn btn-primary footer-donate">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78Z"/></svg>
            Donate
          </a>
          <p>Help keep AxePower independent and improving.</p>
        </div>
      </div>
      <div class="container footer-mega-bottom">
        <a href="index.html" class="footer-wordmark" aria-label="AxePower home">
          <img src="assets/AxePower_white.ico" alt="" width="26" height="26">
          <span>AxePower</span>
        </a>
        <p>&copy; <span id="copyrightYear">${new Date().getFullYear()}</span> AxePower. Windows control, made simpler.</p>
        <a href="#" class="footer-back-top" aria-label="Back to top">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 15 6-6 6 6"/></svg>
        </a>
      </div>
    </div>
  `;

  footer.querySelectorAll('[data-cookie-settings]').forEach(link => {
    link.addEventListener('click', event => {
      event.preventDefault();
      if (window.AxePowerConsent) window.AxePowerConsent.open();
    });
  });

  const backToTop = footer.querySelector('.footer-back-top');
  if (backToTop) {
    backToTop.addEventListener('click', event => {
      event.preventDefault();
      window.scrollTo({ top: 0, behavior: 'smooth' });
    });
  }
}

function updateThemeFavicon(theme) {
  const icoPath = theme === 'light' ? 'assets/AxePower_black.ico' : 'assets/AxePower_white.ico';
  const fav = document.getElementById('appFavicon');
  const shortFav = document.getElementById('appShortcutFavicon');
  const appleFav = document.getElementById('appAppleIcon');
  if (fav) fav.href = icoPath;
  if (shortFav) shortFav.href = icoPath;
  if (appleFav) appleFav.href = icoPath;
}

/**
 * Scrub the dashboard video as it travels through the viewport.
 * The poster remains visible when reduced motion is requested.
 */
function initScrollScrubVideo() {
  const video = document.getElementById('dashboardScrollVideo');
  if (!video) return;

  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const source = video.querySelector('source[data-src]');
  let duration = 0;
  let frameRequested = false;
  let sourceLoading = false;
  let sourceReady = false;
  let objectUrl = '';

  const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

  const updateVideoFrame = () => {
    frameRequested = false;
    if (!duration || reducedMotion.matches) return;

    const rect = video.getBoundingClientRect();
    const scrollStart = window.innerHeight * 0.88;
    const scrollEnd = window.innerHeight * 0.12 - rect.height;
    const progress = clamp(
      (scrollStart - rect.top) / (scrollStart - scrollEnd),
      0,
      1
    );
    const targetTime = progress * Math.max(duration - 0.05, 0);

    if (Math.abs(video.currentTime - targetTime) > 0.025) {
      video.currentTime = targetTime;
    }
  };

  const requestVideoFrame = () => {
    if (frameRequested) return;
    frameRequested = true;
    window.requestAnimationFrame(updateVideoFrame);
  };

  const applyMotionPreference = () => {
    window.removeEventListener('scroll', requestVideoFrame);

    if (reducedMotion.matches) {
      video.pause();
      video.currentTime = 0;
      return;
    }

    loadVideoSource();
    window.addEventListener('scroll', requestVideoFrame, { passive: true });
    requestVideoFrame();
  };

  const prepareVideo = () => {
    duration = Number.isFinite(video.duration) ? video.duration : 0;
    video.pause();
    applyMotionPreference();
  };

  async function loadVideoSource() {
    if (!source || sourceLoading || sourceReady || reducedMotion.matches) return;
    sourceLoading = true;

    try {
      const response = await fetch(source.dataset.src);
      if (!response.ok) throw new Error(`Video request failed: ${response.status}`);
      const blob = await response.blob();
      objectUrl = URL.createObjectURL(blob);
      video.src = objectUrl;
      video.addEventListener('loadedmetadata', prepareVideo, { once: true });
      video.load();
      sourceReady = true;
    } catch (error) {
      video.src = source.dataset.src;
      video.addEventListener('loadedmetadata', prepareVideo, { once: true });
      video.load();
      sourceReady = true;
    } finally {
      sourceLoading = false;
    }
  }

  reducedMotion.addEventListener('change', applyMotionPreference);
  applyMotionPreference();
  window.addEventListener('resize', requestVideoFrame, { passive: true });
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) requestVideoFrame();
  });
  window.addEventListener('pagehide', () => {
    if (objectUrl) URL.revokeObjectURL(objectUrl);
  }, { once: true });
}

/**
 * Theme Toggle (Dark Mode / Light Mode with localStorage persistence)
 */
function initThemeToggle() {
  const themeToggle = document.getElementById('themeToggle');
  if (!themeToggle) return;

  themeToggle.addEventListener('click', () => {
    const currentTheme = document.documentElement.getAttribute('data-theme') || 'dark';
    const nextTheme = currentTheme === 'dark' ? 'light' : 'dark';

    document.documentElement.setAttribute('data-theme', nextTheme);
    localStorage.setItem('axepower-theme', nextTheme);
    updateThemeFavicon(nextTheme);
  });

  // Listen for OS theme changes if user hasn't set manual override
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
    if (!localStorage.getItem('axepower-theme')) {
      const systemTheme = e.matches ? 'dark' : 'light';
      document.documentElement.setAttribute('data-theme', systemTheme);
      updateThemeFavicon(systemTheme);
    }
  });
}

/**
 * Modal Dialog Controller for Privacy, Terms, and Contact
 */
function initLegalModals() {
  const openButtons = document.querySelectorAll('.js-open-modal');
  const closeButtons = document.querySelectorAll('.js-close-modal');
  const backdrops = document.querySelectorAll('.legal-modal-backdrop');

  openButtons.forEach(btn => {
    btn.addEventListener('click', (e) => {
      e.preventDefault();
      const modalId = btn.getAttribute('data-modal');
      const targetModal = document.getElementById(modalId);
      if (targetModal) {
        targetModal.hidden = false;
        requestAnimationFrame(() => targetModal.classList.add('active'));
        document.body.style.overflow = 'hidden';
      }
    });
  });

  function closeModal(modal) {
    modal.classList.remove('active');
    setTimeout(() => {
      modal.hidden = true;
      document.body.style.overflow = '';
    }, 280);
  }

  closeButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      const modal = btn.closest('.legal-modal-backdrop');
      if (modal) closeModal(modal);
    });
  });

  backdrops.forEach(backdrop => {
    backdrop.addEventListener('click', (e) => {
      if (e.target === backdrop) {
        closeModal(backdrop);
      }
    });
  });

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      backdrops.forEach(backdrop => {
        if (backdrop.classList.contains('active')) {
          closeModal(backdrop);
        }
      });
    }
  });
}

/**
 * Dynamically fetch and trigger direct download of the latest GitHub Release installer
 */
function initLatestRelease() {
  const repo = 'mrAbhimanyuVishwakarma/AxePower_Download';
  const releasesApiUrl = `https://api.github.com/repos/${repo}/releases/latest`;
  const fallbackPageUrl = `https://github.com/${repo}/releases/latest`;
  
  const downloadLinks = document.querySelectorAll('.js-direct-download');
  const versionText = document.getElementById('latestVersionText');

  let resolvedDownloadUrl = null;
  let resolvedFileName = null;
  let resolvedVTag = 'latest';
  let isFetching = true;

  // Asynchronously resolve latest release from GitHub API
  const fetchPromise = fetch(releasesApiUrl, {
    headers: { 'Accept': 'application/vnd.github.v3+json' }
  })
  .then(res => {
    if (!res.ok) throw new Error(`GitHub API HTTP ${res.status}`);
    return res.json();
  })
  .then(release => {
    isFetching = false;
    window.AxePowerLatestRelease = release;
    if (release && Array.isArray(release.assets) && release.assets.length > 0) {
      // Find the Windows installer asset (.exe)
      const exeAsset = release.assets.find(a => a.name && a.name.toLowerCase().endsWith('.exe')) || release.assets[0];
      if (exeAsset && exeAsset.browser_download_url) {
        resolvedDownloadUrl = exeAsset.browser_download_url;
        resolvedFileName = exeAsset.name;

        // Update all download buttons on the page with direct file download URL
        downloadLinks.forEach(link => {
          link.href = resolvedDownloadUrl;
          link.setAttribute('download', resolvedFileName);
          link.setAttribute('title', `Download ${resolvedFileName}`);
        });
      }
    }

    if (versionText && (release.tag_name || release.name)) {
      const vTag = release.tag_name || release.name;
      resolvedVTag = vTag;
      versionText.textContent = `${vTag} (Latest)`;
      
      // Sync all version indicators across headers, modals and footers
      document.querySelectorAll('.js-version-num').forEach(el => el.textContent = vTag.replace(/^v/i, ''));
      
      if (release.published_at) {
        const d = new Date(release.published_at);
        const dateFormatted = d.toLocaleDateString('en-US', { month: 'short', year: 'numeric' });
        document.querySelectorAll('.js-release-date').forEach(el => el.textContent = dateFormatted);
      }
    }

    window.dispatchEvent(new CustomEvent('axepower:release-ready', { detail: release }));

    return resolvedDownloadUrl;
  })
  .catch(err => {
    isFetching = false;
    console.warn('Could not auto-resolve latest release asset from GitHub API:', err);
    window.dispatchEvent(new CustomEvent('axepower:release-error'));
    return null;
  });

  // Keep copyright year synchronized
  const yearEl = document.getElementById('copyrightYear');
  if (yearEl) {
    yearEl.textContent = new Date().getFullYear().toString();
  }

  // Attach dynamic click handler to all download buttons
  downloadLinks.forEach(link => {
    link.addEventListener('click', async (e) => {
      // Track the download click event in Google Analytics
      trackAnalyticsEvent('file_download', {
        file_name: resolvedFileName || 'AxePower_Setup.exe',
        file_extension: 'exe',
        link_url: resolvedDownloadUrl || fallbackPageUrl,
        link_text: (link.textContent || 'Download').trim(),
        version: resolvedVTag || 'latest'
      });

      // If we already have the direct download URL attached, allow standard direct download
      if (resolvedDownloadUrl && link.href === resolvedDownloadUrl) {
        return;
      }

      e.preventDefault();

      // If still fetching, wait for resolution
      if (isFetching) {
        link.style.opacity = '0.7';
        link.style.pointerEvents = 'none';
        try {
          const directUrl = await fetchPromise;
          if (directUrl) {
            triggerDirectDownload(directUrl, resolvedFileName);
            return;
          }
        } catch (error) {
          console.error(error);
        } finally {
          link.style.opacity = '';
          link.style.pointerEvents = '';
        }
      }

      // If resolution failed, fallback to the latest releases page
      window.location.href = fallbackPageUrl;
    });
  });

  function triggerDirectDownload(url, filename) {
    const tempAnchor = document.createElement('a');
    tempAnchor.href = url;
    if (filename) tempAnchor.setAttribute('download', filename);
    tempAnchor.style.display = 'none';
    document.body.appendChild(tempAnchor);
    tempAnchor.click();
    document.body.removeChild(tempAnchor);
  }
}

/**
 * Populate the permanent What's New page from the latest GitHub release.
 */
function initReleasePage() {
  const page = document.getElementById('releasePage');
  if (!page) return;

  const versionEl = document.getElementById('releaseVersion');
  const factVersionEl = document.getElementById('releaseFactVersion');
  const dateEl = document.getElementById('releaseDate');
  const notesEl = document.getElementById('releaseNotesContent');
  const statusEl = document.getElementById('releaseStatus');
  const installerNameEl = document.getElementById('releaseInstallerName');
  const installerMetaEl = document.getElementById('releaseInstallerMeta');
  const githubLinkEl = document.getElementById('releaseGithubLink');

  const formatBytes = bytes => {
    if (!Number.isFinite(bytes) || bytes <= 0) return 'Windows x64 installer';
    return `${(bytes / 1048576).toFixed(1)} MB · Windows x64 installer`;
  };

  const appendInlineMarkdown = (parent, value) => {
    const tokenPattern = /(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\(https?:\/\/[^)]+\))/g;
    let cursor = 0;
    value.replace(tokenPattern, (token, _match, offset) => {
      parent.append(document.createTextNode(value.slice(cursor, offset)));
      if (token.startsWith('**')) {
        const strong = document.createElement('strong');
        strong.textContent = token.slice(2, -2);
        parent.append(strong);
      } else if (token.startsWith('`')) {
        const code = document.createElement('code');
        code.textContent = token.slice(1, -1);
        parent.append(code);
      } else {
        const parts = token.match(/^\[([^\]]+)\]\((https?:\/\/[^)]+)\)$/);
        const link = document.createElement('a');
        link.textContent = parts[1];
        link.href = parts[2];
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        parent.append(link);
      }
      cursor = offset + token.length;
      return token;
    });
    parent.append(document.createTextNode(value.slice(cursor)));
  };

  const renderNotes = body => {
    notesEl.replaceChildren();
    const lines = (body || '').split(/\r?\n/);
    let list = null;

    lines.forEach(line => {
      const value = line.trim();
      if (!value || /^#\s/.test(value)) {
        list = null;
        return;
      }

      if (/^##\s+/.test(value)) {
        list = null;
        const heading = document.createElement('h2');
        heading.textContent = value.replace(/^##\s+/, '');
        notesEl.append(heading);
        return;
      }

      if (/^-\s+/.test(value)) {
        if (!list) {
          list = document.createElement('ul');
          notesEl.append(list);
        }
        const item = document.createElement('li');
        appendInlineMarkdown(item, value.replace(/^-\s+/, ''));
        list.append(item);
        return;
      }

      list = null;
      const paragraph = document.createElement('p');
      appendInlineMarkdown(paragraph, value);
      notesEl.append(paragraph);
    });
  };

  const renderRelease = release => {
    if (!release) return;
    const version = release.tag_name || release.name || 'Latest';
    const displayVersion = version.replace(/^v/i, '');
    const installer = Array.isArray(release.assets)
      ? release.assets.find(asset => /\.exe$/i.test(asset.name || '')) || release.assets[0]
      : null;

    versionEl.textContent = displayVersion;
    factVersionEl.textContent = version;
    if (release.published_at) {
      dateEl.textContent = new Date(release.published_at).toLocaleDateString('en-US', {
        month: 'long', day: 'numeric', year: 'numeric'
      });
    }
    if (installer) {
      installerNameEl.textContent = installer.name || 'AxePower Setup';
      installerMetaEl.textContent = formatBytes(installer.size);
    }
    if (release.html_url) githubLinkEl.href = release.html_url;
    renderNotes(release.body);
    statusEl.hidden = true;
    document.title = `What's New in AxePower ${displayVersion}`;
  };

  window.addEventListener('axepower:release-ready', event => renderRelease(event.detail), { once: true });
  window.addEventListener('axepower:release-error', () => {
    statusEl.textContent = 'Live release notes could not be loaded. Use the GitHub link to view the latest release.';
  }, { once: true });
  if (window.AxePowerLatestRelease) renderRelease(window.AxePowerLatestRelease);
}

/**
 * Mobile Navigation Toggle
 */
function initMobileMenu() {
  const toggleBtn = document.getElementById('mobileToggle');
  const navLinks = document.getElementById('navLinks');

  if (!toggleBtn || !navLinks) return;

  function setMenuState(open) {
    toggleBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
    toggleBtn.classList.toggle('active', open);
    navLinks.classList.toggle('mobile-active', open);
    document.body.classList.toggle('menu-open', open);
  }

  toggleBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    const isExpanded = toggleBtn.getAttribute('aria-expanded') === 'true';
    setMenuState(!isExpanded);
  });

  // Close menu on link click
  navLinks.querySelectorAll('.nav-link').forEach(link => {
    link.addEventListener('click', () => {
      setMenuState(false);
    });
  });

  // Close when clicking outside
  document.addEventListener('click', (e) => {
    if (!navLinks.contains(e.target) && !toggleBtn.contains(e.target)) {
      if (toggleBtn.getAttribute('aria-expanded') === 'true') {
        setMenuState(false);
      }
    }
  });

  // Close on Escape key
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && toggleBtn.getAttribute('aria-expanded') === 'true') {
      setMenuState(false);
    }
  });

  // Close when window resized past mobile breakpoint
  window.addEventListener('resize', () => {
    if (window.innerWidth > 768 && toggleBtn.getAttribute('aria-expanded') === 'true') {
      setMenuState(false);
    }
  });
}

/**
 * FAQ Accordion
 */
function initFaqAccordion() {
  const faqItems = document.querySelectorAll('.faq-item');

  faqItems.forEach(item => {
    const questionBtn = item.querySelector('.faq-question');
    if (!questionBtn) return;

    questionBtn.addEventListener('click', () => {
      const isOpen = item.classList.contains('open');

      // Close all other items
      faqItems.forEach(otherItem => {
        if (otherItem !== item) {
          otherItem.classList.remove('open');
          const btn = otherItem.querySelector('.faq-question');
          if (btn) btn.setAttribute('aria-expanded', 'false');
        }
      });

      // Toggle current item
      item.classList.toggle('open', !isOpen);
      questionBtn.setAttribute('aria-expanded', !isOpen);
    });
  });
}

/**
 * Interactive Dashboard Mockup Mode Switcher
 */
function initMockupModeSwitcher() {
  const pills = document.querySelectorAll('.mockup-pill');
  const cpuVal = document.getElementById('mockCpuVal');
  const gpuVal = document.getElementById('mockGpuVal');
  const cpuFill = document.querySelector('.fill-cpu');
  const gpuFill = document.querySelector('.fill-gpu');
  const stateSubtext = document.querySelector('.mockup-subtext strong');

  if (!pills.length) return;

  const modeData = {
    eco: {
      label: 'Eco (Maximum Efficiency & Quiet Fans)',
      cpu: '18%',
      cpuWidth: '18%',
      gpu: '12%',
      gpuWidth: '12%',
      activeColor: '#10b981'
    },
    smart: {
      label: 'Smart (Dynamic Balanced Auto-Scale)',
      cpu: '28%',
      cpuWidth: '28%',
      gpu: '30%',
      gpuWidth: '30%',
      activeColor: '#00d4ff'
    },
    beast: {
      label: 'Beast (Maximum Clock Speeds & Wattage)',
      cpu: '48%',
      cpuWidth: '48%',
      gpu: '75%',
      gpuWidth: '75%',
      activeColor: '#ff385c'
    },
    game: {
      label: 'Game (Boosted Clocks & High Refresh Rate)',
      cpu: '64%',
      cpuWidth: '64%',
      gpu: '88%',
      gpuWidth: '88%',
      activeColor: '#8b5cf6'
    }
  };

  pills.forEach(pill => {
    pill.addEventListener('click', () => {
      const mode = pill.getAttribute('data-mode');
      if (!mode || !modeData[mode]) return;

      pills.forEach(p => {
        p.classList.remove('active');
        p.style.backgroundColor = '';
        p.style.boxShadow = '';
      });

      pill.classList.add('active');
      pill.style.backgroundColor = modeData[mode].activeColor;
      pill.style.boxShadow = `0 2px 10px ${modeData[mode].activeColor}66`;

      if (stateSubtext) {
        stateSubtext.textContent = modeData[mode].label;
      }
      if (cpuVal) cpuVal.textContent = modeData[mode].cpu;
      if (gpuVal) gpuVal.textContent = modeData[mode].gpu;
      if (cpuFill) cpuFill.style.width = modeData[mode].cpuWidth;
      if (gpuFill) gpuFill.style.width = modeData[mode].gpuWidth;
    });
  });
}

/**
 * Smooth link scrolling
 */
function initSmoothScroll() {
  document.querySelectorAll('a[href^="#"]').forEach(anchor => {
    anchor.addEventListener('click', function(e) {
      const targetId = this.getAttribute('href');
      if (targetId === '#' || targetId === '') return;

      const targetElem = document.querySelector(targetId);
      if (targetElem) {
        e.preventDefault();
        const headerOffset = 80;
        const elemPos = targetElem.getBoundingClientRect().top;
        const offsetPos = elemPos + window.pageYOffset - headerOffset;

        window.scrollTo({
          top: offsetPos,
          behavior: 'smooth'
        });
      }
    });
  });
}
