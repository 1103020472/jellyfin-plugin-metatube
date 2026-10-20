using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
// Movie 类型在两个平台都在 ...Entities.Movies 命名空间下，
// 之前只在 __EMBY__ 里 import，导致 Jellyfin 配置编译不过（Movie 未定义）。
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using static Jellyfin.Plugin.MetaTube.Configuration.PluginConfiguration;
#if __EMBY__
using MediaBrowser.Model.Logging;

#else
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

/// <summary>
/// 多版本合并任务（自 StrmAssistant 的 MergeMultiVersionTask 迁移，只保留电影部分）。
///
/// 与原实现的关键差异：不依赖 tmdb/imdb/tvdb ProviderId 分组，而是按 MetaTube
/// ProviderId（值为 "FANZA:xxx" 这类「站点:番号」）分组——本插件的条目都带这个 ID，
/// 同一番号的多个版本（如 ABF-114 与 ABF-114-UC）MetaTube ID 完全相同。
///
/// 触发方式：
///   1. 媒体库扫描完成后自动执行（<see cref="MergeMultiVersionPostScanTask"/>，受总开关控制）；
///   2. 作为普通计划任务手动运行或配置定时触发。
///
/// 合并范围排除「已手动拆分」的条目：详情页「拆分版本」按钮会给拆出来的每个条目在
/// Overview 里写 <see cref="ManualSplitMarker.OverviewMarker"/>，带标记的条目不会参与
/// 自动合并（见 <see cref="ExcludeManuallySplit"/>），否则用户手动拆开的版本会被下一次
/// 扫库重新合并回去。
/// </summary>
public class MergeMultiVersionsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger _logger;

#if __EMBY__
    public MergeMultiVersionsTask(ILogManager logManager, ILibraryManager libraryManager,
        IProviderManager providerManager)
    {
        _logger = logManager.CreateLogger<MergeMultiVersionsTask>();
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }
#else
    public MergeMultiVersionsTask(ILogger<MergeMultiVersionsTask> logger, ILibraryManager libraryManager,
        IProviderManager providerManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }
#endif

    /// <summary>当前正在扫描的电影库（由 PostScan 钩子传入），手动运行时为 null。</summary>
    public static readonly AsyncLocal<CollectionFolder> CurrentScanLibrary = new();

    public string Key => $"{Plugin.Instance.Name}MergeMultiVersions";

    public string Name => "Merge Multi Versions";

    public string Description =>
        $"Merges multiple versions of the same {Plugin.Instance.Name} video (same MetaTube id) into one.";

    public string Category => Plugin.Instance.Name;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // 默认无定时触发；主要靠扫描后自动执行，手动运行也可用。
        yield break;
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        var configuration = Plugin.Instance.Configuration;
        if (!configuration.MergeMultiVersion)
        {
            _logger.Info("MergeMultiVersions is disabled in configuration. Skipping.");
            return;
        }

        var currentScanLibrary = CurrentScanLibrary.Value;
        CurrentScanLibrary.Value = null;

        _logger.Info("MergeMultiVersions started, scope: {0}, scanned library: {1}",
            configuration.MergeMoviesPreference, currentScanLibrary?.Name ?? "(all)");

        var parents = PrepareParents(configuration.MergeMoviesPreference, currentScanLibrary);
        if (parents.Length == 0)
        {
            _logger.Info("MergeMultiVersions: no movie libraries found. Complete.");
            progress?.Report(100);
            return;
        }

        var groups = FindDuplicateMovies(parents);
        if (groups.Count == 0)
        {
            _logger.Info("MergeMultiVersions: no duplicate versions found. Complete.");
            progress?.Report(100);
            return;
        }

        await Task.Yield();

        var current = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var movies = group.OfType<BaseItem>().ToArray();
#if __EMBY__
                _libraryManager.MergeItems(movies);
#else
                await _libraryManager.MergeItemsAsync(movies.ToList(), cancellationToken).ConfigureAwait(false);
#endif
                foreach (var item in movies)
                {
                    _logger.Info("MergeMultiVersions - merged: {0} - {1}", item.Name, item.Path);
                }
            }
            catch (Exception e)
            {
                _logger.Error("MergeMultiVersions - failed to merge group {0}: {1}", current + 1, e.Message);
            }

            current++;
            progress?.Report((double)current / groups.Count * 100);
        }

        _logger.Info("MergeMultiVersions complete, merged {0} group(s).", current);
        progress?.Report(100);
    }

    /// <summary>根据合并范围确定查询父级（电影库集合）。</summary>
    private long[] PrepareParents(MergeMoviesScopeOption scope, CollectionFolder currentScanLibrary)
    {
        // 允许参与合并的库名单；空列表 = 不限制（默认全选）。
        var allowedLibraries = Plugin.Instance.Configuration.GetMergeMoviesLibraries();

#if __EMBY__
        if (scope == MergeMoviesScopeOption.LibraryScope && currentScanLibrary != null)
        {
            // 当前扫描库不在名单内则不合并
            if (allowedLibraries.Count > 0 && !allowedLibraries.Contains(currentScanLibrary.Name))
            {
                _logger.Info("MergeMultiVersions - library \"{0}\" not in merge list. Skip.", currentScanLibrary.Name);
                return Array.Empty<long>();
            }

            return new[] { currentScanLibrary.InternalId };
        }

        var libraries = GetMovieLibraries()
            .Where(l => allowedLibraries.Count == 0 || allowedLibraries.Contains(l.Name))
            .ToList();
        _logger.Info("MergeMultiVersions - movie libraries: {0}", string.Join(", ", libraries.Select(l => l.Name)));
        return libraries
            .Select(l => l.InternalId)
            .ToArray();
#else
        if (scope == MergeMoviesScopeOption.LibraryScope && currentScanLibrary != null)
        {
            if (allowedLibraries.Count > 0 && !allowedLibraries.Contains(currentScanLibrary.Name))
            {
                _logger.Info("MergeMultiVersions - library \"{0}\" not in merge list. Skip.", currentScanLibrary.Name);
                return Array.Empty<long>();
            }

            return new[] { currentScanLibrary.Id };
        }

        var libraries = GetMovieLibraries()
            .Where(l => allowedLibraries.Count == 0 || allowedLibraries.Contains(l.Name))
            .ToList();
        _logger.Info("MergeMultiVersions - movie libraries: {0}", string.Join(", ", libraries.Select(l => l.Name)));
        return libraries
            .Select(l => l.Id)
            .ToArray();
#endif
    }

    private List<CollectionFolder> GetMovieLibraries()
    {
        return _libraryManager
            .GetItemList(new InternalItemsQuery
            {
#if __EMBY__
                IncludeItemTypes = new[] { nameof(CollectionFolder) }
#else
                IncludeItemTypes = new[] { BaseItemKind.CollectionFolder }
#endif
            })
            .OfType<CollectionFolder>()
#if __EMBY__
            .Where(l => l.CollectionType == CollectionType.Movies.ToString() || l.CollectionType is null)
#else
            .Where(l => l.CollectionType == CollectionType.movies || l.CollectionType is null)
#endif
            .ToList();
    }

    /// <summary>
    /// 找出同一 MetaTube ID 对应的多个条目（含已合并/未合并的全部组合）。
    /// 返回按 MetaTube ID 分组的条目列表，每组交给 MergeItems。
    /// </summary>
    private List<List<Movie>> FindDuplicateMovies(long[] parents)
    {
        var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            ParentIds = parents,
#if __EMBY__
            IncludeItemTypes = new[] { nameof(Movie) },
            HasAnyProviderId = new[] { Plugin.Instance.Name }
#else
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.Instance.Name, string.Empty } }
#endif
        }).OfType<Movie>().ToList();

        // MetaTube ProviderId 的值形如 "FANZA:xxx"（ProviderId.Parse 可拆出 Provider 与 Id）。
        // 直接用完整值分组：同站点同番号才会合并；不同站点刮削的同番号视为不同条目，避免误合并。
        var groups = allMovies
            .Where(m => !string.IsNullOrEmpty(m.GetProviderId(Plugin.Instance.Name)))
            .GroupBy(m => m.GetProviderId(Plugin.Instance.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(m => m.Id).Distinct().Count() > 1)
            .Select(g => g.ToList())
            // 手动拆分标记：见 ExcludeManuallySplit 注释。
            .Select(ExcludeManuallySplit)
            .Where(g => g.Count > 1)
            // CD part 检查：见 FilterByPartPolicy 注释。
            .Where(FilterByPartPolicy)
            .ToList();

        return groups;
    }

    /// <summary>
    /// 把「已手动拆分」的条目从合并范围里剔掉。
    ///
    /// 用户在详情页点「拆分版本」后，插件会给拆出来的每个条目在 Overview 末尾写上
    /// <see cref="ManualSplitMarker.OverviewMarker"/>。这些条目必须排除在自动合并之外，
    /// 否则下一次扫库（<see cref="MergeMultiVersionPostScanTask"/>，与手动运行同一个
    /// <see cref="Execute"/> 实现）又会把用户刚拆开的版本合回去。
    ///
    /// 规则是「只合并没有手动拆分标记的 item」：标记条目剔除后，
    /// 组内剩余不足 2 个条目时该组自然不会再合并。
    /// </summary>
    private List<Movie> ExcludeManuallySplit(List<Movie> group)
    {
        var kept = group.Where(m => !ManualSplitMarker.IsMarked(m)).ToList();
        if (kept.Count == group.Count)
            return kept;

        var skipped = group.Where(ManualSplitMarker.IsMarked).ToList();
        _logger.Info(
            "MergeMultiVersions - keep out {0} manually split item(s), {1} left to merge: {2}",
            skipped.Count, kept.Count, string.Join(" / ", skipped.Select(m => m.Name)));

        return kept;
    }

    /// <summary>
    /// CD part 合并策略（配置项 <see cref="PluginConfiguration.MergeMultiVersionWithParts"/>，默认关）：
    ///   * 关闭：组内任意条目带额外 CD part（如 -cd1/-cd2 拆分文件）时，整组跳过不合并；
    ///   * 打开：允许合并带 CD part 的组，但组内每个条目的 part 总数（1 + 额外 part 数）必须完全一致，
    ///     否则跳过该组——part 数不一致说明两边的文件组织不同，合并会破坏播放结构。
    /// </summary>
    private bool FilterByPartPolicy(List<Movie> group)
    {
        // 额外 CD part 数：直接取 BaseItem.AdditionalPartCount（Emby 4.8/4.9 SDK 均有该属性，
        // 而 GetAdditionalParts 需要 DtoOptions/User 参数，跨版本签名不一致）。
        var partCounts = group
            .Select(m => 1 + m.AdditionalPartCount)
            .ToList();

        var anyParts = partCounts.Any(c => c > 1);

        if (!anyParts)
        {
            // 整组都没有 CD part，直接放行。
            return true;
        }

        if (!Plugin.Instance.Configuration.MergeMultiVersionWithParts)
        {
            _logger.Info("MergeMultiVersions - skip group with CD part items (merge-with-parts is off): {0}",
                string.Join(" / ", group.Select(m => m.Name)));
            return false;
        }

        var uniform = partCounts.All(c => c == partCounts[0]);
        if (!uniform)
        {
            _logger.Info(
                "MergeMultiVersions - skip group with inconsistent part counts [{0}] (merge-with-parts requires equal counts): {1}",
                string.Join(",", partCounts), string.Join(" / ", group.Select(m => m.Name)));
            return false;
        }

        return true;
    }
}

/// <summary>
/// 媒体库扫描完成后的自动合并钩子：总开关打开时执行合并。
/// </summary>
public class MergeMultiVersionPostScanTask : ILibraryPostScanTask
{
    private readonly MergeMultiVersionsTask _task;

    public MergeMultiVersionPostScanTask(MergeMultiVersionsTask task) => _task = task;

    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return Plugin.Instance.Configuration.MergeMultiVersion
            ? _task.Execute(cancellationToken, progress)
            : Task.CompletedTask;
    }
}
