#if __EMBY__
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MetaTube;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Jellyfin.Plugin.MetaTube.Api;

/// <summary>
/// Emby 详情页「拆分版本」按钮打标记的后端接口。
///
/// 职责只有一件事：把 <see cref="ManualSplitMarker.OverviewMarker"/> 写进（或从）
/// 每个条目的 Overview 里。/「把版本拆开」那一步仍由前端直接调 Emby 自己的
/// <c>POST /Videos/{Id}/AlternateSources/Delete</c>——那是 Emby 内部实现（版本分组、
/// PresentationUniqueKey、媒体源归属），插件自己重写一遍只会引入不一致。
///
/// 这样拆分后的每个条目都带标记，<c>MergeMultiVersionsTask</c> 就会把它们排除在
/// 自动合并范围之外（含扫库后自动触发的合并链路）。
/// </summary>
public class VersionService : IService
{
    /// <summary>
    /// Emby 的 ServiceStack 容器对插件自定义服务的构造注入不保证可用，
    /// 因此这里不依赖构造函数注入，而是在服务解析时从应用宿主取依赖。
    /// </summary>
    private static ILibraryManager LibraryManager =>
        Plugin.Instance.ApplicationHost.Resolve<ILibraryManager>();

    private static ILogger Logger => Plugin.Instance.logger;

    private static readonly char[] IdSeparators = { ',', ';', ' ', '\t', '\r', '\n' };

    /// <summary>
    /// POST /emby/MetaTube/Versions/SplitMark?ItemIds=1,2&amp;Marked=true
    ///
    /// Emby 按 HTTP 动词分派到同名方法（Get/Post/Put/Delete），
    /// 所以这里必须叫 <c>Post</c> 而不是 <c>Any</c> —— 后者不会被调用。
    /// </summary>
    public object Post(SplitVersionMark request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ItemIds))
            return new SplitVersionMarkResult { Status = "invalid", Message = "缺少 ItemIds" };

        var marked = request.Marked;
        var handled = new List<string>();
        var failed = new List<string>();
        var changed = 0;

        // 去重：前端的 Id 列表可能同时包含主条目 Id 和源里的 ItemId
        var rawIds = request.ItemIds
            .Split(IdSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var rawId in rawIds)
        {
            var item = ItemResolver.Resolve(LibraryManager, rawId);
            if (item == null)
            {
                failed.Add(rawId);
                continue;
            }

            var key = ItemResolver.Key(item);
            try
            {
                if (ManualSplitMarker.Apply(item, marked))
                    changed++;

                handled.Add(key);
                Logger.Info("{0} manually split marker {1}: {2}",
                    marked ? "Added" : "Removed", marked ? "to" : "from", item.Name);
            }
            catch (Exception e)
            {
                failed.Add(rawId);
                Logger.Error("Failed to {0} manual split marker for {1}: {2}",
                    marked ? "add" : "remove", item.Name, e.Message);
            }
        }

        var action = marked ? "已标记" : "已取消标记";
        string status;
        string message;

        if (!handled.Any())
        {
            status = "invalid";
            message = $"没有一个条目处理成功（{string.Join(",", failed)}）";
        }
        else if (failed.Any())
        {
            status = "partial";
            message = $"{action} {handled.Count} 个版本，{failed.Count} 个失败（{string.Join(",", failed)}）";
        }
        else
        {
            status = "ok";
            message = marked
                ? $"{action} {handled.Count} 个版本，自动合并会跳过它们"
                : $"{action} {handled.Count} 个版本，之后允许自动合并";
        }

        return new SplitVersionMarkResult
        {
            Status = status,
            Message = message,
            ChangedCount = changed,
            ItemIds = handled.ToArray(),
            FailedItemIds = failed.ToArray()
        };
    }
}
#endif
