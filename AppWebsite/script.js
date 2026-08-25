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
  initGoogleAnalytics();
  initThemeToggle();
  initMobileMenu();
  initFaqAccordion();
  initMockupModeSwitcher();
  initSmoothScroll();
  initLatestRelease();
  initLegalModals();
});

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
      versionText.textContent = `${vTag} (Latest)`;
      
      // Sync all version indicators across headers, modals and footers
      document.querySelectorAll('.js-version-num').forEach(el => el.textContent = vTag.replace(/^v/i, ''));
      
      if (release.published_at) {
        const d = new Date(release.published_at);
        const dateFormatted = d.toLocaleDateString('en-US', { month: 'short', year: 'numeric' });
        document.querySelectorAll('.js-release-date').forEach(el => el.textContent = dateFormatted);
      }
    }

    return resolvedDownloadUrl;
  })
  .catch(err => {
    isFetching = false;
    console.warn('Could not auto-resolve latest release asset from GitHub API:', err);
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
        version: vTag || 'latest'
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
 * Mobile Navigation Toggle
 */
function initMobileMenu() {
  const toggleBtn = document.getElementById('mobileToggle');
  const navLinks = document.getElementById('navLinks');

  if (!toggleBtn || !navLinks) return;

  toggleBtn.addEventListener('click', () => {
    const isExpanded = toggleBtn.getAttribute('aria-expanded') === 'true';
    toggleBtn.setAttribute('aria-expanded', !isExpanded);
    navLinks.classList.toggle('mobile-active');
  });

  // Close menu on link click
  navLinks.querySelectorAll('.nav-link').forEach(link => {
    link.addEventListener('click', () => {
      toggleBtn.setAttribute('aria-expanded', 'false');
      navLinks.classList.remove('mobile-active');
    });
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
