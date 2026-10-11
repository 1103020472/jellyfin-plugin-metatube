#if __EMBY__
using System;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Jellyfin.Plugin.MetaTube.Api;

/// <summary>
/// 按需拉取某个条目评论的请求。
/// 立即返回（后台执行），进度通过 <see cref="ReviewsStatus"/> 查询。
/// </summary>
[Route("/MetaTube/Reviews/Pull", "POST", Summary = "拉取并翻译指定条目的新增评论")]
[Authenticated]
public class PullReviews : IReturn<PullReviewsResult>
{
    /// <summary>
    /// 条目 Id。用 string 而不是 Guid：Emby Web 前端在详情页拿到的
    /// <c>item.Id</c> 可能是内部数字 Id（如 84684），也可能是 GUID。
    /// 声明成 Guid 会让数字 Id 直接抛 "Unrecognized Guid format"。
    /// </summary>
    public string ItemId { get; set; }
}

/// <summary>发起拉取后的即时响应。</summary>
public class PullReviewsResult
{
    /// <summary>状态：started（已开始）/ running（已在跑）/ invalid（参数或条目有问题）。</summary>
    public string Status { get; set; }

    /// <summary>给用户看的说明文字。</summary>
    public string Message { get; set; }
}

/// <summary>查询拉取进度。</summary>
[Route("/MetaTube/Reviews/Status", "GET", Summary = "查询评论拉取进度")]
[Authenticated]
public class ReviewsStatus : IReturn<ReviewsStatusResult>
{
    /// <summary>条目 Id，含义同 <see cref="PullReviews.ItemId"/>。</summary>
    public string ItemId { get; set; }
}

/// <summary>进度响应。</summary>
public class ReviewsStatusResult
{
    /// <summary>none（从未触发）/ pending / running / done / failed。</summary>
    public string Status { get; set; }

    /// <summary>当前阶段：fetching / translating / saving。</summary>
    public string Stage { get; set; }

    /// <summary>判重后新增的评论条数。</summary>
    public int NewReviewCount { get; set; }

    /// <summary>远程返回的评论总条数。</summary>
    public int TotalReviewCount { get; set; }

    /// <summary>翻译分片总数。</summary>
    public int ChunkCount { get; set; }

    /// <summary>已完成的分片数。分片并发执行，该值不表示时间比例。</summary>
    public int FinishedChunks { get; set; }

    /// <summary>是否真的追加了内容。</summary>
    public bool Appended { get; set; }

    /// <summary>提示 / 错误信息。</summary>
    public string Message { get; set; }

    /// <summary>耗时毫秒（结束时才有）。</summary>
    public long ElapsedMs { get; set; }
}
#endif
