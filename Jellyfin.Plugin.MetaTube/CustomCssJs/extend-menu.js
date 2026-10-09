(function () {
    'use strict';

    const METATUBE_URL = 'http://192.168.31.87:8081/?redirect=';

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
    // =========================
    function extractCode(title) {
        if (!title) return '';
        title = title.toUpperCase();
        const match = title.match(/\b([A-Z]{2,10})[-_ ]?(\d{2,6})\b/);
        return match ? `${match[1]}-${match[2]}` : '';
    }

    // =========================
    // 获取标题
    // =========================
    function getTitle() {
        const el = document.querySelector('.actionsheetPreviewTextItem');
        return el?.textContent?.trim() || '';
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
    // 创建按钮 (新增 hasBorder 参数控制是否带下划线)
    // =========================
    function createButton(icon, text, onclick, hasBorder = false) {
        const button = document.createElement('button');
        button.className = 'listItem listItem-autoactive itemAction listItemCursor listItem-hoverable actionSheetMenuItem actionSheetMenuItem-iconright';
        button.type = 'button';

        button.setAttribute('data-id', 'custom_jav_' + Date.now() + Math.floor(Math.random() * 1000));
        button.setAttribute('data-action', 'custom');

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
    // 注入菜单
    // =========================
    async function injectMenu() {
        const container = document.querySelector('.actionsheetScrollSlider');
        if (!container) return;

        if (container.dataset.javInjected) return;
        container.dataset.javInjected = 'true';

        const title = getTitle();
        if (!title) {
            container.dataset.javInjected = '';
            return;
        }

        const code = extractCode(title);
        const item = await getItemInfo();
        console.log('[JAV MENU ITEM]', item);

        const currentContainer = document.querySelector('.actionsheetScrollSlider');
        if (!currentContainer) return;

        const providers = item?.ProviderIds || {};
        const providerEntries = Object.entries(providers);

        const people = item?.People || [];
        const actor = people.find(p => p.Type === 'Actor');

        // 收集所有需要展示的按钮数据，以便找出哪一个是“最后一个自定义按钮”
        const buttonsToCreate = [];

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
    // 监听
    // =========================
    const observer = new MutationObserver(() => {
        injectMenu();
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true
    });

})();