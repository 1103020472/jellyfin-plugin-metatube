// ==UserScript==
// @name         4k2论坛帖子缩略图 + Emby检测（最终稳定版）
// @namespace    http://tampermonkey.net/
// @version      7.7
// @match        https://4k2.me/*
// @match        https://4k2.com/*
// @grant        GM_xmlhttpRequest
// @grant        GM_setClipboard
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

    // 番号形如 CJVR-071 / URVRSP-614 / 3DSVR-2015。
    // 前缀允许以数字开头（3DSVR、4KVR 之类），否则 "3DSVR-2015" 会被截成 "DSVR-2015"。
    const CODE_REGEX = /[A-Za-z0-9]{3,8}-\d{3,5}/;
    const CODE_GLOBAL_REGEX = /[A-Za-z0-9]{3,8}-\d{3,5}/g;

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

    // 4k2 的番号带前置数字（3DSVR-2015），但 Emby 里统一存成了去掉数字的 DSVR-2015，
    // 直接拿 3DSVR-2015 去搜只会模糊命中、标题里并不含该串，必须再去掉前置数字搜一次。
    function embySearchAliases(code) {
        const aliases = [code];
        const stripped = code.replace(/^\d+/, '');
        if (stripped !== code && /^[A-Za-z]{3,8}-\d{3,5}$/.test(stripped)) {
            aliases.push(stripped);
        }
        return aliases;
    }

    async function findEmbyMovie(alias) {
        const res = await GM_xmlhttpRequestPromise({
            method: "GET",
            url: `${CONFIG.EMBY_BASE_URL}/emby/Items?IncludeItemTypes=Movie&Recursive=true&SearchTerm=${encodeURIComponent(alias)}`,
            headers: {
                "X-Emby-Token": CONFIG.EMBY_API_KEY,
                "Accept": "application/json"
            }
        });

        const data = JSON.parse(res.responseText);
        // Emby 的 SearchTerm 是模糊搜索；只认标题中实际包含番号的条目，
        // 避免把仅因年份/描述命中的影片误判为该番号。
        const normalizedCode = alias.toUpperCase();
        return (data.Items || []).find(item =>
            [item.Name, item.OriginalTitle].some(title =>
                typeof title === 'string' && title.toUpperCase().includes(normalizedCode)
            )
        ) || null;
    }

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
                let movie = null;
                for (const alias of embySearchAliases(code)) {
                    movie = await findEmbyMovie(alias);
                    if (movie) break;
                }
                if (!movie) {
                    // 负结果也缓存：同一页面里徽标检查与后续批量流程会重复问同一个番号。
                    const miss = { exists: false, hasSubtitle: false };
                    embyCache.set(code, miss);
                    return miss;
                }

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


    // ================= VR影像（forum-27）：一键复制直链 =================
    //
    // 流程：只针对「Emby 中不存在」的番号，逐个进详情页解析 down.772.one 直链，
    // 剔除「Y:\光鸭云盘\未同步」里已下载过的文件，结果缓存到按钮上；
    // 再次点击按钮弹出二次交互弹框，按番号维度展示直链 + 帖子日期 + 历史提取次数 +
    // 封面缩略图（悬浮看大图），勾选后一键复制为纯 URL 文本。
    // 直链按番号持久化到 localStorage（4k2-vr-link-cache-v1）：同一番号再次提取直接复用，
    // 不再进详情页；每个番号的触发次数单独累计（4k2-vr-extract-stats-v1）供二次弹框展示。

    const VR_FORUM_ID = '27';
    // 「未同步」目录的文件名清单（浏览器读不到本地盘，必须由用户选一次目录后缓存）。
    const VR_DOWNLOADED_KEY = '4k2-vr-downloaded-files';
    // 按番号缓存的直链提取结果：同一番号再次提取时直接复用，不再进详情页。
    const VR_LINK_CACHE_KEY = '4k2-vr-link-cache-v1';
    // 每个番号的累计提取次数（二次弹框展示「历史提取 N 次」）。
    const VR_EXTRACT_STATS_KEY = '4k2-vr-extract-stats-v1';
    const MEDIA_EXT_REGEX = /\.(?:mp4|mkv|mov|avi|wmv|flv|m2ts|ts)(?:\?.*)?$/i;
    const IMAGE_EXT_REGEX = /\.(?:jpe?g|png|gif|webp|bmp)(?:\?.*)?$/i;
    // 直链域名以后可能变，主要靠扩展名识别，这里只作为兜底提示。
    const VR_LINK_HOST_HINT = /772\.one/i;

    let vrLinkCache = null;       // 提取结果，仅当前页面内存；刷新页面自然清空
    let vrLinkStore = null;       // 按番号持久化的直链缓存（localStorage）
    let vrExtractStats = null;    // 按番号累计的提取次数（localStorage）
    let vrLastRunMeta = null;     // 最近一次提取的统计（缓存命中数等）
    let vrDownloadedSet = null;   // 已下载文件名集合（小写）
    let vrDownloadedMeta = null;  // { count, at }
    let isVrExtracting = false;
    let vrPreviewEl = null;

    function isVrImageForumList() {
        if (/forum-27-\d+\.html/.test(location.pathname)) return true;
        const params = new URLSearchParams(location.search);
        return params.get('mod') === 'forumdisplay' && params.get('fid') === VR_FORUM_ID;
    }

    function toAbsoluteUrl(url, base) {
        if (!url) return null;
        try {
            return new URL(url, base).href;
        } catch {
            return null;
        }
    }

    function fileNameOfUrl(url) {
        try {
            const path = new URL(url).pathname;
            return decodeURIComponent(path.split('/').filter(Boolean).pop() || '').toLowerCase();
        } catch {
            return '';
        }
    }

    function isDirectMediaLink(url) {
        if (!/^https?:\/\//i.test(url)) return false;
        // 排除论坛自身域名与图床，避免把附件页/图片当成直链。
        if (/4k2\.(?:me|com)|bocecdn\.com/i.test(url)) return false;
        return MEDIA_EXT_REGEX.test(url) || VR_LINK_HOST_HINT.test(url);
    }

    // CJVR-071 -> cjvr00071（详情页文件名里的番号形态）
    function normalizeVrCode(code) {
        const parts = String(code).toUpperCase().split('-');
        const num = parts.pop() || '';
        return (parts.join('') + num.padStart(5, '0')).toLowerCase();
    }

    function isVrDownloaded(url) {
        if (!vrDownloadedSet || !vrDownloadedSet.size) return false;
        const name = fileNameOfUrl(url);
        return !!name && vrDownloadedSet.has(name);
    }

    function loadVrDownloadedSet() {
        if (vrDownloadedSet) return true;
        try {
            const saved = JSON.parse(localStorage.getItem(VR_DOWNLOADED_KEY) || 'null');
            if (saved && Array.isArray(saved.files)) {
                vrDownloadedSet = new Set(saved.files.map(name => String(name).toLowerCase()));
                vrDownloadedMeta = { count: saved.files.length, at: saved.at || 0 };
                updateVrDirButtonLabel();
                return true;
            }
        } catch {}
        return false;
    }

    function saveVrDownloadedSet(names) {
        vrDownloadedSet = new Set(names);
        vrDownloadedMeta = { count: names.length, at: Date.now() };
        try {
            localStorage.setItem(VR_DOWNLOADED_KEY, JSON.stringify({
                files: [...vrDownloadedSet],
                at: vrDownloadedMeta.at
            }));
        } catch {}
        updateVrDirButtonLabel();
    }

    // ---------- 按番号持久化：直链缓存 + 提取次数 ----------

    function loadVrLinkStore() {
        if (vrLinkStore) return vrLinkStore;
        vrLinkStore = { version: 1, byCode: {} };
        try {
            const saved = JSON.parse(localStorage.getItem(VR_LINK_CACHE_KEY) || 'null');
            if (saved && saved.byCode && typeof saved.byCode === 'object') {
                vrLinkStore.byCode = saved.byCode;
            }
        } catch {}
        return vrLinkStore;
    }

    function saveVrLinkStore() {
        try {
            localStorage.setItem(VR_LINK_CACHE_KEY, JSON.stringify(loadVrLinkStore()));
        } catch (error) {
            console.warn('[4k2直链] 本地缓存写入失败:', error);
        }
    }

    function getVrCachedLinkEntry(code) {
        const entry = loadVrLinkStore().byCode[code];
        if (!entry || !Array.isArray(entry.links) || !entry.links.length) return null;
        return entry;
    }

    function getVrCachedCodeCount() {
        return Object.keys(loadVrLinkStore().byCode || {}).length;
    }

    function updateVrLinkCacheEntry(code, info) {
        const store = loadVrLinkStore();
        const previous = store.byCode[code] || {};
        // 同一番号可能出现在多个帖子里，直链做并集，避免后抓到的帖子覆盖掉之前的链接。
        const links = [...new Set([...(previous.links || []), ...(info.links || [])])];
        if (!links.length) return;
        store.byCode[code] = {
            code,
            links,
            // 元信息以最早一次记录为准，保证「帖子日期」与展示的直链来源一致。
            cover: previous.cover || info.cover || null,
            title: previous.title || info.title || '',
            threadUrl: previous.threadUrl || info.threadUrl || '',
            postDate: previous.postDate || info.postDate || '',
            postDateRaw: previous.postDateRaw || info.postDateRaw || '',
            updatedAt: Date.now()
        };
    }

    function loadVrExtractStats() {
        if (vrExtractStats) return vrExtractStats;
        vrExtractStats = {};
        try {
            const saved = JSON.parse(localStorage.getItem(VR_EXTRACT_STATS_KEY) || 'null');
            if (saved && typeof saved === 'object' && !Array.isArray(saved)) {
                vrExtractStats = saved;
            }
        } catch {}
        return vrExtractStats;
    }

    function saveVrExtractStats() {
        try {
            localStorage.setItem(VR_EXTRACT_STATS_KEY, JSON.stringify(loadVrExtractStats()));
        } catch (error) {
            console.warn('[4k2直链] 提取次数写入失败:', error);
        }
    }

    function getVrExtractMeta(code) {
        const entry = loadVrExtractStats()[code];
        return {
            count: entry ? Number(entry.count) || 0 : 0,
            firstAt: entry ? Number(entry.firstAt) || 0 : 0,
            lastAt: entry ? Number(entry.lastAt) || 0 : 0
        };
    }

    function bumpVrExtractCount(code, counted) {
        if (!code || counted.has(code)) return;
        counted.add(code);
        const stats = loadVrExtractStats();
        const entry = stats[code] || { count: 0, firstAt: 0, lastAt: 0 };
        entry.count = (Number(entry.count) || 0) + 1;
        entry.lastAt = Date.now();
        if (!entry.firstAt) entry.firstAt = entry.lastAt;
        stats[code] = entry;
    }

    function pad2(value) {
        return String(value).padStart(2, '0');
    }

    function formatVrStamp(timestamp) {
        const date = new Date(Number(timestamp));
        if (Number.isNaN(date.getTime())) return '';
        return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ` +
            `${pad2(date.getHours())}:${pad2(date.getMinutes())}`;
    }

    // 列表页 .kmtime 既可能是绝对日期，也可能是「昨天 03:53」「3 天前」这类相对时间。
    // 统一换算成绝对日期后再展示和缓存，否则缓存里的「昨天」过几天就错了。
    function parseVrPostDate(row) {
        const raw = (row?.querySelector('.kmtime')?.textContent || '')
            .replace(/^\s*发表于\s*/, '')
            .replace(/\s+/g, ' ')
            .trim();
        if (!raw) return { date: '', raw: '' };

        const now = new Date();
        const stamp = (date, withTime) =>
            `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}` +
            (withTime ? ` ${pad2(date.getHours())}:${pad2(date.getMinutes())}` : '');
        const shiftDays = days => new Date(now.getFullYear(), now.getMonth(), now.getDate() + days);

        const absolute = raw.match(/(\d{4})-(\d{1,2})-(\d{1,2})(?:\s+(\d{1,2}):(\d{2}))?/);
        if (absolute) {
            const date = new Date(Number(absolute[1]), Number(absolute[2]) - 1, Number(absolute[3]),
                Number(absolute[4] || 0), Number(absolute[5] || 0));
            return { date: stamp(date, absolute[4] !== undefined), raw };
        }

        if (/刚刚/.test(raw)) return { date: stamp(now, true), raw };

        let match = raw.match(/(\d+)\s*秒前/);
        if (match) return { date: stamp(new Date(now.getTime() - Number(match[1]) * 1000), true), raw };
        match = raw.match(/(\d+)\s*分钟前/);
        if (match) return { date: stamp(new Date(now.getTime() - Number(match[1]) * 60000), true), raw };
        match = raw.match(/(\d+)\s*小时前/);
        if (match) return { date: stamp(new Date(now.getTime() - Number(match[1]) * 3600000), true), raw };
        match = raw.match(/(\d+)\s*天前/);
        if (match) return { date: stamp(shiftDays(-Number(match[1])), false), raw };

        match = raw.match(/(今天|昨天|前天)\s*(\d{1,2}):(\d{2})/);
        if (match) {
            const offset = match[1] === '今天' ? 0 : match[1] === '昨天' ? -1 : -2;
            const date = shiftDays(offset);
            date.setHours(Number(match[2]), Number(match[3]), 0, 0);
            return { date: stamp(date, true), raw };
        }

        match = raw.match(/^(今天|昨天|前天)$/);
        if (match) {
            const offset = match[1] === '今天' ? 0 : match[1] === '昨天' ? -1 : -2;
            return { date: stamp(shiftDays(offset), false), raw };
        }

        // 跨年帖 Discuz 只显示「10-16」这种月日。
        match = raw.match(/^(\d{1,2})-(\d{1,2})$/);
        if (match) return { date: `${now.getFullYear()}-${pad2(match[1])}-${pad2(match[2])}`, raw };

        return { date: raw, raw };
    }

    // 用目录选择器读取「未同步」目录：只取文件名，不读文件内容。
    // 注意：必须在用户点击的同步调用链里触发，否则浏览器会拦截。
    function pickVrUnsyncedDir(onDone) {
        const input = document.createElement('input');
        input.type = 'file';
        input.webkitdirectory = true;
        input.multiple = true;
        input.style.display = 'none';

        let settled = false;
        const finish = names => {
            if (settled) return;
            settled = true;
            input.remove();
            if (names && names.length) {
                saveVrDownloadedSet(names);
            }
            if (typeof onDone === 'function') onDone(!!(names && names.length));
        };

        input.addEventListener('change', () => {
            const names = Array.from(input.files || []).map(file => file.name.toLowerCase());
            finish(names);
        });
        input.addEventListener('cancel', () => finish(null));

        document.body.appendChild(input);
        input.click();
    }

    function parseVrThreadLinks(html, threadUrl, codeInfos) {
        const doc = new DOMParser().parseFromString(html, 'text/html');
        const post = doc.querySelector('#postlist td.t_f, #postlist div[id^="postmessage_"], #postlist .t_f');
        const cover = pickVrCover(post || doc.querySelector('#postlist') || doc, threadUrl);

        const seen = new Set();
        const groups = new Map();
        const fallback = codeInfos.find(info => !info.exists) || codeInfos[0];

        // 直链在正文里是普通 <a>，只能用 DOM 解析；用正则扫 HTML 会漏掉
        // 路径段里含 g 等字符的链接（如 swxd8egl/kiwvr00911_1_8k.mp4）。
        (post || doc).querySelectorAll('a[href]').forEach(anchor => {
            const url = toAbsoluteUrl(anchor.getAttribute('href'), threadUrl);
            if (!url || !isDirectMediaLink(url) || seen.has(url)) return;
            seen.add(url);

            const name = fileNameOfUrl(url);
            const owner = codeInfos.find(info => name.includes(normalizeVrCode(info.code)));
            // 该番号 Emby 里已有，交给原有脚本逻辑，不在这里处理。
            if (owner && owner.exists) return;

            const code = owner ? owner.code : fallback.code;
            if (!groups.has(code)) groups.set(code, { code, links: [] });
            groups.get(code).links.push(url);
        });

        // 「已下载」过滤交给调用方：本地缓存要保留原始直链，
        // 重新选择「未同步」目录后才能按新清单重新过滤。
        return { cover, groups: [...groups.values()] };
    }

    function pickVrCover(root, threadUrl) {
        const candidates = [];
        Array.from(root.querySelectorAll('img')).forEach(img => {
            if (!img || img.classList.contains('user_avatar')) return;
            const raw = img.getAttribute('file') || img.getAttribute('zoomfile') ||
                img.getAttribute('data-original') || img.getAttribute('src');
            const url = toAbsoluteUrl(raw, threadUrl);
            if (!url || !/^https?:\/\//i.test(url)) return;
            if (/4k2\.(?:me|com)|uc_server|static\/image/i.test(url)) return;
            if (!IMAGE_EXT_REGEX.test(url)) return;
            candidates.push(url.replace(/^http:/i, 'https:'));
        });
        return candidates[0] || null;
    }

    function showVrToast(message, ms = 8000) {
        const el = document.createElement('div');
        el.style.cssText = 'position:fixed;top:16px;right:16px;z-index:100003;background:#212529;color:#fff;' +
            'padding:10px 14px;border-radius:6px;font:13px/1.5 sans-serif;box-shadow:0 6px 24px #0007;max-width:380px;';
        el.textContent = message;
        document.body.appendChild(el);
        setTimeout(() => el.remove(), ms);
    }

    function onVrDirectLinkClick(button) {
        if (vrLinkCache !== null) {
            if (vrLinkCache.length) showVrLinkPicker(vrLinkCache);
            else alert('本页上次提取结果为 0 条直链。刷新页面后才会重新提取。');
            return;
        }
        if (isVrExtracting) return;

        if (!loadVrDownloadedSet()) {
            // 首次使用：先让用户选一次「未同步」目录（同步触发，保留用户手势）。
            button.textContent = '[请选择「未同步」目录]';
            pickVrUnsyncedDir(picked => {
                button.textContent = '[一键复制直链]';
                if (picked) collectVrDirectLinks(button);
                else alert('未读取到「未同步」目录，直链提取已取消。');
            });
            return;
        }

        collectVrDirectLinks(button);
    }

    async function collectVrDirectLinks(button) {
        if (isVrExtracting) return;
        isVrExtracting = true;
        button.disabled = true;
        button.style.background = '#6c757d';

        // 同一次提取里同一番号可能出现在多个帖子，次数只记一次。
        const countedCodes = new Set();
        let finished = false;
        try {
            const rows = getForumThreadRows().filter(row => !/^stickthread_/.test(row.id));
            const targets = [];

            // 第一轮：查 Emby，只留下不存在的番号，并记下帖子日期。
            for (let index = 0; index < rows.length; index++) {
                const row = rows[index];
                const subject = row.querySelector('.km_subject');
                const titleLink = row.querySelector('a.kmtit[href*="thread-"]');
                if (!subject || !titleLink) continue;

                const codes = [...new Set((subject.textContent.toUpperCase().match(CODE_GLOBAL_REGEX) || []))];
                if (!codes.length) continue;

                button.textContent = `[检查 Emby ${index + 1}/${rows.length}]`;
                const results = await Promise.all(codes.map(code => queryEmbyMovie(code)));
                const codeInfos = codes.map((code, i) => ({ code, exists: !!(results[i] && results[i].exists) }));
                const missingCodes = codeInfos.filter(info => !info.exists).map(info => info.code);
                if (!missingCodes.length) continue;

                targets.push({
                    codeInfos,
                    missingCodes,
                    title: (subject.textContent || '').replace(/\s+/g, ' ').trim(),
                    threadUrl: titleLink.href,
                    postDate: parseVrPostDate(row)
                });
            }

            let skippedDownloaded = 0;
            let cacheHits = 0;
            let fetchedThreads = 0;
            const byCode = new Map();

            const addGroup = (code, group) => {
                const previous = byCode.get(code);
                if (previous) {
                    group.links.forEach(link => {
                        if (!previous.links.includes(link)) previous.links.push(link);
                    });
                    if (!previous.cover) previous.cover = group.cover || null;
                    if (!previous.postDate) previous.postDate = group.postDate || '';
                    if (!previous.postDateRaw) previous.postDateRaw = group.postDateRaw || '';
                    return previous;
                }
                const entry = {
                    code,
                    title: group.title || '',
                    threadUrl: group.threadUrl || '',
                    cover: group.cover || null,
                    postDate: group.postDate || '',
                    postDateRaw: group.postDateRaw || '',
                    links: group.links.slice(),
                    fromCache: !!group.fromCache,
                    cacheUpdatedAt: group.cacheUpdatedAt || 0
                };
                byCode.set(code, entry);
                return entry;
            };

            // 第二轮：先吃本地缓存，缓存里没有的番号才进详情页。
            for (let index = 0; index < targets.length; index++) {
                const target = targets[index];
                const pendingCodes = [];

                target.missingCodes.forEach(code => {
                    const cached = getVrCachedLinkEntry(code);
                    if (!cached) {
                        pendingCodes.push(code);
                        return;
                    }
                    cacheHits++;
                    bumpVrExtractCount(code, countedCodes);
                    // 缓存里存的是原始直链，这里按当前「未同步」清单过滤。
                    const links = cached.links.filter(link => !isVrDownloaded(link));
                    skippedDownloaded += cached.links.length - links.length;
                    addGroup(code, {
                        links,
                        cover: cached.cover,
                        title: cached.title || target.title,
                        threadUrl: cached.threadUrl || target.threadUrl,
                        postDate: cached.postDate || target.postDate.date,
                        postDateRaw: cached.postDateRaw || target.postDate.raw,
                        fromCache: true,
                        cacheUpdatedAt: cached.updatedAt || 0
                    });
                });

                // 该帖子的番号全都有缓存，不必再读详情页。
                if (!pendingCodes.length) continue;

                button.textContent = `[读取详情 ${index + 1}/${targets.length}]`;
                let response;
                try {
                    response = await requestForum({ method: 'GET', url: target.threadUrl });
                    if (response.status < 200 || response.status >= 400) throw new Error(`HTTP ${response.status}`);
                } catch (error) {
                    console.warn('[4k2直链] 详情页读取失败:', target.threadUrl, error);
                    continue;
                }
                fetchedThreads++;

                const parsed = parseVrThreadLinks(response.responseText, target.threadUrl, target.codeInfos);
                parsed.groups.forEach(group => {
                    bumpVrExtractCount(group.code, countedCodes);
                    // 原始直链（含已下载的）写入本地缓存，供下次跳过详情页。
                    updateVrLinkCacheEntry(group.code, {
                        links: group.links,
                        cover: parsed.cover,
                        title: target.title,
                        threadUrl: target.threadUrl,
                        postDate: target.postDate.date,
                        postDateRaw: target.postDate.raw
                    });

                    const links = group.links.filter(link => !isVrDownloaded(link));
                    skippedDownloaded += group.links.length - links.length;
                    if (!links.length) return;
                    addGroup(group.code, {
                        links,
                        cover: parsed.cover,
                        title: target.title,
                        threadUrl: target.threadUrl,
                        postDate: target.postDate.date,
                        postDateRaw: target.postDate.raw
                    });
                });
            }

            // 直链缓存 + 番号提取次数统一落盘。
            saveVrLinkStore();
            saveVrExtractStats();

            const groups = [...byCode.values()]
                .filter(group => group.links.length)
                .sort((a, b) => a.code.localeCompare(b.code, undefined, { numeric: true, sensitivity: 'base' }));
            const totalLinks = groups.reduce((sum, group) => sum + group.links.length, 0);

            vrLinkCache = groups;
            vrLastRunMeta = { cacheHits, fetchedThreads, at: Date.now() };
            finished = true;

            if (!groups.length) {
                button.textContent = '[一键复制直链]';
                alert(`提取完成：共 ${targets.length} 个番号不在 Emby，没有需要提取的直链` +
                    (skippedDownloaded ? `（已下载 ${skippedDownloaded} 条，已跳过）。` : '。'));
                return;
            }

            button.textContent = `[提取完成 ${groups.length}番号/${totalLinks}直链 · 点此查看]`;
            button.style.background = '#28a745';
            showVrToast(`直链提取完成：${groups.length} 个番号 / ${totalLinks} 条直链` +
                (cacheHits ? `；${cacheHits} 个番号命中本地缓存，未进详情页` : '') +
                (fetchedThreads ? `；读取详情页 ${fetchedThreads} 个` : '') +
                (skippedDownloaded ? `，已跳过 ${skippedDownloaded} 条已下载` : '') +
                `。本地共缓存 ${getVrCachedCodeCount()} 个番号，再次点击按钮查看。`);
        } finally {
            isVrExtracting = false;
            button.disabled = false;
            if (!finished) button.style.background = '#6f42c1';
        }
    }

    // ---------- 二次交互弹框 ----------

    function ensureVrCoverPreview() {
        if (vrPreviewEl) return vrPreviewEl;
        vrPreviewEl = document.createElement('div');
        vrPreviewEl.id = '4k2-vr-cover-preview';
        vrPreviewEl.style.cssText = 'position:fixed;z-index:100002;display:none;pointer-events:none;' +
            'background:#fff;border:1px solid #999;box-shadow:0 6px 24px #0007;padding:4px;border-radius:4px;';
        const img = document.createElement('img');
        img.style.cssText = 'display:block;max-width:460px;max-height:460px;';
        vrPreviewEl.appendChild(img);
        document.body.appendChild(vrPreviewEl);
        return vrPreviewEl;
    }

    function positionVrCoverPreview(anchorEl) {
        if (!vrPreviewEl || vrPreviewEl.style.display === 'none') return;
        const rect = anchorEl.getBoundingClientRect();
        const box = vrPreviewEl.getBoundingClientRect();
        let left = rect.right + 12;
        if (left + box.width > window.innerWidth - 8) left = Math.max(8, rect.left - box.width - 12);
        let top = rect.top;
        if (top + box.height > window.innerHeight - 8) top = Math.max(8, window.innerHeight - box.height - 8);
        vrPreviewEl.style.left = `${left}px`;
        vrPreviewEl.style.top = `${top}px`;
    }

    function showVrCoverPreview(src, anchorEl) {
        if (!src) return;
        const box = ensureVrCoverPreview();
        const img = box.firstElementChild;
        const reveal = () => {
            if (img.getAttribute('src') !== src) return; // 鼠标已移到别的封面
            box.style.display = 'block';
            positionVrCoverPreview(anchorEl);
        };
        img.onload = reveal;
        img.onerror = () => { box.style.display = 'none'; };
        if (img.getAttribute('src') !== src) {
            // 加载完成前不显示，否则会先闪一个只有内边距的空框。
            box.style.display = 'none';
            img.src = src;
        }
        if (img.complete && img.naturalWidth) reveal();
    }

    function hideVrCoverPreview() {
        if (vrPreviewEl) vrPreviewEl.style.display = 'none';
    }

    function copyTextToClipboard(text) {
        // GM_setClipboard 不依赖页面焦点，最稳，优先使用；没有时再退回浏览器剪贴板 API。
        if (typeof GM_setClipboard === 'function') {
            try {
                GM_setClipboard(text, 'text');
                return Promise.resolve(true);
            } catch {}
        }
        return navigator.clipboard.writeText(text).then(() => true).catch(() => {
            try {
                const area = document.createElement('textarea');
                area.value = text;
                area.style.cssText = 'position:fixed;left:-9999px;top:0;';
                document.body.appendChild(area);
                area.select();
                const ok = document.execCommand('copy');
                area.remove();
                return ok;
            } catch {
                return false;
            }
        });
    }

    function showVrLinkPicker(groups) {
        document.getElementById('4k2-vr-link-picker')?.remove();
        hideVrCoverPreview();

        const totalLinks = groups.reduce((sum, group) => sum + group.links.length, 0);
        const overlay = document.createElement('div');
        overlay.id = '4k2-vr-link-picker';
        overlay.style.cssText = 'position:fixed;inset:0;background:#0009;z-index:100000;display:flex;' +
            'align-items:center;justify-content:center;padding:20px;';

        const panel = document.createElement('div');
        panel.style.cssText = 'background:#fff;color:#222;width:min(920px,96vw);max-height:88vh;display:flex;' +
            'flex-direction:column;border-radius:8px;padding:16px;box-shadow:0 8px 32px #0006;font:14px/1.5 sans-serif;';

        const heading = document.createElement('h3');
        heading.textContent = `VR影像直链：${groups.length} 个番号 / ${totalLinks} 条直链`;
        heading.style.margin = '0 0 6px';

        const hint = document.createElement('div');
        hint.style.cssText = 'color:#666;font-size:12px;margin-bottom:10px;';
        hint.textContent = '勾选番号后点“复制”即可把对应直链逐行写入剪贴板；鼠标悬停封面小图可看大图。' +
            '每个番号显示帖子日期与历史提取次数。' +
            (vrLastRunMeta && vrLastRunMeta.cacheHits
                ? `本次 ${vrLastRunMeta.cacheHits} 个番号命中本地缓存（未进详情页）。`
                : '');

        const toolbar = document.createElement('div');
        toolbar.style.cssText = 'display:flex;align-items:center;gap:8px;margin-bottom:8px;';
        const summary = document.createElement('span');
        summary.style.cssText = 'color:#444;font-size:13px;';
        const selectAll = document.createElement('button');
        selectAll.textContent = '全选';
        const selectNone = document.createElement('button');
        selectNone.textContent = '全不选';
        [selectAll, selectNone].forEach(btn => {
            btn.style.cssText = 'background:#eee;border:1px solid #ccc;border-radius:4px;padding:4px 10px;cursor:pointer;';
        });
        toolbar.append(selectAll, selectNone, summary);

        const list = document.createElement('div');
        list.style.cssText = 'flex:1;overflow:auto;border:1px solid #ddd;border-radius:4px;';

        const entries = [];
        const updateSummary = () => {
            const selected = entries.filter(entry => entry.input.checked);
            const links = selected.reduce((sum, entry) => sum + entry.group.links.length, 0);
            summary.textContent = `已选 ${selected.length}/${entries.length} 个番号，共 ${links} 条直链`;
        };

        groups.forEach(group => {
            const card = document.createElement('div');
            card.className = 'vr-link-card';
            card.dataset.code = group.code;
            card.style.cssText = 'display:flex;gap:12px;padding:10px;border-bottom:1px solid #eee;';

            const thumbWrap = document.createElement('div');
            thumbWrap.style.cssText = 'flex:0 0 96px;';
            if (group.cover) {
                const thumb = document.createElement('img');
                thumb.src = group.cover;
                thumb.loading = 'lazy';
                thumb.referrerPolicy = 'no-referrer';
                thumb.style.cssText = 'width:96px;max-height:130px;object-fit:cover;border:1px solid #ccc;' +
                    'border-radius:3px;cursor:zoom-in;background:#f5f5f5;';
                thumb.addEventListener('mouseenter', () => showVrCoverPreview(group.cover, thumb));
                thumb.addEventListener('mouseleave', hideVrCoverPreview);
                thumbWrap.appendChild(thumb);
            } else {
                const empty = document.createElement('div');
                empty.textContent = '无封面';
                empty.style.cssText = 'width:96px;height:70px;display:flex;align-items:center;justify-content:center;' +
                    'border:1px dashed #ccc;border-radius:3px;color:#999;font-size:12px;';
                thumbWrap.appendChild(empty);
            }

            const body = document.createElement('div');
            body.style.cssText = 'flex:1;min-width:0;';

            const header = document.createElement('label');
            header.style.cssText = 'display:flex;align-items:center;gap:8px;cursor:pointer;';
            const input = document.createElement('input');
            input.type = 'checkbox';
            input.checked = true;
            input.addEventListener('change', updateSummary);
            const codeText = document.createElement('strong');
            codeText.textContent = group.code;
            codeText.style.cssText = 'white-space:nowrap;';
            const countText = document.createElement('span');
            countText.textContent = `${group.links.length} 条`;
            countText.style.cssText = 'color:#c62828;font-size:12px;white-space:nowrap;';
            const titleText = document.createElement('span');
            titleText.textContent = group.title || '';
            titleText.style.cssText = 'flex:1;min-width:0;color:#888;font-size:12px;overflow:hidden;' +
                'text-overflow:ellipsis;white-space:nowrap;';
            header.append(input, codeText, countText, titleText);

            // 帖子日期 + 历史提取次数：日期统一换算成绝对日期，悬停可看列表页原始写法。
            const stats = getVrExtractMeta(group.code);
            const meta = document.createElement('div');
            meta.style.cssText = 'margin-top:4px;display:flex;flex-wrap:wrap;align-items:center;gap:10px;' +
                'color:#777;font-size:12px;';

            const dateSpan = document.createElement('span');
            dateSpan.className = 'vr-post-date';
            dateSpan.textContent = `发帖 ${group.postDate || '日期未知'}`;
            if (group.postDateRaw && group.postDateRaw !== group.postDate) {
                dateSpan.title = `列表页显示：${group.postDateRaw}`;
            }

            const countSpan = document.createElement('span');
            countSpan.className = 'vr-extract-count';
            countSpan.textContent = `历史提取 ${stats.count} 次`;
            if (stats.lastAt) {
                const first = stats.firstAt ? `首次：${formatVrStamp(stats.firstAt)}；` : '';
                countSpan.title = `${first}上次提取：${formatVrStamp(stats.lastAt)}`;
            }

            meta.append(dateSpan, countSpan);

            if (group.fromCache) {
                const cacheSpan = document.createElement('span');
                cacheSpan.className = 'vr-cache-badge';
                cacheSpan.textContent = '本地缓存';
                cacheSpan.style.cssText = 'color:#2e7d32;';
                if (group.cacheUpdatedAt) cacheSpan.title = `缓存于 ${formatVrStamp(group.cacheUpdatedAt)}`;
                meta.appendChild(cacheSpan);
            }

            if (group.threadUrl) {
                const threadLink = document.createElement('a');
                threadLink.href = group.threadUrl;
                threadLink.target = '_blank';
                threadLink.rel = 'noopener';
                threadLink.textContent = '帖子';
                threadLink.style.cssText = 'color:#0066cc;text-decoration:none;';
                meta.appendChild(threadLink);
            }

            const linkBox = document.createElement('div');
            linkBox.style.cssText = 'margin-top:6px;display:flex;flex-direction:column;gap:2px;';
            group.links.forEach(link => {
                const anchor = document.createElement('a');
                anchor.href = link;
                anchor.target = '_blank';
                anchor.rel = 'noopener';
                anchor.textContent = link;
                anchor.style.cssText = 'font:12px/1.5 Consolas,Menlo,monospace;color:#0066cc;word-break:break-all;';
                linkBox.appendChild(anchor);
            });

            body.append(header, meta, linkBox);
            card.append(thumbWrap, body);
            list.appendChild(card);
            entries.push({ group, input, card });
        });

        selectAll.onclick = () => {
            entries.forEach(entry => { entry.input.checked = true; });
            updateSummary();
        };
        selectNone.onclick = () => {
            entries.forEach(entry => { entry.input.checked = false; });
            updateSummary();
        };

        const status = document.createElement('div');
        status.style.cssText = 'margin-top:8px;color:#555;font-size:12px;white-space:pre-wrap;';

        const actions = document.createElement('div');
        actions.style.cssText = 'display:flex;gap:8px;justify-content:flex-end;margin-top:12px;';
        const repick = document.createElement('button');
        repick.textContent = '重选未同步目录';
        repick.style.cssText = 'background:#eee;border:1px solid #ccc;border-radius:4px;padding:8px 12px;cursor:pointer;';
        repick.onclick = () => pickVrUnsyncedDir(picked => {
            if (!picked || !vrLinkCache) return;
            // 重新按新的已下载清单过滤，已经下过的不再展示。
            const filtered = vrLinkCache
                .map(group => ({ ...group, links: group.links.filter(link => !isVrDownloaded(link)) }))
                .filter(group => group.links.length);
            vrLinkCache = filtered;
            if (filtered.length) {
                showVrLinkPicker(filtered);
            } else {
                overlay.remove();
                alert('按新的「未同步」清单过滤后，已没有剩余直链。');
            }
        });

        const clearCache = document.createElement('button');
        clearCache.textContent = '清空直链缓存';
        clearCache.title = '清空本地按番号缓存的直链；历史提取次数保留';
        clearCache.style.cssText = 'background:#eee;border:1px solid #ccc;border-radius:4px;padding:8px 12px;cursor:pointer;';
        clearCache.onclick = () => {
            const cachedCount = getVrCachedCodeCount();
            if (!cachedCount) {
                status.textContent = '本地还没有缓存任何番号的直链。';
                return;
            }
            if (!confirm(`确定清空本地缓存的 ${cachedCount} 个番号直链吗？清空后再次提取会重新进详情页。`)) return;
            vrLinkStore = { version: 1, byCode: {} };
            saveVrLinkStore();
            status.textContent = `已清空本地直链缓存（${cachedCount} 个番号）；历史提取次数保留。`;
        };

        const close = document.createElement('button');
        close.textContent = '关闭';
        close.style.cssText = 'background:#eee;border:1px solid #ccc;border-radius:4px;padding:8px 14px;cursor:pointer;';
        close.onclick = () => overlay.remove();

        const copy = document.createElement('button');
        copy.textContent = '复制';
        copy.style.cssText = 'background:#6f42c1;color:#fff;border:0;border-radius:4px;padding:8px 18px;' +
            'cursor:pointer;font-weight:bold;';
        copy.onclick = async () => {
            const selected = entries.filter(entry => entry.input.checked);
            if (!selected.length) {
                alert('请至少勾选一个番号。');
                return;
            }
            const text = selected.flatMap(entry => entry.group.links).join('\n');
            const linkCount = selected.reduce((sum, entry) => sum + entry.group.links.length, 0);
            const ok = await copyTextToClipboard(text);
            status.textContent = ok
                ? `已复制 ${selected.length} 个番号、${linkCount} 条直链到剪贴板。`
                : '复制失败：浏览器剪贴板权限受限，请手动选中链接复制。';
            if (ok) {
                copy.textContent = `已复制 ${linkCount} 条`;
                setTimeout(() => { copy.textContent = '复制'; }, 2000);
            }
        };

        const spacer = document.createElement('div');
        spacer.style.cssText = 'flex:1;';

        actions.append(repick, clearCache, spacer, close, copy);
        panel.append(heading, hint, toolbar, list, status, actions);
        overlay.appendChild(panel);
        overlay.addEventListener('click', event => {
            if (event.target === overlay) overlay.remove();
        });
        document.body.appendChild(overlay);
        updateSummary();
    }

    function ensureVrDirectLinkButton() {
        if (!isVrImageForumList() || document.getElementById('4k2-vr-directlink-btn')) return;
        const btn = document.createElement('button');
        btn.id = '4k2-vr-directlink-btn';
        btn.textContent = '[一键复制直链]';
        btn.style.cssText = 'position:fixed;top:214px;right:16px;background:#6f42c1;color:#fff;' +
            'padding:9px 12px;border:0;border-radius:6px;cursor:pointer;z-index:9999;font-weight:bold;';
        btn.onclick = () => onVrDirectLinkClick(btn);
        document.body.appendChild(btn);
        loadVrDownloadedSet();
    }

    function updateVrDirButtonLabel() {
        const btn = document.getElementById('4k2-vr-dir-btn');
        if (!btn) return;
        btn.textContent = vrDownloadedMeta && vrDownloadedMeta.count
            ? `[未同步 ${vrDownloadedMeta.count} 个文件]`
            : '[选未同步目录]';
    }

    function ensureVrDirButton() {
        if (!isVrImageForumList() || document.getElementById('4k2-vr-dir-btn')) return;
        const btn = document.createElement('span');
        btn.id = '4k2-vr-dir-btn';
        btn.textContent = '[选未同步目录]';
        btn.title = '选择 Y:\\光鸭云盘\\未同步 目录，用于排除已下载的直链';
        btn.style.cssText = 'position:fixed;top:252px;right:16px;background:#6c757d;color:#fff;' +
            'padding:6px 8px;border-radius:6px;cursor:pointer;z-index:9999;font-size:12px;';
        btn.onclick = () => pickVrUnsyncedDir(picked => {
            if (!picked) return;
            if (!vrLinkCache) return;
            const filtered = vrLinkCache
                .map(group => ({ ...group, links: group.links.filter(link => !isVrDownloaded(link)) }))
                .filter(group => group.links.length);
            vrLinkCache = filtered;
            showVrToast(`已更新「未同步」清单：${vrDownloadedMeta.count} 个文件，剩余 ${filtered.length} 个番号可提取。`);
        });
        document.body.appendChild(btn);
        updateVrDirButtonLabel();
    }

    // ================= 初始化 =================

    function init() {
        ensureGlobalToggleButton();
        ensureCopyMagnetsButton(); // 初始化复制按钮
        ensureSubtitleDownloadButton();
        ensureVrDirectLinkButton();
        ensureVrDirButton();
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
