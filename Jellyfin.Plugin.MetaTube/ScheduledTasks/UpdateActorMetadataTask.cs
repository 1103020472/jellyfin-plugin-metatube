using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Providers;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Model.Logging;

#else
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

/// <summary>
/// 补齐演员资料（概述 / 简介 / 出生日期 / 标签 / 国籍）。
///
/// 媒体服务器在刮削影片时只会把 provider id 和头像写到人物条目上
/// （Jellyfin: LibraryManager.SavePeopleMetadataAsync），并不会调用远程人物元数据提供者：
/// 唯一的自动人物刷新 PeopleValidator 用的是 MetadataRefreshMode.ValidationOnly，
/// 而 MetadataService.GetProviders 在该模式下会明确排除 IRemoteMetadataProvider。
/// 所以这里必须主动跑一次，否则演员的概述/出生日期永远是空的。
/// </summary>
public class UpdateActorMetadataTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

#if __EMBY__
    public UpdateActorMetadataTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<UpdateActorMetadataTask>();
        _libraryManager = libraryManager;
    }
#else
    public UpdateActorMetadataTask(ILogger<UpdateActorMetadataTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    public string Key => $"{Plugin.Instance.Name}UpdateActorMetadata";

    public string Name => "Update Actor Metadata";

    public string Description =>
        $"Fills actor overview, bio, birthday, tags and nationality from {Plugin.Instance.Name} server.";

    public string Category => Plugin.Instance.Name;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            // 建议每周跑一次即可（首次可手动运行补齐存量人物）。
            Type = TaskTriggerInfo.TriggerWeekly,
            DayOfWeek = DayOfWeek.Monday,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        await Task.Yield();

        // 取回所有人物条目：
        //  - Jellyfin 用平台自带的人物枚举 GetPeopleItems(InternalPeopleQuery)（返回 List<Person>）；
        //  - Emby 的 ILibraryManager 没有 GetPeopleItems/GetPerson，GetItemPeople 只返回 PersonInfo
        //    （不是条目，无法保存），所以按条目类型取 Person。
#if __EMBY__
        var allPersons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { nameof(Person) }
        }).OfType<Person>().ToList();
#else
        var allPersons = _libraryManager.GetPeopleItems(new InternalPeopleQuery()).ToList();
#endif

        var persons = allPersons
            .Where(person =>
            {
                var pid = person.GetPid(Plugin.Instance.Name);
                return !string.IsNullOrWhiteSpace(pid.Id) && !string.IsNullOrWhiteSpace(pid.Provider);
            })
            .ToList();

        _logger.Info("UpdateActorMetadata: {0}/{1} person(s) have a {2} provider id",
            persons.Count, allPersons.Count, Plugin.Instance.Name);

        if (allPersons.Count == 0)
        {
            // 出现这行说明人物枚举方式在你的版本上不适用，请把这行日志发给插件作者。
            _logger.Warn("UpdateActorMetadata: no person item found, person query may be incompatible");
        }

        // 分三个阶段处理：
        //  1. 逐个拉取演员资料，并决定简介要怎么处理（保留已有中文简介 / 待翻译）；
        //  2. 把需要翻译的简介合并成少量请求（多个演员一起请求 AI 翻译，省 token）；
        //  3. 逐个写回人物条目。
        var enableTranslation = Plugin.Instance.Configuration.TranslationMode.HasFlag(TranslationMode.Summary);

        var pending = new List<PendingActor>();
        var skippedLocked = 0;
        for (var i = 0; i < persons.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(persons.Count == 0 ? 0 : (double)i / persons.Count * 50);

            var person = persons[i];
            try
            {
                // 已锁定的人物不覆盖（尊重手动编辑）。
                if (person.IsLocked)
                {
                    skippedLocked++;
                    continue;
                }

                var pid = person.GetPid(Plugin.Instance.Name);
                if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
                    continue;

                var overview = person.Overview ?? string.Empty;

                // 概述为空 => 视为资料缺失，让服务端重新抓一次源站（lazy=false）。
                // AV-LEAGUE 的旧记录里没有 av-wiki 简介（Overview 里没有版本标记），
                // 也要强制重抓一次；抓到后标记会写进 Overview，之后不再强制抓源站。
                var missing = string.IsNullOrWhiteSpace(overview);
                var needRefetch = missing ||
                    (IsAvLeague(pid.Provider) && !overview.Contains(ActorProvider.OverviewMarker));

                var info = await ApiClient.GetActorInfoAsync(pid.Provider, pid.Id, !needRefetch, cancellationToken);

                // 简介：已经有（翻译过的）简介的人物不再重复翻译，实现幂等、省翻译 token。
                var existingBio = ActorProvider.ExtractBio(overview);
                if (!string.IsNullOrWhiteSpace(existingBio) && ActorProvider.IsBioTranslated(overview))
                {
                    // 保留已有的中文简介，避免被源站日文原文覆盖、也不重复翻译。
                    info.Summary = existingBio;
                    info.SummaryTranslated = true;
                }
                else
                {
                    // 优先用服务端最新抓到的原文；服务端缓存里没有时退回已有简介
                    // （例如上次翻译没开启，Overview 里还是日文原文）。
                    info.Summary = !string.IsNullOrWhiteSpace(info.Summary)
                        ? info.Summary
                        : existingBio ?? string.Empty;
                }

                pending.Add(new PendingActor(person, info, needRefetch));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Error("UpdateActorMetadata for {0} error: {1}", person.Name, e.Message);
            }
        }

        // 第二阶段：批量翻译简介（按目标语言分组，每组内的多个演员合并成一次请求）。
        if (enableTranslation)
        {
            var groups = pending
                .Where(p => !p.Info.SummaryTranslated && TranslationHelper.NeedsTranslation(p.Info.Summary))
                .GroupBy(p => ResolveTargetLanguage(p.Person))
                .ToList();

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var actors = group.Select(p => p.Info).ToList();
                try
                {
                    _logger.Info("UpdateActorMetadata: translate {0} actor bio(s) => {1}", actors.Count, group.Key);
                    await TranslationHelper.TranslateActorSummariesAsync(actors, group.Key, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.Error("UpdateActorMetadata: translate actor bio error: {0}", e.Message);
                }
            }
        }

        // 第三阶段：写回。
        var updated = 0;
        var forced = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(50 + (pending.Count == 0 ? 50 : (double)i / pending.Count * 50));

            var (person, info, refetched) = pending[i];
            try
            {
                if (!ActorProvider.ApplyActorInfo(person, info))
                {
                    // 服务端也没有可写入的内容（例如 Gfriends 只有图片），不写库。
                    continue;
                }

                _logger.Info("UpdateActorMetadata: {0} <= {1}:{2}{3}",
                    person.Name, info.Provider, info.Id, refetched ? "（已强制重新抓取源站）" : string.Empty);
                if (refetched) forced++;

#if __EMBY__
                _libraryManager.UpdateItem(person, person, ItemUpdateType.MetadataEdit, null);
#else
                await _libraryManager
                    .UpdateItemAsync(person, person, ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
#endif
                updated++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Error("UpdateActorMetadata for {0} error: {1}", person.Name, e.Message);
            }
        }

        progress?.Report(100);
        _logger.Info("UpdateActorMetadata complete: updated={0}, forcedRefetch={1}, skippedLocked={2}, total={3}",
            updated, forced, skippedLocked, persons.Count);
    }

    /// <summary>
    /// 已拉取到资料、等待批量翻译 / 写回的人物。
    /// </summary>
    private sealed record PendingActor(Person Person, Jellyfin.Plugin.MetaTube.Metadata.ActorInfo Info,
        bool Refetched);

    /// <summary>
    /// av-wiki 的简介是通过 AV-LEAGUE provider 合并进来的，只有它需要强制刷新一次。
    /// </summary>
    private static bool IsAvLeague(string provider)
    {
        return string.Equals(provider, "AV-LEAGUE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 简介翻译的目标语言：优先用条目的元数据语言，没有则用简体中文。
    /// </summary>
    private static string ResolveTargetLanguage(Person person)
    {
        var language = person.GetPreferredMetadataLanguage();
        return string.IsNullOrWhiteSpace(language) ? "zh-CN" : language;
    }
}
