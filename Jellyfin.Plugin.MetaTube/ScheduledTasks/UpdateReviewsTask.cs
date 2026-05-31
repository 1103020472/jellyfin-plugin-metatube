using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;
#else
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class UpdateReviewsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

#if __EMBY__
    public UpdateReviewsTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<UpdateReviewsTask>();
        _libraryManager = libraryManager;
    }
#else
    public UpdateReviewsTask(ILogger<UpdateReviewsTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    public string Key => $"{Plugin.Instance.Name}UpdateReviews";

    public string Name => "Update Movie Reviews";

    public string Description => $"Updates and translates missing audience reviews for {Plugin.Instance.Name} movies.";

    public string Category => Plugin.Instance.Name;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            // 建议每周运行一次即可
            Type = TaskTriggerInfo.TriggerWeekly,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        // 若配置未开启获取评论功能，则跳过任务
        if (!Plugin.Instance.Configuration.ReviewSwitch)
        {
            _logger.Info("Review switch is disabled in configuration. Skipping UpdateReviewsTask.");
            return;
        }

        await Task.Yield();
        progress?.Report(0);

        // 获取所有由本插件刮削的视频
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
#if __EMBY__
            HasAnyProviderId = new[] { Plugin.Instance.Name },
            IncludeItemTypes = new[] { nameof(Movie) },
#else
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.Instance.Name, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        // 根据 MovieProvider 的规则，只有这两家提供评论
        var reviewProviders = new[] { "FANZA", "MGS" };
        bool enableTranslation = Plugin.Instance.Configuration.TranslationMode != TranslationMode.Disabled;

        foreach (var (idx, item) in items.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double)idx / items.Count * 100);

            try
            {
                // 获取 MetaTube 的提供商信息
                var pid = item.GetPid(Plugin.Instance.Name);
                if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
                    continue;

                if (!reviewProviders.Contains(pid.Provider))
                    continue;

                // 从远程获取最新评论
                var remoteReviews = await ApiClient.GetReviewsAsync(pid.Provider, pid.Id, false, cancellationToken);
                if (remoteReviews == null || !remoteReviews.Any())
                    continue;

                var currentOverview = item.Overview ?? string.Empty;
                var newReviews = new List<ReviewInfo>();

                // --------------------------------------------------------
                // 核心逻辑 1：通过 评论时间 作为 Key 过滤出新增评论
                // --------------------------------------------------------
                foreach (var review in remoteReviews)
                {
                    string commentTime = review.Date != DateTime.MinValue ? $"{review.Date:yyyy-MM-dd}" : "";
                    // 构造特征字符串
                    string keyStr = $"· {commentTime}";
                    // 如果原数据中找不到此时间标记，则判断为新增
                    if (!string.IsNullOrWhiteSpace(commentTime) && !currentOverview.Contains(keyStr))
                    {
                        newReviews.Add(review);
                    }
                }

                if (!newReviews.Any())
                    continue;

                _logger.Info("Found {0} new reviews for movie {1} ({2})", newReviews.Count, item.Name, pid.Id);
                
                // foreach (var review in newReviews)
                // {
                //     _logger.Info(
                //         "New review: [{0}] {1} {2} {3}",
                //         review.Date.ToString("yyyy-MM-dd"),
                //         review.Author,
                //         review.Title,
                //         review.Comment
                //     );
                // }

                // --------------------------------------------------------
                // 核心逻辑 2：构建“伪造”实体，仅翻译新增评论
                // --------------------------------------------------------
                var dummyInfo = new Jellyfin.Plugin.MetaTube.Metadata.MovieInfo
                {
                    Id = pid.Id,
                    Reviews = newReviews,
                    Summary = "" // 保持空值，这样 TranslationHelper 就只会处理评论
                };

                string appendedOverviewPart = string.Empty;

                if (enableTranslation)
                {
                    // 获取目标语言 (兼容项：优先尝试获取项语言，无则使用中文)
                    string targetLang = "zh-CN";
                    if (!string.IsNullOrWhiteSpace(item.GetPreferredMetadataLanguage()))
                    {
                        targetLang = item.GetPreferredMetadataLanguage();
                    }

                    // 调用已有逻辑：这会将翻译并排版好的 HTML 写回 dummyInfo.Summary 中
                    await TranslationHelper.TranslateAsync(dummyInfo, targetLang, cancellationToken);
                    
                    // 增加换行符转换，防止第三方翻译 API 返回 \n 导致前端显示无法换行
                    dummyInfo.Summary = ConvertNewlinesToBr(dummyInfo.Summary);
                    
                    appendedOverviewPart = dummyInfo.Summary ?? string.Empty;
                }
                else
                {
                    // 如果未开启翻译，按原逻辑只拼接不翻译
                    var sb = new StringBuilder();
                    foreach (var c in newReviews)
                    {
                        string author = string.IsNullOrWhiteSpace(c.Author) ? "匿名用户" : c.Author;
                        string commentTime = c.Date != DateTime.MinValue ? $"{c.Date:yyyy-MM-dd}" : "";
                        sb.Append($"👤 {author}");
                        if (!string.IsNullOrWhiteSpace(commentTime)) sb.Append($" · {commentTime}");
                        sb.Append("<br>");

                        int score = Math.Clamp(c.Score, 0, 5);
                        string filledStar = new string('⭐', score);
                        string emptyStar = new string('☆', 5 - score);
                        string starIcon = $"{filledStar}{emptyStar}";
                        string commentTitle = string.IsNullOrWhiteSpace(c.Title) ? "" : c.Title;
                        
                        if (!string.IsNullOrWhiteSpace(starIcon) || !string.IsNullOrWhiteSpace(commentTitle))
                            sb.Append($"<b>{starIcon} {commentTitle}</b><br>");

                        if (!string.IsNullOrWhiteSpace(c.Comment))
                            sb.Append($"{c.Comment}<br>");

                        sb.Append("<br><br>");
                    }
                    
                    var headerSb = new StringBuilder();
                    headerSb.Append("<br><br>");
                    headerSb.Append("————————————————————————————<br>");
                    headerSb.Append("<font color=\"#fce83a\" size=\"4\"><b>📝 观众评论</b></font><br>");
                    headerSb.Append("<br>");
                    appendedOverviewPart = headerSb.ToString() + sb.ToString();
                }

                if (string.IsNullOrWhiteSpace(appendedOverviewPart))
                    continue;

                // 去除重复的头部标记（如果已有评论记录）
                // 因为 TranslationHelper 会给 dummyInfo 自动加上带有 📝 观众评论 的 Header
                var headerPart = "<br><br>————————————————————————————<br><font color=\"#fce83a\" size=\"4\"><b>📝 观众评论</b></font><br><br>";
                
                if (currentOverview.Contains("📝 观众评论") && appendedOverviewPart.Contains(headerPart))
                {
                    // 替换为单个换行，使其自然追加到之前的评论下方
                    appendedOverviewPart = appendedOverviewPart.Replace(headerPart, "<br>"); 
                }

                // 拼接保存
                item.Overview = currentOverview + appendedOverviewPart;

#if __EMBY__
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
#else
                // Jellyfin 更新接口适配（如遇版本过高 API 变动，替换为 _libraryManager.UpdateItem 即可）
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
#endif
                _logger.Info("Successfully updated reviews for movie {0}", item.Name);
            }
            catch (Exception e)
            {
                _logger.Error("Update reviews for video {0} error: {1}", item.Name, e.Message);
            }
        }

        progress?.Report(100);
    }
    
    /// <summary>
    /// 将原生换行符转换为 HTML 的 <br> 标签，确保在前端正常换行显示
    /// </summary>
    private static string ConvertNewlinesToBr(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input; 
        }
        
        return input
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", "<br>");
    }
}