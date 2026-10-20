#if __EMBY__
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Jellyfin.Plugin.MetaTube.Api;

/// <summary>
/// 给「手动拆分出来的多个版本」打上 / 去掉「已手动拆分」标记。
///
/// 前置步骤由前端完成：先调 Emby 自己的
/// <c>POST /Videos/{Id}/AlternateSources/Delete</c> 把版本拆开，
/// 再带着拆出来的条目 Id 列表调本接口打标记（顺序不能反：拆分之后
/// 条目详情里的 MediaSources 就只剩自己一个源，前端拿不到全组 Id 了）。
///
/// 标记写在 Overview 末尾（见 <see cref="ManualSplitMarker"/>），
/// MergeMultiVersionsTask 会跳过带标记的条目。
/// </summary>
[Route("/MetaTube/Versions/SplitMark", "POST", Summary = "给手动拆分出来的版本打上/去掉「已手动拆分」标记")]
[Authenticated]
public class SplitVersionMark : IReturn<SplitVersionMarkResult>
{
    /// <summary>
    /// 条目 Id 列表，逗号分隔（如 "104236,104237"）。
    /// 声明成 string 而不是数组：Emby 的 ServiceStack 对空 body 的 POST 只从
    /// query string 绑定参数，字符串最稳，前端也不用拼重复 key。
    /// </summary>
    public string ItemIds { get; set; }

    /// <summary>true（默认）打标记；false 去掉标记（之后允许自动合并）。</summary>
    public bool Marked { get; set; } = true;
}

/// <summary>打 / 去标记的结果。</summary>
public class SplitVersionMarkResult
{
    /// <summary>ok（全部处理成功）/ partial（部分失败）/ invalid（参数或条目有问题）。</summary>
    public string Status { get; set; }

    /// <summary>给用户看的说明文字。</summary>
    public string Message { get; set; }

    /// <summary>本次真正改写了元数据的条目数（标记本来就在的不计）。</summary>
    public int ChangedCount { get; set; }

    /// <summary>成功处理的条目 Id。</summary>
    public string[] ItemIds { get; set; }

    /// <summary>找不到 / 处理失败的条目 Id。</summary>
    public string[] FailedItemIds { get; set; }
}
#endif
