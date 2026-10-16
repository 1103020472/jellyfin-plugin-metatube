// ==UserScript==
// @name         DMM视频列表 + Emby检测
// @namespace    http://tampermonkey.net/
// @version      1.1.0
// @description  从DMM/FANZA视频详情链接识别番号，并在Emby中检查是否已有影片
// @match        https://video.dmm.co.jp/av/*
// @grant        GM_xmlhttpRequest
// @grant        GM_setClipboard
// @connect      av.emby.cv.local
// @connect      video.dmm.co.jp
// @run-at       document-end
// @noframes
// ==/UserScript==

(function () {
    'use strict';

    const CONFIG = {
        EMBY_BASE_URL: 'http://av.emby.cv.local',
        EMBY_API_KEY: '70952dc783d640099742ca18114ecdeb',
        EMBY_SERVER_ID: '6e78ed602ff94d159b3fe05af0181411',
        EMBY_CONCURRENCY: 3,
    };

    const embyCache = new Map();
    const pendingMap = new Map();
    const queue = [];
    let activeCount = 0;

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

    function normalizeNumber(digits) {
        // DMM的数字部分通常需要保留至少三位：00005 -> 005，00197 -> 197。
        const number = String(Number(digits));
        return number.padStart(3, '0');
    }

    // DMM id示例：nkkvr00197 -> NNKVR-197，dsuvr00005 -> DSUVR-005。
    // 部分id前面有站内系列前缀13，例如13dsvr02004实际对应DSVR-2004。
    // 13前缀的再版id带re序号，例如13dsvr01754re01 -> DSVR-1754。
    // 前缀84同理，例如84kmvr00940 -> KMVR-940。
    // h_ 前缀后的站内系列号位数不固定（如 910/0910），例如 h_910vrvr00114 -> VRVR-114。
    function normalizeCode(rawId) {
        const value = String(rawId || '').trim();
        if (!value) return null;

        const prefixedMatch = value.match(/^13([A-Za-z]+)(\d+)$/i);
        if (prefixedMatch) {
            return `${prefixedMatch[1].toUpperCase()}-${String(Number(prefixedMatch[2]))}`;
        }

        // 13前缀 + re再版标记，例如13dsvr01754re01 -> DSVR-1754（re01为再版序号，忽略）。
        const prefixedReMatch = value.match(/^13([A-Za-z]+)(\d+)re\d+$/i);
        if (prefixedReMatch) {
            return `${prefixedReMatch[1].toUpperCase()}-${String(Number(prefixedReMatch[2]))}`;
        }

        const singlePrefixMatch = value.match(/^1([A-Za-z]+)(\d+)$/i);
        if (singlePrefixMatch) {
            return `${singlePrefixMatch[1].toUpperCase()}-${normalizeNumber(singlePrefixMatch[2])}`;
        }

        const doublePrefixMatch = value.match(/^55([A-Za-z]+)(\d+)$/i);
        if (doublePrefixMatch) {
            return `${doublePrefixMatch[1].toUpperCase()}-${normalizeNumber(doublePrefixMatch[2])}`;
        }

        const prefix84Match = value.match(/^84([A-Za-z]+)(\d+)$/i);
        if (prefix84Match) {
            return `${prefix84Match[1].toUpperCase()}-${normalizeNumber(prefix84Match[2])}`;
        }

        const hSeriesPrefixMatch = value.match(/^h_\d+([A-Za-z]+)(\d+)$/i);
        if (hSeriesPrefixMatch) {
            return `${hSeriesPrefixMatch[1].toUpperCase()}-${normalizeNumber(hSeriesPrefixMatch[2])}`;
        }

        const hyphenMatch = value.match(/^([A-Za-z0-9]+)-(\d+)$/);
        if (hyphenMatch) {
            return `${hyphenMatch[1].toUpperCase()}-${normalizeNumber(hyphenMatch[2])}`;
        }

        const match = value.match(/^(.+?)(\d+)$/);
        if (!match || !/[A-Za-z]/.test(match[1])) return null;

        return `${match[1].toUpperCase()}-${normalizeNumber(match[2])}`;
    }

    async function queryEmby(code) {
        if (!code) return { exists: false, itemId: null };
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
                    ? data.Items.find(item => {
                        return [item.Name, item.OriginalTitle].some(title =>
                            String(title || '').toUpperCase().includes(searchCode)
                        );
                    })
                    : null;
                const result = {
                    exists: Boolean(movie),
                    itemId: movie?.Id || null,
                };
                embyCache.set(code, result);
                return result;
            } catch (error) {
                console.warn('[DMM Emby] 查询失败:', code, error);
                return { exists: false, itemId: null, queryFailed: true };
            } finally {
                pendingMap.delete(code);
            }
        });

        pendingMap.set(code, promise);
        return promise;
    }

    function getCodeFromLink(anchor) {
        try {
            const url = new URL(anchor.href, location.href);
            if (!url.pathname.includes('/av/content/')) return null;
            return normalizeCode(url.searchParams.get('id'));
        } catch {
            return null;
        }
    }

    async function getRecordingMinutes(detailUrl) {
        const url = new URL(detailUrl, location.href);
        if (url.origin !== location.origin) throw new Error('详情页不是DMM同源页面');

        // GM_xmlhttpRequest拿到的是未执行DMM前端脚本的HTML，时长行尚不存在。
        // 用隐藏同源iframe正常加载详情，让DMM脚本在浏览器会话/Cookie下完成渲染后再读DOM。
        const frame = document.createElement('iframe');
        frame.setAttribute('aria-hidden', 'true');
        frame.tabIndex = -1;
        frame.style.cssText = 'position:fixed;width:1px;height:1px;left:-10000px;top:-10000px;border:0;visibility:hidden;';

        return new Promise((resolve, reject) => {
            let finished = false;
            let timer = null;
            let observer = null;
            const cleanup = () => {
                if (timer) clearTimeout(timer);
                if (observer) observer.disconnect();
                frame.removeEventListener('load', inspect);
                frame.remove();
            };
            const finish = (error, minutes) => {
                if (finished) return;
                finished = true;
                cleanup();
                if (error) reject(error);
                else resolve(minutes);
            };
            const inspect = () => {
                try {
                    const doc = frame.contentDocument;
                    if (!doc) return;
                    const row = Array.from(doc.querySelectorAll('tr')).find(item =>
                        /収録時間|收录时间/.test(item.querySelector('th')?.textContent || item.textContent)
                    );
                    if (!row) return;
                    const text = row.querySelector('td')?.textContent || row.textContent || '';
                    const match = text.match(/(\d+)\s*分/);
                    if (match) finish(null, Number(match[1]));
                } catch (error) {
                    finish(new Error(`无法读取DMM详情页内容：${error.message}`));
                }
            };

            frame.addEventListener('load', () => {
                inspect();
                try {
                    observer = new MutationObserver(inspect);
                    observer.observe(frame.contentDocument.documentElement, { childList: true, subtree: true, characterData: true });
                } catch (error) {
                    finish(new Error(`无法监听DMM详情页渲染：${error.message}`));
                }
            });
            frame.addEventListener('load', inspect);
            timer = setTimeout(() => finish(new Error('等待详情页渲染收录时间超时')), 30000);
            frame.src = url.href;
            document.body.appendChild(frame);
        });
    }

    async function mapWithConcurrency(items, concurrency, callback) {
        const results = new Array(items.length);
        let nextIndex = 0;
        const workers = Array.from({ length: Math.min(concurrency, items.length) }, async () => {
            while (true) {
                const index = nextIndex++;
                if (index >= items.length) return;
                results[index] = await callback(items[index], index);
            }
        });
        await Promise.all(workers);
        return results;
    }

    function addLabel(anchor) {
        if (anchor.dataset.embyChecked === 'true') return;
        anchor.dataset.embyChecked = 'true';

        const code = getCodeFromLink(anchor);
        if (!code) return;

        const numberLink = document.createElement('a');
        numberLink.textContent = code;
        numberLink.href = anchor.href;
        numberLink.title = `打开DMM详情：${code}`;
        numberLink.target = '_blank';
        numberLink.rel = 'noopener noreferrer';
        numberLink.style.cssText = [
            'color:#6b7280',
            'font-weight:400',
            'font-size:.9em',
            'text-decoration:none',
            'margin-right:2px',
            'white-space:nowrap',
        ].join(';');

        anchor.parentNode?.insertBefore(numberLink, anchor);
        anchor.dataset.embyCodeAdded = 'true';

        queryEmby(code).then(result => {
            if (!result.exists || anchor.dataset.embyLabelAdded === 'true') return;

            const label = document.createElement('a');
            label.textContent = '【已有】';
            label.title = `Emby中已存在：${code}`;
            label.href = `${CONFIG.EMBY_BASE_URL}/web/index.html#!/item?id=${encodeURIComponent(result.itemId)}&serverId=${encodeURIComponent(CONFIG.EMBY_SERVER_ID)}`;
            label.target = '_blank';
            label.rel = 'noopener noreferrer';
            label.style.cssText = [
                'color:#e60012',
                'font-weight:700',
                'margin-right:4px',
                'white-space:nowrap',
                'text-decoration:none',
            ].join(';');

            const separator = document.createElement('span');
            separator.textContent = '-';
            separator.style.cssText = 'color:#9ca3af;font-weight:400;margin-right:2px;';

            const card = anchor.closest('[data-e2eid="content-card"]');
            if (card && card.dataset.embyCardMarked !== 'true') {
                card.style.backgroundColor = 'rgba(255, 235, 238, 0.82)';
                card.dataset.embyCardMarked = 'true';
            }

            anchor.parentNode?.insertBefore(separator, anchor);
            anchor.parentNode?.insertBefore(label, anchor);
            anchor.dataset.embyLabelAdded = 'true';
        });
    }

    function processLinks(root = document) {
        const selector = 'a[data-e2eid="title"][href*="/av/content/"]';
        if (root.matches?.(selector)) addLabel(root);
        root.querySelectorAll?.(selector).forEach(addLabel);
    }

    function isListPage() {
        return location.pathname.includes('/av/list/');
    }

    function ensureCopyUnownedButton() {
        const buttonId = 'dmm-copy-unowned-codes-button';
        let button = document.getElementById(buttonId);
        if (!isListPage()) {
            if (button) button.style.display = 'none';
            return;
        }
        if (button) {
            button.style.display = 'block';
            return;
        }

        button = document.createElement('button');
        button.id = buttonId;
        button.type = 'button';
        button.textContent = '[复制未拥有番号]';
        button.style.cssText = [
            'position:fixed', 'top:80px', 'right:16px', 'z-index:99999',
            'padding:9px 12px', 'border:0', 'border-radius:6px',
            'background:#2563eb', 'color:#fff', 'font-weight:700',
            'cursor:pointer', 'box-shadow:0 2px 8px #0003',
        ].join(';');

        button.addEventListener('click', async () => {
            if (button.disabled) return;
            const detailUrlByCode = new Map();
            Array.from(document.querySelectorAll('a[data-e2eid="title"][href*="/av/content/"]'))
                .forEach(anchor => {
                    const code = getCodeFromLink(anchor);
                    if (code && !detailUrlByCode.has(code)) detailUrlByCode.set(code, anchor.href);
                });
            const codes = [...detailUrlByCode.keys()];
            if (!codes.length) {
                alert('当前页面没有识别到番号。');
                return;
            }

            button.disabled = true;
            button.textContent = `[查询中 0/${codes.length}]`;
            try {
                let completed = 0;
                const results = await Promise.all(codes.map(async code => {
                    const result = await queryEmby(code);
                    completed++;
                    button.textContent = `[查询中 ${completed}/${codes.length}]`;
                    return { code, detailUrl: detailUrlByCode.get(code), ...result };
                }));

                const unknown = results.filter(item => item.queryFailed);
                const unowned = results.filter(item => !item.exists && !item.queryFailed);
                if (!unowned.length) {
                    alert(unknown.length
                        ? `没有复制任何番号：${unknown.length} 个番号查询失败，避免误把未知状态当成未拥有。`
                        : '当前页面的番号均已存在于 Emby。');
                    return;
                }

                let durationDone = 0;
                button.textContent = `[读取收录时间 0/${unowned.length}]`;
                const durationResults = await mapWithConcurrency(unowned, 3, async item => {
                    try {
                        const minutes = await getRecordingMinutes(item.detailUrl);
                        return { ...item, minutes };
                    } catch (error) {
                        console.warn('[DMM Emby] 收录时间读取失败:', item.code, error);
                        return { ...item, durationFailed: true };
                    } finally {
                        durationDone++;
                        button.textContent = `[读取收录时间 ${durationDone}/${unowned.length}]`;
                    }
                });

                const eligible = durationResults.filter(item => !item.durationFailed && item.minutes < 300);
                const long = durationResults.filter(item => !item.durationFailed && item.minutes >= 300).length;
                const durationFailed = durationResults.filter(item => item.durationFailed).length;
                if (!eligible.length) {
                    alert(`没有符合条件的番号：${long} 个收录时间不少于 300 分钟，${durationFailed} 个无法读取收录时间。`);
                    return;
                }

                GM_setClipboard(eligible.map(item => item.code).join('\n'), 'text');
                const skipped = [
                    long ? `${long} 个时长不少于 300 分钟` : '',
                    durationFailed ? `${durationFailed} 个无法读取时长` : '',
                    unknown.length ? `${unknown.length} 个 Emby 查询失败` : '',
                ].filter(Boolean).join('；');
                button.textContent = `[已复制 ${eligible.length} 个番号]`;
                alert(`已复制 ${eligible.length} 个未拥有且收录时间小于 300 分钟的番号（一行一个）${skipped ? `。已跳过：${skipped}` : '。'}`);
            } catch (error) {
                console.error('[DMM Emby] 复制未拥有番号失败:', error);
                alert('复制番号失败，请重试。');
            } finally {
                button.disabled = false;
                setTimeout(() => {
                    if (button.isConnected) button.textContent = '[复制未拥有番号]';
                }, 2500);
            }
        });

        document.body.appendChild(button);
    }

    function init() {
        processLinks();
        ensureCopyUnownedButton();

        const observer = new MutationObserver(mutations => {
            for (const mutation of mutations) {
                mutation.addedNodes.forEach(node => {
                    if (node.nodeType === Node.ELEMENT_NODE) processLinks(node);
                });
            }
        });

        observer.observe(document.body, { childList: true, subtree: true });

        // DMM是SPA，点击卡片进入详情页时可能只变更URL，不重新加载脚本。
        let lastUrl = location.href;
        const processAfterRouteChange = () => {
            if (location.href === lastUrl) return;
            lastUrl = location.href;
            ensureCopyUnownedButton();
            setTimeout(() => processLinks(), 0);
        };

        const originalPushState = history.pushState;
        history.pushState = function (...args) {
            const result = originalPushState.apply(this, args);
            processAfterRouteChange();
            return result;
        };

        const originalReplaceState = history.replaceState;
        history.replaceState = function (...args) {
            const result = originalReplaceState.apply(this, args);
            processAfterRouteChange();
            return result;
        };

        window.addEventListener('popstate', processAfterRouteChange);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
