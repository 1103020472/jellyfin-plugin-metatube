using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Web;
using Jellyfin.Plugin.MetaTube.Metadata;
#if __EMBY__
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube;

public static class ApiClient
{
    private const string ActorInfoApi = "/v1/actors";
    private const string MovieInfoApi = "/v1/movies";
    private const string ActorSearchApi = "/v1/actors/search";
    private const string MovieSearchApi = "/v1/movies/search";
    private const string ReviewApi = "/v1/reviews";
    private const string ProviderConfigApi = "/v1/providers/config";
    private const string PrimaryImageApi = "/v1/images/primary";
    private const string ThumbImageApi = "/v1/images/thumb";
    private const string BackdropImageApi = "/v1/images/backdrop";
    private const string TranslateApi = "/v1/translate";

    private static string ComposeUrl(string path, NameValueCollection nv)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (string key in nv) query.Add(key, nv.Get(key));

        // Build URL
        var uriBuilder = new UriBuilder(Plugin.Instance.Configuration.Server)
        {
            Path = path,
            Query = query.ToString() ?? string.Empty
        };
        return uriBuilder.ToString();
    }

    private static string ComposeImageApiUrl(string path, string provider, string id, string url = default,
        double ratio = -1, double position = -1, bool auto = false, string badge = default, string umr = default)
    {
        return ComposeUrl(Path.Combine(path, provider, id), new NameValueCollection
        {
            { "url", url },
            { "ratio", ratio.ToString("R") },
            { "pos", position.ToString("R") },
            { "auto", auto.ToString() },
            { "badge", badge },
            { "umr", umr },
            { "quality", Plugin.Instance.Configuration.DefaultImageQuality.ToString() }
        });
    }

    private static string ComposeInfoApiUrl(string path, string provider, string id, bool lazy)
    {
        return ComposeUrl(Path.Combine(path, provider, id), new NameValueCollection
        {
            { "lazy", lazy.ToString() }
        });
    }

    private static string ComposeSearchApiUrl(string path, string q, string provider, bool fallback)
    {
        return ComposeUrl(path, new NameValueCollection
        {
            { "q", q },
            { "provider", provider },
            { "fallback", fallback.ToString() }
        });
    }
    
    private static string ComposeReviewApiUrl(string path, string provider, string pid, bool lazy)
    {
        return ComposeUrl(Path.Combine(path, provider, pid), new NameValueCollection
        {
            { "lazy", lazy.ToString() }
        });
    }

    private static string ComposeTranslateApiUrl(string path, string q, string from, string to, string engine,
        NameValueCollection nv = null)
    {
        return ComposeUrl(path, new NameValueCollection
        {
            { "q", q },
            { "from", from },
            { "to", to },
            { "engine", engine },
            nv ?? new NameValueCollection()
        });
    }

    private static string ComposeProviderConfigApiUrl(string provider, string key, string value)
    {
        return ComposeUrl(ProviderConfigApi, new NameValueCollection
        {
            { "provider", provider },
            { "key", key },
            { "value", value ?? string.Empty }
        });
    }

    public static string GetPrimaryImageApiUrl(string provider, string id, double position = -1, string badge = default, string umr = default)
    {
        return ComposeImageApiUrl(PrimaryImageApi, provider, id,
            ratio: Plugin.Instance.Configuration.PrimaryImageRatio, position: position, badge: badge, umr: umr);
    }

    public static string GetPrimaryImageApiUrl(string provider, string id, string url, double position = -1,
        bool auto = false, string badge = default)
    {
        return ComposeImageApiUrl(PrimaryImageApi, provider, id, url,
            Plugin.Instance.Configuration.PrimaryImageRatio, position, auto, badge);
    }

    public static string GetThumbImageApiUrl(string provider, string id)
    {
        return ComposeImageApiUrl(ThumbImageApi, provider, id);
    }

    public static string GetThumbImageApiUrl(string provider, string id, string url, double position = -1,
        bool auto = false)
    {
        return ComposeImageApiUrl(ThumbImageApi, provider, id, url, position: position, auto: auto);
    }

    public static string GetBackdropImageApiUrl(string provider, string id)
    {
        return ComposeImageApiUrl(BackdropImageApi, provider, id);
    }

    public static string GetBackdropImageApiUrl(string provider, string id, string url, double position = -1,
        bool auto = false)
    {
        return ComposeImageApiUrl(BackdropImageApi, provider, id, url, position: position, auto: auto);
    }

#if __EMBY__
    public static async Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
#else
    public static async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
#endif
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", DefaultUserAgent);

        // 每个请求独立的超时（配置可调），外部取消仍能立刻打断。
        using var cts = CreateRequestContext(cancellationToken);
        var response = await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
#if __EMBY__
        return new HttpResponseInfo
        {
            Content = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false),
            ContentLength = response.Content.Headers.ContentLength,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            StatusCode = response.StatusCode,
            Headers = response.Content.Headers.ToDictionary(kvp => kvp.Key, kvp => string.Join(", ", kvp.Value))
        };
#else
        return response;
#endif
    }

    public static async Task<ActorInfo> GetActorInfoAsync(string provider, string id,
        CancellationToken cancellationToken)
    {
        return await GetActorInfoAsync(provider, id, true /* default */, cancellationToken);
    }

    public static async Task<ActorInfo> GetActorInfoAsync(string provider, string id, bool lazy,
        CancellationToken cancellationToken)
    {
        var apiUrl = ComposeInfoApiUrl(ActorInfoApi, provider, id, lazy);
        return await GetDataAsync<ActorInfo>(apiUrl, true, cancellationToken);
    }

    public static async Task<MovieInfo> GetMovieInfoAsync(string provider, string id,
        CancellationToken cancellationToken)
    {
        return await GetMovieInfoAsync(provider, id, true /* default */, cancellationToken);
    }

    public static async Task<MovieInfo> GetMovieInfoAsync(string provider, string id, bool lazy,
        CancellationToken cancellationToken)
    {
        var apiUrl = ComposeInfoApiUrl(MovieInfoApi, provider, id, lazy);
        return await GetDataAsync<MovieInfo>(apiUrl, true, cancellationToken);
    }

    public static async Task<List<ActorSearchResult>> SearchActorAsync(string q,
        CancellationToken cancellationToken)
    {
        return await SearchActorAsync(q, string.Empty, cancellationToken);
    }

    public static async Task<List<ActorSearchResult>> SearchActorAsync(string q, string provider,
        CancellationToken cancellationToken)
    {
        return await SearchActorAsync(q, provider, true /* default */, cancellationToken);
    }

    public static async Task<List<ActorSearchResult>> SearchActorAsync(string q, string provider,
        bool fallback, CancellationToken cancellationToken)
    {
        var cacheKey = BuildActorSearchCacheKey(q, provider, fallback);
        if (TryGetCachedActorSearch(cacheKey, out var cached))
        {
            LogInfo("演员搜索命中缓存：{0}（省掉一次远端查询）", q);
            return cached;
        }

        var apiUrl = ComposeSearchApiUrl(ActorSearchApi, q, provider, fallback);
        var results = await GetDataAsync<List<ActorSearchResult>>(apiUrl, true, cancellationToken);

        // 只缓存成功且非空的结果：未搜到（404）与异常都不缓存，避免把临时故障固化 10 分钟。
        StoreActorSearch(cacheKey, results);
        return results;
    }

    public static async Task<List<MovieSearchResult>> SearchMovieAsync(string q,
        CancellationToken cancellationToken)
    {
        return await SearchMovieAsync(q, string.Empty, cancellationToken);
    }

    public static async Task<List<MovieSearchResult>> SearchMovieAsync(string q, string provider,
        CancellationToken cancellationToken)
    {
        return await SearchMovieAsync(q, provider, true /* default */, cancellationToken);
    }

    public static async Task<List<MovieSearchResult>> SearchMovieAsync(string q, string provider,
        bool fallback, CancellationToken cancellationToken)
    {
        var apiUrl = ComposeSearchApiUrl(MovieSearchApi, q, provider, fallback);
        return await GetDataAsync<List<MovieSearchResult>>(apiUrl, true, cancellationToken);
    }
    
    public static async Task<List<ReviewInfo>> GetReviewsAsync(
        string provider,
        string pid,
        bool lazy,
        CancellationToken cancellationToken)
    {
        var apiUrl = ComposeReviewApiUrl(ReviewApi, provider, pid, lazy);
        return await GetDataAsync<List<ReviewInfo>>(apiUrl, true, cancellationToken);
    }
    
    public static async Task<List<ReviewInfo>> GetReviewsAsync(
        string provider,
        string pid,
        CancellationToken cancellationToken)
    {
        return await GetReviewsAsync(provider, pid, true /* default */, cancellationToken);
    }

    public static async Task<TranslationInfo> TranslateAsync(string q, string from, string to, string engine,
        NameValueCollection nv, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var apiUrl = ComposeTranslateApiUrl(TranslateApi, q, from, to, engine, nv);
        return await GetDataAsync<TranslationInfo>(apiUrl, false, cancellationToken, timeout);
    }

    #region 演员搜索缓存

    /// <summary>
    /// 演员搜索结果缓存（滑动过期）。
    ///
    /// 演员查询是整条刮削链上最贵的一步：每次都要到 AV-LEAGUE 现抓网页，实测单次 2~4 秒。
    /// 而同一批刮削里同一个演员会被反复查询 —— 实测一轮 1052 次查询只涉及 305 个演员，
    /// 重复率 71%（例如「上原亜衣」一轮里被查了 203 次），每次都白等 3 秒多。
    ///
    /// 这里按「关键词 + provider + fallback」缓存**成功且非空**的结果：
    /// 10 分钟内有访问就续期，连续 10 分钟没人用到才真正过期。
    /// 这样批量刮削期间缓存一直有效，刮完闲置 10 分钟后自动释放，不会长期占内存、也不会让数据变陈旧。
    ///
    /// 空结果与异常不缓存：演员「未搜到」可能只是临时故障，固化成 10 分钟会掩盖真实问题。
    /// </summary>
    private static readonly ConcurrentDictionary<string, ActorSearchCacheEntry> ActorSearchCache = new();

    /// <summary>缓存空闲过期时间：期间每次命中都会续期（滑动过期）。</summary>
    private static readonly TimeSpan ActorSearchCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>缓存条目上限，防止长时间运行无限增长。</summary>
    private const int ActorSearchCacheCapacity = 5000;

    private sealed class ActorSearchCacheEntry
    {
        public List<ActorSearchResult> Results;
        public long LastAccessTicks;
    }

    private static string BuildActorSearchCacheKey(string q, string provider, bool fallback)
    {
        // \u001f 是单元分隔符，正常关键词里不可能出现，避免拼接歧义。
        return $"{provider}\u001f{fallback}\u001f{q}";
    }

    private static bool TryGetCachedActorSearch(string key, out List<ActorSearchResult> results)
    {
        results = null;

        if (!ActorSearchCache.TryGetValue(key, out var entry))
            return false;

        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref entry.LastAccessTicks) > ActorSearchCacheTtl.Ticks)
        {
            // 空闲超过 10 分钟：真正过期，删掉。
            ActorSearchCache.TryRemove(key, out _);
            return false;
        }

        // 命中即续期（滑动过期）。
        Interlocked.Exchange(ref entry.LastAccessTicks, now);

        // 返回副本：调用方可能会对结果列表做增删（如 AddRange / 排序），不能让它动到缓存本体。
        results = new List<ActorSearchResult>(entry.Results);
        return true;
    }

    private static void StoreActorSearch(string key, List<ActorSearchResult> results)
    {
        if (results == null || results.Count == 0)
            return;

        if (ActorSearchCache.Count >= ActorSearchCacheCapacity)
            EvictActorSearchCache();

        ActorSearchCache[key] = new ActorSearchCacheEntry
        {
            Results = new List<ActorSearchResult>(results),
            LastAccessTicks = DateTime.UtcNow.Ticks
        };
    }

    private static void EvictActorSearchCache()
    {
        var now = DateTime.UtcNow.Ticks;

        // 先清掉已经过期的。
        foreach (var kvp in ActorSearchCache)
        {
            if (now - Interlocked.Read(ref kvp.Value.LastAccessTicks) > ActorSearchCacheTtl.Ticks)
                ActorSearchCache.TryRemove(kvp.Key, out _);
        }

        if (ActorSearchCache.Count < ActorSearchCacheCapacity)
            return;

        // 仍然满：按最近访问时间淘汰最久没用的 10%。
        var victims = ActorSearchCache
            .OrderBy(kvp => Interlocked.Read(ref kvp.Value.LastAccessTicks))
            .Take(Math.Max(1, ActorSearchCacheCapacity / 10))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in victims)
            ActorSearchCache.TryRemove(key, out _);
    }

    /// <summary>
    /// 清空演员搜索缓存。切换服务端地址 / Token 后调用，避免拿旧服务端的演员 ID。
    /// </summary>
    public static void ClearActorSearchCache()
    {
        ActorSearchCache.Clear();
    }

    #endregion

    #region JavDB cookie 同步

    private const string JavdbProviderName = "JavDB";
    private const string JavdbCookieConfigKey = "cookie";

    /// <summary>上一次推送的 cookie，用于避免重复推送。</summary>
    private static string _lastPushedJavdbCookie;

    /// <summary>
    /// 把插件配置里的 JavDB cookie 推送到 MetaTube 服务端（运行时生效，无需重启）。
    ///
    /// 推送条件：cookie 非空且与上次推送值不同；失败仅记 Warn，不影响元数据刮削主流程。
    /// 在元数据/搜索请求前调用，所以服务端重启后第一次请求就会重新推送。
    /// </summary>
    public static void PushJavdbCookieIfNeeded()
    {
        var cookie = Plugin.Instance.Configuration.GetJavdbCookie();
        if (cookie.Length == 0)
        {
            _lastPushedJavdbCookie = string.Empty;
            return;
        }
        if (string.Equals(cookie, _lastPushedJavdbCookie, StringComparison.Ordinal))
            return; // unchanged, nothing to do.

        var url = ComposeProviderConfigApiUrl(JavdbProviderName, JavdbCookieConfigKey, cookie);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("User-Agent", DefaultUserAgent);
        if (!string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.Token))
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", Plugin.Instance.Configuration.Token);

        // 同步等待：cookie 没推送成功，后续的 JavDB 请求必然还是未登录，推了才有意义。
        // 超时给短一些（5 秒），别拖慢刮削；失败也只降级为 Warn。
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = HttpClient.SendAsync(request, cts.Token).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                LogInfo("Pushed JavDB cookie to MetaTube server.");
                _lastPushedJavdbCookie = cookie;
            }
            else
            {
                LogWarn("Push JavDB cookie failed: HTTP {0}. JavDB 登录内容（FC2 等）将不可用。",
                    (int)response.StatusCode);
            }
        }
        catch (Exception e)
        {
            LogWarn("Push JavDB cookie error: {0}", e.Message);
        }
    }

    private static readonly ILogger Logger = CreateLogger();

    /// <summary>
    /// ApiClient 是静态类，拿不到构造注入的 logger。
    /// Emby：直接复用插件实例的 logger；Jellyfin：静态上下文拿不到宿主服务，
    /// cookie 推送日志对 Jellyfin 用户不是关键路径，留空即可。
    /// </summary>
    private static ILogger CreateLogger()
    {
#if __EMBY__
        return Plugin.Instance.logger;
#else
        return null;
#endif
    }

    private static void LogInfo(string format, params object[] args)
    {
#if __EMBY__
        Logger?.Info(format, args);
#endif
    }

    private static void LogWarn(string format, params object[] args)
    {
#if __EMBY__
        Logger?.Warn(format, args);
#endif
    }

    #endregion

    private static async Task<T> GetDataAsync<T>(string url, bool requireAuth,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 每次数据请求前先确保 JavDB cookie 已同步到服务端（有变化才真正发请求）。
        PushJavdbCookieIfNeeded();

        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Add General Headers.
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", DefaultUserAgent);

        // Set API Authorization Token.
        if (requireAuth && !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.Token))
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", Plugin.Instance.Configuration.Token);

        // 本次请求的超时（配置可调）+ 外部取消联动。
        // 必须覆盖到读取响应体的那一步：翻译的耗时大头在服务端推理，
        // 若只给 SendAsync 设超时，响应头已到但正文还没生成完时不会被判超时。
        using var cts = CreateRequestContext(cancellationToken, timeout);

        var response = await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

        // Nullable forgiving reason:
        // Response is unlikely to be null.
        // If it happens to be null, an exception is planed to be thrown either way.
        var apiResponse = (await response.Content!
            .ReadFromJsonAsync<ResponseInfo<T>>(cancellationToken: cts.Token).ConfigureAwait(false))!;

        // EnsureSuccessStatusCode ignoring reason:
        // When the status is unsuccessful, the API response contains error details.
        if (!response.IsSuccessStatusCode && apiResponse.Error != null)
            throw new Exception($"API request error: {apiResponse.Error.Code} ({apiResponse.Error.Message})");

        // Note: data field must not be null if there are no errors.
        if (apiResponse.Data == null)
            throw new Exception("Response data field is null");

        return apiResponse.Data;
    }

    #region Http

    private static readonly HttpClient HttpClient;
    private static string DefaultUserAgent => $"{Plugin.Instance.Name}/{Plugin.Instance.Version}";

    /// <summary>配置留空/非法时使用的默认请求超时（秒），与旧版本写死的 10 分钟一致。</summary>
    public const int DefaultRequestTimeoutSeconds = 600;

    /// <summary>
    /// 当前生效的请求超时。取配置值并做安全夹取（1 秒 ~ 2 小时），避免填 0 或负数导致立即超时。
    /// 每次请求都会重新读取，所以改配置无需重启。
    /// </summary>
    private static TimeSpan RequestTimeout
    {
        get
        {
            var seconds = Plugin.Instance.Configuration.RequestTimeoutSeconds;
            if (seconds <= 0)
                seconds = DefaultRequestTimeoutSeconds;

            // 上限 2 小时：留足大模型长文推理的时间，同时防止误填超大值把请求永久挂住。
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 7200));
        }
    }

    static ApiClient()
    {
        HttpClient = new HttpClient(new SocketsHttpHandler
        {
            // Connect Timeout.
            ConnectTimeout = TimeSpan.FromSeconds(30),

            // TCP Keep Alive.
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),

            // Connection Pooling.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90)
        })
        {
            // 整体请求超时固定为"无限"，真正的超时改由每次请求独立的 CancellationTokenSource 控制
            // （见 CreateRequestContext）。
            //
            // 为什么不直接给 HttpClient.Timeout 赋值：它是全局的，一旦有请求在途，
            // 再改这个属性会抛 InvalidOperationException；而且改完只对新请求生效，
            // 无法做到"每个请求各用各的超时"。用 per-request CTS 才能既动态生效又互不干扰。
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// 为单次请求创建「外部取消 + 配置超时」联动的令牌。
    ///
    /// 返回的 CTS 必须在请求结束后释放（调用方用 using）。
    /// 外部令牌取消时（例如用户取消任务）也会立即取消本次请求，不会干等超时。
    /// </summary>
    private static CancellationTokenSource CreateRequestContext(CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 翻译通道可以单独指定超时（Provider.TimeoutSeconds），其它请求沿用全局配置。
        // 夹取到 1 秒 ~ 2 小时，防止误填 0 / 负数导致请求立刻被取消。
        var effective = timeout ?? RequestTimeout;
        if (effective < TimeSpan.FromSeconds(1))
            effective = TimeSpan.FromSeconds(1);
        else if (effective > TimeSpan.FromHours(2))
            effective = TimeSpan.FromHours(2);

        cts.CancelAfter(effective);
        return cts;
    }

    #endregion
}