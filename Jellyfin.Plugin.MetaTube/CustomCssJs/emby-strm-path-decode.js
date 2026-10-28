// =========================================================
// Emby 详情页 - 媒体源 strm 链接显示为「URL 解码后的可读路径」
//
// 现象：
//   番号详情页「媒体信息」区块里，媒体源名称显示的是 strm 文件内的原始
//   URL（百分号编码），中文目录全是 %XX，根本看不出是哪一部、哪个文件：
//     http://alist.istore.local/d/%E5%85%89%E9%B8%AD%E4%BA%91%E7%9B%98/...
//
// 功能：
//   把该文本 decodeURIComponent 之后显示：
//     http://alist.istore.local/d/光鸭云盘/收藏视频/收藏女优/斋藤あみり/WAAA-266/WAAA-266.mp4
//   - 悬停（title）显示完整解码后的链接
//   - 点击复制解码后的链接（http 环境 navigator.clipboard 不可用，走 execCommand 兜底）
//
// 作用范围：
//   仅处理 .mediaSources 里的媒体源名称节点，且只处理「http(s):// 开头 +
//   含 %XX」的文本；本地文件路径（/vol3/... ）、已经解码过的文本一律不动。
//   幂等：同一节点重复处理不会二次解码（靠 dataset 记录上次写入的解码结果）。
//
// 挂载点（Emby 4.9 实测 DOM，av.emby.cv.local item?id=128314）：
//   .mediaSources
//     .mediaSource
//       .padded-left...
//         .sectionTitle.sectionTitle-cards.secondaryText
//           <div>http://alist.../WAAA-266.mp4</div>     ← 改这个 div
//           .mediaInfoItems（MP4 / 4.8 GB / 添加于 ...）
//
// 说明：媒体信息是异步渲染的（进入详情页后再拉 /Items/{id}/PlaybackInfo），
//       所以用 MutationObserver + viewshow 双保险，不能用一次性的 DOMContentLoaded。
// =========================================================
(function () {
    'use strict';

    // 媒体源名称：sectionTitle 下的第一个 div（第二个是 .mediaInfoItems，别动）
    var NAME_SELECTOR = '.mediaSources .mediaSource .sectionTitle.sectionTitle-cards > div:first-child';

    // 只认 URL：http:// 或 https:// 开头
    var URL_RE = /^https?:\/\/\S+$/i;
    // 含百分号编码
    var PCT_RE = /%[0-9A-Fa-f]{2}/;
    // dataset 里记录「上次写入的解码结果」的键
    var MARK = 'strmDecodedText';

    // =========================
    // 解码
    //   整体解一次最干净；遇到非法编码（比如路径里真的有裸 %）会抛错，
    //   这时退回「按 %XX 连续段逐段解」，解不动的段原样保留。
    // =========================
    function decodeUrl(text) {
        try {
            return decodeURIComponent(text);
        } catch (e) {
            // ignore
        }

        return text.replace(/(?:%[0-9A-Fa-f]{2})+/g, function (seq) {
            try {
                return decodeURIComponent(seq);
            } catch (e) {
                return seq;
            }
        });
    }

    // =========================
    // 复制（http 页面下 navigator.clipboard 多数不可用）
    // =========================
    function copyText(text) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text).catch(function () {
                return legacyCopy(text);
            });
        }

        return legacyCopy(text);
    }

    function legacyCopy(text) {
        return new Promise(function (resolve, reject) {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.top = '-1000px';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();

            try {
                var ok = document.execCommand('copy');
                document.body.removeChild(ta);
                ok ? resolve() : reject(new Error('execCommand copy failed'));
            } catch (e) {
                document.body.removeChild(ta);
                reject(e);
            }
        });
    }

    // 极简提示（不引入任何依赖）
    function toast(anchor, text) {
        var el = document.createElement('div');
        el.textContent = text;
        el.style.cssText = [
            'position:fixed',
            'z-index:999999',
            'padding:4px 10px',
            'border-radius:4px',
            'font-size:12px',
            'line-height:1.6',
            'color:#fff',
            'background:rgba(0,0,0,.78)',
            'pointer-events:none',
            'transition:opacity .25s ease',
            'opacity:0'
        ].join(';');

        var rect = anchor.getBoundingClientRect();
        el.style.left = Math.round(rect.left) + 'px';
        el.style.top = Math.round(rect.bottom + 6) + 'px';

        document.body.appendChild(el);
        requestAnimationFrame(function () {
            el.style.opacity = '1';
        });

        setTimeout(function () {
            el.style.opacity = '0';
            setTimeout(function () {
                el.parentNode && el.parentNode.removeChild(el);
            }, 300);
        }, 1100);
    }

    // =========================
    // 单节点处理
    //   返回 true 表示这次真的改了 DOM
    // =========================
    function processNode(el) {
        var raw = (el.textContent || '').trim();

        if (!URL_RE.test(raw)) {
            // 不是 URL（本地路径 / 普通文件名），把可能残留的标记清掉
            if (el.dataset[MARK]) {
                delete el.dataset[MARK];
                delete el.dataset.strmUrl;
                el.removeAttribute('title');
                el.style.cursor = '';
                el.style.wordBreak = '';
                el.style.whiteSpace = '';
            }
            return false;
        }

        // 就是自己上次写进去的解码结果 → 幂等跳过
        if (el.dataset[MARK] === raw) {
            return false;
        }

        // 已经是解码后的文本（没有 %XX 了）→ 记标记，供上面那条幂等判断用
        if (!PCT_RE.test(raw)) {
            if (el.dataset[MARK] !== raw) {
                el.dataset[MARK] = raw;
                el.dataset.strmUrl = raw;
                el.setAttribute('title', raw);
            }
            return false;
        }

        var decoded = decodeUrl(raw);
        if (decoded === raw) {
            return false;
        }

        el.textContent = decoded;
        el.dataset[MARK] = decoded;
        el.dataset.strmUrl = decoded;
        el.setAttribute('title', decoded);

        // 长中文路径要能换行，不然会横向溢出
        el.style.wordBreak = 'break-all';
        el.style.whiteSpace = 'normal';
        el.style.cursor = 'pointer';

        return true;
    }

    function apply() {
        var nodes = document.querySelectorAll(NAME_SELECTOR);
        var changed = 0;

        for (var i = 0; i < nodes.length; i++) {
            try {
                if (processNode(nodes[i])) changed++;
            } catch (e) {
                console.error('[STRM PATH] 处理媒体源名称失败:', e);
            }
        }

        return changed;
    }

    // =========================
    // 点击复制（事件委托，节点是动态渲染的，不能逐个绑）
    // =========================
    document.addEventListener('click', function (e) {
        var el = e.target && e.target.closest ? e.target.closest('[data-strm-url]') : null;
        if (!el) return;

        var url = el.dataset.strmUrl;
        if (!url) return;

        copyText(url).then(function () {
            toast(el, '已复制链接');
        }).catch(function (err) {
            console.error('[STRM PATH] 复制失败:', err);
            toast(el, '复制失败，请手动选择');
        });
    }, false);

    // =========================
    // 触发：去抖的 MutationObserver + viewshow 兜底
    //   媒体信息是异步渲染的，一次性的 DOMContentLoaded 抓不到；
    //   Emby 切换条目时 SPA 可能复用同一批节点，所以每轮都以「当前文本」为准重新判断。
    // =========================
    var timer = null;

    function schedule(delay) {
        if (timer) return;
        timer = setTimeout(function () {
            timer = null;
            apply();
        }, delay || 60);
    }

    var observer = new MutationObserver(function () {
        schedule();
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true
    });

    document.addEventListener('viewshow', function () {
        schedule(0);
    });
    window.addEventListener('hashchange', function () {
        schedule(0);
    });

    // 首屏（脚本可能在 DOM 已渲染后才加载）
    schedule(0);
})();
