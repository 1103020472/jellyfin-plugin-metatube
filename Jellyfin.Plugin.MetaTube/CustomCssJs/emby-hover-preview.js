/*!
 * Emby 卡片悬停 / 长按 播放短视频预览（StrmAssistant PreviewClip）
 * - 桌面：指针停在卡片上「静止」HOVER_DELAY 后播放（用轮询判定静止，不依赖 mouseover 事件序列）
 * - 移动端：长按 ~500ms 后播放，并吞掉随后的 click（避免跳转详情页）
 * - 首次播放需要服务端现场生成，期间显示"生成中"提示；失败会显示原因
 * - VR 左右分屏（宽高比≈2:1）自动只显示左眼
 * - 调试：控制台执行 window.__embyHoverPreviewDebug = true 后可见判定日志
 */
(function () {
    'use strict';

    if (window.__embyHoverPreviewInstalled) return;
    window.__embyHoverPreviewInstalled = true;
    /* 版本戳（排查用）：控制台执行 window.__embyHoverPreviewVersion 看当前加载的是哪一版 */
    window.__embyHoverPreviewVersion = '2026-10-25 19:40 wrap-teardown';

    var TARGET_SELECTOR = '.itemsContainer .cardOverlayContainer';
    var CARD_SELECTOR = '.itemsContainer .card';  // 命中区用整张卡片，数据仍从 overlay 上取
    var HOVER_DELAY = 450;      // 桌面：指针静止多久触发
    var TICK_INTERVAL = 120;     // 静止检测轮询间隔
    var REPEAT_GUARD = 2000;    // 同一条目 2s 内不重复触发
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
        '  max-width: calc(100% - 12px); text-align: center; word-break: break-word;',
        '  pointer-events: none;',
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

    /* 从卡片反查条目 Id（Emby 4.9 是数字短 id）
       优先读卡片上的 data-id（最稳，不依赖列表内部状态），
       没有时再按 _dataItemIndex / data-index 去 itemsContainer 的数据源里取。 */
    function getItemId(overlay) {
        var card = overlay.closest('.card') || overlay;
        if (!card) return null;

        var attrId = card.getAttribute && card.getAttribute('data-id');
        if (attrId) return String(attrId);

        var container = card.closest('.itemsContainer');
        var source = container && (container._itemSource || container.items);
        if (!source) return null;

        var index = card._dataItemIndex != null
            ? card._dataItemIndex
            : (card.dataset ? card.dataset.index : null);
        if (index == null || index === '') return null;

        var item = source[index];

        return item && item.Id != null ? String(item.Id) : null;
    }

    /* 卡片至少一半面积在视口内就算可见。
       旧逻辑要求"整块完全在视口内"，列表最后一行露出一半时永远不会触发。 */
    function isMostlyVisible(elem) {
        var rect = elem.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return false;

        var vw = window.innerWidth || document.documentElement.clientWidth;
        var vh = window.innerHeight || document.documentElement.clientHeight;
        var visW = Math.min(rect.right, vw) - Math.max(rect.left, 0);
        var visH = Math.min(rect.bottom, vh) - Math.max(rect.top, 0);

        if (visW <= 0 || visH <= 0) return false;

        return (visW * visH) / (rect.width * rect.height) >= 0.5;
    }

    function trace() {
        if (!window.__embyHoverPreviewDebug) return;
        try {
            console.log.apply(console, ['[hover-preview]'].concat(Array.prototype.slice.call(arguments)));
        } catch (e) { /* ignore */ }
    }

    /* ---------------- 播放 ---------------- */

    var current = null; // { overlay, wrap, hint, timer, pollTimer, id }

    function stopVideoIn(wrap) {
        var video = wrap.querySelector('video');
        if (!video) return;

        try { video.pause(); } catch (e) { /* ignore */ }
        video.removeAttribute('src');
        try { video.load(); } catch (e) { /* ignore */ }
    }

    function removeWrap(wrap) {
        if (!wrap) return;

        wrap.classList.remove('emby-preview-visible');
        stopVideoIn(wrap);

        setTimeout(function () {
            if (wrap.parentNode) wrap.parentNode.removeChild(wrap);
        }, 220);
    }

    function teardown() {
        var state = current;
        current = null;

        if (state) {
            if (state.timer) clearTimeout(state.timer);
            if (state.pollTimer) clearTimeout(state.pollTimer);

            if (state.wrap) removeWrap(state.wrap);
            if (state.hint && state.hint.parentNode) state.hint.parentNode.removeChild(state.hint);
        }

        /* 兜底：把页面上残留的预览层全部收掉。
           历史上 current.wrap 没赋值，teardown 只清得掉提示条，
           视频层会一直留在卡片上继续播（离开/换卡都停不下来），所以这里统一清扫。 */
        var wraps = document.querySelectorAll('.emby-preview-wrap');
        for (var i = 0; i < wraps.length; i++) removeWrap(wraps[i]);
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

        /* 用 textNode 而不是 innerHTML：失败原因来自服务端（ffmpeg stderr / URL），
           直接拼 innerHTML 可能被当成标签解析 */
        current.hint.textContent = '';

        var dot = document.createElement('span');
        dot.className = 'emby-preview-dot';
        current.hint.appendChild(dot);
        current.hint.appendChild(document.createTextNode(text));
    }

    /* 服务端 Failed 时把原因简短显示出来（ffmpeg/源站错误），否则用户完全看不到反馈。
       原始 Error 可能很长（ffmpeg stderr），只取首行并截断。 */
    function shortReason(status) {
        var raw = (status && status.Error) || '';
        raw = String(raw).replace(/\s+/g, ' ').trim();

        if (!raw || raw === 'undefined' || raw === 'null') return '未知原因, 详见服务端日志';
        if (raw.length > 60) raw = raw.slice(0, 60) + '…';

        return raw;
    }

    function failHint(overlay, status, prefix) {
        showHint(overlay, prefix + '：' + shortReason(status));
        setTimeout(teardown, 4000);
    }

    function playClip(overlay, id) {
        if (!current || current.overlay !== overlay) return;

        var wrap = ensureWrap(overlay);
        current.wrap = wrap;    // teardown 靠它收掉视频层，必须记下来
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
                    failHint(overlay, status, '预览不可用');
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
                    failHint(overlay, status, '预览不可用');
                } else {
                    /* 未知状态（功能未启用时服务端可能返回 None），也给出可见反馈 */
                    failHint(overlay, { Error: status ? '服务端状态 ' + status.Status : '无响应' }, '预览不可用');
                }
            })
            .catch(function () { failHint(overlay, { Error: '请求预览接口失败' }, '预览不可用'); });
    }

    /* ---------------- 桌面：指针"静止"在卡片上才触发 ----------------
       旧实现用 mouseover 起步 + mousemove 位移 >5px 就 cancelHover()：
       抖动清掉计时后，指针仍停在原卡片上不会再产生 mouseover，
       于是"明明停了 3 秒却没反应"，必须离开卡片再进入（实测必现）。
       现在改为：只记录指针位置 + 最后一次活动时间，轮询判断是否已静止
       HOVER_DELAY；抖动只会推迟触发，不会丢状态，滚动/切页后也能自愈。 */

    var pointer = { x: -1, y: -1, ts: 0 };
    var armedCard = null;   // 已经为哪张卡触发过，避免重复请求
    var lastTriggerId = null;
    var lastTriggerTs = 0;

    function notePointer(e) {
        pointer.x = e.clientX;
        pointer.y = e.clientY;
        pointer.ts = Date.now();

        if (!current || !current.overlay) return;

        /* 指针离开正在预览的那张卡 → 立刻收起（指针移到别的卡时先收，
           停稳后由下面的轮询给新卡重新触发） */
        var card = e.target && e.target.closest ? e.target.closest('.card') : null;
        var curCard = current.overlay.closest ? current.overlay.closest('.card') : null;

        if (card && curCard && (card === curCard || curCard.contains(card))) return;

        armedCard = null;
        teardown();
    }

    function resetHover() {
        pointer.x = -1;
        pointer.y = -1;
        pointer.ts = 0;
        armedCard = null;
    }

    document.addEventListener('mousemove', notePointer, true);
    document.addEventListener('mouseover', notePointer, true);

    /* 指针离开当前卡片（或离开窗口）→ 立即收起预览 */
    document.addEventListener('mouseout', function (e) {
        if (!e.relatedTarget) {          // 离开窗口
            resetHover();
            teardown();
            return;
        }

        if (!current || !current.overlay) return;

        var curCard = current.overlay.closest ? current.overlay.closest('.card') : null;
        if (curCard && curCard.contains(e.relatedTarget)) return;  // 还在同一张卡内

        armedCard = null;
        teardown();
    }, true);

    /* 滚动算"活动"：滚动过程中不判定，停下 HOVER_DELAY 后重新评估 */
    function noteScroll() {
        pointer.ts = Date.now();
        armedCard = null;
        teardown();
    }

    window.addEventListener('wheel', noteScroll, { passive: true });
    document.addEventListener('scroll', noteScroll, true);

    function cardAtPoint(x, y) {
        var el = document.elementFromPoint ? document.elementFromPoint(x, y) : null;
        var card = el && el.closest ? el.closest(CARD_SELECTOR) : null;

        if (card) return card;
        if (!el) return null;

        /* 兜底：偶尔有全屏遮罩（例如自定义脚本的 .misty-loading）盖住页面，
           elementFromPoint 拿不到卡片。只在"疑似全屏遮罩"时才走几何命中，
           避免每 tick 遍历上千张卡片。 */
        var vw = window.innerWidth || document.documentElement.clientWidth;
        var vh = window.innerHeight || document.documentElement.clientHeight;
        var r0 = el.getBoundingClientRect();

        if (r0.width < vw * 0.9 || r0.height < vh * 0.9) return null;

        var cards = document.querySelectorAll(CARD_SELECTOR);
        for (var i = 0; i < cards.length; i++) {
            var r = cards[i].getBoundingClientRect();
            if (r.width > 0 && r.height > 0 &&
                x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) {
                return cards[i];
            }
        }

        return null;
    }

    setInterval(function () {
        if (!pointer.ts) return;                            // 指针不在页面上

        var card = cardAtPoint(pointer.x, pointer.y);

        /* 兜底：预览还在，但指针已经不在那张卡上 → 收起（防止漏事件） */
        if (current && current.overlay) {
            var curCard = current.overlay.closest ? current.overlay.closest('.card') : null;
            if (!card || !curCard || !(card === curCard || curCard.contains(card))) {
                armedCard = null;
                teardown();
            }
        }

        if (Date.now() - pointer.ts < HOVER_DELAY) return;  // 还在动，继续等
        if (!card) { armedCard = null; return; }
        if (card === armedCard) return;                     // 这张已经处理过

        var overlay = card.querySelector('.cardOverlayContainer') || card;
        var id = getItemId(overlay);

        if (!id) { trace('skip: 取不到条目 Id', card.className); return; }
        if (!isMostlyVisible(card)) { trace('skip: 卡片不可见', id); return; }
        if (id === lastTriggerId && Date.now() - lastTriggerTs < REPEAT_GUARD) {
            trace('skip: 重复触发保护', id);
            return;
        }

        armedCard = card;
        lastTriggerId = id;
        lastTriggerTs = Date.now();
        trace('trigger', id);
        start(overlay);
    }, TICK_INTERVAL);

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
        if (document.hidden) { resetHover(); cancelPress(); teardown(); }
    });
    window.addEventListener('blur', function () { resetHover(); teardown(); });
})();
