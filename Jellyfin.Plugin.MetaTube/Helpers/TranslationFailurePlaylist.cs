#if __EMBY__
using System;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 把「评论翻译失败」的条目记进一个专用的 Emby 播放列表，便于事后发现与手动处理。
///
/// 背景：翻译是后台进行的（刮削时顺带、或定时任务），失败时异常被调用方记录日志后吞掉。
/// 结果是影片看起来「元数据都刮好了、只是没有评论译文」，不翻日志根本察觉不到。
/// 播放列表是一个在 Emby 各客户端都天然可见的载体，因此用它当失败清单。
///
/// 设计取舍：
///   * **只增不减**。按用户要求完全手动管理——翻译后来成功了也不自动移出，
///     插件不擅自改动列表内容。
///   * 同一个番号重复翻译失败不会重复加入（先查重，再加）。
///   * 播放列表按需自动创建，归属第一个管理员账号（后台任务没有登录用户上下文）。
///   * 加入失败绝不能影响主流程：所有异常都在这里吞掉并记日志，
///     「记录失败」本身失败不应该让翻译流程也失败。
/// </summary>
public static class TranslationFailurePlaylist
{
    /// <summary>失败清单的播放列表名称。</summary>
    public const string PlaylistName = "【插件】翻译失败";

    /// <summary>
    /// 取「翻译失败」播放列表；不存在则创建。任何异常都返回 null 并记日志。
    /// </summary>
    private static Playlist GetOrCreatePlaylist(ILibraryManager libraryManager, ILogger logger,
        out User createOwner)
    {
        createOwner = null;

        var playlistManager = ResolvePlaylistManager(logger);
        if (playlistManager == null)
            return null;

        var existing = FindPlaylist(libraryManager);
        if (existing != null)
            return existing;

        // 后台流程没有登录用户，播放列表必须挂在某个用户名下：优先第一个管理员。
        var owner = GetFirstAdmin(ResolveUserManager(logger))
                    ?? GetFirstUser(ResolveUserManager(logger));

        if (owner == null)
        {
            logger?.Warn("无法创建「{0}」播放列表：服务器上没有任何用户。", PlaylistName);
            return null;
        }

        try
        {
            // CreatePlaylist 返回的只是 Id/Name 结果对象，拿不到实体，
            // 所以创建后重新按名字查一次。
            var created = playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = PlaylistName,
                MediaType = MediaType.Video,
                User = owner,
                ItemIdList = Array.Empty<long>(),
                IsPublic = false
            }).GetAwaiter().GetResult();

            createOwner = owner;

            var playlist = FindPlaylist(libraryManager);
            if (playlist == null)
            {
                logger?.Warn("「{0}」播放列表创建后未能取回（created id: {1}）。", PlaylistName, created?.Id);
                return null;
            }

            logger?.Info("已创建「{0}」播放列表，归属用户 {1}。", PlaylistName, owner.Name);
            return playlist;
        }
        catch (Exception e)
        {
            logger?.Error("创建「{0}」播放列表失败：{1}", PlaylistName, e.Message);
            return null;
        }
    }

    private static Playlist FindPlaylist(ILibraryManager libraryManager)
    {
        return libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { nameof(Playlist) },
            Recursive = true
        }).OfType<Playlist>()
            .FirstOrDefault(p => string.Equals(p.Name, PlaylistName, StringComparison.Ordinal));
    }

    private static IPlaylistManager ResolvePlaylistManager(ILogger logger)
    {
        try
        {
            var manager = Plugin.Instance.ApplicationHost.Resolve<IPlaylistManager>();
            if (manager == null)
                logger?.Warn("无法解析 IPlaylistManager，跳过失败清单记录。");
            return manager;
        }
        catch (Exception e)
        {
            logger?.Warn("解析 IPlaylistManager 失败：{0}", e.Message);
            return null;
        }
    }

    private static IUserManager ResolveUserManager(ILogger logger)
    {
        try
        {
            return Plugin.Instance.ApplicationHost.Resolve<IUserManager>();
        }
        catch (Exception e)
        {
            logger?.Warn("解析 IUserManager 失败：{0}", e.Message);
            return null;
        }
    }

    /// <summary>取第一个管理员。用带过滤条件的查询，避免拉全量用户列表（IUserManager.Users 已过时）。</summary>
    private static User GetFirstAdmin(IUserManager userManager)
    {
        return userManager?.GetUserList(new UserQuery { IsAdministrator = true, Limit = 1 })
            ?.FirstOrDefault();
    }

    /// <summary>取任意一个可用用户（没有任何管理员时的兜底）。</summary>
    private static User GetFirstUser(IUserManager userManager)
    {
        return userManager?.GetUserList(new UserQuery { Limit = 1 })?.FirstOrDefault();
    }

    /// <summary>
    /// 把一个条目加入「翻译失败」清单。重复调用不会产生重复项。
    /// 失败只记日志，不抛出。
    /// </summary>
    public static void Add(BaseItem item, ILibraryManager libraryManager, ILogger logger)
    {
        if (item == null || libraryManager == null)
            return;

        try
        {
            var playlist = GetOrCreatePlaylist(libraryManager, logger, out var createOwner);
            if (playlist == null)
                return;

            var user = createOwner ?? GetFirstAdmin(ResolveUserManager(logger))
                       ?? GetFirstUser(ResolveUserManager(logger));
            if (user == null)
            {
                logger?.Warn("无法把 {0} 加入「{1}」：没有可用用户。", item.Name, PlaylistName);
                return;
            }

            // Emby 内部一律用 long 型 InternalId；为 0 说明实体还没落库，无法加入。
            var itemId = item.InternalId;
            if (itemId == 0)
            {
                logger?.Warn("无法把 {0} 加入「{1}」：条目没有 InternalId（尚未入库？）。",
                    item.Name, PlaylistName);
                return;
            }

            var playlistManager = ResolvePlaylistManager(logger);
            if (playlistManager == null)
                return;

            // 先自己判重，不完全依赖 skipDuplicates。
            // 实测 Emby 的播放列表 HTTP 接口对重复加入是不去重的（同一 Id 会加出两条），
            // 而失败清单是只增不减、会长期累积的列表，一旦重复就很难收拾，
            // 所以这里显式检查一遍：已经在该列表里就不再加入。
            if (IsAlreadyInPlaylist(playlistManager, playlist, itemId, user, logger))
            {
                logger?.Info("{0} 已在「{1}」播放列表中，跳过。", item.Name, PlaylistName);
                return;
            }

            // skipDuplicates: true 作为第二道保险。
            playlistManager.AddToPlaylist(playlist, new[] { itemId }, true, user, CancellationToken.None)
                .GetAwaiter().GetResult();

            logger?.Info("已把 {0} 加入「{1}」播放列表（评论翻译失败）。", item.Name, PlaylistName);
        }
        catch (Exception e)
        {
            // 记录失败不应该让翻译流程也失败。
            logger?.Error("加入「{0}」播放列表失败：{1}", PlaylistName, e.Message);
        }
    }

    /// <summary>
    /// 该条目是否已经在播放列表里。用 Emby 自己的 GetAddToPlaylistInfo 判断，
    /// 它直接返回 ContainsDuplicates，比手工遍历子项可靠。
    /// </summary>
    private static bool IsAlreadyInPlaylist(IPlaylistManager playlistManager, Playlist playlist,
        long itemId, User user, ILogger logger)
    {
        try
        {
            var info = playlistManager.GetAddToPlaylistInfo(playlist, new[] { itemId }, user);
            return info != null && info.ContainsDuplicates;
        }
        catch (Exception e)
        {
            // 判重失败时按「不在列表里」处理：宁可多一条重复项，也不要漏记失败。
            logger?.Warn("检查「{0}」播放列表重复项失败：{1}", PlaylistName, e.Message);
            return false;
        }
    }
}
#endif
