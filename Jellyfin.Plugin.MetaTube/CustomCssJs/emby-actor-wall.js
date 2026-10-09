// ==UserScript==
// @name         Emby 演员海报墙 (全功能路由修复版+合集筛选+浏览记录+云端真同步)
// @namespace    https://github.com/
// @version      1.70
// @description  集成收藏、排序、作品统计、未收筛选、云端同步、合集与多选媒体库过滤、无刷新重载、重名排查，完美适配 Emby 4.9+
// @author       た & Assistant
// @match        *://*/*
// @grant        none
// @run-at       document-start
// ==/UserScript==

(function () {
    'use strict';

    // ================= 配置区 =================
    const CONFIG = {
        // 在这里填写你希望默认勾选的媒体库名称（需与 Emby 中媒体库名称完全一致）
        // 如果留空 []，则默认全选（不按媒体库过滤）
        defaultLibraries: ['115网盘', '光鸭云盘', 'nas', '移动硬盘']
    };
    // ==========================================

    let isActorWallActive = false;
    let wallOverlay = null;

    // --- 核心工具函数 ---
    function getEmbyInfo() {
        if (typeof ApiClient === 'undefined') return null;
        return {
            server: ApiClient.serverAddress(),
            serverId: ApiClient.serverId(),
            token: ApiClient.accessToken(),
            userId: ApiClient.getCurrentUserId()
        };
    }

    function debounce(fn, delay = 300) {
        let timer;
        return function (...args) {
            clearTimeout(timer);
            timer = setTimeout(() => fn.apply(this, args), delay);
        };
    }

    // --- UI 注入 ---
    function addTopButton() {
        if (document.getElementById('actorwall-top-btn')) return;
        const tabsSlider = document.querySelector('.tabs-viewmenubar-slider, .emby-tabs-slider');
        if (!tabsSlider) return;
        const allTabs = Array.from(tabsSlider.querySelectorAll('button.emby-tab-button'));
        const targetTab = allTabs.find(btn => ['首页', '收藏', '推荐', '预告片'].includes(btn.textContent.trim()));
        if (targetTab) {
            const btn = document.createElement('button');
            btn.id = 'actorwall-top-btn';
            btn.type = 'button';
            btn.className = 'emby-button secondaryText emby-tab-button main-tab-button button-hoverable';
            btn.innerHTML = `演员`;
            btn.onclick = (e) => {
                e.preventDefault();
                e.stopPropagation();
                showActorWall();
            };
            targetTab.parentNode.insertBefore(btn, targetTab.nextSibling);
        }
    }

    async function showActorWall(isHistoryRestore = false) {
        const emby = getEmbyInfo();
        if (!emby || !emby.token) {
            setTimeout(() => showActorWall(isHistoryRestore), 800);
            return;
        }
        if (isActorWallActive && wallOverlay) return;
        isActorWallActive = true;
        wallOverlay = document.createElement('div');
        wallOverlay.style.cssText = `position:fixed;inset:0;background:#0f0f0f;color:#eee;overflow:auto;z-index:2147483647;font-family:system-ui,sans-serif;`;
        document.body.appendChild(wallOverlay);

        if (!isHistoryRestore) {
            history.pushState({
                actorWall: true,
                wallState: null
            }, '', '#actor-wall');
        }

        await renderActorWall(emby);
    }

    async function renderActorWall(emby) {
        const cacheKey = `actor_wall_v4_${emby.serverId}_${emby.userId}`; // 升级版本号以清空旧缓存结构
        const STATE_KEY = `actor_wall_state_${emby.serverId}`;
        const VIEWED_KEY = `actor_wall_viewed_${emby.serverId}_${emby.userId}`;

        let actors = [];
        let nameCounts = {};
        let viewedActors = new Set(JSON.parse(localStorage.getItem(VIEWED_KEY) || '[]'));
        const cachedStr = localStorage.getItem(cacheKey);

        if (cachedStr) {
            try { actors = JSON.parse(cachedStr); } catch (e) { localStorage.removeItem(cacheKey); }
        }

        if (actors.length === 0) {
            try {
                const res = await fetch(`${emby.server}/emby/Persons?PersonTypes=Actor&Limit=15000&SortBy=SortName&Fields=ImageTags,PremiereDate,DateCreated&EnableUserData=true&UserId=${emby.userId}`, {
                    headers: { 'X-Emby-Token': emby.token }
                });
                const data = await res.json();
                actors = data.Items || [];
                localStorage.setItem(cacheKey, JSON.stringify(actors.map(a => ({...a, _counts: {}}))));
            } catch (e) { console.error("加载演员列表失败", e); }
        }

        // 计算重名
        actors.forEach(a => { nameCounts[a.Name] = (nameCounts[a.Name] || 0) + 1; });

        const html = `
<style>
    body,html{margin:0;padding:0;background:#0f0f0f;color:#eee;font-family:system-ui,sans-serif;overflow:auto}
    header{position:sticky;top:0;background:#1a1a1a;padding:12px 20px;display:flex;align-items:center;gap:8px;flex-wrap:wrap;z-index:10;border-bottom:1px solid #333}
    button{padding:7px 12px;border-radius:20px;border:none;background:#333;color:#fff;cursor:pointer;font-size:13px;transition:0.2s}
    button:hover{background:#444}
    button:disabled{opacity:0.5;cursor:not-allowed}
    button.active{background:#0099ff;color:white}
    .btn-group{display:flex;gap:4px;background:#222;padding:4px;border-radius:24px}
    select{padding:8px 12px;border-radius:20px;border:none;background:#333;color:#fff;outline:none;cursor:pointer;font-size:13px}
    #wall{padding:20px;display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:20px}
    .card{background:#222;border-radius:12px;overflow:hidden;cursor:pointer;transition:.25s;position:relative}
    .card:hover{transform:scale(1.06);box-shadow:0 10px 30px rgba(0,0,0,0.6);z-index:2}
    .card img{width:100%;height:245px;object-fit:cover;transition:.2s}
    .card .name{padding:12px;text-align:center;font-size:14px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;transition:.2s}
    .placeholder{height:245px;background:#1f1f1f;display:grid;place-items:center;font-size:70px;color:#444}
    .tools{position:absolute;top:10px;right:10px;z-index:3}
    .fav-btn{background:rgba(0,0,0,0.7);border:none;color:#fff;width:34px;height:34px;border-radius:50%;font-size:18px;display:flex;align-items:center;justify-content:center}
    
    .viewed-indicator {
        position: absolute; top: 10px; left: 10px; background: #52B54B; color: white;
        width: 22px; height: 22px; border-radius: 50%; display: flex; align-items: center; justify-content: center;
        font-size: 14px; font-weight: bold; box-shadow: 0 2px 5px rgba(0,0,0,0.5); pointer-events: none; z-index: 3;
    }
    .card.is-viewed img { opacity: 0.85; } 
    .card.is-viewed .name { color: #aaa; } 

    /* 下拉多选框样式 */
    .lib-checkbox-container { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; cursor: pointer; padding: 4px; border-radius: 4px; transition: 0.2s;}
    .lib-checkbox-container:hover { background: #333; }

    @media (max-width: 768px) {
        #wall { grid-template-columns: repeat(auto-fill, minmax(110px,1fr)); gap:10px; padding:10px; }
        .card img, .card .placeholder { height:155px; }
    }
</style>
<header>
    <div style="font-size:18px;font-weight:bold">👥 演员 (<span id="countDisplay">${actors.length}</span>)</div>
    <input id="search" placeholder="搜索..." style="width:100px;padding:8px 15px;border-radius:20px;border:none;background:#333;color:#fff;outline:none">
    
    <div class="library-dropdown-container" style="position:relative;">
        <button id="lib-filter-btn" style="background:#222; border:1px solid #444;">📚 媒体库: 默认</button>
        <div id="lib-dropdown" style="display:none; position:absolute; top:100%; left:0; background:#1a1a1a; border:1px solid #444; z-index:100; max-height:400px; min-width:160px; overflow-y:auto; padding:12px; border-radius:8px; box-shadow: 0 5px 20px rgba(0,0,0,0.8); margin-top:8px;">
           <div id="lib-options-wrapper">加载中...</div>
        </div>
    </div>

    <select id="collection-filter">
        <option value="all">📁 全部合集</option>
    </select>

    <div class="btn-group">
        <button class="sort-btn active" data-sort="az">A-Z</button>
        <button class="sort-btn" data-sort="count">📊 作品</button>
        <button class="sort-btn" data-sort="added">📅 加入</button>
        <button class="sort-btn" data-sort="birth">🎂 生日</button>
        <button class="sort-btn" data-sort="random">🎲 随机</button>
    </div>

    <div class="btn-group">
        <button class="filter-btn" data-type="img" data-val="has">✅ 头像</button>
        <button class="filter-btn" data-type="fav" data-val="yes">❤️ 已收</button>
        <button class="filter-btn" data-type="fav" data-val="no">🤍 未收</button>
    </div>

    <div class="btn-group">
        <button class="filter-btn" data-type="viewed" data-val="yes">👁️ 已看</button>
        <button class="filter-btn" data-type="viewed" data-val="no">🆕 未看</button>
    </div>

    <div class="btn-group">
        <button class="filter-btn" data-type="duplicate" data-val="yes">👥 重名</button>
    </div>

    <button id="openInNewTabBtn" style="background:#0066cc;">新标签打开</button>
    <button onclick="window.clearCache()" id="refreshBtn" style="margin-left:auto">🔄 刷新</button>
    <button onclick="window.closeActorWall()" style="background:#c33">关闭</button>
</header>
<div id="wall"></div>
`;
        wallOverlay.innerHTML = html;

        setTimeout(async () => {
            let ACTORS = actors;
            const SERVER = emby.server;
            const TOKEN = emby.token;
            const USER_ID = emby.userId;
            const PREF_KEY = `ActorWall_Viewed_${USER_ID}`;

            let favorites = new Set(ACTORS.filter(a => a.UserData?.IsFavorite).map(a => a.Id));
            let openInNewTab = JSON.parse(localStorage.getItem(STATE_KEY) || '{}').openInNewTab || false;

            let currentSort = { field: 'az', order: 'asc' };
            let currentFilters = { img: 'all', fav: 'all', viewed: 'all', duplicate: 'all', collection: 'all', libraries: null };
            let collectionActors = new Set();
            let availableLibraries = [];

            // 获取当前多选媒体库组合的 Cache Key
            const getLibsKey = () => (currentFilters.libraries && currentFilters.libraries.length) ? currentFilters.libraries.slice().sort().join(',') : 'all';

            // 点击外部关闭下拉框
            document.addEventListener('click', (e) => {
                const container = document.querySelector('.library-dropdown-container');
                const dropdown = document.getElementById('lib-dropdown');
                if (container && dropdown && !container.contains(e.target)) {
                    dropdown.style.display = 'none';
                }
            });

            document.getElementById('lib-filter-btn').onclick = () => {
                const dropdown = document.getElementById('lib-dropdown');
                dropdown.style.display = dropdown.style.display === 'none' ? 'block' : 'none';
            };

            // 获取全部媒体库
            async function initLibraries() {
                try {
                    const res = await fetch(`${SERVER}/emby/Users/${USER_ID}/Views`, { headers: { 'X-Emby-Token': TOKEN } });
                    const data = await res.json();
                    availableLibraries = data.Items || [];

                    // 初始化默认选中的库
                    if (!currentFilters.libraries) {
                        if (CONFIG.defaultLibraries.length > 0) {
                            currentFilters.libraries = availableLibraries
                                .filter(lib => CONFIG.defaultLibraries.includes(lib.Name))
                                .map(lib => lib.Id);
                        } else {
                            currentFilters.libraries = [];
                        }
                    }
                    renderLibraryDropdown();
                } catch(e) { console.error("加载媒体库失败", e); }
            }

            function renderLibraryDropdown() {
                const wrapper = document.getElementById('lib-options-wrapper');
                const btn = document.getElementById('lib-filter-btn');

                if(currentFilters.libraries.length === 0) {
                    btn.textContent = `📚 媒体库: 全部 (不限)`;
                } else {
                    btn.textContent = `📚 媒体库: 已选 ${currentFilters.libraries.length} 个`;
                }

                wrapper.innerHTML = availableLibraries.map(lib => {
                    const isChecked = currentFilters.libraries.includes(lib.Id) ? 'checked' : '';
                    return `
                    <label class="lib-checkbox-container">
                        <input type="checkbox" value="${lib.Id}" class="lib-checkbox" ${isChecked}> 
                        <span style="font-size: 14px;">${lib.Name}</span>
                    </label>`;
                }).join('');

                wrapper.querySelectorAll('.lib-checkbox').forEach(cb => {
                    cb.onchange = () => {
                        currentFilters.libraries = Array.from(wrapper.querySelectorAll('.lib-checkbox:checked')).map(n => n.value);
                        renderLibraryDropdown();
                        applyFilters();
                        loadCounts();
                    };
                });
            }

            // 云端同步
            function syncViewedToCloudBackground() {
                const prefsStr = Array.from(viewedActors).join(',');
                fetch(`${SERVER}/emby/DisplayPreferences/${PREF_KEY}?userId=${USER_ID}&client=web`, {
                    method: 'POST',
                    headers: { 'X-Emby-Token': TOKEN, 'Content-Type': 'application/json' },
                    body: JSON.stringify({ Id: PREF_KEY, CustomPrefs: { viewedIds: prefsStr } })
                }).catch(()=>{});
            }

            async function fetchViewedFromCloud() {
                try {
                    const res = await fetch(`${SERVER}/emby/DisplayPreferences/${PREF_KEY}?userId=${USER_ID}&client=web`, { headers: { 'X-Emby-Token': TOKEN } });
                    if (res.ok) {
                        const data = await res.json();
                        if (data?.CustomPrefs?.viewedIds) {
                            const ids = data.CustomPrefs.viewedIds.split(',').filter(Boolean);
                            let updated = false;
                            ids.forEach(id => {
                                if (!viewedActors.has(id)) { viewedActors.add(id); updated = true; }
                            });
                            if (updated) localStorage.setItem(VIEWED_KEY, JSON.stringify([...viewedActors]));
                        }
                    }
                } catch (e) {}
            }

            async function loadCollections() {
                try {
                    const res = await fetch(`${SERVER}/emby/Items?IncludeItemTypes=BoxSet&Recursive=true&SortBy=SortName&Fields=BasicSyncInfo`, { headers: { 'X-Emby-Token': TOKEN } });
                    const data = await res.json();
                    const sel = document.getElementById('collection-filter');
                    if (data.Items && data.Items.length > 0) {
                        data.Items.forEach(c => {
                            const opt = document.createElement('option');
                            opt.value = c.Id;
                            opt.textContent = c.Name;
                            sel.appendChild(opt);
                        });
                    } else {
                        sel.style.display = 'none';
                    }
                } catch(e) { console.error("加载合集失败", e); }
            }

            function getWallState() {
                return {
                    sort: currentSort,
                    filters: currentFilters,
                    search: document.getElementById('search').value,
                    collection: document.getElementById('collection-filter').value,
                    scroll: wallOverlay.scrollTop
                };
            }

            async function restoreWallState(state) {
                if (!state) return;

                currentSort = state.sort || currentSort;
                currentFilters = state.filters || currentFilters;
                document.getElementById('search').value = state.search || '';

                if (state.collection && state.collection !== 'all') {
                    document.getElementById('collection-filter').value = state.collection;
                    currentFilters.collection = state.collection;
                    try {
                        const res = await fetch(`${SERVER}/emby/Items?ParentId=${state.collection}&Fields=BasicSyncInfo`, {
                            headers: { 'X-Emby-Token': TOKEN }
                        });
                        const data = await res.json();
                        collectionActors = new Set(data.Items.filter(i => i.Type === 'Person').map(i => i.Id));
                    } catch(e) {}
                }

                updateSortUI();

                document.querySelectorAll('.filter-btn').forEach(btn => {
                    if (currentFilters[btn.dataset.type] === btn.dataset.val) {
                        btn.classList.add('active');
                    } else {
                        btn.classList.remove('active');
                    }
                });

                applyFilters();

                setTimeout(() => {
                    wallOverlay.scrollTop = state.scroll || 0;
                }, 50);
            }

            function updateSortUI() {
                document.querySelectorAll('.sort-btn').forEach(btn => {
                    const field = btn.dataset.sort;
                    let baseText = '';
                    switch (field) {
                        case 'az': baseText = 'A-Z'; break;
                        case 'count': baseText = '📊 作品'; break;
                        case 'added': baseText = '📅 加入'; break;
                        case 'birth': baseText = '🎂 生日'; break;
                        case 'random': baseText = '🎲 随机'; break;
                    }
                    if (currentSort.field === field && field !== 'random') {
                        btn.textContent = baseText + (currentSort.order === 'asc' ? ' 🔼' : ' 🔽');
                    } else {
                        btn.textContent = baseText;
                    }
                });
            }

            function render(list) {
                const wall = document.getElementById('wall');
                document.getElementById('countDisplay').textContent = list.length;
                const libsKey = getLibsKey();

                wall.innerHTML = list.map(actor => {
                    const tag = actor.ImageTags?.Primary;
                    const imgUrl = tag ? `${SERVER}/emby/Items/${actor.Id}/Images/Primary?maxHeight=500&tag=${tag}&X-Emby-Token=${TOKEN}` : '';
                    const isViewed = viewedActors.has(actor.Id);
                    const countStr = actor._counts && actor._counts[libsKey] !== undefined ? actor._counts[libsKey] : '...';

                    return `
                    <div class="card ${isViewed ? 'is-viewed' : ''}" data-id="${actor.Id}" title="左键浏览 / 右键标记未看">
                        ${isViewed ? '<div class="viewed-indicator">✓</div>' : ''}
                        <div class="tools"><button class="fav-btn">${favorites.has(actor.Id)?'❤️':'♡'}</button></div>
                        ${imgUrl ? `<img src="${imgUrl}" loading="lazy" onerror="this.outerHTML='<div class=placeholder>👤</div>'">` : '<div class="placeholder">👤</div>'}
                        <div class="name">${actor.Name} <small>(${countStr})</small></div>
                    </div>`;
                }).join('');
            }

            // 动态更新单一卡片，避免全局重绘闪烁
            function updateCardUI(id, count) {
                const card = document.querySelector(`.card[data-id="${id}"]`);
                if (card) {
                    if (count === 0) {
                        card.style.display = 'none'; // 直接隐藏 0 作品卡片
                        // 顺便更新顶部数字
                        const d = document.getElementById('countDisplay');
                        d.textContent = Math.max(0, parseInt(d.textContent) - 1);
                    } else {
                        const small = card.querySelector('.name small');
                        if (small) small.textContent = `(${count})`;
                    }
                }
            }

            function applyFilters() {
                const kw = document.getElementById('search').value.toLowerCase().trim();
                const libsKey = getLibsKey();

                let list = ACTORS.filter(a => {
                    if (kw && !a.Name.toLowerCase().includes(kw)) return false;
                    if (currentFilters.img === 'has' && !a.ImageTags?.Primary) return false;
                    if (currentFilters.fav === 'yes' && !favorites.has(a.Id)) return false;
                    if (currentFilters.fav === 'no' && favorites.has(a.Id)) return false;
                    if (currentFilters.viewed === 'yes' && !viewedActors.has(a.Id)) return false;
                    if (currentFilters.viewed === 'no' && viewedActors.has(a.Id)) return false;
                    if (currentFilters.collection && currentFilters.collection !== 'all' && !collectionActors.has(a.Id)) return false;
                    if (currentFilters.duplicate === 'yes' && (nameCounts[a.Name] || 0) <= 1) return false;

                    // 过滤当前媒体库条件下的0作品演员（前提是已经加载完该条件的数据）
                    if (a._counts && a._counts[libsKey] === 0) return false;

                    return true;
                });

                list.sort((a, b) => {
                    let diff = 0;
                    switch (currentSort.field) {
                        case 'count':
                            const cA = (a._counts && a._counts[libsKey] !== undefined) ? a._counts[libsKey] : 0;
                            const cB = (b._counts && b._counts[libsKey] !== undefined) ? b._counts[libsKey] : 0;
                            diff = cA - cB;
                            break;
                        case 'added': diff = (a.DateCreated ? new Date(a.DateCreated).getTime() : 0) - (b.DateCreated ? new Date(b.DateCreated).getTime() : 0); break;
                        case 'birth': diff = (a.PremiereDate ? new Date(a.PremiereDate).getTime() : 0) - (b.PremiereDate ? new Date(b.PremiereDate).getTime() : 0); break;
                        case 'random': return Math.random() - 0.5;
                        default: diff = a.Name.localeCompare(b.Name, 'zh-CN'); break;
                    }
                    return currentSort.order === 'asc' ? diff : -diff;
                });
                render(list);
            }

            // 右键取消已看状态
            document.getElementById('wall').oncontextmenu = (e) => {
                const card = e.target.closest('.card');
                if (!card) return;
                e.preventDefault();

                const id = card.dataset.id;
                if (viewedActors.has(id)) {
                    viewedActors.delete(id);
                    localStorage.setItem(VIEWED_KEY, JSON.stringify([...viewedActors]));
                    card.classList.remove('is-viewed');
                    const ind = card.querySelector('.viewed-indicator');
                    if (ind) ind.remove();

                    fetch(`${SERVER}/emby/Users/${USER_ID}/PlayedItems/${id}`, { method: 'DELETE', headers: { 'X-Emby-Token': TOKEN } }).catch(()=>{});
                    syncViewedToCloudBackground();
                }
            };

            document.getElementById('wall').onclick = async (e) => {
                const card = e.target.closest('.card');
                if (!card) return;
                const id = card.dataset.id;

                if (e.target.closest('.fav-btn')) {
                    const isFav = favorites.has(id);
                    const method = isFav ? 'DELETE' : 'POST';
                    isFav ? favorites.delete(id) : favorites.add(id);
                    e.target.closest('.fav-btn').textContent = favorites.has(id) ? '❤️' : '♡';

                    if (currentFilters.fav !== 'all') applyFilters();

                    fetch(`${SERVER}/emby/Users/${USER_ID}/FavoriteItems/${id}`, { method: method, headers: { 'X-Emby-Token': TOKEN } }).catch(()=>{});
                } else {
                    if (!viewedActors.has(id)) {
                        viewedActors.add(id);
                        localStorage.setItem(VIEWED_KEY, JSON.stringify([...viewedActors]));

                        card.classList.add('is-viewed');
                        if (!card.querySelector('.viewed-indicator')) {
                            card.insertAdjacentHTML('afterbegin', '<div class="viewed-indicator">✓</div>');
                        }

                        fetch(`${SERVER}/emby/Users/${USER_ID}/PlayedItems/${id}`, { method: 'POST', headers: { 'X-Emby-Token': TOKEN } }).catch(()=>{});
                        syncViewedToCloudBackground();
                    }

                    if (openInNewTab) {
                        window.open(`${SERVER}/web/index.html#!/item?id=${id}&serverId=${emby.serverId}`, '_blank');
                    } else {
                        setTimeout(() => {
                            const state = getWallState();
                            history.replaceState({ actorWall: true, wallState: state }, '');

                            let success = false;
                            if (window.appRouter && typeof window.appRouter.showItem === 'function') {
                                window.appRouter.showItem(id, emby.serverId);
                                success = true;
                            } else if (window.Emby && window.Emby.Page && window.Emby.Page.showItem) {
                                window.Emby.Page.showItem(id);
                                success = true;
                            } else {
                                location.hash = `#!/item?id=${id}&serverId=${emby.serverId}`;
                                window.dispatchEvent(new HashChangeEvent('hashchange'));
                                success = true;
                            }

                            if (success) {
                                setTimeout(() => window.closeActorWall(true), 200);
                            }
                        }, 50);
                    }
                }
            };

            document.getElementById('collection-filter').onchange = async (e) => {
                const cid = e.target.value;
                currentFilters.collection = cid;
                if (cid === 'all') { applyFilters(); return; }

                try {
                    const res = await fetch(`${SERVER}/emby/Items?ParentId=${cid}&Fields=BasicSyncInfo`, { headers: { 'X-Emby-Token': TOKEN } });
                    const data = await res.json();
                    collectionActors = new Set(data.Items.filter(i => i.Type === 'Person').map(i => i.Id));
                    applyFilters();
                } catch(err) {}
            };

            document.querySelectorAll('.sort-btn').forEach(btn => {
                btn.onclick = () => {
                    const field = btn.dataset.sort;
                    if (currentSort.field === field) {
                        currentSort.order = currentSort.order === 'asc' ? 'desc' : 'asc';
                    } else {
                        currentSort.field = field;
                        currentSort.order = (field === 'az' ? 'asc' : 'desc');
                    }
                    document.querySelectorAll('.sort-btn').forEach(b => b.classList.remove('active'));
                    btn.classList.add('active');
                    updateSortUI();
                    applyFilters();
                };
            });

            document.querySelectorAll('.filter-btn').forEach(btn => {
                btn.onclick = () => {
                    const type = btn.dataset.type;
                    const val = btn.dataset.val;

                    if (currentFilters[type] === val) {
                        currentFilters[type] = 'all';
                        btn.classList.remove('active');
                    } else {
                        currentFilters[type] = val;
                        document.querySelectorAll(`.filter-btn[data-type="${type}"]`).forEach(b => b.classList.remove('active'));
                        btn.classList.add('active');
                    }
                    applyFilters();
                };
            });

            // --- 核心修复 2：无刷新重载 ---
            window.clearCache = async () => {
                const btn = document.getElementById('refreshBtn');
                btn.textContent = '🔄 刷新中...';
                btn.disabled = true;

                try {
                    const res = await fetch(`${SERVER}/emby/Persons?PersonTypes=Actor&Limit=15000&SortBy=SortName&Fields=ImageTags,PremiereDate,DateCreated&EnableUserData=true&UserId=${USER_ID}`, {
                        headers: { 'X-Emby-Token': TOKEN }
                    });
                    const data = await res.json();

                    // 保留现有的 counts 缓存
                    const oldCountsMap = new Map(ACTORS.map(a => [a.Id, a._counts]));
                    ACTORS = (data.Items || []).map(a => ({...a, _counts: oldCountsMap.get(a.Id) || {}}));

                    // 重新计算重名和喜欢
                    nameCounts = {};
                    ACTORS.forEach(a => { nameCounts[a.Name] = (nameCounts[a.Name] || 0) + 1; });
                    favorites = new Set(ACTORS.filter(a => a.UserData?.IsFavorite).map(a => a.Id));

                    await fetchViewedFromCloud();
                    localStorage.setItem(cacheKey, JSON.stringify(ACTORS));

                    applyFilters();
                    loadCounts();
                } catch (err) {
                    alert("刷新失败，请检查网络");
                } finally {
                    btn.textContent = '🔄 刷新';
                    btn.disabled = false;
                }
            };

            // --- 核心修复 4：正确退出避免白屏 ---
            window.closeActorWall = (isNavigatingAway = false) => {
                const state = { openInNewTab };
                localStorage.setItem(STATE_KEY, JSON.stringify(state));
                if (wallOverlay) {
                    wallOverlay.remove();
                    wallOverlay = null;
                }
                isActorWallActive = false;

                // 只有在用户主动点击“关闭”按钮，并且仍在假路由下时才回退历史
                if (!isNavigatingAway && location.hash.includes('actor-wall')) {
                    if (history.length > 2) {
                        history.back();
                    } else {
                        location.hash = '#!/home';
                    }
                }
            };

            document.getElementById('openInNewTabBtn').onclick = function() {
                openInNewTab = !openInNewTab;
                this.style.background = openInNewTab ? '#009900' : '#0066cc';
                this.textContent = openInNewTab ? '✅ 新标签打开' : '新标签打开';
            };

            // 动态多媒体库拉取 Count
            async function loadCounts() {
                const libsKey = getLibsKey();
                const needLoad = ACTORS.filter(a => !a._counts || a._counts[libsKey] === undefined);

                for (let i = 0; i < needLoad.length; i += 8) {
                    // 如果中途切换了媒体库，立刻停止当前批次去执行新的
                    if (libsKey !== getLibsKey()) break;

                    const batch = needLoad.slice(i, i + 8);
                    await Promise.all(batch.map(async a => {
                        try {
                            if (!a._counts) a._counts = {};
                            let total = 0;

                            if (currentFilters.libraries.length === 0) {
                                // 查全部
                                const r = await fetch(`${SERVER}/emby/Items?PersonIds=${a.Id}&IncludeItemTypes=Movie,Episode&Recursive=true&Limit=0`, { headers: { 'X-Emby-Token': TOKEN } });
                                const d = await r.json();
                                total = d.TotalRecordCount || 0;
                            } else {
                                // 查多个指定的媒体库并相加
                                const promises = currentFilters.libraries.map(libId =>
                                    fetch(`${SERVER}/emby/Items?PersonIds=${a.Id}&ParentId=${libId}&IncludeItemTypes=Movie,Episode&Recursive=true&Limit=0`, { headers: { 'X-Emby-Token': TOKEN } }).then(r => r.json())
                                );
                                const results = await Promise.all(promises);
                                total = results.reduce((sum, res) => sum + (res.TotalRecordCount || 0), 0);
                            }
                            a._counts[libsKey] = total;
                            updateCardUI(a.Id, total);
                        } catch { a._counts[libsKey] = 0; updateCardUI(a.Id, 0); }
                    }));

                    if (i % 40 === 0) {
                        localStorage.setItem(cacheKey, JSON.stringify(ACTORS));
                    }
                }
                localStorage.setItem(cacheKey, JSON.stringify(ACTORS));
            }

            document.getElementById('search').oninput = debounce(applyFilters);

            if(openInNewTab) {
                const newTabBtn = document.getElementById('openInNewTabBtn');
                newTabBtn.style.background = '#009900';
                newTabBtn.textContent = '✅ 新标签打开';
            }

            await initLibraries();
            await loadCollections();
            await fetchViewedFromCloud();

            const saved = history.state && history.state.wallState;
            if (saved) {
                await restoreWallState(saved);
            } else {
                applyFilters();
            }

            loadCounts();
            updateSortUI();
        }, 100);
    }

    function init() {
        addTopButton();
        const observer = new MutationObserver(() => addTopButton());
        observer.observe(document.documentElement, { childList: true, subtree: true });

        window.addEventListener('hashchange', () => {
            // 如果路由发生了实质性变更且不在我们墙内，无声关闭不回退
            if (isActorWallActive && !location.hash.includes('actor-wall')) {
                window.closeActorWall(true);
            }
        });

        window.addEventListener('popstate', (e) => {
            if (e.state && e.state.actorWall) {
                showActorWall(true);
            } else if (isActorWallActive) {
                window.closeActorWall(true);
            }
        });
    }

    init();
})();