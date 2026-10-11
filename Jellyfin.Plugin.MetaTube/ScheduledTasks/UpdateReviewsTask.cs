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
            AncestorIds = new long[] { 9049L, 70509L, 3L, 53977L },
#else
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.Instance.Name, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        // 评论 provider 过滤、时间阈值判重等逻辑已移入 ReviewAppender，两个入口共用。
        foreach (var (idx, item) in items.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double)idx / items.Count * 100);

            try
            {
                // 追加逻辑与「详情页按需拉取」完全共用，保证行为一致：
                // 只追加新增评论、按时间标记判重、不重抓简介。
                await ReviewAppender.AppendAsync(item, _logger, _libraryManager, null, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.Error("Update reviews for video {0} error: {1}", item.Name, e.Message);
            }
        }

        progress?.Report(100);
    }
}