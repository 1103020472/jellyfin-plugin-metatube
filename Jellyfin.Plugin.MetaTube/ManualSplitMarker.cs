using MediaBrowser.Controller.Entities;
#if __EMBY__
using MediaBrowser.Controller.Library;
#endif

namespace Jellyfin.Plugin.MetaTube;

/// <summary>
/// 「已手动拆分版本」标记的读写。
///
/// 背景：<c>MergeMultiVersionsTask</c> 会把同一个 MetaTube 番号（MetaTube ProviderId 相同）
/// 的多份文件合并成一个条目的多个版本，扫库后还会自动再合并一次。用户在详情页手动
/// 把版本拆开后，如果不留记号，下一次自动合并又会把它们合回去。
///
/// 标记落在 Overview 末尾，用自闭合自定义标签 <c>&lt;metatube-split/&gt;</c>：
///   * HTML 渲染时不可见（未知元素且无子节点），不影响详情页观感；
///   * 是纯文本，Emby 保存元数据时不会剥掉它；
///   * 与既有的 <c>&lt;metatube-reviews/&gt;</c>（见 <see cref="ReviewAppender.ManualPullMarker"/>）
///     使用同一套约定，排查时一眼能认出是插件写的标记。
///
/// ⚠️ 已知局限：整个 Overview 重新刮削（ReplaceAllMetadata）会被 MovieProvider 覆盖，
/// 标记随之丢失，条目会在下一次扫描后被自动合并。这是与 &lt;metatube-reviews/&gt; 相同的
/// 既有取舍；重新点一次「拆分版本」即可再打上标记（拆分本身是幂等的）。
/// </summary>
public static class ManualSplitMarker
{
    /// <summary>
    /// 写在 Overview 末尾的标记。改动会导致历史标记失效（合并任务会重新合并这些条目）。
    /// </summary>
    public const string OverviewMarker = "<metatube-split/>";

    /// <summary>Overview 文本里是否带「已手动拆分」标记。</summary>
    public static bool IsMarked(string overview)
    {
        return !string.IsNullOrEmpty(overview)
               && overview.Contains(OverviewMarker, StringComparison.Ordinal);
    }

    /// <summary>条目是否被标记为「已手动拆分」——合并任务的排除条件。</summary>
    public static bool IsMarked(BaseItem item)
    {
        return item != null && IsMarked(item.Overview);
    }

    /// <summary>补上标记（已存在则不重复追加）。</summary>
    public static string WithMarker(string overview)
    {
        var text = overview ?? string.Empty;
        return IsMarked(text) ? text : text + OverviewMarker;
    }

    /// <summary>去掉标记（不存在则原样返回）。</summary>
    public static string WithoutMarker(string overview)
    {
        var text = overview ?? string.Empty;
        return IsMarked(text)
            ? text.Replace(OverviewMarker, string.Empty, StringComparison.Ordinal)
            : text;
    }

#if __EMBY__
    /// <summary>
    /// 给条目打上 / 去掉「已手动拆分」标记并落库。
    ///
    /// 返回是否真的改了库：标记本来就在（或本来就没有）时不写库，
    /// 避免对同一批条目反复点按钮产生无意义的元数据更新。
    ///
    /// 仅 Emby 实现：标记的写入入口是 Api/VersionService（Emby 详情页按钮），
    /// Jellyfin 侧没有对应入口，只需要能读（<see cref="IsMarked(BaseItem)"/>）。
    /// </summary>
    public static bool Apply(BaseItem item, bool marked)
    {
        if (item == null)
            return false;

        var before = item.Overview ?? string.Empty;
        var after = marked ? WithMarker(before) : WithoutMarker(before);

        if (string.Equals(before, after, StringComparison.Ordinal))
            return false;

        item.Overview = after;
        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
        return true;
    }
#endif
}
