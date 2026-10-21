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

    // =========================
    // 条目动作菜单正向识别（白名单）
    //   Emby 所有弹层菜单（条目动作菜单 / 字幕 / 音轨 / 版本 / 系统用户菜单…）
    //   都由同一个 actionsheet 组件渲染。源码里只有调用方传了 options.item
    //   （条目动作菜单，itemcontextmenu.js）才会渲染条目预览区
    //   .actionsheetItemPreview；字幕/音轨/版本/系统菜单都不传，没有该节点。
    //   之前用黑名单逐个识别，每遇到一种新菜单（如字幕菜单）就漏一次；
    //   改为白名单：没有预览区的一律不注入。
    // =========================
    function isItemActionMenu(container) {
        if (!container) return false;
        const dlg = container.closest('.actionSheet');
        return !!(dlg && dlg.querySelector('.actionsheetItemPreview'));
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
    // 简易模态框（输入 / 确认）
    //
    // Emby 4.9 没有现成的 prompt 组件，这里只要「输入一个名字」和
    // 「确认一下」两种形态，所以用纯 DOM 自建，不去依赖会变的内置模块。
    // =========================
    const MODAL_ID = 'jav-alias-modal';

    function showModal(options) {
        return new Promise((resolve) => {
            const opt = options || {};
            const title = opt.title || '';
            const message = opt.message || '';
            const withInput = !!opt.withInput;
            const okText = opt.okText || '确定';
            const cancelText = opt.cancelText || '取消';

            const old = document.getElementById(MODAL_ID);
            if (old) old.remove();

            const overlay = document.createElement('div');
            overlay.id = MODAL_ID;
            overlay.style.cssText = [
                'position:fixed', 'inset:0', 'z-index:2147483000',
                'background:rgba(0,0,0,0.6)',
                'display:flex', 'align-items:center', 'justify-content:center'
            ].join(';');

            const box = document.createElement('div');
            box.style.cssText = [
                'min-width:340px', 'max-width:520px', 'padding:20px 22px',
                'border-radius:10px', 'background:#202020', 'color:#fff',
                'box-shadow:0 8px 32px rgba(0,0,0,0.6)',
                'font-size:15px', 'line-height:1.6'
            ].join(';');

            const titleEl = document.createElement('div');
            titleEl.textContent = title;
            titleEl.style.cssText = 'font-size:17px;font-weight:600;margin-bottom:10px';
            box.appendChild(titleEl);

            if (message) {
                const msgEl = document.createElement('div');
                msgEl.textContent = message;
                msgEl.style.cssText = 'opacity:.85;white-space:pre-wrap;margin-bottom:14px';
                box.appendChild(msgEl);
            }

            let inputEl = null;
            if (withInput) {
                inputEl = document.createElement('input');
                inputEl.type = 'text';
                inputEl.value = opt.value || '';
                inputEl.placeholder = opt.placeholder || '';
                inputEl.style.cssText = [
                    'width:100%', 'box-sizing:border-box', 'padding:10px 12px',
                    'border-radius:6px', 'border:1px solid rgba(255,255,255,0.25)',
                    'background:rgba(255,255,255,0.06)', 'color:#fff', 'font-size:15px',
                    'margin-bottom:16px', 'outline:none'
                ].join(';');
                box.appendChild(inputEl);
            }

            const row = document.createElement('div');
            row.style.cssText = 'display:flex;gap:10px;justify-content:flex-end';

            const mkBtn = function (text, primary) {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.textContent = text;
                btn.style.cssText = [
                    'padding:8px 16px', 'border-radius:6px', 'cursor:pointer',
                    'font-size:15px', 'border:0',
                    primary
                        ? 'background:#52b54b;color:#fff'
                        : 'background:rgba(255,255,255,0.14);color:#fff'
                ].join(';');
                return btn;
            };

            const cancelBtn = mkBtn(cancelText, false);
            const okBtn = mkBtn(okText, true);
            row.appendChild(cancelBtn);
            row.appendChild(okBtn);
            box.appendChild(row);
            overlay.appendChild(box);

            const finish = function (ok) {
                overlay.remove();
                document.removeEventListener('keydown', onKey, true);
                resolve({ ok: ok, value: inputEl ? inputEl.value.trim() : '' });
            };

            function onKey(e) {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    e.stopPropagation();
                    finish(false);
                } else if (e.key === 'Enter' && (!inputEl || document.activeElement === inputEl)) {
                    e.preventDefault();
                    e.stopPropagation();
                    finish(true);
                }
            }

            cancelBtn.addEventListener('click', function () { finish(false); });
            okBtn.addEventListener('click', function () { finish(true); });
            overlay.addEventListener('click', function (e) {
                if (e.target === overlay) finish(false);
            });
            document.addEventListener('keydown', onKey, true);

            document.body.appendChild(overlay);
            if (inputEl) {
                inputEl.focus();
                inputEl.select();
            } else {
                okBtn.focus();
            }
        });
    }

    async function promptText(title, message, value, placeholder, okText) {
        const r = await showModal({
            title: title,
            message: message,
            withInput: true,
            value: value,
            placeholder: placeholder,
            okText: okText
        });
        return r.ok ? r.value : null;
    }

    async function confirmDialog(title, message, okText) {
        const r = await showModal({ title: title, message: message, okText: okText });
        return r.ok;
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
        // 管理员状态也进 key：脚本刚加载时权限还没问回来，第一遍注入会少一个
        // 「关联为常用名」按钮；权限回来后 key 变了，才会重新注入补上。
        return `${getItemId() || ''}|${getTitle() || ''}|${isAdminReady() ? 'a' : '-'}`;
    }

    // =========================
    // 注入菜单
    // =========================
    async function injectMenu() {
        const container = document.querySelector('.actionsheetScrollSlider');
        if (!container) return;

        // 白名单：只有条目动作菜单（带条目预览区）才注入，
        // 其余（字幕/音轨/版本/系统菜单等）一律清理残留后跳过
        if (!isItemActionMenu(container)
            || isSystemMenu(container) || isMediaSourceMenu(container)) {
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
        if (!isItemActionMenu(currentContainer)
            || isSystemMenu(currentContainer) || isMediaSourceMenu(currentContainer)) {
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

        // =========================
        // 演员详情页：把这个名字挂到某个「常用名」下
        //   场景：老番号刮出来的演员名还是旧艺名，想一次性关联到常用名，
        //   以后刮到旧名自动换成常用名。名字直接取当前演员页的 Item.Name，
        //   用户只需要粘贴/输入常用名。
        //   改的是服务器配置，只对管理员显示（接口本身也要求管理员）。
        //   注意这里是同步判断，见 primeIsAdmin 的注释。
        // =========================
        if (item?.Type === 'Person' && item?.Name && isAdminReady()) {
            buttonsToCreate.push({
                type: 'alias',
                icon: 'person_add',
                text: `🔗 关联为常用名（当前：${item.Name}）`,
                handler: async () => {
                    closeMenu();
                    await linkActorAlias(item.Name);
                }
            });
        }

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

        // =========================
        // 拆分版本 / 取消手动拆分标记
        //   合并状态（MediaSources 里还有别的版本）→ 给「拆分」入口；
        //   已打过手动拆分标记 → 给「取消标记」入口（取消后允许自动合并）。
        // =========================
        if (item?.Id) {
            const splitMarked = hasSplitMarker(item.Overview);

            if (isMergedItem(item)) {
                buttonsToCreate.push({
                    type: 'split',
                    icon: 'call_split',
                    text: '🔓 拆分版本（手动）',
                    handler: async () => {
                        closeMenu();
                        await splitVersions(item);
                    }
                });
            }

            if (splitMarked) {
                buttonsToCreate.push({
                    type: 'merge',
                    icon: 'merge_type',
                    text: '🔗 取消手动拆分标记',
                    handler: async () => {
                        closeMenu();
                        await unmarkSplitVersions(item);
                    }
                });
            }
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
    // 手动拆分版本（拆分 + 打标记）
    //
    // 为什么需要标记：
    //   合并任务（MergeMultiVersionsTask）按 MetaTube 番号把多个版本合成一个条目，
    //   而且每次扫库后还会自动再合并一次。用户手动拆开的版本如果不留记号，
    //   下一次扫描就被自动合回去了。
    //
    // 标记写在条目概述末尾（与 <metatube-reviews/> 同一套约定）：
    //   <metatube-split/>
    //   HTML 渲染时不可见，插件后端（ManualSplitMarker）读写它来做过滤。
    //
    // 拆分动作本身仍然调 Emby 自己的接口：
    //   POST /emby/Videos/{主条目Id}/AlternateSources/Delete
    //   版本分组、媒体源归属这些都是 Emby 内部实现，插件只负责打标记。
    //
    // 顺序不能反：必须先从 MediaSources 拿到全组条目 Id，再拆分（拆完就只剩自己一个源了），
    // 然后带着这些 Id 去后端打标记。
    // =========================
    const SPLIT_MARKER = '<metatube-split/>';

    function hasSplitMarker(overview) {
        return `${overview || ''}`.indexOf(SPLIT_MARKER) >= 0;
    }

    // 组内所有版本的条目 Id：合并状态下 item.MediaSources 会列出全部版本，
    // 每个源的 ItemId 就是那个版本自己的条目 Id。
    function getSplitTargetIds(item) {
        const ids = [];
        for (const source of (item?.MediaSources || [])) {
            const id = `${source?.ItemId || ''}`.trim();
            if (id && ids.indexOf(id) < 0) ids.push(id);
        }
        const ownId = `${item?.Id || ''}`.trim();
        if (ownId && ids.indexOf(ownId) < 0) ids.push(ownId);
        return ids;
    }

    // 拆分请求要发给主条目（Type=Default 那个源所属的条目）
    function getPrimaryVersionId(item, ids) {
        const def = (item?.MediaSources || []).find(s => `${s?.Type || ''}` === 'Default' && s?.ItemId);
        const id = `${def?.ItemId || ''}`.trim();
        return id || ids[0] || `${item?.Id || ''}`.trim();
    }

    function isMergedItem(item) {
        const sources = item?.MediaSources || [];
        return sources.length > 1 || sources.some(s => `${s?.Type || ''}` === 'Grouping');
    }

    async function ungroupVersions(itemId) {
        const client = window.ApiClient;
        if (!client) throw new Error('ApiClient 不可用');

        // 首选官方 API 封装（与详情页自带的「拆分版本」等价）
        if (typeof client.ungroupVersions === 'function') {
            return client.ungroupVersions(itemId);
        }

        // 兜底：直接打接口（ApiClient 没暴露该方法时）
        const url = client.getUrl(`Videos/${itemId}/AlternateSources/Delete`);
        const token = encodeURIComponent(client.accessToken());
        const res = await fetch(`${url}${url.indexOf('?') >= 0 ? '&' : '?'}X-Emby-Token=${token}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: '{}'
        });
        if (!res.ok) throw new Error(`拆分接口返回 HTTP ${res.status}`);
        return null;
    }

    function markVersions(ids, marked) {
        const list = ids.map(v => encodeURIComponent(v)).join(',');
        return apiPost(`/MetaTube/Versions/SplitMark?Marked=${marked ? 'true' : 'false'}&ItemIds=${list}`);
    }

    async function splitVersions(item) {
        if (splitVersions._running) {
            toast('⏳ 正在处理中，请稍候…');
            return;
        }
        splitVersions._running = true;

        try {
            const ids = getSplitTargetIds(item);
            if (ids.length < 2) {
                toast('⚠️ 这个条目只有 1 个版本，不需要拆分');
                return;
            }

            toast('🔓 正在拆分版本…');
            await ungroupVersions(getPrimaryVersionId(item, ids));

            // 拆分成功后再打标记：标记失败也只会让自动合并把版本合回去（可重试），
            // 不会留下「标记了但没拆开」的状态。
            const result = await markVersions(ids, true);
            if (result?.Status === 'invalid') {
                toast(`⚠️ 已拆分，但标记失败：${result.Message || '未知原因'}`);
            } else {
                toast(`✅ ${result?.Message || `已拆分并标记 ${ids.length} 个版本`}`);
            }

            setTimeout(() => location.reload(), 1500);
        } catch (e) {
            console.error('[JAV MENU] 拆分版本失败:', e);
            toast(`❌ 拆分失败: ${e.message || e}`);
        } finally {
            splitVersions._running = false;
        }
    }

    async function unmarkSplitVersions(item) {
        try {
            const result = await markVersions(getSplitTargetIds(item), false);
            if (result?.Status === 'invalid') {
                toast(`❌ ${result.Message || '取消标记失败'}`);
                return;
            }
            toast(`✅ ${result?.Message || '已取消标记'}`);
            setTimeout(() => location.reload(), 1200);
        } catch (e) {
            console.error('[JAV MENU] 取消拆分标记失败:', e);
            toast(`❌ 取消标记失败: ${e.message || e}`);
        }
    }

    // =========================
    // 当前用户是不是管理员
    //
    // ⚠️ 这里必须是「同步读缓存」，不能 await：injectMenu 里一旦多出一个 await，
    //    就会出现「旧按钮已清掉、新按钮还没插上」的窗口，MutationObserver 在这个
    //    窗口里又会触发一次 injectMenu，每次都会发一次 getItemInfo()，滚成
    //    疯狂请求 /Items 的死循环（菜单点不动就是这么来的）。
    //     所以：脚本加载时先问一次，结果缓存下来，注入时同步判断。
    // =========================
    let isAdminCache = null;

    function primeIsAdmin() {
        if (primeIsAdmin._started) return;
        primeIsAdmin._started = true;

        try {
            const promise = window.ApiClient.getCurrentUser();
            if (!promise || typeof promise.then !== 'function') return;

            promise.then(function (user) {
                isAdminCache = user?.Policy?.IsAdministrator !== false;
                // 权限结果回来得比第一次注入晚时，菜单可能已经渲染完了，补一次
                if (isAdminCache) scheduleInjectMenu();
            }).catch(function (e) {
                // 拿不到就按「能用」处理：接口本身还会再校验一次权限
                console.warn('[JAV MENU] 读取当前用户失败:', e);
                isAdminCache = true;
            });
        } catch (e) {
            console.warn('[JAV MENU] 读取当前用户异常:', e);
            isAdminCache = true;
        }
    }

    /// 还没问到结果时先不显示（避免非管理员看到点了会报错的按钮）
    function isAdminReady() {
        return isAdminCache === true;
    }

    // =========================
    // 演员页：把当前名字挂到某个「常用名」下
    //
    // 用户的操作只有一个：粘贴/输入常用名。
    // 各种边界都在后端判断（已经关联过 / 现在挂在别人下面 / 这个名字本身
    // 就是一条常用名），这里只负责把后端的话原样讲给用户，需要拍板的
    // 情况再弹一次确认，确认后带标记重发。
    // =========================
    async function linkActorAlias(alias) {
        // 用户通常是先把常用名复制好再点按钮的，能读剪贴板就直接填上
        let preset = '';
        try {
            const clip = await navigator.clipboard.readText();
            const trimmed = `${clip || ''}`.trim();
            if (trimmed && trimmed.length <= 60) preset = trimmed;
        } catch (e) {
            // 没权限就算了，用户自己输入
        }

        const common = await promptText(
            '关联为常用名',
            `把「${alias}」挂到哪个常用名下？\n（填那位演员现在用的正式名字，以后刮到这个旧名会自动换成它）`,
            preset,
            '常用名',
            '关联'
        );

        if (common == null) return;
        if (!common) {
            toast('❌ 常用名不能为空');
            return;
        }

        await submitAliasLink(alias, common, false, false);
    }

    async function submitAliasLink(alias, common, overwrite, merge) {
        const qs = new URLSearchParams({
            Alias: alias,
            CommonName: common,
            Overwrite: overwrite ? 'true' : 'false',
            MergeGroup: merge ? 'true' : 'false'
        });

        showIndicator('🔗 正在关联…');
        try {
            const res = await apiPost(`/MetaTube/ActorAlias/Link?${qs.toString()}`);
            hideIndicator();

            switch (res?.Status) {
                case 'ok':
                    toast(`✅ ${res.Message}`);
                    break;
                case 'exists':
                    toast(`ℹ️ ${res.Message}`);
                    break;
                case 'confirm': {
                    const ok = await confirmDialog('需要确认', res.Message, '就这么改');
                    if (!ok) return;
                    await submitAliasLink(alias, common, res.NeedOverwrite || overwrite, res.NeedMerge || merge);
                    break;
                }
                default:
                    toast(`❌ ${res?.Message || '关联失败'}`);
            }
        } catch (e) {
            hideIndicator();
            console.error('[JAV MENU] 关联演员别名失败:', e);
            toast(`❌ 关联失败: ${e.message || e}`);
        }
    }

    // =========================
    // 监听
    //
    // ⚠️ 必须防抖 + 防重入：injectMenu 自己要 remove 旧按钮、插新按钮，
    //    这些 DOM 变动又会触发 MutationObserver；而 injectMenu 中间还有
    //    await（取 Item 信息）。没有锁的话同一次菜单打开会被并发跑很多遍，
    //    每一遍都发一次 /Items 请求，表现为「疯狂循环请求 Items、菜单点不动」。
    // =========================
    let injectBusy = false;
    let injectTimer = null;

    function scheduleInjectMenu(delay) {
        if (injectTimer) return;

        injectTimer = setTimeout(async function () {
            injectTimer = null;

            if (injectBusy) {
                // 上一轮还没跑完，等它结束再说
                scheduleInjectMenu(120);
                return;
            }

            injectBusy = true;
            try {
                await injectMenu();
            } catch (e) {
                console.error('[JAV MENU] 注入菜单失败:', e);
            } finally {
                injectBusy = false;
            }
        }, delay || 60);
    }

    const observer = new MutationObserver(() => {
        scheduleInjectMenu();
        scheduleLinkify();
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true
    });

    // 首屏（脚本加载时 DOM 可能已经渲染完）
    primeIsAdmin();
    scheduleLinkify();
    scheduleInjectMenu(0);

})();
