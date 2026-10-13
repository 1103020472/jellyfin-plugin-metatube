using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Web;
using Jellyfin.Plugin.MetaTube.Metadata;
#if __EMBY__
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube;

public static class ApiClient
{
    private const string ActorInfoApi = "/v1/actors";
    private const string MovieInfoApi = "/v1/movies";
    private const string ActorSearchApi = "/v1/actors/search";
    private const string MovieSearchApi = "/v1/movies/search";
    private const string ReviewApi = "/v1/reviews";
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
        var apiUrl = ComposeSearchApiUrl(ActorSearchApi, q, provider, fallback);
        return await GetDataAsync<List<ActorSearchResult>>(apiUrl, true, cancellationToken);
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
        NameValueCollection nv, CancellationToken cancellationToken)
    {
        var apiUrl = ComposeTranslateApiUrl(TranslateApi, q, from, to, engine, nv);
        return await GetDataAsync<TranslationInfo>(apiUrl, false, cancellationToken);
    }

    private static async Task<T> GetDataAsync<T>(string url, bool requireAuth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
        using var cts = CreateRequestContext(cancellationToken);

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
    private static CancellationTokenSource CreateRequestContext(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);
        return cts;
    }

    #endregion
}