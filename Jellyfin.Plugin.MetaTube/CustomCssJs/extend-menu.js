(function () {
    'use strict';

    // =========================================================
    // MetaTube 在 Emby Web 上的增强脚本（CustomCssJs 加载）
    //
    // 1) 菜单扩展：在动作菜单（列表页卡片 / 详情页的「…」）最上面插入自定义按钮；
    //    演员有 Twitter / Instagram 时，置顶插入「打开 Twitter / Instagram」。
    // 2) 概述链接：把概述正文里的 “Twitter: xxx” / “Instagram: xxx”
    //    动态转成可点链接（Emby 渲染概述时会清洗 <a>，所以这里是渲染完成后
    //    由 JS 直接改 DOM 插入的，不会被清洗掉）。
    //
    // 两个功能都是靠服务端写进人物概述的固定格式识别：
    //   Twitter: ozaki_erika330
    //   Instagram: natsu_kirinnn
    // =========================================================

    const METATUBE_URL = 'http://192.168.31.87:8081/?redirect=';

    // =========================
    // 社交账号规则
    //   注意：handle 只从接口返回的 Item.Overview 里取（和「打开 MetaTube」按钮
    //   一样只依赖 Item 数据）。不要在页面上全局找 x.com / instagram 链接来兜底：
    //   上一个演员页面残留的 DOM 会让菜单串数据（A 的 handle 出现在 B 的菜单里）。
    // =========================
    const SOCIAL_RULES = [
        {
            key: 'twitter',
            label: 'Twitter',
            icon: 'alternate_email',
            pattern: /^Twitter\s*[:：]\s*@?([A-Za-z0-9_]{1,20})$/,
            href: handle => `https://x.com/${handle}`
        },
        {
            key: 'instagram',
            label: 'Instagram',
            icon: 'photo_camera',
            pattern: /^Instagram\s*[:：]\s*@?([A-Za-z0-9_.]{1,30})$/,
            href: handle => `https://www.instagram.com/${handle}/`
        }
    ];

    // =========================
    // Emby 系统菜单识别
    //   右上角头像弹出的「应用设置 / 管理 Emby Server」菜单不属于任何媒体条目，
    //   绝不能往里插「复制番号」等自定义按钮。
    //   （症状：菜单里冒出一个“复制番号 (用户名)”，因为系统菜单没有条目预览标题，
    //   兜底取到的正是用户名。）
    //
    //   精确识别这一条系统菜单专属入口：
    //     <div class="listItemBodyText actionSheetItemText listItemBodyText-lf">管理 Emby Server</div>
    //   条目动作菜单不会出现它，命中即判定。
    // =========================
    const SYSTEM_MENU_ANCHOR_SELECTOR = '.listItemBodyText.actionSheetItemText';
    const SYSTEM_MENU_ANCHOR_RE = /^\s*管理\s*Emby\s*Server\s*$/i;

    // =========================
    // 「版本」媒体源选择菜单识别
    //   点击详情页/播放信息里的「版本」下拉（普通 / 4K / 60fps …）时，Emby 也会用
    //   同一个 actionsheet 容器渲染选项菜单；而详情页 URL 带 ?id=，getItemInfo()
    //   照样能拿到 Item，导致「打开 MetaTube / 打开演员」错误地出现在版本菜单里。
    //
    //   这种菜单的条目 data-id 就是 select option 的 value（如 mediasource_84431），
    //   且条目文本能和页面上 label 为「版本」的 select 的选项文本对上，命中即判定。
    // =========================
    const MEDIA_SOURCE_MENU_RE = /^mediasource_\d+$/i;

    function getLabeledSelectOptions(labelRe) {
        const result = [];
        document.querySelectorAll('select.emby-select').forEach(function (select) {
            const label = select.getAttribute('label') || '';
            const wrapperLabel = select.closest('.selectLabel')?.querySelector('.selectLabelText')?.textContent || '';
            if (!labelRe.test(label) && !labelRe.test(wrapperLabel)) return;
            select.querySelectorAll('option').forEach(function (opt) {
                const text = (opt.textContent || '').trim();
                if (text) result.push(text);
            });
        });
        return result;
    }

    function isMediaSourceMenu(container) {
        if (!container) return false;

        const items = container.querySelectorAll('[data-id]');
        if (!items.length) return false;

        let hasMediaSourceId = false;
        const itemTexts = [];
        items.forEach(function (el) {
            if (MEDIA_SOURCE_MENU_RE.test(el.getAttribute('data-id') || '')) {
                hasMediaSourceId = true;
            }
            const text = el.textContent || '';
            if (text.trim()) itemTexts.push(text.trim());
        });

        // 强信号：条目本身就是 mediasource_* 选项
        if (hasMediaSourceId) return true;

        // 兜底：条目文本与「版本」下拉的选项完全重合（不同皮肤/版本下 data-id 可能缺失）
        if (!itemTexts.length) return false;
        const versionOptions = getLabeledSelectOptions(/版本|edition|version/i);
        if (!versionOptions.length) return false;

        return itemTexts.every(function (text) {
            return versionOptions.indexOf(text) >= 0;
        });
    }

    // 标题预览节点（列表页卡片 / 详情页菜单都会有），它带的是条目信息
    function getPreviewText() {
        const el = document.querySelector('.actionsheetPreviewTextItem');
        return el?.textContent?.trim() || '';
    }

    function isSystemMenu(container) {
        if (!container) return false;

        const anchors = container.querySelectorAll(SYSTEM_MENU_ANCHOR_SELECTOR);
        for (const el of anchors) {
            if (SYSTEM_MENU_ANCHOR_RE.test(el.textContent || '')) return true;
        }
        return false;
    }

    // =========================
    // 获取ItemId (修复在列表页无法获取ID的问题)
    // =========================
    function getItemId() {
        const previewImage = document.querySelector('.actionsheetItemPreviewImage-bg');
        if (previewImage && previewImage.style.backgroundImage) {
            const imgMatch = previewImage.style.backgroundImage.match(/\/Items\/([a-zA-Z0-9\-]+)/i);
            if (imgMatch) {
                return imgMatch[1];
            }
        }
        const urlMatch = location.href.match(/[?&]id=([^&]+)/i);
        return urlMatch ? urlMatch[1] : null;
    }

    // =========================
    // 获取Item信息
    // =========================
    async function getItemInfo() {
        const itemId = getItemId();
        if (!itemId) return null;

        try {
            const userId = window.ApiClient._serverInfo.UserId;
            return await window.ApiClient.getItem(userId, itemId);
        } catch (e) {
            console.error('[JAV MENU] 获取 Item 失败:', e);
            return null;
        }
    }

    // =========================
    // 提取番号
    //   只认条目预览标题，并且要求标题里存在分隔结构（ABP-123 / ABP_123 / ABP 123）。
    //   绝不再用页面文本兜底：系统菜单取不到标题时会读到用户名，纯字母数字的用户名
    //   会被旧正则匹配成“番号”。分隔符必需可以挡住裸用户名（如 Administrator）。
    // =========================
    function extractCode(title) {
        if (!title) return '';
        title = `${title}`.toUpperCase();
        const match = title.match(/\b([A-Z]{2,10})[-_ ](\d{2,6})\b/);
        return match ? `${match[1]}-${match[2]}` : '';
    }

    // =========================
    // 获取标题（仅条目动作菜单的预览标题）
    // =========================
    function getTitle() {
        return getPreviewText();
    }

    // =========================
    // Toast (修复样式，调用 Emby 原生提示)
    // =========================
    function toast(text) {
        if (window.require) {
            window.require(['toast'], function (toast) {
                toast(text);
            });
        } else if (window.Dashboard && window.Dashboard.alert) {
            window.Dashboard.alert({ message: text });
        } else {
            alert(text);
        }
    }

    // =========================
    // 关闭菜单
    // =========================
    function closeMenu() {
        if (window.require) {
            window.require(['dialogHelper'], function (dialogHelper) {
                const dlg = document.querySelector('.actionSheet.opened');
                if (dlg && dialogHelper && typeof dialogHelper.close === 'function') {
                    dialogHelper.close(dlg);
                }
            });
        }

        const backdrop = document.querySelector('.dialogBackdrop, .dialog-backdrop');
        if (backdrop) {
            backdrop.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }));
            backdrop.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, cancelable: true }));
            backdrop.click();
        }
    }

    // =========================
    // 兼容复制
    // =========================
    async function copyText(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            console.log('[Clipboard API Failed]', e);
        }

        try {
            const textarea = document.createElement('textarea');
            textarea.value = text;
            textarea.style.position = 'fixed';
            textarea.style.opacity = '0';
            document.body.appendChild(textarea);
            textarea.focus();
            textarea.select();
            const ok = document.execCommand('copy');
            textarea.remove();
            return ok;
        } catch (e) {
            console.error(e);
            return false;
        }
    }

    // =========================
    // 社交账号解析
    // =========================
    // 去掉残留的 <br> / HTML 注释，便于按行精确匹配
    function normalizeLine(line) {
        return `${line}`
            .replace(/<br\s*\/?>/gi, '')
            .replace(/<!--[\s\S]*?-->/g, '')
            .trim();
    }

    // 文本 -> 命中的规则；不匹配返回 null（整行匹配，避免误伤正文）
    function matchSocialLine(line) {
        const text = normalizeLine(line);
        if (!text) return null;

        for (const rule of SOCIAL_RULES) {
            const match = text.match(rule.pattern);
            if (match) {
                return { rule: rule, handle: match[1], text: `${rule.label}: ${match[1]}`, href: rule.href(match[1]) };
            }
        }
        return null;
    }

    // 从概述文本里取出 handle：{ twitter: 'xxx', instagram: 'yyy' }
    function extractSocial(text) {
        const found = {};
        if (!text) return found;

        `${text}`.split(/\r?\n/).forEach(function (line) {
            const hit = matchSocialLine(line);
            if (hit && !found[hit.rule.key]) {
                found[hit.rule.key] = hit.handle;
            }
        });
        return found;
    }

    // =========================
    // 概述里的社交账号转成可点链接
    // =========================
    function linkifyTextNode(node) {
        const original = node.nodeValue;
        if (!original) return;

        const parts = original.split('\n');
        let changed = false;
        const fragment = document.createDocumentFragment();

        parts.forEach(function (line, index) {
            if (index > 0) fragment.appendChild(document.createTextNode('\n'));

            const hit = matchSocialLine(line);
            if (!hit) {
                fragment.appendChild(document.createTextNode(line));
                return;
            }

            // 保留原有缩进
            const indent = line.match(/^\s*/)[0];
            if (indent) fragment.appendChild(document.createTextNode(indent));

            const link = document.createElement('a');
            link.href = hit.href;
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            link.textContent = hit.text;
            link.title = `打开 ${hit.rule.label}`;
            link.style.color = 'inherit';
            link.style.textDecoration = 'underline';
            link.style.cursor = 'pointer';
            fragment.appendChild(link);
            changed = true;
        });

        // 没有任何变化时不要动 DOM，避免触发无意义的 MutationObserver 回调
        if (changed) node.parentNode.replaceChild(fragment, node);
    }

    function linkifySocialText(root) {
        const scope = root && root.nodeType === 1 ? root : document.body;
        if (!scope || !scope.ownerDocument) return;

        const walker = scope.ownerDocument.createTreeWalker(scope, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                const value = node.nodeValue;
                if (!value || value.indexOf(':') < 0) return NodeFilter.FILTER_REJECT;

                const parent = node.parentNode;
                if (!parent) return NodeFilter.FILTER_REJECT;

                // 已经处理过的 <a>、以及编辑框等直接跳过
                const name = parent.nodeName;
                if (name === 'A' || name === 'TEXTAREA' || name === 'INPUT'
                    || name === 'SCRIPT' || name === 'STYLE') {
                    return NodeFilter.FILTER_REJECT;
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        const nodes = [];
        let node;
        while ((node = walker.nextNode())) nodes.push(node);
        nodes.forEach(linkifyTextNode);
    }

    // 页面 DOM 变动很频繁，扫描做防抖；只处理文本节点，不会自我触发死循环
    let linkifyTimer = null;
    function scheduleLinkify() {
        if (linkifyTimer) return;
        linkifyTimer = setTimeout(function () {
            linkifyTimer = null;
            try {
                linkifySocialText(document.body);
            } catch (e) {
                console.error('[JAV MENU] 概述链接处理失败:', e);
            }
        }, 300);
    }

    // =========================
    // 创建按钮 (新增 hasBorder 参数控制是否带下划线)
    // =========================
    function createButton(icon, text, onclick, hasBorder = false) {
        const button = document.createElement('button');
        button.className = 'listItem listItem-autoactive itemAction listItemCursor listItem-hoverable actionSheetMenuItem actionSheetMenuItem-iconright';
        button.type = 'button';

        button.setAttribute('data-id', 'custom_jav_' + Date.now() + Math.floor(Math.random() * 1000));
        button.setAttribute('data-action', 'custom');
        // 自己注入的按钮标记：菜单容器会被 Emby 复用，换条目时要靠它把旧按钮清掉
        button.setAttribute(CUSTOM_BUTTON_ATTR, '1');

        // 根据 hasBorder 决定是否包含 listItem-border 类
        const borderClass = hasBorder ? 'listItem-border' : '';

        button.innerHTML = `
            <div class="listItem-content listItem-content-bg listItemContent-touchzoom ${borderClass}">
                <div class="actionSheetItemImageContainer actionSheetItemImageContainer-customsize actionSheetItemImageContainer-transparent listItemImageContainer listItemImageContainer-margin listItemImageContainer-square defaultCardBackground" style="aspect-ratio:1">
                    <i class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent md-icon listItemIcon md-icon autortl">${icon}</i>
                </div>
                <div class="actionsheetListItemBody actionsheetListItemBody-iconright listItemBody listItemBody-1-lines">
                    <div class="listItemBodyText actionSheetItemText listItemBodyText-nowrap listItemBodyText-lf">${text}</div>
                </div>
            </div>
        `;

        const blockEventAndRun = async (e) => {
            e.preventDefault();
            e.stopPropagation();
            e.stopImmediatePropagation();

            if (button.dataset.clicked) return;
            button.dataset.clicked = 'true';
            setTimeout(() => { button.dataset.clicked = ''; }, 300);

            await onclick(e);
        };

        button.addEventListener('click', blockEventAndRun);
        button.addEventListener('pointerup', (e) => {
            e.stopPropagation();
        });

        return button;
    }

    // =========================
    // 菜单上下文（解决菜单容器复用导致的串数据）
    // =========================
    // 自己注入的按钮都带这个属性，换条目时按它清理
    const CUSTOM_BUTTON_ATTR = 'data-jav-custom';

    function removeInjectedButtons(container) {
        if (!container) return;
        container.querySelectorAll(`[${CUSTOM_BUTTON_ATTR}]`).forEach(function (node) {
            node.remove();
        });
    }

    // 当前菜单对应的条目标识（不需要额外请求）：
    //  - 列表页：动作菜单预览图的 /Items/<id>/
    //  - 详情页：URL 里的 ?id=
    //  - 再叠加菜单标题，避免 id 取不到时判断不出来
    function menuContextKey() {
        return `${getItemId() || ''}|${getTitle() || ''}`;
    }

    // =========================
    // 注入菜单
    // =========================
    async function injectMenu() {
        const container = document.querySelector('.actionsheetScrollSlider');
        if (!container) return;

        // 系统菜单（含「管理 Emby Server」入口）一律不注入，并把可能残留的自定义按钮清掉
        if (isSystemMenu(container) || isMediaSourceMenu(container)) {
            document.querySelectorAll('.actionsheetScrollSlider').forEach(removeInjectedButtons);
            return;
        }

        const contextKey = menuContextKey();

        // 同一个条目、按钮还在 => 不重复注入；
        // 换成别的条目（Emby 复用同一个菜单容器）=> 走下面的清理 + 重新注入。
        if (container.dataset.javInjectedFor === contextKey
            && container.querySelector(`[${CUSTOM_BUTTON_ATTR}]`)) {
            return;
        }

        const item = await getItemInfo();

        // 等接口返回的这段时间用户可能又切了条目，此时结果已经过期
        if (menuContextKey() !== contextKey) return;

        const currentContainer = document.querySelector('.actionsheetScrollSlider');
        if (!currentContainer) return;

        // 请求期间用户可能切换到系统菜单 / 关闭菜单，这里再确认一次
        if (isSystemMenu(currentContainer) || isMediaSourceMenu(currentContainer)) {
            document.querySelectorAll('.actionsheetScrollSlider').forEach(removeInjectedButtons);
            return;
        }

        const title = getTitle();

        // 详情页（含演员页）的「…」菜单没有列表页那种标题预览，
        // 所以标题为空不再直接放弃，能拿到 Item 就继续。
        if (!title && !item) {
            removeInjectedButtons(currentContainer);
            currentContainer.dataset.javInjectedFor = '';
            return;
        }

        // 关键：先把上一次注入的按钮清掉，否则会出现
        // “A 演员的 Twitter/Instagram 留在 B 演员菜单里”的串数据。
        removeInjectedButtons(currentContainer);
        // 其它（已关闭/被复用的）菜单里残留的自定义按钮也一并清掉，避免泄漏到下次打开。
        document.querySelectorAll('.actionsheetScrollSlider').forEach(function (el) {
            if (el !== currentContainer) removeInjectedButtons(el);
        });
        currentContainer.dataset.javInjectedFor = contextKey;

        const code = extractCode(title);
        console.log('[JAV MENU ITEM]', item);

        const providers = item?.ProviderIds || {};
        const providerEntries = Object.entries(providers);

        const people = item?.People || [];
        const actor = people.find(p => p.Type === 'Actor');

        // 演员的 Twitter / Instagram：只认这个条目自己的概述（和 ProviderIds 一样，
        // 都是接口返回的 Item 数据），绝不去读页面上可能残留的上一个演员的内容。
        const social = extractSocial(item?.Overview);

        // 收集所有需要展示的按钮数据，以便找出哪一个是“最后一个自定义按钮”
        const buttonsToCreate = [];

        // 社交账号按钮放最上面（演员详情页最常用）。
        for (const rule of SOCIAL_RULES) {
            const handle = social[rule.key];
            if (!handle) continue;

            buttonsToCreate.push({
                type: rule.key,
                icon: rule.icon,
                text: `打开 ${rule.label} (@${handle})`,
                handler: () => {
                    window.open(rule.href(handle), '_blank');
                    closeMenu();
                }
            });
        }

        if (code) {
            buttonsToCreate.push({
                type: 'copy',
                icon: 'content_copy',
                text: `复制番号 (${code})`,
                handler: async () => {
                    const ok = await copyText(code);
                    closeMenu();
                    toast(ok ? `已复制番号: ${code}` : '复制失败');
                }
            });
        }

        if (providerEntries.length > 0 && providerEntries[0][1]) {
            const firstProviderVal = `${providerEntries[0][1]}`;
            buttonsToCreate.push({
                type: 'metatube',
                icon: 'open_in_new',
                text: '打开 MetaTube',
                handler: () => {
                    window.open(`${METATUBE_URL}${firstProviderVal}`, '_blank');
                    closeMenu();
                }
            });
        }

        if (actor && actor.Id) {
            buttonsToCreate.push({
                type: 'actor',
                icon: 'person',
                text: `打开演员: ${actor.Name || '第一个演员'}`,
                handler: () => {
                    const serverId = window.ApiClient?.serverId ? window.ApiClient.serverId() : (window.ApiClient?._serverInfo?.Id || '');
                    window.open(`/web/index.html#!/item?id=${actor.Id}&serverId=${serverId}`, '_blank');
                    closeMenu();
                }
            });
        }

        // =========================
        // 按需拉取评论
        //   后端接口（插件里的 ReviewService）只做「追加」：
        //   读取现有 Overview → 抓新增评论 → 翻译 → 追加写回。
        //   不会重抓简介，也不会重写标题。已存在的评论靠时间标记判重，
        //   所以重复点击不会重复翻译、不会重复花钱。
        //   翻译可能耗时几十秒，接口是异步的，这里轮询 /Status 拿进度。
        // =========================
        const pid = item?.ProviderIds?.MetaTube || '';
        const provider = `${pid}`.split(':')[0];
        const REVIEW_PROVIDERS = ['FANZA', 'MGS'];

        if (item?.Id && REVIEW_PROVIDERS.indexOf(provider) >= 0) {
            buttonsToCreate.push({
                type: 'reviews',
                icon: 'comment',
                text: '📝 拉取评论',
                handler: async () => {
                    closeMenu();
                    await pullReviews(item.Id);
                }
            });
        }

        // 倒序插入以保持置顶和正常的排列顺序
        for (let i = buttonsToCreate.length - 1; i >= 0; i--) {
            const btnData = buttonsToCreate[i];
            // 只有处于数组最后的按钮（渲染出来在最下面）才渲染底边横线
            const isLastCustomButton = (i === buttonsToCreate.length - 1);

            const btnNode = createButton(
                btnData.icon,
                btnData.text,
                btnData.handler,
                isLastCustomButton
            );

            currentContainer.insertBefore(btnNode, currentContainer.firstChild);
        }

        console.log('[JAV MENU] 完美分隔线注入完成');
    }

    // =========================
    // 按需拉取评论：调用插件后端接口 + 轮询进度
    //
    // 为什么是轮询而不是等一个请求返回：
    //   翻译一个番号的评论可能耗时几十秒到几分钟（分片并发请求大模型），
    //   同步等待会挂住 HTTP 请求直到超时。
    //   所以 Pull 接口只负责「启动」并立即返回，进度靠 Status 接口查。
    //
    // 进度是「已完成 N/M 片」的离散值，不是时间比例：
    //   分片是并发翻译的，可能长时间停在 0，然后一次性跳到 M。
    //   文案上用「已完成」而不是「进度%」，避免误导。
    // =========================
    const REVIEW_POLL_INTERVAL_MS = 2000;
    const REVIEW_POLL_TIMEOUT_MS = 15 * 60 * 1000; // 15 分钟仍无结果就放弃轮询

    function getServerId() {
        return window.ApiClient?.serverId ? window.ApiClient.serverId() : (window.ApiClient?._serverInfo?.Id || '');
    }

    // =========================
    // 插件接口的 URL 拼接
    //
    // Emby 会把插件路由挂在全局前缀下（通常是 /emby），也就是
    //   [Route("/MetaTube/Reviews/Pull","POST")]  →  POST /emby/MetaTube/Reviews/Pull
    // ApiClient.getUrl() 会补上服务端基路径，实测可用，因此优先用它。
    //
    // ⚠️ 只在「网络层失败或 404」时才尝试下一个候选：
    //    POST 不是幂等操作，如果因为 500 就换 URL 重发，会把同一次拉取
    //    发出两次（日志里能看到两条相隔几毫秒的相同请求）。
    //    业务错误（500/400）说明请求已经到达服务端，必须直接上报，不能重试。
    // =========================
    function buildUrls(path) {
        const list = [];
        try {
            list.push(window.ApiClient.getUrl(path));
        } catch (e) {
            console.warn('[JAV MENU] ApiClient.getUrl 失败:', e);
        }
        // 兜底：显式带上 /emby 前缀（多数 Emby 部署的实际情况）
        if (path.indexOf('/emby/') !== 0) {
            list.push(`/emby${path}`);
        }
        return list.filter((v, i, a) => v && a.indexOf(v) === i);
    }

    // 依次尝试候选 URL；只有「连不上 / 404」才换下一个
    async function apiRequest(path, init) {
        const urls = buildUrls(path);
        let lastErr;

        for (let i = 0; i < urls.length; i++) {
            const url = urls[i];
            const isLast = i === urls.length - 1;
            const withToken = url + (url.indexOf('?') >= 0 ? '&' : '?')
                + 'X-Emby-Token=' + encodeURIComponent(window.ApiClient.accessToken());

            let res;
            try {
                res = await fetch(withToken, init);
            } catch (e) {
                // 网络层失败（DNS/连接被拒）→ 可以安全换下一个候选
                lastErr = e;
                if (!isLast) continue;
                throw e;
            }

            // 404 → 前缀猜错，换下一个候选（请求没打到任何处理逻辑，重发安全）
            if (res.status === 404 && !isLast) {
                lastErr = new Error(`HTTP 404 (${url})`);
                continue;
            }

            // 其余状态码：请求已经到达服务端，直接解析/上报，绝不重发
            let body = null;
            try {
                body = await res.json();
            } catch (e) {
                body = null;
            }

            if (!res.ok) {
                // Emby 的错误响应形如 { "error": "..." } 或纯文本
                const detail = body?.error || body?.Error || body?.message || `HTTP ${res.status}`;
                throw new Error(detail);
            }

            return body;
        }

        throw lastErr || new Error('请求失败');
    }

    function apiGet(path) {
        return apiRequest(path, { headers: { 'Accept': 'application/json' } });
    }

    function apiPost(path) {
        return apiRequest(path, { method: 'POST', headers: { 'Accept': 'application/json' } });
    }

    function describeStatus(st) {
        if (!st) return '准备中…';
        switch (st.Stage) {
            case 'fetching':
                return '正在获取评论…';
            case 'translating':
                if (st.ChunkCount > 0) {
                    return `正在翻译 已完成 ${st.FinishedChunks}/${st.ChunkCount} 片…`;
                }
                return `正在翻译…（新增 ${st.NewReviewCount} 条）`;
            case 'saving':
                return '正在写入…';
            case 'done':
                if (st.Appended) return `✅ 已追加 ${st.NewReviewCount} 条评论`;
                return `⏭️ ${st.Message || '没有新评论'}`;
            case 'failed':
                return `❌ 失败：${st.Message || '未知错误'}`;
            default:
                return st.Message || '处理中…';
        }
    }

    // =========================
    // 常驻进度指示器
    //
    // 翻译一个番号可能要 1~2 分钟，toast 几秒就消失，期间页面上没有任何反馈，
    // 用户会以为没反应。所以这里在右下角挂一个常驻浮层，实时刷新进度，
    // 结束（成功/失败）后再移除。
    // =========================
    const REVIEW_INDICATOR_ID = 'jav-review-indicator';

    function showIndicator(text) {
        let el = document.getElementById(REVIEW_INDICATOR_ID);
        if (!el) {
            el = document.createElement('div');
            el.id = REVIEW_INDICATOR_ID;
            el.style.cssText = [
                'position:fixed',
                'right:20px',
                'bottom:20px',
                'z-index:999999',
                'max-width:360px',
                'padding:12px 16px',
                'border-radius:8px',
                'background:rgba(0,0,0,0.85)',
                'color:#fff',
                'font-size:14px',
                'line-height:1.5',
                'box-shadow:0 4px 16px rgba(0,0,0,0.4)',
                'pointer-events:none',
                'white-space:pre-wrap'
            ].join(';');
            document.body.appendChild(el);
        }
        el.textContent = text;
    }

    function hideIndicator() {
        const el = document.getElementById(REVIEW_INDICATOR_ID);
        if (el) el.remove();
    }

    async function pullReviews(itemId) {
        // 防止重复触发（菜单按钮已做防抖，这里再兜一层）
        if (pullReviews._running) {
            toast('⏳ 正在处理中，请稍候…');
            return;
        }
        pullReviews._running = true;

        try {
            showIndicator('📝 正在开始…');

            const started = await apiPost(`/MetaTube/Reviews/Pull?ItemId=${itemId}`);
            if (started?.Status === 'invalid') {
                hideIndicator();
                toast(`❌ ${started.Message || '无法开始'}`);
                return;
            }
            if (started?.Status === 'running') {
                toast('⏳ 该条目正在处理中…');
            }

            showIndicator('📝 正在拉取评论…');

            const begin = Date.now();
            while (true) {
                await new Promise(r => setTimeout(r, REVIEW_POLL_INTERVAL_MS));

                if (Date.now() - begin > REVIEW_POLL_TIMEOUT_MS) {
                    hideIndicator();
                    toast('⏱️ 等待超时，请稍后刷新页面查看结果');
                    return;
                }

                let st;
                try {
                    st = await apiGet(`/MetaTube/Reviews/Status?ItemId=${itemId}`);
                } catch (e) {
                    console.warn('[JAV MENU] 查询评论进度失败:', e);
                    continue; // 偶发失败不中断轮询
                }

                // 实时刷新常驻浮层
                showIndicator(describeStatus(st));

                if (st?.Status === 'done' || st?.Status === 'failed') {
                    hideIndicator();
                    toast(describeStatus(st));
                    // 追加成功就刷新详情页，让新评论显示出来
                    if (st.Appended) {
                        setTimeout(() => location.reload(), 1500);
                    }
                    return;
                }
            }
        } catch (e) {
            hideIndicator();
            console.error('[JAV MENU] 拉取评论失败:', e);
            toast(`❌ 拉取评论失败: ${e.message || e}`);
        } finally {
            pullReviews._running = false;
        }
    }

    // =========================
    // 监听
    // =========================
    const observer = new MutationObserver(() => {
        injectMenu();
        scheduleLinkify();
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true
    });

    // 首屏（脚本加载时 DOM 可能已经渲染完）
    scheduleLinkify();

})();
