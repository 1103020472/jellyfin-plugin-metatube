using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MovieInfo = MediaBrowser.Controller.Providers.MovieInfo;
#if __EMBY__
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

#else
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

#if __EMBY__
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder, IHasMetadataFeatures
#else
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
#endif
{
    private const string AvBase = "AVBASE";
    private const string AvLeague = "AV-LEAGUE";
    private const string Gfriends = "Gfriends";
    private const string Rating = "JP-18+";

    private static readonly string[] AvBaseSupportedProviderNames = { "DUGA", "FANZA", "Getchu", "MGS" };

#if __EMBY__
    public MetadataFeatures[] Features => new[]
        { MetadataFeatures.Collections, MetadataFeatures.Adult, MetadataFeatures.RequiredSetup };

    public MovieProvider(ILogManager logManager) : base(logManager.CreateLogger<MovieProvider>())
#else
    public MovieProvider(ILogger<MovieProvider> logger) : base(logger)
#endif
    {
    }

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info,
        CancellationToken cancellationToken)
    {
        Logger.Info("GetMetadata MovieInfo.Name: {0}", info.Name);
        var pid = info.GetPid(Name);
        bool isNewItem = string.IsNullOrWhiteSpace(pid.Id);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movies and pick the first result.
            var firstResult = (await GetSearchResults(info, cancellationToken)).FirstOrDefault();
            if (firstResult != null) pid = firstResult.GetPid(Name);
        }

        Logger.Info("Get movie info: {0}", pid.ToString());

        // pid.Update 为 true 时表示"本次要强制回源站重抓"（见 ProviderId.Update）。
        // 另外配置项 EnableForceRefetch 打开后，任何一次元数据刷新都强制回源站。
        // 标记是一次性的：抓完在下面写回 update: false。
        var forceRefetch = pid.Update == true || Configuration.EnableForceRefetch;
        if (forceRefetch) Logger.Info("Force refetch movie from source site: {0}", pid.ToString());

        var m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, !forceRefetch, cancellationToken);
        if (!string.IsNullOrWhiteSpace(m.Number))
        {
            m.Number = m.Number.ToUpperInvariant();
        }
        
        if (m.Number != null && m.Number.StartsWith("3DSVR"))
        {
            m.Number = m.Number.Replace("3DSVR", "DSVR");
            Logger.Info("Unified movie number: {0}", m.Number);
        }
        
        // 新增规则：处理 DSVR 或 VRKM 开头的番号，格式化数字部分
        if (m.Number != null && (m.Number.StartsWith("DSVR") || m.Number.StartsWith("VRKM")))
        {
            // 按分隔符 - 拆分前缀和数字部分
            var parts = m.Number.Split('-');
            if (parts.Length == 2)
            {
                string prefix = parts[0]; // 前缀：DSVR / VRKM
                string numStr = parts[1]; // 原始数字字符串

                // 1. 去除数字部分的所有前缀 0
                string noZeroNum = numStr.TrimStart('0');

                // 2. 保证数字部分至少 3 位，不足补 0
                string formatNum = noZeroNum.PadLeft(3, '0');

                // 3. 拼接成新番号
                string newNumber = $"{prefix}-{formatNum}";

                // 只有发生变化才更新并记录日志
                if (newNumber != m.Number)
                {
                    m.Number = newNumber;
                    Logger.Info("Formatted movie number: {0}", m.Number);
                }
            }
        }


        var reviewProviders = new[] { "FANZA", "MGS" };
        if (Configuration.ReviewSwitch && reviewProviders.Contains(pid.Provider))
        {
            Logger.Info("获取评论：{0}", pid.Id);
            try
            {
                var reviewInfos = await ApiClient.GetReviewsAsync(pid.Provider, pid.Id, cancellationToken);
                m.Reviews = reviewInfos;
            }
            catch (Exception e)
            {
                Logger.Error("获取评论失败：{0}", pid.Id, e);
            }
        }
        

        // Preserve original title.
        var originalTitle = m.Title;

        // Convert to real actor names.
        if (Configuration.EnableRealActorNames)
            await ConvertToRealActorNames(m, cancellationToken);

        // Substitute title.
        if (Configuration.EnableTitleSubstitution)
            m.Title = Configuration.GetTitleSubstitutionTable().Substitute(m.Title);

        // Substitute actors.
        if (Configuration.EnableActorSubstitution)
            m.Actors = Configuration.GetActorSubstitutionTable().Substitute(m.Actors).ToArray();

        // Substitute genres.
        if (Configuration.EnableGenreSubstitution)
            m.Genres = Configuration.GetGenreSubstitutionTable().Substitute(m.Genres).ToArray();

        // Translate movie info.
        // 检测标题是否已经是中文：按【字】统计汉字占比，达到 90% 即认为翻译过。
        // 用比例而不是"出现假名就否决"：演员名通常不翻译，翻译完的标题里
        // 仍可能残留日文（例如「美人妻 北野美奈」「新人 桜井あゆ」），但假名占比低。
        var hanRatio = TranslationHelper.ChineseRatio(info.Name);
        bool alreadyTranslate = !string.IsNullOrWhiteSpace(info.Name)
                                && !info.Name.StartsWith("[")
                                && hanRatio >= TranslationHelper.ChineseRatioThreshold;

        // 2. 修改翻译触发条件
        // 如果未开启翻译，或者已经有了中文名称，就跳过翻译步骤
        if (Configuration.TranslationMode != TranslationMode.Disabled)
        {
            // 开关打开，同时有中文，就跳过翻译
            if (alreadyTranslate && Configuration.EnableCheck && !isNewItem)
            {
                Logger.Info("非新入库影片，且已经是中文了（汉字占比 {0:P0}），跳过翻译阶段:'{1}'.",
                    hanRatio, m.Number);
                // 置为null表示不更新summary。
                m.Summary = null;
                if (m.Number != null && info.Name.Length > m.Number.Length)
                {
                    m.Title = info.Name.Substring(m.Number.Length + 1);
                    //Logger.Info("还原title:'{0}'.", m.Title);
                }
            }
            else
            {
                await TranslateMovieInfo(m, info.MetadataLanguage, cancellationToken);
                m.Summary = ConvertNewlinesToBr(m.Summary);
            }
        }

        // Distinct and clean blank list
        m.Genres = m.Genres?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.Actors = m.Actors?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.PreviewImages = m.PreviewImages?.Where(
            x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();

        // Build parameters.
        var parameters = new Dictionary<string, string>
        {
            { @"{provider}", m.Provider },
            { @"{id}", m.Id },
            { @"{number}", m.Number },
            { @"{title}", m.Title },
            { @"{series}", m.Series },
            { @"{maker}", m.Maker },
            { @"{label}", m.Label },
            { @"{director}", m.Director },
            { @"{runtime}", m.Runtime.ToString() },
            { @"{actors}", m.Actors?.Any() == true ? string.Join(' ', m.Actors) : string.Empty },
            { @"{first_actor}", m.Actors?.FirstOrDefault() },
            { @"{year}", $"{m.ReleaseDate:yyyy}" },
            { @"{month}", $"{m.ReleaseDate:MM}" },
            { @"{date}", $"{m.ReleaseDate:yyyy-MM-dd}" }
        };

        var result = new MetadataResult<Movie>
        {
            Item = new Movie
            {
                Name = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.NameTemplate
                        : PluginConfiguration.DefaultNameTemplate, parameters),
                Tagline = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.TaglineTemplate
                        : PluginConfiguration.DefaultTaglineTemplate, parameters),
                OriginalTitle = originalTitle,
                Overview = m.Summary,
                OfficialRating = Rating,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                Genres = m.Genres?.Any() == true ? m.Genres : Array.Empty<string>()
            },
            HasMetadata = true
        };

        // Set provider id.
        // update: false —— 强制重抓标记已经消费掉，落库后下次刷新回归 lazy 抓取。
        result.Item.SetPid(Name, m.Provider, m.Id, pid.Position, forceRefetch ? false : pid.Update);

        // Set trailer url.
        var trailerUrl = !string.IsNullOrWhiteSpace(m.PreviewVideoUrl)
            ? m.PreviewVideoUrl
            : m.PreviewVideoHlsUrl;
        if (!string.IsNullOrWhiteSpace(trailerUrl))
            result.Item.SetTrailerUrl(trailerUrl);

        // Set community rating.
        if (Configuration.EnableRatings)
            result.Item.CommunityRating = m.Score > 0 ? (float)Math.Round(m.Score * 2, 1) : null;

        // Add collection.
        if (Configuration.EnableCollections && !string.IsNullOrWhiteSpace(m.Series))
        {
            result.Item.AddCollection(m.Series);
            Logger.Info("Add Collection for movie {0} [{1}]", pid.ToString(), m.Series);
        }


        // Add studio.
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddStudio(m.Maker);

        // Add tag (series).
        if (!string.IsNullOrWhiteSpace(m.Series))
            result.Item.AddTag(m.Series);

        // Add tag (maker).
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddTag(m.Maker);

        // Add tag (label).
        if (!string.IsNullOrWhiteSpace(m.Label))
            result.Item.AddTag(m.Label);

        // Add director.
        if (Configuration.EnableDirectors && !string.IsNullOrWhiteSpace(m.Director))
            result.AddPerson(new PersonInfo
            {
                Name = m.Director,
#if __EMBY__
                Type = PersonType.Director
#else
                Type = PersonKind.Director
#endif
            });

        // Add actors.
        foreach (var name in m.Actors ?? Enumerable.Empty<string>())
        {
            var actor = new PersonInfo
            {
                Name = name,
#if __EMBY__
                Type = PersonType.Actor,
#else
                Type = PersonKind.Actor,
#endif
            };
            await SetActorImageUrl(actor, cancellationToken);
            result.AddPerson(actor);
        }

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Name);

        var searchResults = new List<MovieSearchResult>();
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movie by name.
            Logger.Info("Search for movie: {0}", info.Name);

            // 诊断：确认 Emby 究竟传了什么。若 Name 已被截断（如 MBDD）而 Year 为 null，
            // 说明数字在客户端就被丢掉了，需要在下面用其它兜底手段还原。
            Logger.Info("MovieInfo raw: Name=[{0}], Year=[{1}], PremiereDate=[{2}]",
                info.Name, info.Year, info.PremiereDate);

            // Emby 会把番号里的数字误判成年份剥离：例如输入 MBDD-2076 时，
            // info.Name 只剩 "MBDD"，而 "2076" 被放进了 info.Year。
            // 这里把被剥离的数字拼回去，还原完整番号。
            var searchQuery = RestoreNumber(info.Name, info.Year);
            if (!string.Equals(searchQuery, info.Name, StringComparison.Ordinal))
            {
                Logger.Info("Restored movie number: Name={0}, Year={1} => {2}",
                    info.Name, info.Year, searchQuery);
            }

            // ===================== 配置区：以后加前缀只改这里 =====================
            // 格式：旧前缀 → 新前缀（数字3位用00补，4位用0补）
            var prefixMap = new Dictionary<string, string>
            {
                { "DSVR-", "3DSVR" },   // DSVR-xxx → 3DSVR00xxx / 3DSVR0xxxx
                { "VRKM-", "VRKM" },    // VRKM-xxx → VRKM00xxx / VRKM0xxxx
                { "SAVR-", "SAVR" }     // ✅ 新增 SAVR 支持
            };
            // ======================================================================

            string searchName = searchQuery;

            if (!string.IsNullOrEmpty(searchQuery))
            {
                // 遍历所有配置的前缀，自动匹配
                foreach (var (oldPrefix, newPrefix) in prefixMap)
                {
                    if (searchQuery.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // 正则提取数字
                        var match = Regex.Match(searchQuery, $@"{Regex.Escape(oldPrefix)}(\d+)", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            string digits = match.Groups[1].Value;
                            string append = digits.Length == 3 ? "00" : "0";
                            searchName = newPrefix + append + digits;
                        }
                        break;
                    }
                }
                Logger.Info("修改番号名：{0}", searchName);
            }

            searchResults.AddRange(await ApiClient.SearchMovieAsync(searchName, pid.Provider, cancellationToken));
        }
        else
        {
            // Exact search.
            Logger.Info("Search for movie: {0}", pid.ToString());
            searchResults.Add(await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id,
                pid.Update != true, cancellationToken));
        }

        if (Configuration.EnableMovieProviderFilter)
        {
            if (Configuration.GetMovieProviderFilter() is { } filter &&
                filter.Any()) // Apply only if filter is not empty.
            {
                // Filter out mismatched results.
                searchResults.RemoveAll(m => !filter.Contains(m.Provider, StringComparer.OrdinalIgnoreCase));
                // Reorder results by stable sort.
                searchResults = searchResults.OrderBy(
                    m => filter.FindIndex(s => s.Equals(m.Provider, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            else
            {
                Logger.Warn("Movie provider filter enabled but never used");
            }
        }

        var results = new List<RemoteSearchResult>();
        if (!searchResults.Any())
        {
            Logger.Warn("Movie not found or has been filtered: {0}", pid.Id);
            return results;
        }

        foreach (var m in searchResults)
        {
            var result = new RemoteSearchResult
            {
                Name = $"[{m.Provider}] {m.Number} {m.Title}",
                SearchProviderName = Name,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                ImageUrl = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, m.ThumbUrl, 1.0, true)
            };
            result.SetPid(Name, m.Provider, m.Id, pid.Position);
            results.Add(result);
        }

        return results;
    }

    private async Task SetActorImageUrl(PersonInfo actor, CancellationToken cancellationToken)
    {
        try
        {
            var results = await ApiClient.SearchActorAsync(actor.Name, cancellationToken);
            if (results?.Any() != true)
            {
                Logger.Warn("Actor not found: {0}", actor.Name);
                return;
            }

            // 优先选择 AV-LEAGUE
            var preferredResult = results.FirstOrDefault(r =>
                string.Equals(r.Provider, AvLeague, StringComparison.OrdinalIgnoreCase));
            if (preferredResult == null)
            {
                Logger.Warn("没有找到AV-LEAGUE的演员元数据：{0}（服务端返回的 provider：{1}）",
                    actor.Name, string.Join(", ", results.Select(r => r.Provider).Distinct()));
            }

            // 如果没有 AV-LEAGUE，则使用第一个（服务端已按 provider 优先级降序排序）
            var firstResult = preferredResult ?? results.First();

            if (firstResult.Images?.Any() == true)
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    firstResult.Provider, firstResult.Id, firstResult.Images.First(), 0.5, true);
            }

            // 无论是否有图片都要记录来源，否则人物的元数据刷新无法定位到 AV-LEAGUE。
            if (!string.IsNullOrWhiteSpace(firstResult.Provider) && !string.IsNullOrWhiteSpace(firstResult.Id))
            {
                actor.SetPid(Name, firstResult.Provider, firstResult.Id);
                Logger.Info("演员元数据来源：{0} => {1}:{2}", actor.Name, firstResult.Provider, firstResult.Id);
            }

            // Gfriends 仅作为兜底：AV-LEAGUE 已经给出头像时不再覆盖，否则“优先 AV-LEAGUE”会被这里覆盖掉。
            if (preferredResult?.Images?.Any() != true)
            {
                foreach (var result in results.Where(result => result.Provider == Gfriends && result.Images?.Any() == true))
                {
                    actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                        result.Provider, result.Id, result.Images.First(), 0.5, true);
                    Logger.Info("演员头像回退到 Gfriends：{0}", actor.Name);
                }
            }
        }
        catch (Exception e)
        {
            Logger.Error("Get actor image error: {0} ({1})", actor.Name, e.Message);
        }
    }

    private async Task ConvertToRealActorNames(MovieSearchResult m, CancellationToken cancellationToken)
    {
        if (!AvBaseSupportedProviderNames.Contains(m.Provider, StringComparer.OrdinalIgnoreCase)) return;

        try
        {
            var searchResults = await ApiClient.SearchMovieAsync(m.Id, AvBase, cancellationToken);
            if (searchResults?.Any() != true)
            {
                Logger.Warn("Movie not found on AVBASE: {0}", m.Id);
            }
            else if (searchResults.Count > 1)
            {
                // Ignore multiple results to avoid ambiguity.
                Logger.Warn("Multiple movies found on AVBASE: {0}", m.Id);
            }
            else
            {
                var firstResult = searchResults.First();
                if (firstResult.Actors?.Any() == true) m.Actors = firstResult.Actors;
            }
        }
        catch (Exception e)
        {
            Logger.Error("Convert to real actor names error: {0} ({1})", m.Number, e.Message);
        }
    }

    private async Task TranslateMovieInfo(Metadata.MovieInfo m, string language, CancellationToken cancellationToken)
    {
        try
        {
            Logger.Info("Translate movie info language: {0} => {1}", m.Number, language);
            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await TranslationHelper.TranslateAsync(m, language, cancellationToken);
            Logger.Info("Translate movie end, cost:{0}ms: {1} => {2}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, m.Number, language);
        }
        catch (Exception e)
        {
            Logger.Error("Translate error: {0}", e.Message);
        }
    }

    private static string RenderTemplate(string template, Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var sb = parameters.Where(kvp => template.Contains(kvp.Key))
            .Aggregate(new StringBuilder(template),
                (sb, kvp) => sb.Replace(kvp.Key, kvp.Value));

        return sb.ToString().Trim();
    }
    
    private static string ConvertNewlinesToBr(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input; // 空值保护，避免空引用异常
        }
        
        // 分步替换，确保所有换行符都被处理
        string result = input
            .Replace("\r\n", "\n")                
            .Replace("\r", "\n")                // Linux/Mac换行符
            .Replace("\n", "<br>");               // 单独的回车符
        
        return result;
    }

    /// <summary>
    /// 还原被 Emby 误当成"发行年份"而拆走的番号数字。
    ///
    /// 背景：番号里的 4 位数字如果落在 1900-2099 区间，会被 Emby 的片名解析规则
    /// （Emby.Naming 中 video clean-string 的 "(19\d\d|20\d\d)" 规则）识别成年份：
    ///   MBDD-2076  →  Name="MBDD", Year=2076
    ///   ABP-123    →  Name="ABP-123"（123 不在年份区间，不受影响）
    /// 该规则在"入库扫描"和"手动识别(Identify)"两条路径上都会生效，
    /// 因此必须把 Year 拼回 Name，否则搜索词会退化成 MBDD，命中大量无关结果。
    /// </summary>
    /// <param name="name">Emby 传入的片名（可能已被截断）。</param>
    /// <param name="year">Emby 解析出的年份（可能为 null）。</param>
    private static string RestoreNumber(string name, int? year)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        // 情况一：Name 里已含数字，说明番号完整，无需还原。
        // （例如 ABP-123、MBDD-2076 在未启用年份解析时的正常形态）
        if (name.Any(char.IsDigit))
            return name;

        // 情况二：Name 只剩字母前缀，且 Emby 给出了年份 —— 判定为被拆走的番号数字。
        if (year.HasValue)
            return $"{name.TrimEnd('-', ' ')}-{year.Value}";

        // 情况三：Name 是纯字母、但没有任何年份线索。
        // 此时无法凭空还原数字，原样返回（保持原有行为，不做无根据的猜测）。
        return name;
    }

}