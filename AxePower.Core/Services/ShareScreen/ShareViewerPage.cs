using System;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// The page a viewer opens.
///
/// It exists instead of the media server's own player because control needs a channel back to the
/// host, and the server offers none. Placeholders in braces are substituted by
/// <see cref="ShareViewerHost"/> before the page is served.
/// </summary>
internal static class ShareViewerPage
{
    private static readonly Lazy<string> NobleScript = new(() =>
    {
        using var stream = typeof(ShareViewerPage).Assembly.GetManifestResourceStream("AxePower.ShareScreen.noble.min.js")
                           ?? throw new InvalidOperationException("The viewer page's crypto bundle is missing from the build.");
        using var reader = new System.IO.StreamReader(stream);
        // Inside an inline <script>, a literal "</script" would end the block early.
        return reader.ReadToEnd().Replace("</script", @"<\/script", StringComparison.OrdinalIgnoreCase);
    });

    /// <summary>The page with its vendored scripts inlined; placeholders other than these are left for the host.</summary>
    public static string WithScripts() => Html.Replace("__NOBLE__", NobleScript.Value);

    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>__TITLE__</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  html,body { margin:0; height:100%; background:#0A0B0D; color:#F2F3F5;
              font:14px/1.45 "Segoe UI",system-ui,-apple-system,sans-serif; overflow:hidden; }
  #stage { position:fixed; inset:0; display:flex; align-items:center; justify-content:center; }
  video { width:100%; height:100%; object-fit:contain; background:#0A0B0D; display:block; }
  #msg { position:fixed; inset:0; display:flex; align-items:center; justify-content:center;
         text-align:center; padding:24px; pointer-events:none; }
  #msg span { background:rgba(0,0,0,.6); padding:14px 18px; border-radius:10px; max-width:560px; }
  #bar { position:fixed; left:0; right:0; bottom:0; display:flex; gap:10px; align-items:center;
         justify-content:center; padding:12px; background:linear-gradient(transparent,rgba(0,0,0,.75));
         transition:opacity .25s; }
  #bar.hide { opacity:0; pointer-events:none; }
  button { font:600 13px/1 "Segoe UI",sans-serif; color:#F2F3F5; background:rgba(255,255,255,.14);
           border:0; border-radius:20px; padding:11px 18px; cursor:pointer; }
  button:hover { background:rgba(255,255,255,.26); }
  button.on { background:#23A55A; }
  button.off { background:#DA373C; }
  #warn { position:fixed; top:0; left:0; right:0; background:#DA373C; color:#fff; padding:9px 14px;
          font-weight:600; text-align:center; display:none; }
  #warn.show { display:block; }
  #pill { position:fixed; left:14px; bottom:64px; background:rgba(0,0,0,.72); padding:7px 11px;
          border-radius:8px; font-weight:600; font-size:12px; }
</style>
</head>
<body>
<div id="warn">You are controlling someone else's PC. Everything you type and click happens on their desktop.</div>
<div id="stage"><video id="v" autoplay playsinline></video></div>
<div id="msg"><span id="msgtext">Connecting...</span></div>
<div id="pill">__NAME__</div>
<div id="bar">
  <button id="ctl">Request control</button>
  <button id="fs">Fullscreen</button>
  <button id="snd">Unmute</button>
</div>
<script>
// AXE-CRYPTO-BEGIN
// The page is served over http on the LAN, where browsers withhold crypto.subtle, so the audited
// noble libraries do the work in script (see THIRD_PARTY_NOTICES.md). The control code and the
// viewer token are never sent: the page proves it knows them, then seals every input event with
// AES-256-GCM (docs/security/SHARE_SCREEN_THREAT_MODEL.md sections 4.2 and 7).
__NOBLE__
var AxeCrypto = (function () {
  var N = globalThis.AxeNoble;
  function utf8(s) { return new TextEncoder().encode(s); }
  function hex(bytes) {
    var s = '';
    for (var i = 0; i < bytes.length; i++) s += (bytes[i] < 16 ? '0' : '') + bytes[i].toString(16);
    return s;
  }
  function b64(bytes) {
    var s = '';
    for (var i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
    return btoa(s);
  }
  function hkdf(ikm, salt, info) { return N.hkdf(N.sha256, ikm, salt && salt.length ? salt : undefined, info, 32); }
  function hmac(key, msg) { return N.hmac(N.sha256, key, msg); }
  // 12-byte GCM nonce: four zero bytes, then the sequence as a 64-bit big-endian number.
  function nonceFor(seq) {
    var n = new Uint8Array(12), v = new DataView(n.buffer);
    v.setUint32(4, Math.floor(seq / 4294967296));
    v.setUint32(8, seq >>> 0);
    return n;
  }
  return {
    utf8: utf8, hex: hex, hkdf: hkdf, hmac: hmac,
    authKey: function (viewerToken) {
      return hkdf(utf8(viewerToken), null, utf8('AxePower-ShareScreen-Auth-v1'));
    },
    controlKey: function (viewerToken, code) {
      return hkdf(utf8(viewerToken), utf8(code), utf8('AxePower-ShareScreen-Control-v1'));
    },
    frameKey: function (controlKey, nonce) {
      return hkdf(controlKey, utf8(nonce), utf8('AxePower-ShareScreen-ControlFrames-v2'));
    },
    proof: function (key, channel, nonce) {
      return hex(hmac(key, utf8('axe-auth|' + channel + '|' + nonce)));
    },
    sealFrame: function (frameKey, seq, json) {
      return seq + '|' + b64(N.gcm(frameKey, nonceFor(seq), utf8('axe-ctl|' + seq)).encrypt(utf8(json)));
    }
  };
})();
// AXE-CRYPTO-END
</script>
<script>
(function () {
  var WHEP = "__WHEP__", WS = "__WS__";
  var v = document.getElementById('v'), msg = document.getElementById('msg'),
      msgtext = document.getElementById('msgtext'), bar = document.getElementById('bar'),
      ctl = document.getElementById('ctl'), warn = document.getElementById('warn');
  var sock = null, controlling = false, frameKey = null, frameSeq = 0;
  // The viewer token rides in the fragment, which the browser never sends. It stays in this page.
  var VIEWER = new URLSearchParams(location.hash.slice(1)).get('v') || '';

  function say(t) { msgtext.textContent = t; msg.style.display = t ? 'flex' : 'none'; }

  // ---- WHEP playback -----------------------------------------------------
  async function play() {
    say('Connecting...');
    var pc = new RTCPeerConnection({ iceServers: [] });
    pc.addTransceiver('video', { direction: 'recvonly' });
    pc.addTransceiver('audio', { direction: 'recvonly' });
    pc.ontrack = function (e) { v.srcObject = e.streams[0]; say(''); };
    pc.onconnectionstatechange = function () {
      if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
        say('Connection lost. Retrying...');
        try { pc.close(); } catch (_) {}
        setTimeout(play, 2000);
      }
    };

    try {
      await pc.setLocalDescription(await pc.createOffer());
      // Wait for ICE gathering: the server takes one complete offer, not trickled candidates.
      await new Promise(function (res) {
        if (pc.iceGatheringState === 'complete') return res();
        var t = setTimeout(res, 2500);
        pc.onicegatheringstatechange = function () {
          if (pc.iceGatheringState === 'complete') { clearTimeout(t); res(); }
        };
      });

      // The media server lets a viewer in only with a fresh single-use proof of the link's
      // token, so knowing the room path alone (it travels in plain HTTP) is not enough to watch.
      if (!VIEWER) throw new Error('this link is incomplete; ask the host for the full link');
      var nr = await fetch(location.pathname.replace(/\/+$/, '') + '/nonce', { cache: 'no-store' });
      if (!nr.ok) throw new Error('host busy (' + nr.status + ')');
      var watchNonce = (await nr.json()).n;
      var watchProof = AxeCrypto.proof(AxeCrypto.authKey(VIEWER), 'whep', watchNonce);
      var r = await fetch(WHEP + '?n=' + encodeURIComponent(watchNonce) + '&p=' + watchProof, {
        method: 'POST',
        headers: { 'Content-Type': 'application/sdp' },
        body: pc.localDescription.sdp
      });
      if (!r.ok) throw new Error('server said ' + r.status);
      await pc.setRemoteDescription({ type: 'answer', sdp: await r.text() });
    } catch (err) {
      say('Stream not ready (' + err.message + '). Retrying...');
      try { pc.close(); } catch (_) {}
      setTimeout(play, 2500);
    }
  }
  play();

  // ---- Controls ----------------------------------------------------------
  document.getElementById('fs').onclick = function () {
    if (document.fullscreenElement) document.exitFullscreen(); else document.documentElement.requestFullscreen();
  };
  var snd = document.getElementById('snd');
  v.muted = true;
  snd.onclick = function () { v.muted = !v.muted; snd.textContent = v.muted ? 'Unmute' : 'Mute'; };

  var hideTimer;
  document.onmousemove = function () {
    bar.classList.remove('hide');
    clearTimeout(hideTimer);
    hideTimer = setTimeout(function () { if (controlling) bar.classList.add('hide'); }, 2500);
  };

  // ---- Remote control ----------------------------------------------------
  // The watch link never carries the control code: the host reads it out separately, so passing
  // this page's URL to someone does not pass them the keyboard.
  ctl.onclick = function () {
    if (controlling) { stopControl(); return; }
    var code = prompt('Enter the control code the host gave you.\n\nThis lets you move the mouse and type on their PC.');
    if (code) startControl(code.trim());
  };

  // The code never leaves this page: the host hands out a single-use nonce, and the page answers
  // it with an HMAC keyed by the viewer token and the code. Every input event is then encrypted
  // under a key bound to that nonce and numbered, so nothing can be read, injected, replayed or
  // reordered on the way.
  async function startControl(code) {
    if (!VIEWER) { alert('This link is incomplete, so control cannot be requested. Ask the host for the full link.'); return; }
    var nonce;
    try {
      var r = await fetch(location.pathname.replace(/\/+$/, '') + '/nonce', { cache: 'no-store' });
      if (!r.ok) throw new Error(String(r.status));
      nonce = (await r.json()).n;
    } catch (_) {
      alert('The host is not answering. Try again in a moment.');
      return;
    }
    var key = AxeCrypto.controlKey(VIEWER, code.toUpperCase());
    frameKey = AxeCrypto.frameKey(key, nonce);
    frameSeq = 0;
    sock = new WebSocket(WS + '?n=' + encodeURIComponent(nonce) + '&p=' + AxeCrypto.proof(key, 'control', nonce));
    sock.onopen = function () {
      controlling = true;
      ctl.textContent = 'Release control';
      ctl.className = 'off';
      warn.classList.add('show');
      attach();
    };
    sock.onclose = function (e) {
      if (!controlling) alert('The host refused that code, control is switched off, or someone else is in control.');
      stopControl();
    };
    sock.onerror = function () { /* onclose reports it */ };
  }

  function stopControl() {
    controlling = false;
    detach();
    warn.classList.remove('show');
    bar.classList.remove('hide');
    ctl.textContent = 'Request control';
    ctl.className = '';
    if (sock) { try { sock.close(); } catch (_) {} sock = null; }
    frameKey = null;
  }

  function send(o) {
    if (!sock || sock.readyState !== 1 || !frameKey) return;
    var json = JSON.stringify(o);
    frameSeq++;
    sock.send(AxeCrypto.sealFrame(frameKey, frameSeq, json));
  }

  // The video is letterboxed inside the element, so the pointer has to be measured against the
  // picture rather than the box, or every click lands offset.
  function frac(e) {
    var r = v.getBoundingClientRect(), vw = v.videoWidth, vh = v.videoHeight;
    if (!vw || !vh) return null;
    var scale = Math.min(r.width / vw, r.height / vh), dw = vw * scale, dh = vh * scale;
    var ox = r.left + (r.width - dw) / 2, oy = r.top + (r.height - dh) / 2;
    var x = (e.clientX - ox) / dw, y = (e.clientY - oy) / dh;
    if (x < 0 || x > 1 || y < 0 || y > 1) return null;
    return [x, y];
  }

  var lastMove = 0;
  function onMove(e) {
    var now = performance.now();
    if (now - lastMove < 8) return;           // ~120 Hz is plenty and keeps the socket quiet
    lastMove = now;
    var f = frac(e);
    if (f) send({ t: 'm', x: f[0], y: f[1] });
  }
  function onDown(e) { if (frac(e)) { e.preventDefault(); send({ t: 'd', b: e.button }); } }
  function onUp(e) { if (frac(e)) { e.preventDefault(); send({ t: 'u', b: e.button }); } }
  function onWheel(e) { if (frac(e)) { e.preventDefault(); send({ t: 'w', d: e.deltaY > 0 ? -120 : 120 }); } }
  function onCtx(e) { e.preventDefault(); }

  var VK = {
    Backspace:8, Tab:9, Enter:13, ShiftLeft:160, ShiftRight:161, ControlLeft:162, ControlRight:163,
    AltLeft:164, AltRight:165, Pause:19, CapsLock:20, Escape:27, Space:32, PageUp:33, PageDown:34,
    End:35, Home:36, ArrowLeft:37, ArrowUp:38, ArrowRight:39, ArrowDown:40, Insert:45, Delete:46,
    MetaLeft:91, MetaRight:92, ContextMenu:93,
    NumpadMultiply:106, NumpadAdd:107, NumpadSubtract:109, NumpadDecimal:110, NumpadDivide:111,
    NumLock:144, ScrollLock:145,
    Semicolon:186, Equal:187, Comma:188, Minus:189, Period:190, Slash:191, Backquote:192,
    BracketLeft:219, Backslash:220, BracketRight:221, Quote:222
  };
  function vk(code) {
    if (VK[code] !== undefined) return VK[code];
    if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3);
    if (/^Digit[0-9]$/.test(code)) return code.charCodeAt(5);
    if (/^Numpad[0-9]$/.test(code)) return 96 + (code.charCodeAt(6) - 48);
    var f = /^F([1-9]|1[0-2])$/.exec(code);
    if (f) return 111 + parseInt(f[1], 10);
    return 0;
  }
  function onKey(e) {
    var code = vk(e.code);
    if (!code) return;
    // The browser keeps a few combinations for itself (Ctrl+W, F11); those cannot be forwarded.
    e.preventDefault();
    send({ t: 'k', c: code, p: e.type === 'keydown' ? 1 : 0 });
  }

  function attach() {
    v.addEventListener('mousemove', onMove);
    v.addEventListener('mousedown', onDown);
    window.addEventListener('mouseup', onUp);
    v.addEventListener('wheel', onWheel, { passive: false });
    v.addEventListener('contextmenu', onCtx);
    window.addEventListener('keydown', onKey);
    window.addEventListener('keyup', onKey);
  }
  function detach() {
    v.removeEventListener('mousemove', onMove);
    v.removeEventListener('mousedown', onDown);
    window.removeEventListener('mouseup', onUp);
    v.removeEventListener('wheel', onWheel);
    v.removeEventListener('contextmenu', onCtx);
    window.removeEventListener('keydown', onKey);
    window.removeEventListener('keyup', onKey);
  }

  window.addEventListener('beforeunload', stopControl);
  // Losing focus mid-chord would leave a modifier stuck on the host, so drop control on blur.
  window.addEventListener('blur', function () { if (controlling) send({ t: 'r' }); });
})();
</script>
</body>
</html>
""";
}
