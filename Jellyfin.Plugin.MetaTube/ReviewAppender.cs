using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
#if __EMBY__
using MediaBrowser.Model.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube;

/// <summary>
/// 「按需追加番号评论」的公共实现。
///
/// 与 <c>UpdateReviewsTask</c> 的定时全库扫描共用同一份追加语义：
///   * 只读取现有 <c>Overview</c> → 追加新增评论 → 写回，绝不重新抓取/重写简介；
///   * 靠评论时间标记（<c>· yyyy-MM-dd HH:mm</c>）判重，已存在的评论不会再次翻译；
///   * 因此重复触发不会重复消耗翻译额度（没有新评论时一次翻译请求都不发）。
///
/// 触发入口有两个：
///   1. <c>UpdateReviewsTask</c> —— 定时全库增量；
///   2. <c>Api.ReviewService</c> —— Emby 详情页按钮按需触发单个番号。
/// </summary>
public static class ReviewAppender
{
    /// <summary>只有这两家提供评论（与 UpdateReviewsTask 保持一致）。</summary>
    public static readonly string[] SupportedProviders = { "FANZA", "MGS" };

    /// <summary>
    /// 2026-06-11 之后的评论用「精确到分」的时间标记，之前只用日期。
    /// 判重依赖这个格式，改动会导致历史评论被当成新增而重复翻译。
    /// </summary>
    private static readonly DateTime TimeThreshold = new(2026, 6, 11, 23, 59, 59);

    // 表头在「📝 观众评论」后只补一个 <br>：紧接着的第一条评论自己会再补一个 <br>
    // （作者行末尾），合起来正好是一个空行。这里多写一个就会变成两个空行。
    private const string ReviewHeader =
        "<br><br>————————————————————————————<br><font color=\"#fce83a\" size=\"4\"><b>📝 观众评论</b></font><br>";

    private const string ReviewHeaderMarker = "📝 观众评论";

    /// <summary>
    /// 「详情页按钮手动拉取过评论」的标记，写在 Overview 末尾。
    ///
    /// 用自闭合自定义标签：HTML 渲染时不会显示任何内容（未知元素且无子节点），
    /// 因此不会影响详情页观感；同时它是纯文本，Emby 不会在保存时把它剥掉。
    ///
    /// 用途：<c>FullReviewRefresh</c> 关闭时，定时任务只处理带此标记的条目。
    /// 注意判重必须放在 Overview 的「评论区」之外也成立——标记在末尾，且即便出现在
    /// 评论正文里也无害（只会让该条目被多刷新一次，不会造成数据损坏）。
    /// </summary>
    public const string ManualPullMarker = "<metatube-reviews/>";

    /// <summary>
    /// 单个条目追加评论的结果。
    /// </summary>
    public sealed class AppendResult
    {
        /// <summary>是否执行了追加（false 表示没有新增评论或该条目无有效 provider）。</summary>
        public bool Appended { get; set; }

        /// <summary>本次新增（判重后）的评论条数。</summary>
        public int NewReviewCount { get; set; }

        /// <summary>远程返回的评论总条数。</summary>
        public int TotalReviewCount { get; set; }

        /// <summary>评论被切成的翻译分片数（0 表示未翻译）。</summary>
        public int ChunkCount { get; set; }

        /// <summary>未执行追加的原因，用于日志与前端提示。</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 为一个条目抓取并追加新增评论（必要时翻译）。
    ///
    /// <paramref name="onProgress"/> 参数为
    /// <c>(stage, finishedChunks, chunkCount, newReviewCount)</c>；分片是并发翻译的，
    /// finishedChunks 只表示「已经有多少片回来了」，不是时间比例。可传 null。
    ///
    /// <paramref name="markAsManuallyPulled"/> 为 true 时表示这次是用户从详情页按钮
    /// 主动触发的，会在 Overview 里写入 <see cref="ManualPullMarker"/>，
    /// 使该条目之后被定时任务纳入刷新范围（见 <c>FullReviewRefresh</c>）。
    ///
    /// 注意：调用方需自行保证同一个条目不会被并发调用（见 <c>ReviewService</c> 的在途去重），
    /// 否则两次调用可能读到同一份 Overview 并各自追加，造成重复。
    /// </summary>
    public static async Task<AppendResult> AppendAsync(
        BaseItem item,
        ILogger logger,
        ILibraryManager libraryManager,
        Action<string, int, int, int> onProgress,
        CancellationToken cancellationToken,
        bool markAsManuallyPulled = false)
    {
        var result = new AppendResult();

        if (item == null)
        {
            result.Message = "条目不存在";
            return result;
        }

        var pid = item.GetPid(Plugin.Instance.Name);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            result.Message = "该条目没有 MetaTube 来源信息";
            return result;
        }

        if (!SupportedProviders.Contains(pid.Provider))
        {
            result.Message = $"来源 {pid.Provider} 不支持评论（仅 FANZA / MGS）";
            return result;
        }

        // ---- 1. 抓取远程评论 ----
        onProgress?.Invoke("fetching", 0, 0, 0);
        List<ReviewInfo> remoteReviews;
        try
        {
            remoteReviews = await ApiClient.GetReviewsAsync(pid.Provider, pid.Id, false, cancellationToken);
        }
        catch (Exception e) when (ApiErrorClassifier.IsNoReviews(e))
        {
            // 源站没有评论时服务端返回 404 info not found，这是正常结果而非故障。
            // 仍然打标记：用户主动拉过一次就应纳入刷新范围，现在没有不代表以后没有。
            logger?.Info("番号 {0}（{1}）源站暂无评论（服务端返回：{2}）", pid.Id, pid.Provider, e.Message);
            result.Message = "源站暂无评论";
            await TryMarkManuallyPulledAsync(
                item, logger, libraryManager, markAsManuallyPulled, cancellationToken);
            return result;
        }

        if (remoteReviews == null || !remoteReviews.Any())
        {
            result.Message = "远程没有评论";
            // 用户主动拉过一次就应纳入刷新范围：现在没有评论不代表以后没有。
            await TryMarkManuallyPulledAsync(
                item, logger, libraryManager, markAsManuallyPulled, cancellationToken);
            return result;
        }

        result.TotalReviewCount = remoteReviews.Count;

        // ---- 2. 按时间标记判重，只保留新增 ----
        var currentOverview = item.Overview ?? string.Empty;
        var newReviews = new List<ReviewInfo>();
        foreach (var review in remoteReviews)
        {
            if (review.Date == DateTime.MinValue)
                continue;

            var format = review.Date > TimeThreshold ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";
            var keyStr = $"· {review.Date.ToString(format)}";
            if (!currentOverview.Contains(keyStr))
                newReviews.Add(review);
        }

        if (!newReviews.Any())
        {
            result.Message = "没有新评论";
            // 手动拉取过（哪怕没拉到新评论）也要打标记，否则用户点了一次却仍是"未订阅"，
            // 定时任务永远不会刷新这个番号。
            await TryMarkManuallyPulledAsync(
                item, logger, libraryManager, markAsManuallyPulled, cancellationToken);
            return result;
        }

        result.NewReviewCount = newReviews.Count;
        logger?.Info("Found {0} new reviews for {1} ({2})", newReviews.Count, item.Name, pid.Id);

        // ---- 3. 构造只含评论的伪实体，交给 TranslationHelper ----
        var dummyInfo = new MovieInfo
        {
            Id = pid.Id,
            Reviews = newReviews,
            Summary = string.Empty // 保持空值，TranslationHelper 就只会处理评论
        };

        var enableTranslation = Plugin.Instance.Configuration.TranslationMode != TranslationMode.Disabled;
        string appendedOverviewPart;

        if (enableTranslation)
        {
            var targetLang = "zh-CN";
            if (!string.IsNullOrWhiteSpace(item.GetPreferredMetadataLanguage()))
                targetLang = item.GetPreferredMetadataLanguage();

            // 分片数在翻译开始前算不出来（SplitText 在 TranslationHelper 内部），
            // 这里先按配置估算用于进度显示，真实值由 TranslationHelper 回填。
            var estimatedChunks = EstimateChunkCount(newReviews);
            onProgress?.Invoke("translating", 0, estimatedChunks, newReviews.Count);

            var translated = await TranslationHelper.TranslateReviewsAsync(
                dummyInfo, targetLang, onProgress, cancellationToken);

            result.ChunkCount = translated.ChunkCount;
            // 第三方翻译接口可能返回 \n，前端 Overview 不认裸换行，必须转成 <br>
            appendedOverviewPart = ConvertNewlinesToBr(translated.Html ?? string.Empty);

            onProgress?.Invoke("translating", translated.ChunkCount, translated.ChunkCount, newReviews.Count);
        }
        else
        {
            // 未开启翻译：只拼接原文（与 UpdateReviewsTask 的原逻辑一致）
            appendedOverviewPart = BuildPlainHtml(newReviews);
        }

        if (string.IsNullOrWhiteSpace(appendedOverviewPart))
        {
            result.Message = "翻译结果为空";
            await TryMarkManuallyPulledAsync(
                item, logger, libraryManager, markAsManuallyPulled, cancellationToken);
            return result;
        }

        // ---- 4. 追加写回 Overview（不重抓简介）----
        onProgress?.Invoke("saving", result.ChunkCount, result.ChunkCount, newReviews.Count);

        // 已有评论区时去掉重复的表头，让新评论自然接在下面。
        // 上一条评论已经以 <br> 结尾，这里再补一个 <br> 正好凑成一个空行。
        if (currentOverview.Contains(ReviewHeaderMarker) && appendedOverviewPart.Contains(ReviewHeader))
            appendedOverviewPart = appendedOverviewPart.Replace(ReviewHeader, "<br>");

        item.Overview = EnsureManualPullMarker(currentOverview + appendedOverviewPart, markAsManuallyPulled);
#if __EMBY__
        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
#else
        await libraryManager.UpdateItemAsync(
            item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
#endif

        result.Appended = true;
        result.Message = $"新增 {result.NewReviewCount} 条评论";
        logger?.Info("Successfully appended reviews for {0}", item.Name);
        return result;
    }

    /// <summary>
    /// 手动拉取但本次没有新增评论时，只补一个标记并写回。
    /// 已经带标记则不写库，避免无意义的元数据更新。
    /// </summary>
    private static Task TryMarkManuallyPulledAsync(
        BaseItem item,
        ILogger logger,
        ILibraryManager libraryManager,
        bool markAsManuallyPulled,
        CancellationToken cancellationToken)
    {
        if (!markAsManuallyPulled)
            return Task.CompletedTask;

        var current = item.Overview ?? string.Empty;
        if (HasManualPullMarker(current))
            return Task.CompletedTask;

        item.Overview = EnsureManualPullMarker(current, true);
#if __EMBY__
        // Emby 的保存是同步 API，这里没有可 await 的调用。
        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
        logger?.Info("Marked {0} as manually pulled (no new reviews)", item.Name);
        return Task.CompletedTask;
#else
        logger?.Info("Marked {0} as manually pulled (no new reviews)", item.Name);
        return libraryManager.UpdateItemAsync(
            item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
#endif
    }

    /// <summary>该条目的 Overview 是否已带「手动拉取过评论」标记。</summary>
    public static bool HasManualPullMarker(string overview)
    {
        return !string.IsNullOrEmpty(overview)
               && overview.Contains(ManualPullMarker, StringComparison.Ordinal);
    }

    /// <summary>该条目是否应被定时任务的「非全量」模式处理。</summary>
    public static bool IsMarkedAsManuallyPulled(BaseItem item)
    {
        return HasManualPullMarker(item?.Overview);
    }

    /// <summary>
    /// 按需在 Overview 末尾补上手动拉取标记。
    ///
    /// 关键点：标记必须**始终位于末尾**。第一次手动拉取时标记写在末尾，之后再次追加评论
    /// 时新评论会接在标记后面，导致标记漂到正文中间（虽然 <see cref="HasManualPullMarker"/>
    /// 只做 Contains 判断、功能上仍然正确，但会让标记夹在评论之间，既难看又难排查）。
    /// 所以这里先把已有的标记全部剥掉，再统一追加到末尾，保证幂等且位置稳定。
    /// </summary>
    private static string EnsureManualPullMarker(string overview, bool markAsManuallyPulled)
    {
        var text = overview ?? string.Empty;

        if (!markAsManuallyPulled)
        {
            // 不写标记时也要剥掉旧标记吗？不要——定时任务路径必须保留用户已有标记，
            // 否则每次全量/增量刷新都会把这个番号从"已订阅"里踢出去。
            return text;
        }

        if (text.Contains(ManualPullMarker, StringComparison.Ordinal))
            text = text.Replace(ManualPullMarker, string.Empty, StringComparison.Ordinal);

        return text + ManualPullMarker;
    }

    /// <summary>粗略估算分片数，仅用于翻译开始前的进度提示。</summary>
    private static int EstimateChunkCount(IEnumerable<ReviewInfo> reviews)
    {
        var maxChunkSize = Math.Max(Configuration.MaxChunkSize, 1);
        var length = reviews.Sum(r =>
            (r.Comment?.Length ?? 0) + (r.Title?.Length ?? 0) + (r.Author?.Length ?? 0) + 32);
        return Math.Max(1, (int)Math.Ceiling(length / (double)maxChunkSize));
    }

    /// <summary>未开启翻译时使用的纯文本拼接（保持与原逻辑一致）。</summary>
    private static string BuildPlainHtml(IEnumerable<ReviewInfo> reviews)
    {
        var sb = new StringBuilder();
        foreach (var c in reviews)
        {
            var author = string.IsNullOrWhiteSpace(c.Author) ? "匿名用户" : c.Author;
            var commentTime = c.Date != DateTime.MinValue ? $"{c.Date:yyyy-MM-dd HH:mm}" : "";
            sb.Append($"👤 {author}");
            if (!string.IsNullOrWhiteSpace(commentTime)) sb.Append($" · {commentTime}");
            sb.Append("<br>");

            var score = Math.Clamp(c.Score, 0, 5);
            var starIcon = $"{new string('⭐', score)}{new string('☆', 5 - score)}";
            var commentTitle = string.IsNullOrWhiteSpace(c.Title) ? "" : c.Title;

            if (!string.IsNullOrWhiteSpace(starIcon) || !string.IsNullOrWhiteSpace(commentTitle))
                sb.Append($"<b>{starIcon} {commentTitle}</b><br>");

            if (!string.IsNullOrWhiteSpace(c.Comment))
                sb.Append($"{c.Comment}<br>");

            // 评论之间只留一个空行（理由同 TranslationHelper 的评论拼接）。
            sb.Append("<br>");
        }

        var header = new StringBuilder();
        header.Append("<br><br>");
        header.Append("————————————————————————————<br>");
        header.Append("<font color=\"#fce83a\" size=\"4\"><b>📝 观众评论</b></font><br>");
        header.Append("<br>");
        return header + sb.ToString();
    }

    /// <summary>
    /// 将原生换行符转换为 HTML 的 &lt;br&gt;，确保前端 Overview 正常换行。
    /// 与 MovieProvider / ActorProvider 里的同名实现保持一致。
    ///
    /// 额外把「空行」也规整掉：模型有时会把原文的一个空行改写成多个空行，
    /// 或者把已有 &lt;br&gt; 和裸换行混着输出，导致相邻 &lt;br&gt; 越滚越多。
    /// 这里统一压成最多一个空行（&lt;br&gt;&lt;br&gt;）。
    /// </summary>
    private static string ConvertNewlinesToBr(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var text = input
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", "<br>");

        // 兜底还原星级占位符（正常情况下 TranslationHelper 已经还原过了）。
        text = LeftoverStarTokenRegex.Replace(text, m =>
        {
            var score = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 5);
            return new string('⭐', score) + new string('☆', 5 - score);
        });

        return ExcessiveBrRegex.Replace(text, "<br><br>");
    }

    // 3 个以上连续的 <br>（含带空格 / 自闭合写法）压缩成一个空行。
    private static readonly Regex ExcessiveBrRegex =
        new(@"(?:<br\s*/?>){3,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 保险丝：万一有星级占位符 [[S4]] 没被还原（例如模型把它改写成别的形式），
    // 也要保证这种内部标记绝不会出现在用户可见的简介里。
    private static readonly Regex LeftoverStarTokenRegex =
        new(@"\[\[\s*S\s*(\d{1,2})\s*\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;
}
