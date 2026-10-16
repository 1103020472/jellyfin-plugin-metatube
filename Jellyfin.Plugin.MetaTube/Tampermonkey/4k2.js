// ==UserScript==
// @name         4k2论坛帖子缩略图 + Emby检测（最终稳定版）
// @namespace    http://tampermonkey.net/
// @version      7.5
// @match        https://4k2.me/*
// @match        https://4k2.com/*
// @grant        GM_xmlhttpRequest
// @connect      4k2.me
// @connect      4k2.com
// @connect      bocecdn.com
// @connect      av.emby.cv.local
// @run-at       document-end
// ==/UserScript==

(function () {
    'use strict';

    const CONFIG = {
        thumbnailSize: 768,
        batchConcurrency: 3,
        embyConcurrency: 3,
        EMBY_BASE_URL: "http://av.emby.cv.local",
        EMBY_API_KEY: "70952dc783d640099742ca18114ecdeb",
    };

    const CODE_REGEX = /[A-Za-z]{3,7}-\d{3,5}/;
    const CODE_GLOBAL_REGEX = /[A-Za-z]{3,7}-\d{3,5}/g;

    let isBatchProcessing = false;
    // 仅保存在当前页面内存中；刷新页面会自然清空。
    let subtitleCollectionCache = null;
    const embyCache = new Map();
    const processingSet = new Set();

    // ================= Emby队列 =================
    const queue = [];
    let activeCount = 0;

    function runQueue() {
        if (activeCount >= CONFIG.embyConcurrency || queue.length === 0) return;
        const task = queue.shift();
        activeCount++;
        task().finally(() => {
            activeCount--;
            runQueue();
        });
        runQueue();
    }

    function enqueue(task) {
        return new Promise((resolve, reject) => {
            queue.push(() => task().then(resolve).catch(reject));
            runQueue();
        });
    }

    function GM_xmlhttpRequestPromise(details) {
        return new Promise((resolve, reject) => {
            details.onload = resolve;
            details.onerror = reject;
            details.ontimeout = reject;
            GM_xmlhttpRequest(details);
        });
    }

    const pendingMap = new Map();

    async function queryEmbyMovie(code) {
        if (!code || !CONFIG.EMBY_API_KEY) {
            return { exists: false, hasSubtitle: false };
        }

        if (embyCache.has(code)) {
            return embyCache.get(code);
        }

        if (pendingMap.has(code)) {
            return pendingMap.get(code);
        }

        const promise = enqueue(async () => {
            try {
                const res = await GM_xmlhttpRequestPromise({
                    method: "GET",
                    url: `${CONFIG.EMBY_BASE_URL}/emby/Items?IncludeItemTypes=Movie&Recursive=true&SearchTerm=${encodeURIComponent(code)}`,
                    headers: {
                        "X-Emby-Token": CONFIG.EMBY_API_KEY,
                        "Accept": "application/json"
                    }
                });

                const data = JSON.parse(res.responseText);
                // Emby 的 SearchTerm 是模糊搜索；只认标题中实际包含番号的条目，
                // 避免把仅因年份/描述命中的影片误判为该番号。
                const normalizedCode = code.toUpperCase();
                const movie = (data.Items || []).find(item =>
                    [item.Name, item.OriginalTitle].some(title =>
                        typeof title === 'string' && title.toUpperCase().includes(normalizedCode)
                    )
                );
                if (!movie) return { exists: false, hasSubtitle: false };

                let hasSubtitle = false;

                try {
                    const mediaRes = await GM_xmlhttpRequestPromise({
                        method: "GET",
                        url: `${CONFIG.EMBY_BASE_URL}/emby/Items/${movie.Id}/PlaybackInfo`,
                        headers: { "X-Emby-Token": CONFIG.EMBY_API_KEY }
                    });

                    const mediaData = JSON.parse(mediaRes.responseText);
                    const sources = mediaData.MediaSources || [];

                    hasSubtitle = sources.some(source =>
                        (source.MediaStreams || []).some(stream =>
                            stream.Type === "Subtitle" &&
                            (
                                stream.IsExternal === true ||
                                stream.Path ||
                                stream.DeliveryMethod === "External"
                            )
                        )
                    );
                } catch {}

                const result = { exists: true, hasSubtitle };
                embyCache.set(code, result);
                return result;

            } catch {
                return { exists: false, hasSubtitle: false };
            } finally {
                pendingMap.delete(code);
            }
        });

        pendingMap.set(code, promise);
        return promise;
    }

    function addLabel(el, text, isBlue) {
        if (el.dataset.embyChecked) return;
        el.dataset.embyChecked = "true";

        const span = document.createElement('span');
        span.textContent = text;
        span.style.fontWeight = 'bold';
        span.style.marginRight = '6px';
        span.style.color = isBlue ? '#0066FF' : 'red';

        el.parentNode.insertBefore(span, el);
    }

    async function processNode(sub) {
        if (sub.dataset.embyChecked) return;

        const match = sub.textContent.match(CODE_REGEX);
        if (!match) return;

        const code = match[0].toUpperCase();
        const { exists, hasSubtitle } = await queryEmbyMovie(code);
        if (!exists) return;

        const inSubtitleForum = isSubtitleForum(sub);
        addLabel(sub, inSubtitleForum && hasSubtitle ? '【已有字幕】' : '【已下载】', inSubtitleForum && hasSubtitle);
    }

    function processEmbyBadges(nodes) {
        nodes.forEach(n => processNode(n));
    }

    function isSubtitleForum(sub) {
        return document.title.includes('字幕区') || sub.textContent.includes('字幕');
    }

    // ================= 字幕区：筛选已下载影片并购买/下载字幕 =================
    function isSubtitleForumList() {
        const params = new URLSearchParams(location.search);
        return /forum-20-\d+\.html/.test(location.pathname) ||
            (params.get('mod') === 'forumdisplay' && params.get('fid') === '20') ||
            // 个人空间的主题列表页（home.php?mod=space&uid=...&do=thread）
            // 使用相同的 kmlist 卡片结构，直接复用提取流程。
            (params.get('mod') === 'space' && params.get('do') === 'thread');
    }

    function requestForum(details) {
        return GM_xmlhttpRequestPromise({
            timeout: 30000,
            withCredentials: true,
            ...details,
            headers: {
                'X-Requested-With': 'XMLHttpRequest',
                ...(details.headers || {})
            }
        });
    }

    function getForumThreadRows() {
        const rows = document.querySelectorAll('#threadlisttableid li.kmlist');
        if (rows.length) return Array.from(rows);
        // 兜底：个别列表页没有 #threadlisttableid 容器时退化为全页匹配
        return Array.from(document.querySelectorAll('li.kmlist'));
    }

    function getAttachmentAid(url) {
        const rawAid = new URL(url, location.href).searchParams.get('aid');
        if (!rawAid) return null;
        if (/^\d+$/.test(rawAid)) return rawAid;

        // Discuz 已购附件会把 aid 编码为 base64（aid|hash|...），
        // 而未购链接使用纯数字 aid；两者应归一到同一个附件 ID。
        try {
            const base64 = rawAid.replace(/-/g, '+').replace(/_/g, '/');
            const padded = base64.padEnd(Math.ceil(base64.length / 4) * 4, '=');
            const decodedAid = atob(padded).split('|', 1)[0];
            if (/^\d+$/.test(decodedAid)) return decodedAid;
        } catch {}
        return rawAid;
    }

    function loadPurchasedAidSet() {
        try {
            return new Set(JSON.parse(localStorage.getItem('4k2-subtitle-purchased-aids') || '[]'));
        } catch {
            return new Set();
        }
    }

    function parseSubtitleAttachments(html, threadUrl, codes, listAuthor = '', purchasedAids = new Set()) {
        const doc = new DOMParser().parseFromString(html, 'text/html');
        const result = [];
        const allowedExt = /\.(?:srt|ass|ssa|vtt|zip|rar|7z)$/i;
        const post = doc.querySelector('#postlist div[id^="post_"], div[id^="post_"]');
        const detailAuthor = Array.from(post?.querySelectorAll('.pls a[href*="space-uid-"]') || [])
            .map(link => link.textContent.trim()).find(Boolean) || '';
        const author = listAuthor || detailAuthor || '未知';

        // .tattl = 附件列表型（楼主未把附件插入正文，如 50893）；
        // span#attach_xxx = 正文内联型（楼主把附件插入正文，如 27066 的 savr-542.rar，
        // 该结构没有 .tattl、也没有 attachpay 锚点）。
        const blocks = [
            ...doc.querySelectorAll('dl.tattl'),
            ...doc.querySelectorAll('span[id^="attach_"]'),
        ];
        blocks.forEach(block => {
            const isAttachSpan = block.tagName === 'SPAN' && /^attach_\d+$/.test(block.id || '');
            const links = Array.from(block.querySelectorAll('a[href]'));
            const payLink = links.find(link => link.href.includes('action=attachpay'));
            const directLink = links.find(link => link.href.includes('mod=attachment'));
            const link = payLink || directLink;
            if (!link) return;

            const name = (link.textContent || '').trim();
            if (!allowedExt.test(name)) return;

            const resourceUrl = new URL(link.getAttribute('href'), threadUrl).href;
            const aid = getAttachmentAid(resourceUrl);
            if (!aid) return;

            const priceText = block.textContent || '';
            const priceMatch = priceText.match(/(?:售价|价格)\s*[：:]?\s*(\d+)\s*金钱/);
            // 已购集合来自本脚本购买记录（localStorage）：购买后正文内联附件
            // 往往不再显示售价，必须靠 aid 匹配才能识别"已购买"状态。
            const alreadyBought = purchasedAids.has(aid);

            let isPurchased = false;
            if (isAttachSpan) {
                // 正文内联附件无 attachpay 锚点：有售价=未购待购买；
                // 无售价但已在已购记录=已购买；两者皆无则跳过（免费附件等）。
                if (!priceMatch && !alreadyBought) return;
                isPurchased = alreadyBought;
            } else {
                // mod=attachment 也可能是阅读权限附件，并不代表已购买。
                // 仅当同一附件仍显示售价、但购买入口已消失时，才判定为已购。
                isPurchased = (!payLink && !!directLink && !!priceMatch) || alreadyBought;
                if (!payLink && !isPurchased) return;
                if (payLink && !priceMatch) return;
            }

            // 正文内联附件按 Discuz 规则构造购买地址（未购附件无 attachpay 锚点）。
            let payUrl = payLink ? resourceUrl : null;
            if (!payUrl && !isPurchased) {
                const pay = new URL('forum.php', threadUrl);
                pay.searchParams.set('mod', 'misc');
                pay.searchParams.set('action', 'attachpay');
                pay.searchParams.set('aid', aid);
                const tidMatch = new URL(threadUrl).pathname.match(/thread-(\d+)/);
                if (tidMatch) pay.searchParams.set('tid', tidMatch[1]);
                payUrl = pay.href;
            }

            result.push({
                name,
                aid,
                payUrl,
                downloadUrl: isPurchased ? resourceUrl : null,
                threadUrl,
                author,
                price: isPurchased ? 0 : Number(priceMatch[1]),
                alreadyOwned: isPurchased
            });
        });

        // 番号匹配规则：仅当详情页存在多个候选附件时，才需要按番号挑选对应附件；
        // 只有一个附件时直接采用（文件名可能不含番号，避免被漏掉）。
        if (result.length > 1) {
            const matched = result.filter(item =>
                codes.some(code => item.name.toUpperCase().includes(code))
            );
            if (!matched.length) return [];
            matched.forEach(item => {
                item.code = codes.find(code => item.name.toUpperCase().includes(code)) || codes[0] || '未知';
            });
            return matched;
        }
        const fallbackCode = codes[0] || '未知';
        result.forEach(item => { item.code = fallbackCode; });
        return result;
    }

    async function collectSubtitleAttachments(button) {
        if (subtitleCollectionCache !== null) {
            if (subtitleCollectionCache.length) showSubtitleSelection(subtitleCollectionCache);
            else alert('本页上次提取结果为 0 个附件。刷新页面后才会重新提取。');
            return;
        }
        if (isBatchProcessing) return;
        isBatchProcessing = true;
        button.textContent = '[正在提取字幕...]';
        button.disabled = true;

        try {
            const subjects = Array.from(document.querySelectorAll('#threadlisttableid .km_subject'));
            await Promise.all(subjects.map(processNode));

            // 只提取打了【已下载】标签的行（Emby 存在且无字幕的影片），
            // 显式排除【已有字幕】行与未打标行（Emby 不存在/查询失败）。
            const rows = getForumThreadRows().filter(row => {
                const labelText = row.querySelector('.kmtit')?.textContent || '';
                return labelText.includes('【已下载】') && !labelText.includes('【已有字幕】');
            });
            const found = [];
            for (let index = 0; index < rows.length; index++) {
                const row = rows[index];
                const subject = row.querySelector('.km_subject');
                const titleLink = row.querySelector('a.kmtit[href*="thread-"]');
                const author = row.querySelector('.kmfoot a[href*="space-uid-"]')?.textContent.trim() || '';
                if (!subject || !titleLink) continue;

                const codes = [...new Set((subject.textContent.toUpperCase().match(CODE_GLOBAL_REGEX) || []))];
                const eligibleCodes = [];
                for (const code of codes) {
                    const emby = await queryEmbyMovie(code);
                    if (emby.exists && !emby.hasSubtitle) eligibleCodes.push(code);
                }
                if (!eligibleCodes.length) continue;

                button.textContent = `[检查帖子 ${index + 1}/${rows.length}]`;
                try {
                    const response = await requestForum({ method: 'GET', url: titleLink.href });
                    if (response.status < 200 || response.status >= 400) continue;
                    found.push(...parseSubtitleAttachments(response.responseText, titleLink.href, eligibleCodes, author, loadPurchasedAidSet()));
                } catch (error) {
                    console.warn('[4k2字幕] 详情页读取失败:', titleLink.href, error);
                }
            }

            const byAid = new Map();
            found.forEach(item => {
                const previous = byAid.get(item.aid);
                // 同一附件的付费态/已购态可能同时被详情页或列表重复返回，保留已购态。
                if (!previous || (item.alreadyOwned && !previous.alreadyOwned)) {
                    byAid.set(item.aid, item);
                }
            });
            const byLogicalFile = new Map();
            [...byAid.values()].forEach(item => {
                const logicalKey = `${item.threadUrl}|${item.code}|${item.name.toLocaleLowerCase()}`;
                const previous = byLogicalFile.get(logicalKey);
                if (!previous || (item.alreadyOwned && !previous.alreadyOwned)) {
                    byLogicalFile.set(logicalKey, item);
                }
            });
            const unique = [...byLogicalFile.values()]
                .sort((a, b) => a.code.localeCompare(b.code, undefined, { numeric: true, sensitivity: 'base' }) ||
                    a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' }));
            subtitleCollectionCache = unique;
            if (!unique.length) {
                alert(`提取完成：检查了 ${rows.length} 个【已下载】番号帖子，没有找到可购买的对应字幕附件。`);
                return;
            }
            showSubtitleSelection(unique);
        } finally {
            isBatchProcessing = false;
            button.disabled = false;
            button.textContent = '[一键提取字幕]';
        }
    }

    function showSubtitleSelection(items) {
        document.getElementById('4k2-subtitle-picker')?.remove();
        const overlay = document.createElement('div');
        overlay.id = '4k2-subtitle-picker';
        overlay.style.cssText = 'position:fixed;inset:0;background:#0009;z-index:100000;display:flex;align-items:center;justify-content:center;padding:20px;';

        const panel = document.createElement('div');
        panel.style.cssText = 'background:#fff;color:#222;width:min(760px,95vw);max-height:85vh;overflow:auto;border-radius:8px;padding:18px;box-shadow:0 8px 32px #0006;font:14px/1.5 sans-serif;';
        const heading = document.createElement('h3');
        heading.textContent = `已提取 ${items.length} 个字幕附件（同番号默认只选一个，已购买优先）`;
        heading.style.margin = '0 0 10px';
        const summary = document.createElement('div');
        summary.style.cssText = 'margin:8px 0 12px;padding:8px;background:#fff8e1;border:1px solid #ffe082;';
        const list = document.createElement('div');
        list.style.cssText = 'max-height:55vh;overflow:auto;border:1px solid #ddd;';
        const checks = [];
        const updateSummary = () => {
            const selected = checks.filter(c => c.input.checked);
            const total = selected.reduce((sum, c) => sum + c.item.price, 0);
            summary.textContent = `已勾选 ${selected.length}/${items.length} 个；预计花费 ${total} 金钱。点击“确认购买并下载”后将按列表顺序购买。`;
        };

        const preferredByCode = new Map();
        items.forEach(item => {
            const previous = preferredByCode.get(item.code);
            if (!previous || (item.alreadyOwned && !previous.alreadyOwned)) {
                preferredByCode.set(item.code, item);
            }
        });

        items.forEach(item => {
            const row = document.createElement('label');
            row.style.cssText = 'display:flex;gap:9px;align-items:center;padding:9px;border-bottom:1px solid #eee;cursor:pointer;';
            const input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = preferredByCode.get(item.code) === item;
            input.addEventListener('change', updateSummary);
            checks.push({ input, item });
            const text = document.createElement('span');
            text.textContent = `${item.code}　${item.name}　(${item.alreadyOwned ? '已购买' : `${item.price} 金钱`})　作者：${item.author || '未知'}`;
            const link = document.createElement('a');
            link.href = item.threadUrl;
            link.target = '_blank';
            link.rel = 'noopener';
            link.textContent = '详情';
            link.style.marginLeft = 'auto';
            row.append(input, text, link);
            list.appendChild(row);
        });

        const status = document.createElement('div');
        status.style.cssText = 'margin-top:10px;white-space:pre-wrap;color:#555;';
        const actions = document.createElement('div');
        actions.style.cssText = 'display:flex;gap:8px;justify-content:flex-end;margin-top:14px;';
        const close = document.createElement('button');
        close.textContent = '取消';
        close.onclick = () => overlay.remove();
        const confirm = document.createElement('button');
        confirm.textContent = '确认购买并下载';
        confirm.style.cssText = 'background:#c62828;color:white;border:0;border-radius:4px;padding:8px 14px;cursor:pointer;font-weight:bold;';
        confirm.onclick = async () => {
            const selected = checks.filter(c => c.input.checked).map(c => c.item);
            if (!selected.length) return alert('请至少选择一个附件。');
            confirm.disabled = true;
            close.disabled = true;
            checks.forEach(c => { c.input.disabled = true; });
            await purchaseAndDownloadSubtitles(selected, status, confirm);
            close.disabled = false;
            close.textContent = '关闭';
        };
        actions.append(close, confirm);
        panel.append(heading, summary, list, status, actions);
        overlay.appendChild(panel);
        overlay.addEventListener('click', event => {
            if (event.target === overlay && !confirm.disabled) overlay.remove();
        });
        document.body.appendChild(overlay);
        updateSummary();
    }

    function responseToText(response) {
        if (typeof response.responseText === 'string') return response.responseText;
        return '';
    }

    async function buySubtitleAttachment(item, beforeSubmit) {
        if (item.alreadyOwned) return { alreadyOwned: true };
        const modalUrl = new URL(item.payUrl);
        modalUrl.searchParams.set('infloat', 'yes');
        modalUrl.searchParams.set('handlekey', 'attachpay');
        const modalRes = await requestForum({ method: 'GET', url: modalUrl.href });
        const modalDoc = new DOMParser().parseFromString(modalRes.responseText || '', 'text/html');
        const form = modalDoc.querySelector('#attachpayform');
        if (!form) {
            const bodyText = modalDoc.body?.textContent || '';
            if (/已经购买|已购买|已购买过/.test(bodyText)) return { alreadyOwned: true };
            throw new Error('无法读取购买确认表单；未提交购买。');
        }

        const visibleText = form.textContent || '';
        if (!visibleText.includes(item.name)) throw new Error('购买弹窗附件名与清单不一致；未提交购买。');
        const priceMatch = visibleText.match(/售价\s*\(金钱\)\s*(\d+)/);
        if (!priceMatch || Number(priceMatch[1]) !== item.price) throw new Error('购买价格与确认清单不一致；未提交购买。');

        const action = new URL(form.getAttribute('action'), item.payUrl).href;
        const payload = new URLSearchParams();
        form.querySelectorAll('input[name]').forEach(input => {
            if (input.type === 'checkbox' && !input.checked) return;
            if (input.type === 'submit') return;
            let value = input.value;
            // Discuz 有时在弹窗展示阶段才给隐藏字段赋值；原始响应为空时
            // 使用当前已登录论坛页提供的令牌及本条已解析出的附件/主题信息。
            if (!value && input.name === 'formhash') {
                value = document.querySelector('input[name="formhash"]')?.value || '';
            } else if (!value && input.name === 'aid') {
                value = item.aid;
            } else if (!value && input.name === 'referer') {
                value = item.threadUrl;
            } else if (!value && input.name === 'handlekey') {
                value = 'attachpay';
            }
            payload.set(input.name, value);
        });
        payload.set('paysubmit', 'true');
        if (!payload.get('formhash') || !payload.get('aid')) {
            throw new Error('购买表单缺少必要令牌或附件编号；未提交购买。');
        }
        // 先记下提交尝试；若网络在服务器扣款后中断，之后不能盲目重复提交。
        beforeSubmit();
        const result = await requestForum({
            method: 'POST',
            url: action,
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                'Referer': item.threadUrl
            },
            data: payload.toString()
        });
        const text = responseToText(result);
        if (result.status < 200 || result.status >= 400 || /余额不足|购买失败|错误|请先登录/.test(text)) {
            throw new Error('论坛未确认购买成功；为避免重复扣费，已停止后续购买。');
        }
        return { alreadyOwned: false };
    }

    async function downloadSubtitleAttachment(item) {
        let downloadUrl = item.downloadUrl;
        if (!downloadUrl) {
            // 购买后论坛会把原付费附件链接替换成 mod=attachment 的真实下载地址。
            const detail = await requestForum({ method: 'GET', url: item.threadUrl });
            const refreshed = parseSubtitleAttachments(detail.responseText, item.threadUrl, [item.code], '', loadPurchasedAidSet())
                .find(candidate => candidate.aid === item.aid || candidate.name === item.name);
            downloadUrl = refreshed?.downloadUrl;
        }
        if (!downloadUrl) throw new Error('购买后详情页仍未显示真实下载链接；请稍后刷新详情页检查。');
        item.downloadUrl = downloadUrl;
        item.alreadyOwned = true;
        item.price = 0;

        const response = await requestForum({ method: 'GET', url: downloadUrl, responseType: 'blob' });
        const blob = response.response;
        const type = (response.responseHeaders || '').match(/content-type:\s*([^;\r\n]+)/i)?.[1] || '';
        if (!(blob instanceof Blob) || /text\/html/i.test(type) || blob.size < 1) {
            throw new Error('已购买，但论坛没有返回附件文件；请在详情页点附件链接完成下载。');
        }
        const objectUrl = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = objectUrl;
        anchor.download = item.name;
        anchor.style.display = 'none';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        setTimeout(() => URL.revokeObjectURL(objectUrl), 60000);
    }

    async function purchaseAndDownloadSubtitles(items, status, button) {
        let bought = 0;
        let downloaded = 0;
        const failures = [];
        const purchasedKey = '4k2-subtitle-purchased-aids';
        const attemptedKey = '4k2-subtitle-attempted-aids';
        let purchased = new Set();
        let attempted = new Set();
        try { purchased = new Set(JSON.parse(localStorage.getItem(purchasedKey) || '[]')); } catch {}
        try { attempted = new Set(JSON.parse(localStorage.getItem(attemptedKey) || '[]')); } catch {}

        for (let index = 0; index < items.length; index++) {
            const item = items[index];
            status.textContent = `处理中 ${index + 1}/${items.length}：${item.name}\n购买前校验价格 ${item.price} 金钱…`;
            try {
                if (!purchased.has(item.aid) && !item.alreadyOwned) {
                    if (!attempted.has(item.aid)) {
                        const result = await buySubtitleAttachment(item, () => {
                            attempted.add(item.aid);
                            localStorage.setItem(attemptedKey, JSON.stringify([...attempted]));
                        });
                        purchased.add(item.aid);
                        localStorage.setItem(purchasedKey, JSON.stringify([...purchased]));
                        if (!result.alreadyOwned) bought++;
                    }
                }
                status.textContent = `处理中 ${index + 1}/${items.length}：${item.name}\n购买已确认，正在下载…`;
                await downloadSubtitleAttachment(item);
                downloaded++;
            } catch (error) {
                failures.push(`${item.name}: ${error.message || error}`);
                status.textContent = `已停止在 ${index + 1}/${items.length}。\n${failures.join('\n')}`;
                break; // 购买结果或附件响应不确定时，不继续以免发生重复扣费。
            }
        }

        status.textContent = `流程结束：本次购买 ${bought} 个，触发下载 ${downloaded} 个，失败 ${failures.length} 个。` +
            (failures.length ? `\n${failures.join('\n')}` : '');
        button.textContent = '已完成';
        button.disabled = true;
    }

    function ensureSubtitleDownloadButton() {
        if (!isSubtitleForumList() || document.getElementById('4k2-subtitle-download-btn')) return;
        const btn = document.createElement('button');
        btn.id = '4k2-subtitle-download-btn';
        btn.textContent = '[一键提取字幕]';
        btn.style.cssText = 'position:fixed;top:170px;right:16px;background:#c62828;color:#fff;padding:9px 12px;border:0;border-radius:6px;cursor:pointer;z-index:9999;font-weight:bold;';
        btn.onclick = () => collectSubtitleAttachments(btn);
        document.body.appendChild(btn);
    }

    // ================= 图片 + 磁力 =================

    function processList() {
        const threads = document.querySelectorAll('#threadlisttableid li.kmlist');

        threads.forEach(li => {
            if (li.dataset.threadUrl) return;

            const atag = li.querySelector('.kmtit');
            if (!atag) return;

            const href = atag.href;
            if (!href || !href.includes('thread-')) return;

            li.dataset.threadUrl = href;
        });
    }

    function getThreadItemsForToggle() {
        return Array.from(document.querySelectorAll('li[data-thread-url]'));
    }

    function updateGlobalToggleButtonState() {
        const btn = document.getElementById('toggle-all-img-btn');
        const copyBtn = document.getElementById('copy-magnets-btn'); // 获取新增的复制按钮
        if (!btn) return;

        const list = getThreadItemsForToggle();
        const opened = list.filter(i => i.querySelector('.thread-images-container')).length;
        const isAllOpened = list.length > 0 && opened === list.length;

        btn.textContent = isAllOpened ? '[关全部图]' : '[开全部图]';

        // 仅在全部图展开时显示“复制未下载磁力”按钮
        if (copyBtn) {
            copyBtn.style.display = isAllOpened ? 'block' : 'none';
        }
    }

    async function toggleThreadImages(item, url) {
        const exist = item.querySelector('.thread-images-container');
        if (exist) {
            exist.remove();
            return;
        }

        const { images, magnets } = await fetchThreadData(url);
        const box = createContainer(images, magnets);
        item.appendChild(box);
    }

    function fetchThreadData(url) {
        return new Promise(resolve => {
            GM_xmlhttpRequest({
                method: 'GET',
                url,
                onload: r => {
                    const html = r.responseText;

                    const images = [...new Set(html.match(/https?:\/\/bocecdn\.com\/[^\s"']+\.(jpg|jpeg|png|gif)/gi) || [])];

                    const magnets = [...new Set(
                        (html.match(/magnet:\?xt=urn:btih:[A-Za-z0-9]+(&[^\s"']*)?/gi) || [])
                            .map(m => m.replace(/&amp;/g, '&'))
                    )];

                    resolve({ images, magnets });
                },
                onerror: () => resolve({ images: [], magnets: [] })
            });
        });
    }

    function createContainer(images, magnets) {
        const c = document.createElement('div');
        c.className = 'thread-images-container';
        c.style.cssText = 'padding:10px;background:#f9f9f9;border:1px solid #ddd;margin:5px 0;';

        // 图片
        images.forEach(u => {
            const i = document.createElement('img');
            i.src = u;
            i.style.maxWidth = CONFIG.thumbnailSize + 'px';
            i.style.margin = '3px';
            i.onclick = () => window.open(u);
            c.appendChild(i);
        });

        // 磁力
        if (magnets.length > 0) {
            const div = document.createElement('div');
            div.style.marginTop = '8px';

            magnets.forEach((m, i) => {
                const a = document.createElement('a');
                a.href = m;
                a.textContent = `磁力${i + 1}`;
                a.style.display = 'block';
                a.style.color = '#007bff';
                div.appendChild(a);
            });

            c.appendChild(div);
        }

        return c;
    }

    function ensureGlobalToggleButton() {
        if (document.getElementById('toggle-all-img-btn')) return;

        const btn = document.createElement('span');
        btn.id = 'toggle-all-img-btn';
        btn.textContent = '[开全部图]';

        btn.style.cssText = `
            position:fixed; top:80px; right:16px;
            background:#28a745;color:white;padding:8px;
            border-radius:6px;cursor:pointer;z-index:9999;
        `;

        btn.onclick = async () => {
            if (isBatchProcessing) return;
            isBatchProcessing = true;
            btn.textContent = '[处理中...]'; // 点击后给个反馈

            processList();

            const list = getThreadItemsForToggle();

            for (const item of list) {
                await toggleThreadImages(item, item.dataset.threadUrl);
            }

            isBatchProcessing = false;
            updateGlobalToggleButtonState();
        };

        document.body.appendChild(btn);
    }

    // ================= 新增：一键复制未下载磁力按钮 =================
    function ensureCopyMagnetsButton() {
        if (document.getElementById('copy-magnets-btn')) return;

        const btn = document.createElement('span');
        btn.id = 'copy-magnets-btn';
        btn.textContent = '[复制未下载磁力]';

        // 位置设置在主按钮正下方 (top: 125px)
        btn.style.cssText = `
            position:fixed; top:125px; right:16px;
            background:#007bff;color:white;padding:8px;
            border-radius:6px;cursor:pointer;z-index:9999;
            display: none; /* 默认隐藏，等待全部图打开后显示 */
        `;

        btn.onclick = () => {
            const list = getThreadItemsForToggle();
            const magnetsToCopy = [];

            list.forEach(item => {
                const text = item.textContent || "";

                // 检查节点文本中是否包含“【已下载】”或“【已有字幕】”标签
                if (!text.includes('【已下载】') && !text.includes('【已有字幕】')) {
                    // 只获取当前这一个帖子容器里面的磁力链接
                    const magnetLinks = item.querySelectorAll('.thread-images-container a[href^="magnet:"]');
                    magnetLinks.forEach(a => {
                        magnetsToCopy.push(a.href);
                    });
                }
            });

            if (magnetsToCopy.length === 0) {
                alert('当前列表没有找到符合条件的未下载磁力链接！');
                return;
            }

            // 每个磁力链接增加换行符
            const textToCopy = magnetsToCopy.join('\n');

            // 写入系统剪贴板
            navigator.clipboard.writeText(textToCopy).then(() => {
                const originalText = btn.textContent;
                btn.textContent = `[成功复制 ${magnetsToCopy.length} 条!]`;
                btn.style.background = '#28a745'; // 变绿提示成功

                setTimeout(() => {
                    btn.textContent = originalText;
                    btn.style.background = '#007bff'; // 2秒后恢复颜色和文字
                }, 2000);
            }).catch(err => {
                alert('复制失败，可能是浏览器剪贴板权限受限。');
                console.error(err);
            });
        };

        document.body.appendChild(btn);
    }


    // ================= 初始化 =================

    function init() {
        ensureGlobalToggleButton();
        ensureCopyMagnetsButton(); // 初始化复制按钮
        ensureSubtitleDownloadButton();
        processList();

        setTimeout(() => {
            processEmbyBadges(document.querySelectorAll('.km_subject'));
        }, 500);

        const container = document.querySelector('#threadlisttableid');
        if (!container) return;

        let timer = null;

        const ob = new MutationObserver(() => {
            clearTimeout(timer);
            timer = setTimeout(() => {
                processList();
                processEmbyBadges(document.querySelectorAll('.km_subject:not([data-emby-checked])'));
                updateGlobalToggleButtonState(); // 列表变动时刷新按钮状态
            }, 300);
        });

        ob.observe(container, { childList: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

})();
