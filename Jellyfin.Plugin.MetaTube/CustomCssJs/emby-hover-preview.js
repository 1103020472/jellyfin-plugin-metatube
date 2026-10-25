/*!
 * Emby 卡片悬停 / 长按 播放短视频预览（StrmAssistant PreviewClip）
 * - 桌面：鼠标在卡片上停稳 ~450ms 后播放
 * - 移动端：长按 ~500ms 后播放，并吞掉随后的 click（避免跳转详情页）
 * - 首次播放需要服务端现场生成（约 15-30s），期间显示"生成中"提示
 * - VR 左右分屏（宽高比≈2:1）自动只显示左眼
 */
(function () {
    'use strict';

    if (window.__embyHoverPreviewInstalled) return;
    window.__embyHoverPreviewInstalled = true;

    var TARGET_SELECTOR = '.itemsContainer .cardOverlayContainer';
    var CARD_SELECTOR = '.itemsContainer .card';  // 命中区用整张卡片，数据仍从 overlay 上取
    var HOVER_DELAY = 450;      // 桌面：停稳多久触发
    var LONGPRESS_DELAY = 500;  // 移动端：长按多久触发
    var MOVE_TOLERANCE = 12;    // 长按期间允许的抖动像素
    var POLL_INTERVAL = 2500;   // 轮询生成状态的间隔
    var POLL_TIMEOUT = 180000;  // 最长等待

    var CSS = [
        '.emby-preview-wrap {',
        '  position: absolute; inset: 0; z-index: 3; overflow: hidden;',
        '  pointer-events: none; border-radius: inherit; background: #000;',
        '  opacity: 0; transition: opacity .2s ease-in-out;',
        '}',
        '.emby-preview-wrap.emby-preview-visible { opacity: 1; }',
        '.emby-preview-wrap video {',
        '  position: absolute; top: 0; left: 0; width: 100%; height: 100%;',
        '  object-fit: cover; background: #000;',
        '}',
        /* VR 左右分屏：放大到 200% 宽并锚定左侧，等于只显示左眼 */
        '.emby-preview-wrap.emby-preview-sbs video { width: 200%; }',
        '.emby-preview-hint {',
        '  position: absolute; left: 50%; bottom: 8px; transform: translateX(-50%);',
        '  z-index: 4; padding: 3px 8px; border-radius: 10px;',
        '  background: rgba(0,0,0,.65); color: #fff; font-size: 11px; line-height: 1.4;',
        '  white-space: nowrap; pointer-events: none;',
        '}',
        '.emby-preview-hint .emby-preview-dot {',
        '  display: inline-block; width: 6px; height: 6px; margin-right: 5px;',
        '  border-radius: 50%; background: #4aa3ff; vertical-align: middle;',
        '  animation: embyPreviewPulse 1s ease-in-out infinite;',
        '}',
        '@keyframes embyPreviewPulse { 0%,100% { opacity: .25; } 50% { opacity: 1; } }'
    ].join('\n');

    (function injectStyle() {
        var style = document.createElement('style');
        style.type = 'text/css';
        style.appendChild(document.createTextNode(CSS));
        (document.head || document.documentElement).appendChild(style);
    })();

    /* ---------------- 工具 ---------------- */

    function getApiClient() {
        return window.ApiClient || (window.Emby && window.Emby.ApiClient) || null;
    }

    function buildUrl(path) {
        var api = getApiClient();
        if (!api) return null;

        var url = api.getUrl(path);
        var token = api.accessToken ? api.accessToken() : null;

        if (token) {
            url += (url.indexOf('?') >= 0 ? '&' : '?') + 'api_key=' + encodeURIComponent(token);
        }

        return url;
    }

    /* 从卡片反查条目 Id（Emby 4.9 是数字短 id） */
    function getItemId(overlay) {
        var card = overlay.closest('.card');
        if (!card) return null;

        var container = card.closest('.itemsContainer');
        var source = container && (container._itemSource || container.items);
        if (!source) return null;

        var index = card._dataItemIndex != null ? card._dataItemIndex : card.dataset.index;
        var item = source[index];

        return item && item.Id != null ? String(item.Id) : null;
    }

    function isInViewport(elem) {
        var rect = elem.getBoundingClientRect();
        var height = window.innerHeight || document.documentElement.clientHeight;
        var width = window.innerWidth || document.documentElement.clientWidth;

        return rect.top >= 0 && rect.left >= 0 && rect.bottom <= height && rect.right <= width;
    }

    /* ---------------- 播放 ---------------- */

    var current = null; // { overlay, wrap, hint, timer, id }

    function teardown() {
        if (!current) return;

        var state = current;
        current = null;

        if (state.timer) clearTimeout(state.timer);
        if (state.pollTimer) clearTimeout(state.pollTimer);

        var wrap = state.wrap;
        if (wrap && wrap.parentNode) {
            wrap.classList.remove('emby-preview-visible');
            var video = wrap.querySelector('video');
            if (video) {
                try { video.pause(); } catch (e) { /* ignore */ }
                video.removeAttribute('src');
                try { video.load(); } catch (e) { /* ignore */ }
            }
            setTimeout(function () { if (wrap.parentNode) wrap.parentNode.removeChild(wrap); }, 220);
        }

        if (state.hint && state.hint.parentNode) state.hint.parentNode.removeChild(state.hint);
    }

    /* Emby 的卡片结构（实测 4.9）：
         .cardBox
           button.cardContent   <- 封面图在这里（img.cardImage 的父节点）
           .cardOverlayContainer <- 这是封面【下方】的空白条，不是盖在封面上的层
       所以必须挂到封面图容器上，否则视频会出现在海报下方的空白处。 */
    function cardRoot(overlay) {
        return overlay.closest('.card') || overlay.parentElement || overlay;
    }

    function mountFor(overlay) {
        var card = cardRoot(overlay);
        var img = card.querySelector('img.cardImage') || card.querySelector('img');
        var holder = img && img.parentElement;

        if (holder && holder !== card) {
            var cs = window.getComputedStyle ? window.getComputedStyle(holder) : null;
            if (cs && cs.position === 'static') holder.style.position = 'relative';
            return holder;
        }

        return overlay;
    }

    function ensureWrap(overlay) {
        var wrap = cardRoot(overlay).querySelector('.emby-preview-wrap');

        if (!wrap) {
            wrap = document.createElement('div');
            wrap.className = 'emby-preview-wrap';
            mountFor(overlay).appendChild(wrap);
        }

        return wrap;
    }

    function showHint(overlay, text) {
        if (!current) return;

        if (!current.hint) {
            var hint = document.createElement('div');
            hint.className = 'emby-preview-hint';
            mountFor(overlay).appendChild(hint);
            current.hint = hint;
        }

        current.hint.innerHTML = '<span class="emby-preview-dot"></span>' + text;
    }

    function playClip(overlay, id) {
        if (!current || current.overlay !== overlay) return;

        var wrap = ensureWrap(overlay);
        var video = wrap.querySelector('video');

        if (!video) {
            video = document.createElement('video');
            video.muted = true;
            video.loop = true;
            video.playsInline = true;
            video.setAttribute('playsinline', '');
            video.setAttribute('webkit-playsinline', '');
            video.preload = 'auto';

            video.addEventListener('loadedmetadata', function () {
                /* 预览输出宽度固定（640），高度按源片比例算：
                   16:9 → 360，2:1 VR 左右分屏 → 320，4:3 → 480。
                   只有宽高比落在 1.85~2.15 之间才当作 VR 分屏，避免误裁普通宽屏。 */
                if (video.videoWidth > 0 && video.videoHeight > 0) {
                    var ratio = video.videoWidth / video.videoHeight;
                    if (ratio >= 1.85 && ratio <= 2.15) {
                        wrap.classList.add('emby-preview-sbs');
                    }
                }
            });

            video.addEventListener('canplay', function () {
                wrap.classList.add('emby-preview-visible');
                if (current && current.hint && current.hint.parentNode) {
                    current.hint.parentNode.removeChild(current.hint);
                    current.hint = null;
                }
            });

            video.addEventListener('error', function () { teardown(); });

            wrap.appendChild(video);
        }

        if (!video.src) video.src = buildUrl('StrmAssistant/Preview/' + encodeURIComponent(id));

        var p = video.play();
        if (p && p.catch) p.catch(function () { /* 自动播放被拦，保持静音等待用户 */ });
    }

    function pollStatus(overlay, id, startedAt) {
        if (!current || current.overlay !== overlay) return;

        if (Date.now() - startedAt > POLL_TIMEOUT) {
            showHint(overlay, '预览生成超时');
            setTimeout(teardown, 2000);
            return;
        }

        var url = buildUrl('StrmAssistant/Preview/' + encodeURIComponent(id) + '/Status');
        if (!url) return;

        fetch(url, { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (status) {
                if (!current || current.overlay !== overlay) return;

                if (status && status.Status === 'Ready') {
                    playClip(overlay, id);
                } else if (status && status.Status === 'Failed') {
                    showHint(overlay, '预览不可用');
                    setTimeout(teardown, 2000);
                } else {
                    showHint(overlay, status && status.Status === 'Generating' ? '预览生成中…' : '预览排队中…');
                    current.pollTimer = setTimeout(function () { pollStatus(overlay, id, startedAt); }, POLL_INTERVAL);
                }
            })
            .catch(function () {
                if (!current || current.overlay !== overlay) return;
                current.pollTimer = setTimeout(function () { pollStatus(overlay, id, startedAt); }, POLL_INTERVAL);
            });
    }

    /* 悬停/长按的命中区是整张卡片（封面 + 下方空白条都算），
       但条目数据仍然从 .cardOverlayContainer 上取，保持原有取值路径不变。 */
    function overlayOf(node) {
        if (!node || !node.closest) return null;
        var card = node.closest(CARD_SELECTOR);
        if (!card) return null;
        return card.querySelector('.cardOverlayContainer') || card;
    }

    function start(overlay) {
        if (!getApiClient()) return;

        var id = getItemId(overlay);
        if (!id) return;

        teardown();
        current = { overlay: overlay, id: id };

        showHint(overlay, '加载预览…');

        var url = buildUrl('StrmAssistant/Preview/' + encodeURIComponent(id));
        if (!url) { teardown(); return; }

        fetch(url, { method: 'POST', credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (status) {
                if (!current || current.overlay !== overlay) return;

                if (status && status.Status === 'Ready') {
                    playClip(overlay, id);
                } else if (status && (status.Status === 'Queued' || status.Status === 'Generating')) {
                    showHint(overlay, status.Status === 'Generating' ? '预览生成中…' : '预览排队中…');
                    current.pollTimer = setTimeout(function () {
                        pollStatus(overlay, id, Date.now());
                    }, POLL_INTERVAL);
                } else if (status && status.Status === 'Failed') {
                    teardown();
                } else {
                    teardown();
                }
            })
            .catch(function () { teardown(); });
    }

    /* ---------------- 桌面：悬停停稳才触发 ---------------- */

    var hoverElem = null;
    var hoverTimer = null;
    var lastMoveX = 0;
    var lastMoveY = 0;

    function cancelHover() {
        if (hoverTimer) { clearTimeout(hoverTimer); hoverTimer = null; }
        hoverElem = null;
    }

    document.addEventListener('mouseover', function (e) {
        if (e.pointerType === 'touch') return;

        var overlay = overlayOf(e.target);
        if (!overlay || overlay === hoverElem) return;

        cancelHover();
        hoverElem = overlay;
        lastMoveX = e.pageX;
        lastMoveY = e.pageY;

        hoverTimer = setTimeout(function () {
            if (hoverElem === overlay && isInViewport(overlay)) start(overlay);
        }, HOVER_DELAY);
    }, true);

    document.addEventListener('mousemove', function (e) {
        if (!hoverElem) return;

        var dx = e.pageX - lastMoveX;
        var dy = e.pageY - lastMoveY;

        if (dx * dx + dy * dy > 25) {
            /* 还在移动：重置计时，避免快速划过时误触发 */
            lastMoveX = e.pageX;
            lastMoveY = e.pageY;
            cancelHover();
        }
    }, true);

    document.addEventListener('mouseout', function (e) {
        if (!hoverElem) return;

        var to = e.relatedTarget;
        while (to) {
            if (to === hoverElem) return;
            to = to.parentNode;
        }

        cancelHover();
        teardown();
    }, true);

    /* ---------------- 移动端：长按 ---------------- */

    var pressTimer = null;
    var pressX = 0;
    var pressY = 0;
    var pressOverlay = null;
    var suppressClickUntil = 0;

    function cancelPress() {
        if (pressTimer) { clearTimeout(pressTimer); pressTimer = null; }
        pressOverlay = null;
    }

    document.addEventListener('pointerdown', function (e) {
        if (e.pointerType === 'mouse') return;

        var overlay = overlayOf(e.target);
        if (!overlay) return;

        cancelPress();
        pressOverlay = overlay;
        pressX = e.clientX;
        pressY = e.clientY;

        pressTimer = setTimeout(function () {
            if (!pressOverlay) return;

            suppressClickUntil = Date.now() + 1200;
            start(pressOverlay);
        }, LONGPRESS_DELAY);
    }, true);

    document.addEventListener('pointermove', function (e) {
        if (!pressOverlay) return;

        var dx = e.clientX - pressX;
        var dy = e.clientY - pressY;

        if (dx * dx + dy * dy > MOVE_TOLERANCE * MOVE_TOLERANCE) cancelPress();
    }, true);

    ['pointerup', 'pointercancel', 'pointerleave'].forEach(function (name) {
        document.addEventListener(name, function () { cancelPress(); }, true);
    });

    /* 长按触发后，吞掉紧随的 click，否则松手会跳详情页 */
    document.addEventListener('click', function (e) {
        if (Date.now() < suppressClickUntil) {
            suppressClickUntil = 0;
            e.stopPropagation();
            e.stopImmediatePropagation();
            e.preventDefault();
        }
    }, true);

    /* 长按不弹系统菜单 / 不选中文字 */
    document.addEventListener('contextmenu', function (e) {
        if (overlayOf(e.target)) {
            e.preventDefault();
        }
    }, true);

    document.addEventListener('selectstart', function (e) {
        if (overlayOf(e.target)) e.preventDefault();
    }, true);

    /* 页面切换 / 失焦时收尾 */
    document.addEventListener('visibilitychange', function () {
        if (document.hidden) { cancelHover(); cancelPress(); teardown(); }
    });
    window.addEventListener('blur', function () { cancelHover(); teardown(); });
    window.addEventListener('scroll', function () { teardown(); }, { passive: true });
})();
