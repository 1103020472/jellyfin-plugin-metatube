// =========================================================
// Emby 演员详情页 - 更多头像预览（CustomCssJs 加载）
//
// 功能：
//   在演员（Person）详情页「封面 / 基本信息」区块下方、「影片」列表上方，
//   插入一行该演员的其他 Primary 头像（来自 RemoteImages 接口，
//   本环境数据源为 MetaTube），横向滑动浏览；点击任意头像可全屏放大，
//   支持左右切换、键盘方向键 / Esc。
//
// 接口：
//   GET /emby/Items/{id}/RemoteImages?Type=Primary&Limit=100
//   返回 { Images: [{ ProviderName, Url, Type, ... }], TotalRecordCount }
//   其中 Url 为 MetaTube（192.168.31.87:8081）处理后的图片直链，浏览器可直接加载。
//
// 挂载点：
//   .details-additionalContent 的最前面（其上方是 .topDetailsContainer 封面信息区，
//   后面第一个可见区块即 .linkedItems 里的「影片」行）。
//
// 仅对 Type === 'Person' 的详情页生效；SPA 路由切换时自动渲染 / 清理。
// =========================================================

(function () {
    'use strict';

    // ================= 配置区 =================
    const CONFIG = {
        imageType: 'Primary',   // 头像类型：Primary=头像；也可改成 'Thumb' / 'Backdrop'
        limit: 100,             // 最多拉取多少张
        thumbWidth: 120,        // 桌面端缩略图宽度（px），高度按 2:3 自动计算
        thumbWidthMobile: 96,   // 移动端缩略图宽度（px）
        sectionTitle: '更多头像'
    };
    // ==========================================

    const LOG_PREFIX = '[ActorPhotos]';
    const SECTION_CLASS = 'actorPhotosSection';
    const STYLE_ID = 'actorPhotosStyles';

    // itemId -> 图片列表 / 条目类型，避免 SPA 内来回切换时重复请求
    const imagesCache = new Map();
    const itemTypeCache = new Map();

    // 每次页面变动自增；异步操作回来后若 token 已变说明路由又切换了，结果作废
    let tickToken = 0;
    let lightbox = null;

    // ---------------- 工具 ----------------

    function getApi() {
        const client = window.ApiClient;
        if (!client || typeof client.serverAddress !== 'function') return null;
        const token = typeof client.accessToken === 'function' ? client.accessToken() : '';
        if (!token) return null;
        return {
            client,
            server: client.serverAddress().replace(/\/+$/, ''),
            token,
            userId: typeof client.getCurrentUserId === 'function' ? client.getCurrentUserId() : ''
        };
    }

    function currentItemId() {
        // hash 形如 #!/item?id=3196&serverId=...
        const m = location.hash.match(/[?&]id=([^&]+)/i);
        return m ? decodeURIComponent(m[1]) : null;
    }

    function debounce(fn, delay) {
        let timer = null;
        return function () {
            clearTimeout(timer);
            timer = setTimeout(fn, delay);
        };
    }

    async function isPerson(api, id) {
        if (itemTypeCache.has(id)) return itemTypeCache.get(id) === 'Person';
        try {
            const item = await api.client.getItem(api.userId, id);
            const type = (item && item.Type) || '';
            itemTypeCache.set(id, type);
            return type === 'Person';
        } catch (e) {
            console.warn(LOG_PREFIX, '获取条目信息失败', e);
            return false;
        }
    }

    async function fetchPhotos(api, id) {
        if (imagesCache.has(id)) return imagesCache.get(id);

        const url = `${api.server}/emby/Items/${encodeURIComponent(id)}/RemoteImages`
            + `?Type=${encodeURIComponent(CONFIG.imageType)}`
            + `&Limit=${CONFIG.limit}`
            + `&X-Emby-Token=${encodeURIComponent(api.token)}`;

        const res = await fetch(url, { headers: { accept: 'application/json' } });
        if (!res.ok) throw new Error(`RemoteImages HTTP ${res.status}`);
        const data = await res.json();

        const list = (data.Images || [])
            .map(img => ({
                url: img.Url,
                provider: img.ProviderName || '',
                language: img.Language || ''
            }))
            .filter(img => !!img.url);

        imagesCache.set(id, list);
        return list;
    }

    // ---------------- 样式 ----------------

    function injectStyles() {
        if (document.getElementById(STYLE_ID)) return;
        const style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = `
.${SECTION_CLASS} { margin-bottom: 1.8em; }
.actorPhotos-title { display: flex; align-items: baseline; gap: .5em; }
.actorPhotos-count { font-size: .68em; font-weight: 400; color: var(--textSecondaryColor, #999); }
.actorPhotos-scroller {
    overflow-x: auto; overflow-y: hidden;
    -webkit-overflow-scrolling: touch;
    scrollbar-width: none; -ms-overflow-style: none;
}
.actorPhotos-scroller::-webkit-scrollbar { display: none; }
.actorPhotos-row { display: flex; flex-direction: row; gap: .7em; padding: .25em .15em .9em; }
.actorPhotos-card {
    flex: 0 0 auto; width: ${CONFIG.thumbWidth}px;
    padding: 0; border: 0; background: transparent; cursor: pointer;
    border-radius: var(--card-border-radius, 6px);
    transition: transform .18s ease;
}
.actorPhotos-card:hover, .actorPhotos-card:focus-visible {
    transform: scale(1.08); z-index: 2; outline: none;
    box-shadow: 0 0 0 2px var(--focus-outline-color, rgba(120,180,255,.8));
}
.actorPhotos-cardBox {
    position: relative; width: 100%; padding-top: 150%;
    border-radius: inherit; overflow: hidden;
    background: var(--card-background-color, #1c1c1c);
    box-shadow: 0 1px 5px rgba(0,0,0,.55);
}
.actorPhotos-cardBox img {
    position: absolute; inset: 0; width: 100%; height: 100%;
    object-fit: cover; background: #141414;
}
.actorPhotos-card.is-broken { display: none; }
.actorPhotos-loading {
    padding: .2em 0 1.2em; font-size: .9em;
    color: var(--textSecondaryColor, #999);
}
@media (max-width: 768px) {
    .actorPhotos-card { width: ${CONFIG.thumbWidthMobile}px; }
}

/* 全屏灯箱 */
.actorPhotosLb {
    position: fixed; inset: 0; z-index: 2147483646;
    background: rgba(0,0,0,.92);
    display: flex; align-items: center; justify-content: center;
}
.actorPhotosLb-img {
    max-width: 92vw; max-height: 86vh; object-fit: contain;
    border-radius: 4px; box-shadow: 0 12px 60px rgba(0,0,0,.85);
    background: #111;
}
.actorPhotosLb-btn {
    position: absolute; border: 0; cursor: pointer;
    width: 52px; height: 52px; border-radius: 50%;
    background: rgba(0,0,0,.55); color: #fff; font-size: 32px; line-height: 1;
    display: flex; align-items: center; justify-content: center;
    transition: background .15s ease;
}
.actorPhotosLb-btn:hover { background: rgba(255,255,255,.22); }
.actorPhotosLb-close { top: 20px; right: 20px; font-size: 24px; }
.actorPhotosLb-prev { left: 20px; top: 50%; transform: translateY(-50%); }
.actorPhotosLb-next { right: 20px; top: 50%; transform: translateY(-50%); }
.actorPhotosLb-caption {
    position: absolute; bottom: 18px; left: 0; right: 0;
    text-align: center; color: #ccc; font-size: 14px; pointer-events: none;
}
@media (max-width: 768px) {
    .actorPhotosLb-btn { width: 42px; height: 42px; font-size: 26px; }
    .actorPhotosLb-prev { left: 8px; }
    .actorPhotosLb-next { right: 8px; }
}
`;
        document.head.appendChild(style);
    }

    // ---------------- 区块构建 ----------------

    function buildSection(itemId, photos) {
        const sec = document.createElement('div');
        sec.className = `verticalSection verticalSection-cards ${SECTION_CLASS}`;
        sec.dataset.actorPhotosFor = itemId;

        const title = document.createElement('h2');
        title.className = 'sectionTitle sectionTitle-cards padded-left padded-left-page padded-right actorPhotos-title';
        title.textContent = CONFIG.sectionTitle;
        const count = document.createElement('span');
        count.className = 'actorPhotos-count';
        count.textContent = `(${photos.length})`;
        title.appendChild(count);
        sec.appendChild(title);

        const scroller = document.createElement('div');
        scroller.className = 'actorPhotos-scroller scrollX hiddenScrollX padded-left padded-left-page padded-right';

        const row = document.createElement('div');
        row.className = 'actorPhotos-row focuscontainer-x';

        photos.forEach((photo, index) => {
            const card = document.createElement('button');
            card.type = 'button';
            card.className = 'card actorPhotos-card card-hoverable';
            card.title = photo.provider
                ? `${photo.provider} #${index + 1}`
                : `头像 #${index + 1}`;

            const box = document.createElement('div');
            box.className = 'actorPhotos-cardBox';
            const img = document.createElement('img');
            img.loading = 'lazy';
            img.decoding = 'async';
            img.referrerPolicy = 'no-referrer';
            img.alt = `演员头像 ${index + 1}`;
            img.src = photo.url;
            img.addEventListener('error', () => card.classList.add('is-broken'));
            box.appendChild(img);
            card.appendChild(box);

            card.addEventListener('click', () => openLightbox(photos, index));
            row.appendChild(card);
        });

        scroller.appendChild(row);
        sec.appendChild(scroller);
        return sec;
    }

    // ---------------- 灯箱 ----------------

    function openLightbox(photos, startIndex) {
        closeLightbox();

        const root = document.createElement('div');
        root.className = 'actorPhotosLb';
        root.innerHTML = `
            <img class="actorPhotosLb-img" alt="演员头像大图">
            <button type="button" class="actorPhotosLb-btn actorPhotosLb-close" aria-label="关闭">&#10005;</button>
            <button type="button" class="actorPhotosLb-btn actorPhotosLb-prev" aria-label="上一张">&#8249;</button>
            <button type="button" class="actorPhotosLb-btn actorPhotosLb-next" aria-label="下一张">&#8250;</button>
            <div class="actorPhotosLb-caption"></div>
        `;
        document.body.appendChild(root);

        const imgEl = root.querySelector('.actorPhotosLb-img');
        const captionEl = root.querySelector('.actorPhotosLb-caption');
        let index = startIndex;

        function render() {
            const p = photos[index];
            imgEl.src = p.url;
            const parts = [];
            if (p.provider) parts.push(p.provider);
            parts.push(`${index + 1} / ${photos.length}`);
            captionEl.textContent = parts.join(' · ');
        }

        function step(delta) {
            index = (index + delta + photos.length) % photos.length;
            render();
        }

        function onKeydown(e) {
            if (e.key === 'Escape') { closeLightbox(); }
            else if (e.key === 'ArrowLeft') { step(-1); }
            else if (e.key === 'ArrowRight') { step(1); }
        }

        root.querySelector('.actorPhotosLb-close').addEventListener('click', closeLightbox);
        root.querySelector('.actorPhotosLb-prev').addEventListener('click', (e) => { e.stopPropagation(); step(-1); });
        root.querySelector('.actorPhotosLb-next').addEventListener('click', (e) => { e.stopPropagation(); step(1); });
        // 点击图片以外的遮罩区域关闭
        root.addEventListener('click', (e) => { if (e.target === root) closeLightbox(); });
        document.addEventListener('keydown', onKeydown);

        lightbox = { root, onKeydown };
        render();
    }

    function closeLightbox() {
        if (lightbox) {
            document.removeEventListener('keydown', lightbox.onKeydown);
            lightbox.root.remove();
            lightbox = null;
        }
    }

    // ---------------- 主流程 ----------------

    function removeAllSections(exceptContainer) {
        document.querySelectorAll(`.${SECTION_CLASS}`).forEach(node => {
            if (!exceptContainer || node.parentNode !== exceptContainer) node.remove();
        });
    }

    async function tick() {
        const myToken = ++tickToken;
        const api = getApi();
        const itemId = currentItemId();
        const container = document.querySelector('.details-additionalContent');

        // 不在条目详情页：清掉可能残留的区块和灯箱
        if (!api || !itemId || !container) {
            removeAllSections();
            closeLightbox();
            return;
        }

        // 已为当前演员渲染过则跳过
        const existing = container.querySelector(`.${SECTION_CLASS}`);
        if (existing && existing.dataset.actorPhotosFor === itemId) return;

        // 路由切换：先移除其它容器 / 旧条目残留的区块
        removeAllSections(container);

        if (!(await isPerson(api, itemId))) {
            if (myToken !== tickToken) return;
            removeAllSections();
            return;
        }
        if (myToken !== tickToken || currentItemId() !== itemId) return;

        let photos;
        try {
            photos = await fetchPhotos(api, itemId);
        } catch (e) {
            console.warn(LOG_PREFIX, '加载演员头像失败', itemId, e);
            return;
        }

        // 请求期间路由可能已切换
        if (myToken !== tickToken || currentItemId() !== itemId) return;
        if (!photos.length) return;

        const freshContainer = document.querySelector('.details-additionalContent');
        if (!freshContainer) return;

        const duplicated = freshContainer.querySelector(`.${SECTION_CLASS}`);
        if (duplicated && duplicated.dataset.actorPhotosFor === itemId) return;
        if (duplicated) duplicated.remove();

        const section = buildSection(itemId, photos);
        // 封面信息区（.topDetailsContainer）结束后，additionalContent 的第一个位置：
        // 视觉上即「封面下面、影片上面」
        freshContainer.insertBefore(section, freshContainer.firstChild);
    }

    function init() {
        injectStyles();

        const scheduledTick = debounce(tick, 120);
        const observer = new MutationObserver(scheduledTick);
        observer.observe(document.documentElement, { childList: true, subtree: true });

        window.addEventListener('hashchange', () => { closeLightbox(); tick(); });
        window.addEventListener('popstate', () => { closeLightbox(); tick(); });
        window.addEventListener('pageshow', tick);

        tick();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
