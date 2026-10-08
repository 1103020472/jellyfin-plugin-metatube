using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Providers;
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
/// 补齐演员资料（概述 / 出生日期 / 标签 / 国籍）。
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
        $"Fills actor overview, birthday, tags and nationality from {Plugin.Instance.Name} server.";

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

        var updated = 0;
        var skippedLocked = 0;
        var forced = 0;
        for (var i = 0; i < persons.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(persons.Count == 0 ? 100 : (double)i / persons.Count * 100);

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

                // 概述为空 => 视为资料缺失，让服务端重新抓一次源站（lazy=false），
                // 这样即使服务端缓存里存的是旧的不完整记录也能被刷新；
                // 概述已有内容的人走 lazy=true，直接读服务端缓存，不访问源站。
                var missing = string.IsNullOrWhiteSpace(person.Overview);

                var info = await ApiClient.GetActorInfoAsync(pid.Provider, pid.Id, !missing, cancellationToken);

                if (!ActorProvider.ApplyActorInfo(person, info))
                {
                    // 服务端也没有可写入的内容（例如 Gfriends 只有图片），不写库。
                    continue;
                }

                _logger.Info("UpdateActorMetadata: {0} <= {1}:{2}{3}",
                    person.Name, info.Provider, info.Id, missing ? "（已强制重新抓取源站）" : string.Empty);
                if (missing) forced++;

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
}
