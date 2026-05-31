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
        Logger.Info("test pid:{0}", pid);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movies and pick the first result.
            var firstResult = (await GetSearchResults(info, cancellationToken)).FirstOrDefault();
            if (firstResult != null) pid = firstResult.GetPid(Name);
        }

        Logger.Info("Get movie info: {0}", pid.ToString());

        var m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(m.Number))
        {
            m.Number = m.Number.ToUpperInvariant();
        }
        
        if (m.Number != null && m.Number.StartsWith("3DSVR"))
        {
            m.Number = m.Number.Replace("3DSVR", "DSVR");
            Logger.Info("--------LJ---------统一番号名：{0}", m.Number);    
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
                    Logger.Info("--------LJ---------格式化番号数字：{0}", m.Number);
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
        // 1. 定义检测中文的逻辑
        // \u4e00-\u9fa5 是基本汉字的范围
        bool alreadyTranslate = !string.IsNullOrWhiteSpace(info.Name) && !Regex.IsMatch(info.Name, @"[\u3040-\u309F\u30A0-\u30FF\u31F0-\u31FF]") && !info.Name.StartsWith("[");

        // 2. 修改翻译触发条件
        // 如果未开启翻译，或者已经有了中文名称，就跳过翻译步骤
        if (Configuration.TranslationMode != TranslationMode.Disabled)
        {
            // 开关打开，同时有中文，就跳过翻译
            if (alreadyTranslate && Configuration.EnableCheck && !isNewItem)
            {
                Logger.Info("非新入库影片，且已经是中文了，跳过翻译阶段:'{0}'.", m.Number);
                // 置为null表示不更新summary。
                m.Summary = null;
                if (m.Number != null)
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
        result.Item.SetPid(Name, m.Provider, m.Id, pid.Position);

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
            
            // ===================== 配置区：以后加前缀只改这里 =====================
            // 格式：旧前缀 → 新前缀（数字3位用00补，4位用0补）
            var prefixMap = new Dictionary<string, string>
            {
                { "DSVR-", "3DSVR" },   // DSVR-xxx → 3DSVR00xxx / 3DSVR0xxxx
                { "VRKM-", "VRKM" },    // VRKM-xxx → VRKM00xxx / VRKM0xxxx
                { "SAVR-", "SAVR" }     // ✅ 新增 SAVR 支持
            };
            // ======================================================================

            string searchName = info.Name;

            if (!string.IsNullOrEmpty(info.Name))
            {
                // 遍历所有配置的前缀，自动匹配
                foreach (var (oldPrefix, newPrefix) in prefixMap)
                {
                    if (info.Name.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // 正则提取数字
                        var match = Regex.Match(info.Name, $@"{Regex.Escape(oldPrefix)}(\d+)", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            string digits = match.Groups[1].Value;
                            string append = digits.Length == 3 ? "00" : "0";
                            searchName = newPrefix + append + digits;
                        }
                        break;
                    }
                }
                Logger.Info("---------LJ--------修改番号名：{0}", searchName);
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
            var preferredResult = results.FirstOrDefault(r => r.Provider == "AV-LEAGUE");
            if (preferredResult == null)
            {
                Logger.Warn("没有找到AV-LEAGUE的演员元数据：{0}", actor.Name);
            }

            // 如果没有 AV-LEAGUE，则使用第一个
            var firstResult = preferredResult ?? results.First();

            if (firstResult.Images?.Any() == true)
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    firstResult.Provider, firstResult.Id, firstResult.Images.First(), 0.5, true);
                actor.SetPid(Name, firstResult.Provider, firstResult.Id);
            }

            // Use the Gfriends to update the actor profile image, if any.
            foreach (var result in results.Where(result => result.Provider == Gfriends && result.Images?.Any() == true))
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    result.Provider, result.Id, result.Images.First(), 0.5, true);
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

}