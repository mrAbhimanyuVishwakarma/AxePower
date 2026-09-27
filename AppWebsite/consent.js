/*
 * Analytics consent for the AxePower website.
 *
 * Google Analytics is not loaded, and sets no cookies, until the visitor chooses "Accept".
 * "Reject" keeps it off. The choice is kept in this browser (localStorage) and can be changed
 * at any time from the "Cookie settings" link in the footer. A browser sending the Global
 * Privacy Control signal is treated as a rejection until the visitor opts in themselves.
 */
(function () {
  'use strict';

  var STORAGE_KEY = 'axepower-analytics-consent'; // 'granted' | 'denied'
  var GA_ID = 'G-DXTX507FC9';
  var analyticsLoaded = false;
  var banner = null;

  function readChoice() {
    try { return localStorage.getItem(STORAGE_KEY); } catch (e) { return null; }
  }

  function saveChoice(value) {
    try { localStorage.setItem(STORAGE_KEY, value); } catch (e) { /* private mode: ask again next visit */ }
  }

  function loadAnalytics() {
    if (analyticsLoaded) return;
    analyticsLoaded = true;

    window.dataLayer = window.dataLayer || [];
    window.gtag = function () { window.dataLayer.push(arguments); };
    // Analytics only; nothing for advertising
    window.gtag('consent', 'default', {
      analytics_storage: 'granted',
      ad_storage: 'denied',
      ad_user_data: 'denied',
      ad_personalization: 'denied'
    });
    window.gtag('js', new Date());
    window.gtag('config', GA_ID, { anonymize_ip: true, allow_google_signals: false, allow_ad_personalization_signals: false });

    var script = document.createElement('script');
    script.async = true;
    script.src = 'https://www.googletagmanager.com/gtag/js?id=' + GA_ID;
    document.head.appendChild(script);
  }

  /** Removes Google Analytics cookies left from an earlier "Accept". */
  function clearAnalyticsCookies() {
    var host = location.hostname;
    var domains = ['', host, '.' + host, '.' + host.split('.').slice(-2).join('.')];
    document.cookie.split(';').forEach(function (cookie) {
      var name = cookie.split('=')[0].trim();
      if (name !== '_ga' && name.indexOf('_ga_') !== 0 && name !== '_gid' && name !== '_gat') return;
      domains.forEach(function (domain) {
        document.cookie = name + '=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/' + (domain ? '; domain=' + domain : '');
      });
    });
  }

  function accept() {
    saveChoice('granted');
    hideBanner();
    loadAnalytics();
  }

  function reject() {
    var wasLoaded = analyticsLoaded;
    saveChoice('denied');
    clearAnalyticsCookies();
    hideBanner();
    // A tag that already ran keeps running until the page reloads
    if (wasLoaded) location.reload();
  }

  function hideBanner() {
    if (banner) banner.hidden = true;
  }

  function showBanner() {
    if (!banner) banner = buildBanner();
    banner.hidden = false;
    var focusTarget = banner.querySelector('[data-consent="reject"]');
    if (focusTarget) focusTarget.focus();
  }

  function buildBanner() {
    var el = document.createElement('div');
    el.className = 'consent-banner';
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-live', 'polite');
    el.setAttribute('aria-label', 'Cookie consent');
    el.hidden = true;
    el.innerHTML =
      '<p class="consent-text">We would like to use analytics cookies to see which pages are useful. ' +
      'They are only set if you allow them, and the site works the same either way. ' +
      '<a href="privacy.html">Privacy Policy</a></p>' +
      '<div class="consent-actions">' +
      '<button type="button" class="btn btn-secondary consent-btn" data-consent="reject">Reject</button>' +
      '<button type="button" class="btn btn-primary consent-btn" data-consent="accept">Accept</button>' +
      '</div>';
    el.querySelector('[data-consent="accept"]').addEventListener('click', accept);
    el.querySelector('[data-consent="reject"]').addEventListener('click', reject);
    document.body.appendChild(el);
    return el;
  }

  // Start-up: analytics only for a visitor who has said yes
  var choice = readChoice();
  if (choice === 'granted') loadAnalytics();

  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('[data-cookie-settings]').forEach(function (link) {
      link.addEventListener('click', function (event) {
        event.preventDefault();
        showBanner();
      });
    });

    var gpc = navigator.globalPrivacyControl === true;
    if (!choice && !gpc) showBanner();
  });

  window.AxePowerConsent = { open: showBanner };
})();
