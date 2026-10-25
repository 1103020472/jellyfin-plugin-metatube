// ==UserScript==
// @name         Sehuatang论坛 + Emby检测
// @namespace    http://tampermonkey.net/
// @version      1.0.0
// @description  在帖子标题前展示标红的【已有】（Emby已存在该番号）
// @match        https://www.sehuatang.org/forum.php*
// @grant        GM_xmlhttpRequest
// @connect      www.sehuatang.org
// @connect      av.emby.cv.local
// @run-at       document-end
// ==/UserScript==

(function () {
    'use strict';

    const params = new URLSearchParams(location.search);
    if (params.get('mod') !== 'forumdisplay' || params.get('fid') !== '160') return;

    const CONFIG = {
        EMBY_BASE_URL: 'http://av.emby.cv.local',
        EMBY_API_KEY: '70952dc783d640099742ca18114ecdeb',
        EMBY_SERVER_ID: '6e78ed602ff94d159b3fe05af0181411',
        EMBY_CONCURRENCY: 3,
    };

    const CODE_REGEX = /[A-Za-z]{2,10}-\d{2,5}/;

    // 生成番号的候选写法：
    // 1. 去掉字母前缀前面的数字（3DSVR-2015 -> DSVR-2015）
    // 2. 数字补零位数变体（DSVR-030 / DSVR-0030 / DSVR-30）
    function codeCandidates(code) {
        const upper = code.toUpperCase();
        const m = upper.match(/^(\d*)([A-Z]+)-(\d+)$/);
        if (!m) return [upper];
        const prefix = m[2];
        const num = m[3];
        const variants = new Set([upper, `${prefix}-${num}`]);
        const stripped = String(parseInt(num, 10));
        variants.add(`${prefix}-${stripped}`);
        variants.add(`${prefix}-${num.padStart(4, '0')}`);
        variants.add(`${prefix}-${stripped.padStart(3, '0')}`);
        return [...variants];
    }

    const embyCache = new Map();   // code -> result
    const pendingMap = new Map();  // code -> promise
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

    // 单个写法在 Emby 中严格校验（Emby SearchTerm 是模糊匹配，需二次确认）
    async function strictSearch(searchCode) {
        const response = await request({
            method: 'GET',
            url: `${CONFIG.EMBY_BASE_URL}/emby/Items?IncludeItemTypes=Movie&Recursive=true&SearchTerm=${encodeURIComponent(searchCode)}`,
            headers: {
                'X-Emby-Token': CONFIG.EMBY_API_KEY,
                Accept: 'application/json',
            },
        });

        const data = JSON.parse(response.responseText || '{}');
        if (!Array.isArray(data.Items)) return { exists: false, itemId: null };

        const movie = data.Items.find(item =>
            [item.Name, item.OriginalTitle, item.Path].some(field =>
                String(field || '').toUpperCase().includes(searchCode)
            ) || Object.values(item.ProviderIds || {}).some(pid =>
                String(pid || '').toUpperCase().includes(searchCode)
            )
        );
        return { exists: Boolean(movie), itemId: movie?.Id || null };
    }

    async function queryEmby(code) {
        if (embyCache.has(code)) return embyCache.get(code);
        if (pendingMap.has(code)) return pendingMap.get(code);

        const promise = enqueue(async () => {
            const candidates = codeCandidates(code);
            try {
                for (const candidate of candidates) {
                    const result = await strictSearch(candidate);
                    if (result.exists) {
                        const found = { exists: true, itemId: result.itemId, queryOk: true, matchedCode: candidate };
                        embyCache.set(code, found);
                        return found;
                    }
                }
                const absent = { exists: false, itemId: null, queryOk: true };
                embyCache.set(code, absent);
                return absent;
            } catch (error) {
                console.warn('[Sehuatang Emby] 查询失败:', code, error);
                return { exists: false, itemId: null, queryOk: false };
            } finally {
                pendingMap.delete(code);
            }
        });

        pendingMap.set(code, promise);
        return promise;
    }

    function processTitle(anchor) {
        if (anchor.dataset.sehuatangEmbyChecked === 'true') return;
        anchor.dataset.sehuatangEmbyChecked = 'true';

        const match = `${anchor.textContent || ''} ${anchor.title || ''}`.match(CODE_REGEX);
        if (!match) return;
        const code = match[0].toUpperCase();

        queryEmby(code).then(result => {
            if (!result.exists || !result.itemId || anchor.dataset.sehuatangEmbyLabelAdded === 'true') return;

            const label = document.createElement('a');
            label.textContent = '【已有】';
            label.title = `Emby中已存在：${result.matchedCode || code}`;
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
            anchor.dataset.sehuatangEmbyLabelAdded = 'true';
        });
    }

    function processTitles(root = document) {
        const selector = 'a.xst, h3.xw0 > a';
        if (root.matches?.(selector)) processTitle(root);
        root.querySelectorAll?.(selector).forEach(processTitle);
    }

    function init() {
        processTitles();

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
