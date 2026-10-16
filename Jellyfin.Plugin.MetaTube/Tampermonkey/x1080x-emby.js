// ==UserScript==
// @name         X1080X论坛 + Emby检测
// @namespace    http://tampermonkey.net/
// @version      1.6.0
// @description  检查Emby并汇总下载论坛附件
// @match        https://agaghhh.cc/forum.php*
// @grant        GM_xmlhttpRequest
// @connect      agaghhh.cc
// @connect      av.emby.cv.local
// @run-at       document-end
// ==/UserScript==

(function () {
    'use strict';

    const params = new URLSearchParams(location.search);
    if (params.get('mod') !== 'forumdisplay' || params.get('fid') !== '163') return;

    const CONFIG = {
        EMBY_BASE_URL: 'http://av.emby.cv.local',
        EMBY_API_KEY: '70952dc783d640099742ca18114ecdeb',
        EMBY_SERVER_ID: '6e78ed602ff94d159b3fe05af0181411',
        EMBY_CONCURRENCY: 3,
        THREAD_FETCH_CONCURRENCY: 3,
    };

    const CODE_REGEX = /\b[A-Za-z]{2,10}-\d{2,5}\b/;
    const embyCache = new Map();
    const pendingMap = new Map();
    const queue = [];
    let activeCount = 0;
    let attachmentItems = [];
    let uiStatus = null;
    let extractButton = null;
    let downloadButton = null;

    function runQueue() {
        while (activeCount < CONFIG.EMBY_CONCURRENCY && queue.length > 0) {
            const task = queue.shift();
            activeCount++;
            task().finally(() => {
                activeCount--;
                runQueue();
            });
        }
    }

    function enqueue(task) {
        return new Promise((resolve, reject) => {
            queue.push(() => task().then(resolve, reject));
            runQueue();
        });
    }

    function request(details) {
        return new Promise((resolve, reject) => {
            GM_xmlhttpRequest({
                ...details,
                onload: resolve,
                onerror: reject,
                ontimeout: reject,
            });
        });
    }

    async function queryEmby(code) {
        if (embyCache.has(code)) return embyCache.get(code);
        if (pendingMap.has(code)) return pendingMap.get(code);

        const promise = enqueue(async () => {
            try {
                const response = await request({
                    method: 'GET',
                    url: `${CONFIG.EMBY_BASE_URL}/emby/Items?IncludeItemTypes=Movie&Recursive=true&SearchTerm=${encodeURIComponent(code)}`,
                    headers: {
                        'X-Emby-Token': CONFIG.EMBY_API_KEY,
                        Accept: 'application/json',
                    },
                });

                const data = JSON.parse(response.responseText || '{}');
                const searchCode = code.toUpperCase();
                const movie = Array.isArray(data.Items)
                    ? data.Items.find(item =>
                        [item.Name, item.OriginalTitle].some(title =>
                            String(title || '').toUpperCase().includes(searchCode)
                        )
                    )
                    : null;
                const result = { exists: Boolean(movie), itemId: movie?.Id || null, queryOk: true };
                embyCache.set(code, result);
                return result;
            } catch (error) {
                console.warn('[X1080X Emby] 查询失败:', code, error);
                return { exists: false, itemId: null, queryOk: false };
            } finally {
                pendingMap.delete(code);
            }
        });

        pendingMap.set(code, promise);
        return promise;
    }

    function processTitle(anchor) {
        if (anchor.dataset.embyChecked === 'true') return;
        anchor.dataset.embyChecked = 'true';

        const match = anchor.textContent.match(CODE_REGEX);
        if (!match) return;
        const code = match[0].toUpperCase();

        queryEmby(code).then(result => {
            if (!result.exists || !result.itemId || anchor.dataset.embyLabelAdded === 'true') return;

            const label = document.createElement('a');
            label.textContent = '【已有】';
            label.title = `Emby中已存在：${code}`;
            label.href = `${CONFIG.EMBY_BASE_URL}/web/index.html#!/item?id=${encodeURIComponent(result.itemId)}&serverId=${encodeURIComponent(CONFIG.EMBY_SERVER_ID)}`;
            label.target = '_blank';
            label.rel = 'noopener noreferrer';
            label.style.cssText = [
                'color:#e60012',
                'font-weight:700',
                'margin-right:5px',
                'white-space:nowrap',
                'text-decoration:none',
            ].join(';');

            anchor.parentNode?.insertBefore(label, anchor);
            anchor.dataset.embyLabelAdded = 'true';
        });
    }

    function processTitles(root = document) {
        const selector = 'a.xst, h3.xw0 > a';
        if (root.matches?.(selector)) processTitle(root);
        root.querySelectorAll?.(selector).forEach(processTitle);
    }

    function getThreadCandidates() {
        const selector = 'a.xst, h3.xw0 > a';
        const seen = new Set();
        const candidates = [];

        document.querySelectorAll(selector).forEach(anchor => {
            const match = `${anchor.textContent || ''} ${anchor.title || ''}`.match(CODE_REGEX);
            if (!match) return;

            try {
                const url = new URL(anchor.href, location.href);
                if (url.hostname !== location.hostname || url.searchParams.get('mod') !== 'viewthread') return;
                const tid = url.searchParams.get('tid');
                if (!tid || seen.has(tid)) return;
                seen.add(tid);
                candidates.push({ code: match[0].toUpperCase(), url: url.href, tid });
            } catch {}
        });

        return candidates;
    }

    function fetchThreadAttachments(thread) {
        return new Promise((resolve, reject) => {
            let settled = false;
            const frame = document.createElement('iframe');
            frame.setAttribute('aria-hidden', 'true');
            frame.style.cssText = 'position:fixed;width:1px;height:1px;left:-10px;top:-10px;opacity:0;pointer-events:none;border:0;';

            const cleanup = () => {
                clearTimeout(timer);
                frame.onload = null;
                frame.onerror = null;
                frame.remove();
            };
            const finish = (callback, value) => {
                if (settled) return;
                settled = true;
                cleanup();
                callback(value);
            };
            const timer = setTimeout(() => finish(reject, new Error('详情页加载超时')), 30000);

            frame.onload = () => {
                try {
                    const frameWindow = frame.contentWindow;
                    const parsed = frame.contentDocument;
                    if (!frameWindow || !parsed || frameWindow.location.origin !== location.origin) {
                        throw new Error('详情页发生跨域跳转，无法读取页面内容');
                    }

                    const attachments = [];
                    parsed.querySelectorAll('.pattl a[href*="mod=attachment"], p.attnm a[href*="mod=attachment"]').forEach(link => {
                        try {
                            const url = new URL(link.getAttribute('href'), thread.url);
                            if (url.origin !== location.origin || url.searchParams.get('mod') !== 'attachment') return;

                            const filename = (link.textContent || link.title || '').trim();
                            if (!filename) return;
                            attachments.push({
                                code: thread.code,
                                threadUrl: thread.url,
                                url: url.href,
                                filename,
                            });
                        } catch {}
                    });

                    if (attachments.length === 0) {
                        const pageText = parsed.body?.innerText || '';
                        if (/登录后|请先登录|无权访问|权限不足|您无权/i.test(pageText)) {
                            throw new Error('论坛返回登录或权限提示');
                        }
                    }
                    finish(resolve, attachments);
                } catch (error) {
                    finish(reject, error);
                }
            };
            frame.onerror = () => finish(reject, new Error('浏览器无法加载详情页'));

            try {
                const url = new URL(thread.url, location.href);
                if (url.origin !== location.origin) throw new Error('详情页地址与论坛不同源');
                frame.src = url.href;
                document.body.appendChild(frame);
            } catch (error) {
                finish(reject, error);
            }
        });
    }

    function setPanelStatus(message, isError = false) {
        if (!uiStatus) return;
        uiStatus.textContent = message;
        uiStatus.style.color = isError ? '#b42318' : '#444';
    }

    function safeDownloadName(filename) {
        const safe = String(filename || 'attachment')
            .replace(/[<>:"\/\\|?*\u0000-\u001f]/g, '_')
            .replace(/[. ]+$/g, '')
            .trim();
        return safe || 'attachment';
    }

    function showDownloadDialog() {
        const overlay = document.createElement('div');
        overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.48);z-index:100000;display:flex;align-items:center;justify-content:center;padding:20px;';

        const modal = document.createElement('div');
        modal.style.cssText = 'width:min(720px,96vw);max-height:85vh;background:#fff;border-radius:8px;box-shadow:0 12px 40px rgba(0,0,0,.28);display:flex;flex-direction:column;color:#222;font:14px/1.5 Arial,sans-serif;';

        const header = document.createElement('div');
        header.style.cssText = 'padding:16px 20px;border-bottom:1px solid #ddd;font-size:17px;font-weight:700;';
        header.textContent = `确认下载附件（${attachmentItems.length} 个）`;

        const list = document.createElement('div');
        list.style.cssText = 'overflow:auto;padding:8px 20px;';

        attachmentItems.forEach((item, index) => {
            const row = document.createElement('label');
            row.style.cssText = 'display:flex;align-items:flex-start;gap:9px;padding:8px 2px;border-bottom:1px solid #eee;cursor:pointer;';

            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.checked = true;
            checkbox.dataset.attachmentIndex = String(index);
            checkbox.style.cssText = 'margin-top:4px;';

            const details = document.createElement('span');
            details.style.cssText = 'min-width:0;overflow-wrap:anywhere;';
            const name = document.createElement('span');
            name.textContent = item.filename;
            const code = document.createElement('span');
            code.textContent = `  (${item.code})`;
            code.style.cssText = 'color:#777;font-size:12px;';
            details.append(name, code);
            row.append(checkbox, details);
            list.appendChild(row);
        });

        const footer = document.createElement('div');
        footer.style.cssText = 'display:flex;justify-content:flex-end;gap:10px;padding:14px 20px;border-top:1px solid #ddd;';

        const cancel = document.createElement('button');
        cancel.textContent = '取消';
        cancel.style.cssText = 'padding:8px 16px;border:1px solid #bbb;border-radius:5px;background:#fff;cursor:pointer;';
        cancel.onclick = () => overlay.remove();

        const confirm = document.createElement('button');
        confirm.textContent = '确认下载';
        confirm.style.cssText = 'padding:8px 16px;border:0;border-radius:5px;background:#1677d2;color:#fff;cursor:pointer;';
        confirm.onclick = () => {
            const selected = [...list.querySelectorAll('input[type="checkbox"]')]
                .filter(input => input.checked)
                .map(input => attachmentItems[Number(input.dataset.attachmentIndex)]);
            if (selected.length === 0) {
                setPanelStatus('请至少勾选一个附件。', true);
                return;
            }
            overlay.remove();
            downloadAttachments(selected);
        };

        footer.append(cancel, confirm);
        modal.append(header, list, footer);
        overlay.appendChild(modal);
        overlay.addEventListener('click', event => {
            if (event.target === overlay) overlay.remove();
        });
        document.body.appendChild(overlay);
    }

    function triggerBrowserDownload(item) {
        const link = document.createElement('a');
        link.href = item.url;
        link.download = safeDownloadName(item.filename);
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.style.display = 'none';
        document.body.appendChild(link);
        link.click();
        link.remove();
    }

    function downloadAttachments(items) {
        for (let i = 0; i < items.length; i++) {
            setPanelStatus(`正在提交下载请求 ${i + 1}/${items.length}：${items[i].filename}`);
            triggerBrowserDownload(items[i]);
        }
        setPanelStatus(`已向浏览器提交 ${items.length} 个原生下载请求，请查看下载栏；若浏览器询问多个文件下载，请允许。`);
    }

    async function extractAttachments() {
        const threads = getThreadCandidates();
        if (threads.length === 0) {
            setPanelStatus('当前页面没有识别到带番号的帖子。', true);
            return;
        }

        extractButton.disabled = true;
        downloadButton.disabled = true;
        attachmentItems = [];
        setPanelStatus(`正在检查 ${threads.length} 个番号是否已在 Emby…`);

        const lookupResults = await Promise.all(threads.map(thread => queryEmby(thread.code)));
        const absentThreads = [];
        let embyLookupFailures = 0;
        threads.forEach((thread, index) => {
            const result = lookupResults[index];
            if (!result.queryOk) {
                embyLookupFailures++;
                return;
            }
            if (!result.exists) absentThreads.push(thread);
        });

        if (absentThreads.length === 0) {
            extractButton.disabled = false;
            setPanelStatus(embyLookupFailures
                ? `没有提取附件：${threads.length - embyLookupFailures} 个番号已在 Emby，另有 ${embyLookupFailures} 个查询失败并已跳过。`
                : `没有可提取的附件：${threads.length} 个番号都已在 Emby。`);
            return;
        }

        const found = [];
        const failures = [];
        let nextIndex = 0;
        let finished = 0;

        setPanelStatus(`Emby 未找到 ${absentThreads.length} 个番号，正在读取详情页…`);
        const workers = Array.from({ length: Math.min(CONFIG.THREAD_FETCH_CONCURRENCY, absentThreads.length) }, async () => {
            while (nextIndex < absentThreads.length) {
                const index = nextIndex++;
                const thread = absentThreads[index];
                try {
                    const files = await fetchThreadAttachments(thread);
                    found.push(...files);
                } catch (error) {
                    failures.push({ thread, error });
                } finally {
                    finished++;
                    setPanelStatus(`正在读取未下载番号的详情页 ${finished}/${absentThreads.length}…`);
                }
            }
        });

        await Promise.all(workers);

        const unique = new Map();
        found.forEach(item => {
            if (!unique.has(item.url)) unique.set(item.url, item);
        });
        attachmentItems = [...unique.values()];

        const codesWithAttachments = new Set(attachmentItems.map(item => item.code));
        downloadButton.disabled = attachmentItems.length === 0;
        extractButton.disabled = false;

        const skippedExisting = threads.length - absentThreads.length - embyLookupFailures;
        const summary = `已提取 ${codesWithAttachments.size} 个未下载番号的附件，共 ${attachmentItems.length} 个文件；已跳过 Emby 已有的 ${skippedExisting} 个番号。`;
        setPanelStatus(failures.length
            ? `${summary} 另有 ${failures.length} 个详情页读取失败（${failures[0].thread.code}：${failures[0].error.message || '未知错误'}）${embyLookupFailures ? `，${embyLookupFailures} 个 Emby 查询失败也已跳过` : ''}。`
            : embyLookupFailures
                ? `${summary} ${embyLookupFailures} 个 Emby 查询失败并已跳过。`
            : summary, failures.length > 0);
        console.info('[X1080X 附件提取]', { threadCount: threads.length, skippedExisting, embyLookupFailures, attachmentItems, failures });
    }

    function createAttachmentPanel() {
        if (document.getElementById('x1080x-attachment-panel')) return;

        const panel = document.createElement('div');
        panel.id = 'x1080x-attachment-panel';
        panel.style.cssText = 'position:fixed;top:92px;right:16px;z-index:99999;width:250px;padding:12px;background:#fff;border:1px solid #bbb;border-radius:7px;box-shadow:0 3px 12px rgba(0,0,0,.2);font:13px/1.5 Arial,sans-serif;';

        extractButton = document.createElement('button');
        extractButton.textContent = '【一键提取附件】';
        extractButton.style.cssText = 'width:100%;padding:8px;border:0;border-radius:5px;background:#1677d2;color:#fff;font-weight:700;cursor:pointer;';
        extractButton.onclick = extractAttachments;

        uiStatus = document.createElement('div');
        uiStatus.style.cssText = 'margin-top:8px;color:#444;overflow-wrap:anywhere;';
        uiStatus.textContent = '点击提取当前页所有帖子的附件。';

        downloadButton = document.createElement('button');
        downloadButton.textContent = '选择并下载附件';
        downloadButton.disabled = true;
        downloadButton.style.cssText = 'width:100%;margin-top:8px;padding:7px;border:1px solid #1677d2;border-radius:5px;background:#fff;color:#1677d2;font-weight:700;cursor:pointer;';
        downloadButton.onclick = showDownloadDialog;

        panel.append(extractButton, uiStatus, downloadButton);
        document.body.appendChild(panel);
    }

    function init() {
        processTitles();
        createAttachmentPanel();

        const observer = new MutationObserver(mutations => {
            for (const mutation of mutations) {
                mutation.addedNodes.forEach(node => {
                    if (node.nodeType === Node.ELEMENT_NODE) processTitles(node);
                });
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
