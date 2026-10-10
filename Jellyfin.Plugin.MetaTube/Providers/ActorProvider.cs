using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
#if __EMBY__
using MediaBrowser.Model.Logging;

#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

public class ActorProvider : BaseProvider, IRemoteMetadataProvider<Person, PersonLookupInfo>, IHasOrder
{
    /// <summary>
    /// 人物 Overview 里简介段落的标签。定时任务靠它判断"已经有简介"，
    /// 已经有（中文）简介的人物不再重复翻译。
    /// </summary>
    internal const string BioLabel = "简介: ";

    /// <summary>
    /// 人物 Overview 的格式版本标记（HTML 注释，展示时不可见）。
    /// 演员资料的解析逻辑升级后，定时任务靠它判断该人物是否需要强制重抓一次源站
    /// （例如 av-wiki 简介是后加的内容，旧记录里没有），抓到后标记会写进 Overview，
    /// 之后就不再强制抓源站。升级解析逻辑时把这个数字 +1 即可全量刷新一次。
    /// </summary>
    internal const string OverviewMarker = "<!-- metatube-actor:2 -->";

    /// <summary>
    /// 紧跟在简介后面的标记（HTML 注释，展示时不可见）：表示这段简介已经翻译过。
    /// 定时任务靠它判断是否需要翻译，保证幂等、不重复消耗翻译 token。
    /// </summary>
    internal const string BioTranslatedMarker = "<!-- metatube-bio:zh -->";

#if __EMBY__
    public ActorProvider(ILogManager logManager) : base(logManager.CreateLogger<ActorProvider>())
#else
    public ActorProvider(ILogger<ActorProvider> logger) : base(logger)
#endif
    {
    }

    public async Task<MetadataResult<Person>> GetMetadata(PersonLookupInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Name);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            var firstResult = (await GetSearchResults(info, cancellationToken)).FirstOrDefault();
            if (firstResult != null) pid = firstResult.GetPid(Name);
        }

        Logger.Info("Get actor info: {0}", pid.ToString());

        // pid.Update 为 true 时表示"本次要强制回源站重抓"（由条目上的 provider id 携带，
        // 见 ProviderId.Update）。这正是媒体服务器点"刷新元数据"时会走到的路径：
        // 服务器只会带上条目里已有的 provider id，所以强制刷新必须靠这个标记传递。
        // 另外配置项 EnableForceRefetch 打开后，任何一次元数据刷新都强制回源站。
        // 标记是一次性的：抓完在下面写回 update: false，避免以后每次刷新都回源站。
        var forceRefetch = pid.Update == true || Configuration.EnableForceRefetch;
        if (forceRefetch) Logger.Info("Force refetch actor from source site: {0}", pid.ToString());

        var m = await ApiClient.GetActorInfoAsync(pid.Provider, pid.Id, !forceRefetch, cancellationToken);
        Logger.Info("Get actor info: {0} => {1} (生日 {2} / 出道 {3} / 三围 {4})",
            pid.ToString(), m.Name,
            m.Birthday.Year > 1 ? m.Birthday.ToString("yyyy-MM-dd") : "未知",
            m.DebutDate.Year > 1 ? m.DebutDate.ToString("yyyy-MM-dd") : "未知",
            m.Measurements);

        // 演员简介（av-wiki）原文是日文，翻译成中文后再写进 Overview。
        await TranslateActorBio(m, info.MetadataLanguage, cancellationToken);

        var result = new MetadataResult<Person>
        {
            Item = new Person
            {
                Name = m.Name,
                PremiereDate = GetRealBirthday(m.Birthday),
                ProductionYear = m.Birthday.Year > 1 ? m.Birthday.Year : null,
                Overview = FormatOverview(m)
            },
            HasMetadata = true
        };
        
        // lj-保存标签
        if (m.Tags != null)
        {
            foreach (var tag in m.Tags) result.Item.AddTag(tag);
        }

        // Set ProviderIdModel.
        // update: false —— 强制重抓标记已经消费掉，落库后下次刷新回归 lazy 抓取。
        // 只在本次确实是强制重抓时才写，避免平白改变已有条目的 provider id 字符串。
        result.Item.SetPid(Name, m.Provider, m.Id, update: forceRefetch ? false : pid.Update);

        // Set actor nationality.
        if (!string.IsNullOrWhiteSpace(m.Nationality))
            result.Item.ProductionLocations = new[] { m.Nationality };

        return result;
    }

    /// <summary>
    /// 把服务端返回的演员资料写入一个人物条目（补齐任务使用）。
    /// 注意：不会改名，不会清空已有的非空字段；返回是否真的写入了新内容。
    /// 简介由调用方决定（翻译后的、或保留的旧简介），这里只负责写进 Overview。
    /// </summary>
    internal static bool ApplyActorInfo(BaseItem item, ActorInfo a)
    {
        var changed = false;

        var overview = FormatOverview(a);
        if (!string.IsNullOrWhiteSpace(overview) &&
            !string.Equals(item.Overview, overview, StringComparison.Ordinal))
        {
            item.Overview = overview;
            changed = true;
        }

        // 只有拿到真实年月日时才写出生日期。
        var birthday = GetRealBirthday(a.Birthday);
        if (birthday != null)
        {
            if (item.PremiereDate != birthday)
            {
                item.PremiereDate = birthday;
                changed = true;
            }

            if (item.ProductionYear != birthday.Value.Year)
            {
                item.ProductionYear = birthday.Value.Year;
                changed = true;
            }
        }
        else if (a.Birthday.Year > 1 && item.ProductionYear != a.Birthday.Year)
        {
            // 只有年份（AV-LEAGUE 的占位日期），只写年份，避免凭空造出“1992-02-02”。
            item.ProductionYear = a.Birthday.Year;
            changed = true;
        }

        if (a.Tags != null)
        {
            foreach (var tag in a.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
            {
                if (item.Tags != null && item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    continue;

                item.AddTag(tag);
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(a.Nationality) && item.ProductionLocations == null)
        {
            item.ProductionLocations = new[] { a.Nationality };
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// AV-LEAGUE 部分演员只公布出生年份（例如 天ヶ瀬ゆか 的页面写的是「1992年（34歳）」），
    /// 服务端用 2月2日 02:02:02 做占位（见服务端 provider/av-league/av-league.go 的 parseDate）。
    /// 注意：该值存进服务端数据库后时间部分会被规整成 00:00:00，
    /// 所以只按「2月2日」判断，不能依赖时分秒。
    /// 这种占位日期不能当成真实生日写入，否则会凭空造出「1992-02-02」。
    /// </summary>
    internal static DateTime? GetRealBirthday(DateTime birthday)
    {
        if (birthday.Year <= 1) return null;
        if (birthday.Month == 2 && birthday.Day == 2) return null;
        return birthday;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        PersonLookupInfo info, CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Name);

        var searchResults = new List<ActorSearchResult>();
        if (string.IsNullOrWhiteSpace(pid.Id))
        {
            // Search actor by name.
            Logger.Info("Search for actor: {0}", info.Name);
            searchResults.AddRange(await ApiClient.SearchActorAsync(info.Name, pid.Provider, cancellationToken));
        }
        else
        {
            // Exact search.
            Logger.Info("Search for actor: {0}", pid.ToString());
            searchResults.Add(await ApiClient.GetActorInfoAsync(pid.Provider, pid.Id,
                pid.Update != true, cancellationToken));
        }

        var results = new List<RemoteSearchResult>();
        if (!searchResults.Any())
        {
            Logger.Warn("Actor not found: {0}", pid.Id);
            return results;
        }

        foreach (var m in searchResults)
        {
            var result = new RemoteSearchResult
            {
                Name = $"[{m.Provider}] {m.Name}",
                SearchProviderName = Name,
                ImageUrl = m.Images?.Any() == true
                    ? ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, m.Images.First(), 0.5, true)
                    : string.Empty
            };
            result.SetPid(Name, m.Provider, m.Id);
            results.Add(result);
        }

        return results;
    }

    private static string FormatOverview(ActorInfo a)
    {
        var aliases = a.Aliases?.Where(alias => !string.Equals(alias, a.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // AV-LEAGUE 只公布出生年份时，日期字段是占位值，这里退化成“生年”。
        var birthday = GetRealBirthday(a.Birthday);
        var birthText = birthday != null
            ? birthday.Value.ToString("yyyy年M月d日")
            : a.Birthday.Year > 1 ? $"{a.Birthday.Year}年" : string.Empty;

        // lj-替换为中文标签
        var info = new List<(string, string)>
        {
            ("別名", string.Join(", ", aliases ?? Enumerable.Empty<string>())),
            ("出生", birthText),
            ("三围", a.Measurements),
            ("罩杯", a.CupSize),
            ("身高", a.Height > 0 ? $"{a.Height}cm" : string.Empty),
            ("血型", !string.IsNullOrWhiteSpace(a.BloodType) ? $"{a.BloodType}型" : string.Empty),
            ("出道日", a.DebutDate.GetValidDateTime()?.ToString("yyyy年M月d日")),
            ("Twitter", a.Twitter),
            ("Instagram", a.Instagram)
        };

        var overview = string.Join("\n<br>\n",
            info.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Item2)).Select(kvp => $"{kvp.Item1}: {kvp.Item2}"));

        // 演员简介（服务端 summary 字段，来自 av-wiki，已翻译成中文）放在结构化信息之后。
        if (!string.IsNullOrWhiteSpace(a.Summary))
        {
            overview += $"\n<br>\n<br>\n{BioLabel}{ConvertNewlinesToBr(a.Summary.Trim())}";
            if (a.SummaryTranslated) overview += BioTranslatedMarker;
        }

        // 版本标记：定时任务靠它判断是否需要强制重抓一次源站（见 OverviewMarker）。
        return overview + "\n" + OverviewMarker;
    }

    /// <summary>
    /// 从人物 Overview 里取出已经写入的简介（没有则返回 null）。
    /// </summary>
    internal static string ExtractBio(string overview)
    {
        if (string.IsNullOrWhiteSpace(overview)) return null;

        var start = overview.IndexOf(BioLabel, StringComparison.Ordinal);
        if (start < 0) return null;
        start += BioLabel.Length;

        var end = overview.Length;
        foreach (var marker in new[] { BioTranslatedMarker, OverviewMarker })
        {
            var index = overview.IndexOf(marker, start, StringComparison.Ordinal);
            if (index >= 0 && index < end) end = index;
        }

        var bio = overview.Substring(start, end - start).Trim();
        return string.IsNullOrWhiteSpace(bio) ? null : bio;
    }

    /// <summary>
    /// 人物 Overview 里的简介是否已经翻译过。
    /// </summary>
    internal static bool IsBioTranslated(string overview)
    {
        return !string.IsNullOrWhiteSpace(overview) &&
               overview.Contains(BioTranslatedMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// 翻译演员简介（av-wiki 原文为日文）。只在还没翻译时翻译，失败时保留原文。
    /// </summary>
    private async Task TranslateActorBio(ActorInfo m, string language, CancellationToken cancellationToken)
    {
        if (!TranslationHelper.NeedsTranslation(m.Summary)) return;

        try
        {
            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await TranslationHelper.TranslateActorAsync(m, language, cancellationToken);
            Logger.Info("Translate actor bio end, cost:{0}ms: {1} => {2}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, m.Id, language);
        }
        catch (Exception e)
        {
            Logger.Error("Translate actor bio error: {0}", e.Message);
        }
    }

    private static string ConvertNewlinesToBr(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        return input
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", "<br>");
    }
}