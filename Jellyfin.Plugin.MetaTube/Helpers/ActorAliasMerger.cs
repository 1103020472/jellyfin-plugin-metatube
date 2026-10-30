using System.Collections.Concurrent;
using System.Text;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Providers;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 合并后的一张头像。必须带上它所属的记录（provider + id）：
/// 图片地址是 <c>/v1/images/primary/{provider}/{id}?url=...</c>，别名带来的图
/// 必须用别名那条记录的 provider/id 去拼，不能一律用主名的。
/// </summary>
public sealed class ActorImageRef
{
    public string Provider { get; init; }

    public string Id { get; init; }

    public string Url { get; init; }
}

/// <summary>
/// 演员资料合并结果（主记录 + 替换表里的别名数据）。
/// </summary>
public sealed class MergedActorInfo
{
    /// <summary>
    /// 合并后的详情。<see cref="ProviderInfo.Provider"/> / <see cref="ProviderInfo.Id"/> /
    /// <see cref="ActorSearchResult.Name"/> 永远是主记录的值，不会被别名覆盖。
    /// </summary>
    public ActorInfo Info { get; init; }

    /// <summary>
    /// 合并去重后的头像：主记录的图在前（av-league 优先），别名按表内顺序接在后面。
    /// </summary>
    public IReadOnlyList<ActorImageRef> Images { get; init; } = Array.Empty<ActorImageRef>();

    /// <summary>真正补进数据（至少 1 个字段或 1 张图）的别名，按尝试顺序。</summary>
    public IReadOnlyList<string> MatchedAliases { get; init; } = Array.Empty<string>();

    /// <summary>被别名补上的字段名，仅用于日志/诊断。</summary>
    public IReadOnlyList<string> FilledFields { get; init; } = Array.Empty<string>();

    public bool HasAliasData => MatchedAliases.Count > 0;
}

/// <summary>
/// 演员别名数据合并。
///
/// 背景：替换表里「常用名 = 别名1, 别名2」只被用来把源站返回的别名换成常用名，
/// 演员详情/头像依然只取主记录（Person 身上的 pid）那一条。同一个人在源站往往还有
/// 几条以旧艺名/罗马字收录的记录，字段互有残缺，只抓主记录就会漏字段、漏头像。
///
/// 这里做的事：以**当前演员名**为身份去替换表里查它的别名，按表内顺序逐个到源站
/// 搜一遍（最多 <see cref="DefaultMaxAttempts"/> 个），把确实补到东西的记录合并进来：
///   * 字段：主名优先，主名空才用别名，多个别名按表内顺序取第一个非空；
///     生日/出道日的「2月2日」「只有年份」是 AV-LEAGUE 的占位值，视作空。
///   * 別名（Aliases）：**不取**别名记录的 aliases 字段，只保留主记录的原文别名。
///   * 头像：合并去重，主记录的图在前、别名的图在后；每张图记住自己的来源记录。
///
/// 注意几处刻意的保守设计（都是为了防止把「陌生人」的数据合进来）：
///   * 只认替换表，不拿主记录自带的 aliases 当候选（用户要求，表里的名字是人工核过的）；
///   * 搜索结果必须二次校验：取回详情后，名字或别名归一化后要能对上，否则丢弃；
///   * 同一个别名最多核对 <see cref="MaxCandidatesPerAlias"/> 个候选，避免无限翻页；
///   * 单个别名 5 秒超时、整轮 12 秒预算、条数上限全部可配，失败只降级不报错。
/// </summary>
public static class ActorAliasMerger
{
    /// <summary>默认最多尝试几个别名（表里挂了很多个别名时的兜底）。</summary>
    public const int DefaultMaxAttempts = 6;

    /// <summary>默认最多凑几个「有数据」的别名。</summary>
    public const int DefaultMaxHits = 3;

    /// <summary>单个别名的搜索 / 取详情超时。</summary>
    private static readonly TimeSpan AliasTimeout = TimeSpan.FromSeconds(5);

    /// <summary>整轮合并的时间预算：超了就不再往下试，宁可少合并也不要拖死演员页。</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(12);

    /// <summary>合并结果缓存时长（演员资料变化极慢，页面来回切换不该每次都重查）。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private const int CacheCapacity = 2000;

    /// <summary>一个别名最多核对几个候选记录（每个候选一次 lazy 取详情，很便宜）。</summary>
    private const int MaxCandidatesPerAlias = 3;

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private sealed class CacheEntry
    {
        public MergedActorInfo Result;
        public long LastAccessTicks;
    }

    /// <summary>
    /// 把替换表里的别名数据合并进主记录。
    /// </summary>
    /// <param name="main">主记录（Person 的 pid 对应的那条）。不会被别名覆盖身份字段。</param>
    /// <param name="personName">当前演员名，用来在替换表里定位它的别名。</param>
    /// <param name="log">可选的 Info 级日志回调（各调用方的 logger 类型不同，这里只收字符串）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<MergedActorInfo> MergeAsync(ActorInfo main, string personName,
        Action<string> log, CancellationToken cancellationToken)
    {
        main ??= new ActorInfo();

        var name = (personName ?? string.Empty).Trim();
        var images = new List<ActorImageRef>();
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddImages(main, images, seenImages);

        var mainOnly = new MergedActorInfo
        {
            Info = main,
            Images = images
        };

        var options = Plugin.Instance.Configuration;
        if (!options.EnableActorAliasMerge) return mainOnly;
        if (string.IsNullOrWhiteSpace(main.Provider) || string.IsNullOrWhiteSpace(main.Id)) return mainOnly;

        // 替换表是内存里的解析结果（已按原文 memo），查不到直接返回，一个请求都不发。
        var aliases = ResolveAliases(options.GetActorSubstitutionMap(), name, main.Name);
        if (aliases.Count == 0) return mainOnly;

        // 缓存键带上了候选别名本身：替换表改了别名，键就变了，不会读到旧结果。
        var cacheKey = string.Join("\u001f", new[]
        {
            main.Provider, main.Id, name, string.Join(",", aliases)
        });
        if (TryGetCached(cacheKey, out var cached)) return Snapshot(cached);

        var maxAttempts = Clamp(options.ActorAliasMergeMaxAttempts, DefaultMaxAttempts, 1, 20);
        var maxHits = Clamp(options.ActorAliasMergeMaxHits, DefaultMaxHits, 1, 10);
        var deadline = DateTime.UtcNow + TotalBudget;

        var matched = new List<string>();
        var filled = new List<string>();
        var attempts = 0;

        foreach (var alias in aliases)
        {
            if (matched.Count >= maxHits) break;
            if (attempts >= maxAttempts)
            {
                log?.Invoke(string.Format(
                    "[MetaTube] 别名合并：{0} 已达尝试上限 {1} 个，剩余 {2} 个别名跳过。",
                    name, maxAttempts, aliases.Count - attempts));
                break;
            }

            if (DateTime.UtcNow > deadline)
            {
                log?.Invoke(string.Format(
                    "[MetaTube] 别名合并：{0} 查询预算 {1:F0}s 用尽，剩余别名跳过。",
                    name, TotalBudget.TotalSeconds));
                break;
            }

            attempts++;
            cancellationToken.ThrowIfCancellationRequested();

            var other = await ResolveAsync(alias, main, log, cancellationToken).ConfigureAwait(false);
            if (other == null) continue;

            // 别名指回同一条记录：数据已经在主记录里了，不算命中。
            if (SameRecord(main, other)) continue;

            var filledBefore = filled.Count;
            MergeFields(main, other, filled);
            var imagesBefore = images.Count;
            AddImages(other, images, seenImages);

            if (filled.Count == filledBefore && images.Count == imagesBefore)
            {
                log?.Invoke(string.Format(
                    "[MetaTube] 别名合并：{0} 的别名「{1}」({2}:{3}) 没有可补充的数据。",
                    name, alias, other.Provider, other.Id));
                continue;
            }

            matched.Add(alias);
            log?.Invoke(string.Format(
                "[MetaTube] 别名合并：{0} 合并别名「{1}」({2}:{3})，补全 {4} 个字段、{5} 张头像。",
                name, alias, other.Provider, other.Id,
                filled.Count - filledBefore, images.Count - imagesBefore));
        }

        var result = new MergedActorInfo
        {
            Info = main,
            Images = images,
            MatchedAliases = matched,
            FilledFields = filled
        };

        StoreCache(cacheKey, result);
        return result;
    }

    /// <summary>
    /// 找出当前演员在替换表里的别名（按表内顺序）。
    /// 只认「本名 == 常用名」这一种身份；本名本身就是别名的情形不处理（用户明确要求）。
    /// </summary>
    private static List<string> ResolveAliases(ActorSubstitutionMap map, string personName, string mainName)
    {
        if (map == null || map.IsEmpty) return new List<string>();

        // 名字优先用 Person 上的名字；它没命中时再用源站记录名兜一次（同一个人的两种写法）。
        var group = map.Find(personName);
        if (group == null && !string.IsNullOrWhiteSpace(mainName)) group = map.Find(mainName);
        if (group == null) return new List<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in group.Aliases)
        {
            var alias = raw?.Trim();
            if (string.IsNullOrWhiteSpace(alias)) continue;
            if (Same(alias, personName) || Same(alias, mainName)) continue;
            if (!seen.Add(alias)) continue;
            result.Add(alias);
        }

        return result;
    }

    /// <summary>
    /// 按别名到源站搜一条记录并取回详情；找不到、超时、对不上人都返回 null。
    /// </summary>
    private static async Task<ActorInfo> ResolveAsync(string alias, ActorInfo main, Action<string> log,
        CancellationToken cancellationToken)
    {
        List<ActorSearchResult> results;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(AliasTimeout);

            // provider 留空 = 让服务端并发查所有数据源；fallback 打开以复用服务端数据库里的存量记录。
            results = await ApiClient.SearchActorAsync(alias, string.Empty, true, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Invoke(string.Format("[MetaTube] 别名合并：搜索「{0}」超时（{1:F0}s），跳过。",
                alias, AliasTimeout.TotalSeconds));
            return null;
        }
        catch (OperationCanceledException)
        {
            // 外层（刷新/任务）主动取消：必须往上抛，不能当成"这个别名没数据"。
            throw;
        }
        catch (Exception e)
        {
            // 服务端没搜到会返回 404，这里是正常降级，不该当故障。
            log?.Invoke(string.Format("[MetaTube] 别名合并：搜索「{0}」无结果或失败：{1}", alias, e.Message));
            return null;
        }

        if (results == null || results.Count == 0) return null;

        // 同一数据源的结果优先（跟主记录同源的资料字段口径最一致），其余保持服务端给的顺序。
        var candidates = results
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Provider) && !string.IsNullOrWhiteSpace(r.Id))
            .OrderByDescending(r => Same(r.Provider, main.Provider))
            .Take(MaxCandidatesPerAlias);

        foreach (var candidate in candidates)
        {
            if (Same(candidate.Provider, main.Provider) && Same(candidate.Id, main.Id)) continue;

            ActorInfo info;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(AliasTimeout);

                // lazy=true：优先用服务端数据库里的存量记录，避免为了合并又去回源站抓一次详情。
                info = await ApiClient.GetActorInfoAsync(candidate.Provider, candidate.Id, true, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                log?.Invoke(string.Format("[MetaTube] 别名合并：取「{0}」({1}:{2}) 详情超时，跳过。",
                    alias, candidate.Provider, candidate.Id));
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                log?.Invoke(string.Format("[MetaTube] 别名合并：取「{0}」({1}:{2}) 详情失败：{3}",
                    alias, candidate.Provider, candidate.Id, e.Message));
                continue;
            }

            if (info == null) continue;

            // 二次校验：搜索是模糊的，短别名很可能命中同源站的另一个人。
            if (!IsSamePerson(info, alias, main.Name))
            {
                log?.Invoke(string.Format(
                    "[MetaTube] 别名合并：搜索结果「{0}」({1}:{2}) 与别名「{3}」对不上，丢弃。",
                    info.Name, info.Provider, info.Id, alias));
                continue;
            }

            return info;
        }

        return null;
    }

    /// <summary>
    /// 字段级合并：只补主记录里空着的字段，别名的值不覆盖已有值，也不动身份字段。
    /// </summary>
    private static void MergeFields(ActorInfo target, ActorInfo source, List<string> filled)
    {
        // 生日：真实年月日才算有值（2月2日是 AV-LEAGUE 的「只有年份」占位）。
        if (ActorProvider.GetRealBirthday(target.Birthday) == null)
        {
            if (ActorProvider.GetRealBirthday(source.Birthday) != null)
            {
                target.Birthday = source.Birthday;
                filled.Add("生日");
            }
            else if (target.Birthday.Year <= 1 && source.Birthday.Year > 1)
            {
                target.Birthday = source.Birthday;
                filled.Add("生年");
            }
        }

        // 出道日：同样的占位规则。
        if (target.DebutDate.GetValidDateTime() == null)
        {
            if (ActorProvider.GetRealBirthday(source.DebutDate) != null)
            {
                target.DebutDate = source.DebutDate;
                filled.Add("出道日");
            }
            else if (target.DebutDate.Year <= 1 && source.DebutDate.Year > 1)
            {
                target.DebutDate = source.DebutDate;
                filled.Add("出道年");
            }
        }

        if (target.Height <= 0 && source.Height > 0)
        {
            target.Height = source.Height;
            filled.Add("身高");
        }

        Fill(() => target.Measurements, v => target.Measurements = v, source.Measurements, "三围", filled);
        Fill(() => target.CupSize, v => target.CupSize = v, source.CupSize, "罩杯", filled);
        Fill(() => target.BloodType, v => target.BloodType = v, source.BloodType, "血型", filled);
        Fill(() => target.Nationality, v => target.Nationality = v, source.Nationality, "国籍", filled);
        Fill(() => target.Twitter, v => target.Twitter = v, source.Twitter, "Twitter", filled);
        Fill(() => target.Instagram, v => target.Instagram = v, source.Instagram, "Instagram", filled);
        Fill(() => target.Hobby, v => target.Hobby = v, source.Hobby, "爱好", filled);
        Fill(() => target.Skill, v => target.Skill = v, source.Skill, "特长", filled);
        Fill(() => target.Summary, v => target.Summary = v, source.Summary, "简介", filled);

        // 标签取并集（ApplyActorInfo 本来就是只加不删）。
        if (source.Tags?.Length > 0)
        {
            var tags = new List<string>(target.Tags ?? Array.Empty<string>());
            var before = tags.Count;

            foreach (var raw in source.Tags)
            {
                var tag = raw?.Trim();
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (tags.Any(t => Same(t, tag))) continue;
                tags.Add(tag);
            }

            if (tags.Count > before)
            {
                target.Tags = tags.ToArray();
                filled.Add("标签");
            }
        }

        // 刻意不合并 source.Aliases：「別名」那一行只保留主记录原文，
        // 否则会混进别名记录自己的别名列表，还会污染演员页的「同步别名」判断。
    }

    private static void Fill(Func<string> get, Action<string> set, string value, string label, List<string> filled)
    {
        if (!string.IsNullOrWhiteSpace(get())) return;
        if (string.IsNullOrWhiteSpace(value)) return;

        set(value.Trim());
        filled.Add(label);
    }

    /// <summary>
    /// 把一条记录的头像追加进去：单条记录内部仍保持「av-league 优先」，跨记录按调用顺序。
    /// </summary>
    private static void AddImages(ActorInfo info, List<ActorImageRef> images, HashSet<string> seen)
    {
        if (info?.Images == null || info.Images.Length == 0) return;
        if (string.IsNullOrWhiteSpace(info.Provider) || string.IsNullOrWhiteSpace(info.Id)) return;

        // 单条记录内部仍保持原来的「av-league 优先」，其余维持服务端给的顺序（稳定排序）。
        foreach (var raw in info.Images
                     .Where(url => !string.IsNullOrWhiteSpace(url))
                     .OrderByDescending(url => url.Contains("av-league.com", StringComparison.OrdinalIgnoreCase)))
        {
            var url = raw.Trim();
            if (!seen.Add(url)) continue;

            images.Add(new ActorImageRef
            {
                Provider = info.Provider,
                Id = info.Id,
                Url = url
            });
        }
    }

    /// <summary>
    /// 名字归一化：全角转半角（NFKC）+ 去掉所有空白与中点 + 转小写。
    /// 日文演员名在不同站点上「空格有无 / 全半角 / 大小写」很随意，必须归一后再比。
    /// </summary>
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var normalized = name.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch == '\u30fb' || ch == '\u2022') continue; // ・ ・
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 校验拿回来的记录确实是同一个人：名字或别名能跟「被查的别名 / 主记录名」对上。
    /// 对不上就当没数据——错误合并会把生日之类永久写进人物条目，宁可漏不可错。
    /// </summary>
    private static bool IsSamePerson(ActorInfo info, string alias, string mainName)
    {
        var expected = new[] { NormalizeName(alias), NormalizeName(mainName) }
            .Where(n => n.Length > 0)
            .ToArray();
        if (expected.Length == 0) return false;

        if (expected.Contains(NormalizeName(info.Name))) return true;

        return info.Aliases?.Any(a => expected.Contains(NormalizeName(a))) == true;
    }

    private static bool SameRecord(ActorInfo a, ActorInfo b)
    {
        return Same(a.Provider, b.Provider) && Same(a.Id, b.Id);
    }

    private static bool Same(string a, string b)
    {
        return string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int Clamp(int value, int fallback, int min, int max)
    {
        if (value <= 0) value = fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private static MergedActorInfo Snapshot(MergedActorInfo source)
    {
        return new MergedActorInfo
        {
            Info = source.Info.Clone(),
            Images = source.Images.ToArray(),
            MatchedAliases = source.MatchedAliases.ToArray(),
            FilledFields = source.FilledFields.ToArray()
        };
    }

    private static bool TryGetCached(string key, out MergedActorInfo result)
    {
        result = null;

        if (!Cache.TryGetValue(key, out var entry)) return false;

        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref entry.LastAccessTicks) > CacheTtl.Ticks)
        {
            Cache.TryRemove(key, out _);
            return false;
        }

        Interlocked.Exchange(ref entry.LastAccessTicks, now);
        result = entry.Result;
        return true;
    }

    private static void StoreCache(string key, MergedActorInfo result)
    {
        if (result == null) return;
        if (Cache.Count >= CacheCapacity) EvictCache();

        Cache[key] = new CacheEntry
        {
            // 存快照：调用方后面会就地改 Summary（翻译）等字段，不能污染缓存本体。
            Result = Snapshot(result),
            LastAccessTicks = DateTime.UtcNow.Ticks
        };
    }

    private static void EvictCache()
    {
        var now = DateTime.UtcNow.Ticks;

        foreach (var kvp in Cache)
        {
            if (now - Interlocked.Read(ref kvp.Value.LastAccessTicks) > CacheTtl.Ticks)
                Cache.TryRemove(kvp.Key, out _);
        }

        if (Cache.Count < CacheCapacity) return;

        foreach (var key in Cache
                     .OrderBy(kvp => Interlocked.Read(ref kvp.Value.LastAccessTicks))
                     .Take(Math.Max(1, CacheCapacity / 10))
                     .Select(kvp => kvp.Key)
                     .ToList())
            Cache.TryRemove(key, out _);
    }
}
