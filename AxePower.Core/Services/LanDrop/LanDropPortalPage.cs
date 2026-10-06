namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// The phone portal: one self-contained page (markup, styles and script) served at <c>/</c>.
/// Its script authenticates with the session cookie the server sets on first load and calls
/// only the routes in <see cref="LanDropService"/>; nothing in it holds the access token.
/// </summary>
internal static class LanDropPortalPage
{
    public const string Html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
    <title>AxePower Mobile Hub</title>
    <style>
        :root {
            --bg: #090D16;
            --surface: #111827;
            --surface-elevated: #1F2937;
            --card-border: #374151;
            --accent: #00F0FF;
            --accent-glow: rgba(0, 240, 255, 0.2);
            --text-primary: #F9FAFB;
            --text-secondary: #9CA3AF;
            --success: #10B981;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }
        body { background-color: var(--bg); color: var(--text-primary); padding: 16px; min-height: 100vh; display: flex; flex-direction: column; align-items: center; }
        .container { width: 100%; max-width: 540px; display: flex; flex-direction: column; gap: 18px; }
        .header { display: flex; align-items: center; justify-content: space-between; padding: 14px 18px; background: var(--surface); border: 1px solid var(--card-border); border-radius: 16px; box-shadow: 0 4px 20px rgba(0,0,0,0.4); }
        .logo-group { display: flex; align-items: center; gap: 12px; }
        .logo-badge { width: 36px; height: 36px; border-radius: 10px; background: linear-gradient(135deg, #00F0FF, #3B82F6); display: flex; align-items: center; justify-content: center; font-weight: bold; color: #090D16; font-size: 18px; }
        .title { font-size: 18px; font-weight: 700; }
        .status-pill { font-size: 12px; font-weight: 600; padding: 4px 10px; border-radius: 20px; background: rgba(16, 185, 129, 0.15); color: var(--success); border: 1px solid rgba(16, 185, 129, 0.3); display: flex; align-items: center; gap: 6px; }
        .status-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--success); }
        
        .tab-bar { display: flex; gap: 8px; background: var(--surface); padding: 6px; border-radius: 12px; border: 1px solid var(--card-border); }
        .tab-btn { flex: 1; padding: 10px; font-size: 13px; font-weight: 600; text-align: center; border-radius: 8px; background: transparent; border: none; color: var(--text-secondary); cursor: pointer; }
        .tab-btn.active { background: var(--accent); color: #090D16; }

        .card { background: var(--surface); border: 1px solid var(--card-border); border-radius: 16px; padding: 20px; box-shadow: 0 4px 20px rgba(0,0,0,0.3); }
        .card-title { font-size: 15px; font-weight: 600; margin-bottom: 14px; display: flex; align-items: center; gap: 8px; color: var(--text-primary); }
        .dropzone { border: 2px dashed var(--card-border); border-radius: 14px; padding: 28px 16px; text-align: center; background: rgba(31, 41, 55, 0.4); cursor: pointer; transition: all 0.2s ease; display: flex; flex-direction: column; align-items: center; gap: 10px; }
        .dropzone.dragover { border-color: var(--accent); background: var(--accent-glow); }
        .dropzone-icon { font-size: 32px; }
        .dropzone-text { font-size: 14px; color: var(--text-primary); font-weight: 500; }
        .dropzone-sub { font-size: 12px; color: var(--text-secondary); }
        .btn-upload { margin-top: 10px; background: var(--accent); color: #090D16; font-weight: 600; border: none; border-radius: 10px; padding: 10px 20px; font-size: 14px; cursor: pointer; }
        .progress-bar { width: 100%; height: 8px; background: #374151; border-radius: 4px; overflow: hidden; margin-top: 14px; display: none; }
        .progress-fill { height: 100%; background: var(--accent); width: 0%; transition: width 0.15s ease; }
        .file-list { display: flex; flex-direction: column; gap: 10px; margin-top: 8px; }
        .file-item { display: flex; align-items: center; justify-content: space-between; padding: 12px 14px; background: var(--surface-elevated); border: 1px solid var(--card-border); border-radius: 12px; }
        .file-info { display: flex; flex-direction: column; gap: 3px; max-width: 75%; }
        .file-name { font-size: 13px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .file-meta { font-size: 11px; color: var(--text-secondary); }
        .btn-download { background: rgba(0, 240, 255, 0.12); color: var(--accent); border: 1px solid rgba(0, 240, 255, 0.3); border-radius: 8px; padding: 8px 14px; font-size: 12px; font-weight: 600; text-decoration: none; display: inline-flex; align-items: center; gap: 4px; }
        .empty-msg { font-size: 13px; color: var(--text-secondary); text-align: center; padding: 16px; font-style: italic; }
        .text-input-group { display: flex; gap: 10px; margin-top: 8px; }
        .text-input { flex: 1; background: var(--surface-elevated); border: 1px solid var(--card-border); border-radius: 10px; padding: 10px 14px; color: var(--text-primary); font-size: 13px; outline: none; }
        .btn-send { background: var(--accent); color: #090D16; border: none; border-radius: 10px; padding: 10px 16px; font-weight: 600; font-size: 13px; cursor: pointer; }
        .clipboard-box { margin-top: 10px; background: rgba(31, 41, 55, 0.6); border: 1px solid var(--card-border); border-radius: 10px; padding: 12px 14px; font-size: 13px; display: flex; align-items: center; justify-content: space-between; }
        .btn-copy { background: transparent; border: 1px solid var(--card-border); color: var(--accent); border-radius: 6px; padding: 4px 10px; font-size: 11px; cursor: pointer; }

        /* Remote Controls */
        .battery-card { display: flex; align-items: center; justify-content: space-between; padding: 16px; background: #0E1626; border: 1px solid var(--card-border); border-radius: 14px; margin-bottom: 14px; }
        .battery-val { font-size: 24px; font-weight: bold; color: var(--accent); }
        .remote-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; }
        .btn-remote { background: var(--surface-elevated); border: 1px solid var(--card-border); color: var(--text-primary); border-radius: 12px; padding: 16px 8px; font-size: 13px; font-weight: 600; cursor: pointer; display: flex; flex-direction: column; align-items: center; gap: 6px; }
        .btn-remote:active { background: var(--accent-glow); border-color: var(--accent); }
        .toast { position: fixed; bottom: 20px; left: 50%; transform: translateX(-50%); background: #1F2937; border: 1px solid var(--accent); color: #fff; padding: 10px 20px; border-radius: 20px; font-size: 13px; box-shadow: 0 4px 20px rgba(0,0,0,0.5); opacity: 0; pointer-events: none; transition: opacity 0.2s; }
        .toast.show { opacity: 1; }
    </style>
</head>
<body>
    <div class=""container"">
        <header class=""header"">
            <div class=""logo-group"">
                <div class=""logo-badge"">⚡</div>
                <div>
                    <div class=""title"">AxePower Mobile Hub</div>
                    <div style=""font-size: 11px; color: var(--text-secondary);"" id=""hostSub"">AxePower PC</div>
                </div>
            </div>
            <div class=""status-pill"">
                <div class=""status-dot""></div>
                <span>Connected</span>
            </div>
        </header>

        <div class=""tab-bar"">
            <button class=""tab-btn active"" id=""tabDropBtn"" onclick=""switchTab('drop')"">📂 File Drop</button>
            <button class=""tab-btn"" id=""tabRemoteBtn"" onclick=""switchTab('remote')"">⚡ Remote PC</button>
        </div>

        <!-- Section 1: File Drop -->
        <div id=""secDrop"" style=""display: flex; flex-direction: column; gap: 16px;"">
            <section class=""card"">
                <div class=""card-title"">📤 Send Files to PC</div>
                <div class=""dropzone"" id=""dropzone"">
                    <div class=""dropzone-icon"">📁</div>
                    <div class=""dropzone-text"">Tap or Drop files here</div>
                    <div class=""dropzone-sub"">Instant transfer over local Wi-Fi</div>
                    <input type=""file"" id=""fileInput"" multiple style=""display:none;"">
                    <button class=""btn-upload"" onclick=""document.getElementById('fileInput').click()"">Choose Files</button>
                </div>
                <div class=""progress-bar"" id=""progressBar"">
                    <div class=""progress-fill"" id=""progressFill""></div>
                </div>
            </section>

            <section class=""card"">
                <div class=""card-title"">📥 Available on PC for Download</div>
                <div class=""file-list"" id=""sharedFileList"">
                    <div class=""empty-msg"">No files currently shared from PC</div>
                </div>
            </section>

            <section class=""card"">
                <div class=""card-title"">📋 Quick Text &amp; Links</div>
                <div class=""text-input-group"">
                    <input type=""text"" id=""quickTextInput"" class=""text-input"" placeholder=""Type a link, note or OTP to send to PC..."">
                    <button class=""btn-send"" onclick=""sendQuickText()"">Send</button>
                </div>
                <div id=""clipboardContainer"" style=""display:none;"">
                    <div style=""font-size: 11px; color: var(--text-secondary); margin-top: 14px;"">Latest from PC:</div>
                    <div class=""clipboard-box"">
                        <span id=""clipboardText"" style=""overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:80%;""></span>
                        <button class=""btn-copy"" onclick=""copyClipboard()"">Copy</button>
                    </div>
                </div>
            </section>
        </div>

        <!-- Section 2: Remote PC Controls -->
        <div id=""secRemote"" style=""display: none; flex-direction: column; gap: 16px;"">
            <section class=""card"">
                <div class=""card-title"">🔋 Live PC Telemetry</div>
                <div class=""battery-card"">
                    <div>
                        <div style=""font-size: 12px; color: var(--text-secondary);"">PC Battery State</div>
                        <div class=""battery-val"" id=""battPercent"">--%</div>
                    </div>
                    <div id=""battStatus"" style=""font-size: 13px; font-weight: 600; color: var(--success);"">AC POWER</div>
                </div>

                <div class=""card-title"" style=""margin-top: 10px;"">🎵 Media &amp; Audio Controls</div>
                <div class=""remote-grid"">
                    <button class=""btn-remote"" onclick=""sendAction('prev')""><span>⏮</span><span>Previous</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('play-pause')""><span>⏯</span><span>Play / Pause</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('next')""><span>⏭</span><span>Next</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('vol-down')""><span>🔉</span><span>Vol Down</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('mute')""><span>🔇</span><span>Mute</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('vol-up')""><span>🔊</span><span>Vol Up</span></button>
                </div>

                <div class=""card-title"" style=""margin-top: 20px;"">🔒 Power &amp; Security</div>
                <button class=""btn-remote"" style=""width: 100%; padding: 14px; background: #2A171D; border-color: #EF4444; color: #EF4444;"" onclick=""sendAction('lock')"">
                    <span>🔒</span><span>Lock PC Workstation</span>
                </button>
            </section>
        </div>
    </div>

    <div class=""toast"" id=""toast""></div>

    <script>
        // The QR link carries the access token once. Opening it set an HttpOnly session cookie,
        // so the token is taken out of the address bar straight away and every request after
        // this authenticates with the cookie. No script here ever holds or stores the token.
        if (new URLSearchParams(location.search).has('t')) {
            history.replaceState(null, '', location.pathname);
        }
        const axeUrl = (path) => path;
        const axeFetch = (path, init) => fetch(path, Object.assign({ credentials: 'same-origin' }, init));

        function showToast(msg) {
            const toast = document.getElementById('toast');
            toast.textContent = msg;
            toast.classList.add('show');
            setTimeout(() => toast.classList.remove('show'), 3000);
        }

        function switchTab(t) {
            if (t === 'drop') {
                document.getElementById('secDrop').style.display = 'flex';
                document.getElementById('secRemote').style.display = 'none';
                document.getElementById('tabDropBtn').classList.add('active');
                document.getElementById('tabRemoteBtn').classList.remove('active');
            } else {
                document.getElementById('secDrop').style.display = 'none';
                document.getElementById('secRemote').style.display = 'flex';
                document.getElementById('tabDropBtn').classList.remove('active');
                document.getElementById('tabRemoteBtn').classList.add('active');
            }
        }

        const dropzone = document.getElementById('dropzone');
        const fileInput = document.getElementById('fileInput');
        const progressBar = document.getElementById('progressBar');
        const progressFill = document.getElementById('progressFill');

        ['dragenter', 'dragover'].forEach(name => {
            dropzone.addEventListener(name, (e) => { e.preventDefault(); dropzone.classList.add('dragover'); });
        });
        ['dragleave', 'drop'].forEach(name => {
            dropzone.addEventListener(name, (e) => { e.preventDefault(); dropzone.classList.remove('dragover'); });
        });

        dropzone.addEventListener('drop', (e) => {
            if (e.dataTransfer.files.length > 0) uploadFiles(e.dataTransfer.files);
        });

        fileInput.addEventListener('change', () => {
            if (fileInput.files.length > 0) uploadFiles(fileInput.files);
        });

        async function uploadFiles(files) {
            progressBar.style.display = 'block';
            for (let i = 0; i < files.length; i++) {
                const file = files[i];
                const formData = new FormData();
                formData.append('file', file);

                try {
                    progressFill.style.width = '20%';
                    const xhr = new XMLHttpRequest();
                    xhr.open('POST', axeUrl('/api/upload'), true);
                    xhr.upload.onprogress = (e) => {
                        if (e.lengthComputable) {
                            progressFill.style.width = Math.round((e.loaded / e.total) * 100) + '%';
                        }
                    };
                    xhr.onload = () => {
                        if (xhr.status === 200) showToast('✅ Sent ' + file.name + ' to PC!');
                        else showToast('❌ Failed to upload ' + file.name);
                    };
                    xhr.onerror = () => showToast('❌ Network error uploading ' + file.name);
                    xhr.send(formData);
                } catch (err) {
                    showToast('❌ Error: ' + err.message);
                }
            }
            setTimeout(() => {
                progressBar.style.display = 'none';
                progressFill.style.width = '0%';
            }, 1000);
        }

        async function sendQuickText() {
            const input = document.getElementById('quickTextInput');
            const val = input.value.trim();
            if (!val) return;

            try {
                const res = await axeFetch('/api/text', { method: 'POST', body: val });
                if (res.ok) {
                    showToast('🚀 Text sent to PC!');
                    input.value = '';
                }
            } catch (err) {
                showToast('❌ Error sending text');
            }
        }

        async function sendAction(act) {
            try {
                const res = await axeFetch('/api/remote', { method: 'POST', body: act });
                if (res.ok) showToast('⚡ Action sent: ' + act);
            } catch (e) {
                showToast('❌ Action error');
            }
        }

        let currentClipboard = '';
        function copyClipboard() {
            if (currentClipboard) {
                navigator.clipboard.writeText(currentClipboard).then(() => showToast('📋 Copied to clipboard!'));
            }
        }

        async function refreshStatus() {
            try {
                const res = await axeFetch('/api/status');
                if (!res.ok) return;
                const data = await res.json();

                if (data.hostName) document.getElementById('hostSub').textContent = data.hostName + ' (' + data.ip + ')';

                if (data.battery) {
                    document.getElementById('battPercent').textContent = data.battery.percent + '%';
                    document.getElementById('battStatus').textContent = data.battery.charging ? '⚡ CHARGING / AC' : '🔋 ON BATTERY';
                    document.getElementById('battStatus').style.color = data.battery.charging ? 'var(--accent)' : 'var(--success)';
                }

                const listContainer = document.getElementById('sharedFileList');
                if (data.sharedFiles && data.sharedFiles.length > 0) {
                    listContainer.innerHTML = '';
                    data.sharedFiles.forEach(f => {
                        const item = document.createElement('div');
                        item.className = 'file-item';
                        item.innerHTML = `
                            <div class=""file-info"">
                                <div class=""file-name"" title=""${f.name}"">${f.name}</div>
                                <div class=""file-meta"">${f.formattedSize} • ${f.time}</div>
                            </div>
                            <a href=""${axeUrl(`/api/download?id=${f.id}`)}"" class=""btn-download"">⬇ Download</a>
                        `;
                        listContainer.appendChild(item);
                    });
                } else {
                    listContainer.innerHTML = '<div class=""empty-msg"">No files currently shared from PC</div>';
                }

                if (data.clipboard) {
                    currentClipboard = data.clipboard;
                    document.getElementById('clipboardText').textContent = data.clipboard;
                    document.getElementById('clipboardContainer').style.display = 'block';
                }
            } catch (e) {}
        }

        refreshStatus();
        setInterval(refreshStatus, 3000);
    </script>
</body>
</html>";
}
