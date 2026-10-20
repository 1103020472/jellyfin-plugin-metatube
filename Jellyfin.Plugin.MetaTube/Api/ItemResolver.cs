#if __EMBY__
using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MetaTube.Api;

/// <summary>
/// 把前端传来的条目 Id 解析成 <see cref="BaseItem"/>。
///
/// Emby Web 的 <c>item.Id</c> 有两种形态：
///   * 内部数字 Id（如 "84684"）—— 详情页最常见的形态，用 GetItemById(long)；
///   * GUID 字符串 —— 用 GetItemById(Guid)。
/// 两者都尝试，避免因形态不同而 500。
///
/// 抽成公共类是为了让 ReviewService / VersionService 共用同一套解析与去重键规则，
/// 否则同一条目在两个服务里会被当成两个不同的键。
/// </summary>
internal static class ItemResolver
{
    internal static BaseItem Resolve(ILibraryManager libraryManager, string rawId)
    {
        if (libraryManager == null || string.IsNullOrWhiteSpace(rawId))
            return null;

        var id = rawId.Trim();

        // 1. 内部数字 Id（Emby 4.x 详情页的 item.Id 常见形态）
        if (long.TryParse(id, out var numericId))
        {
            var byLong = libraryManager.GetItemById(numericId);
            if (byLong != null)
                return byLong;
        }

        // 2. GUID
        if (Guid.TryParse(id, out var guid))
            return libraryManager.GetItemById(guid);

        return null;
    }

    /// <summary>
    /// 条目的稳定键（内部数字 Id 优先）：前端每次可能传不同形态的 Id，
    /// 但同一个条目必须归一到同一个键，否则去重 / 状态查询会失效。
    /// </summary>
    internal static string Key(BaseItem item)
    {
        if (item == null)
            return string.Empty;

        return item.InternalId != 0 ? item.InternalId.ToString() : item.Id.ToString();
    }
}
#endif
