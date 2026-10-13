using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaTube.Extensions;
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
        var configuration = Plugin.Instance.Configuration;

        // ReviewSwitch 是总开关：刮削与定时任务都读它，关掉则一切评论相关逻辑都不跑。
        if (!configuration.ReviewSwitch)
        {
            _logger.Info("Review switch is disabled in configuration. Skipping UpdateReviewsTask.");
            return;
        }

        // 非全量模式下只刷新「手动拉取过评论」的番号，其余条目直接跳过，
        // 避免全库抓评论带来的长时间运行与翻译额度消耗。
        var fullRefresh = configuration.FullReviewRefresh;
        _logger.Info("UpdateReviewsTask started, full review refresh: {0}", fullRefresh);

        await Task.Yield();
        progress?.Report(0);

        // 获取所有由本插件刮削的视频
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
#if __EMBY__
            HasAnyProviderId = new[] { Plugin.Instance.Name },
            IncludeItemTypes = new[] { nameof(Movie) },
            AncestorIds = new long[] { 9049L, 70509L, 3L, 53977L },
#else
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.Instance.Name, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        // 非全量模式：先按 Overview 里的手动拉取标记过滤，减少后续无效请求。
        if (!fullRefresh)
        {
            var before = items.Count;
            items = items.Where(ReviewAppender.IsMarkedAsManuallyPulled).ToList();
            _logger.Info("Full review refresh is off, processing {0}/{1} manually pulled items.",
                items.Count, before);
        }

        // 过滤后可能一条都不剩（例如还没手动拉取过任何番号）：
        // 此时必须提前返回，否则下面的 (double)idx / items.Count 会除零。
        if (items.Count == 0)
        {
            _logger.Info("No items to update reviews for. Skipping.");
            progress?.Report(100);
            return;
        }

        // 评论 provider 过滤、时间阈值判重等逻辑已移入 ReviewAppender，两个入口共用。
        foreach (var (idx, item) in items.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double)idx / items.Count * 100);

            try
            {
                // 追加逻辑与「详情页按需拉取」完全共用，保证行为一致：
                // 只追加新增评论、按时间标记判重、不重抓简介。
                // 定时任务本身不算「手动拉取」，所以不写标记。
                await ReviewAppender.AppendAsync(
                    item, _logger, _libraryManager, null, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.Error("Update reviews for video {0} error: {1}", item.Name, e.Message);
            }
        }

        progress?.Report(100);
    }
}