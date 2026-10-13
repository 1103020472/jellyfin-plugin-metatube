using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Translation;
#if __EMBY__
using System.ComponentModel;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;

#else
using MediaBrowser.Model.Plugins;
#endif

namespace Jellyfin.Plugin.MetaTube.Configuration;

#if __EMBY__
public class PluginConfiguration : EditableOptionsBase
{
    public override string EditorTitle => Plugin.Instance.Name;
#else
public class PluginConfiguration : BasePluginConfiguration
{
#endif

#if __EMBY__
    [DisplayName("Server")]
    [Description("Full url of the MetaTube Server, HTTPS protocol is recommended.")]
    [Required]
#endif
    public string Server { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Token")]
    [Description("Access token for the MetaTube Server, or blank if no token is set by the backend.")]
#endif
    public string Token { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Enable collections")]
    [Description("Automatically create collections by series.")]
#endif
    public bool EnableCollections { get; set; } = false;

#if __EMBY__
    [DisplayName("Enable directors")]
    [Description("Add directors to corresponding video metadata.")]
#endif
    public bool EnableDirectors { get; set; } = true;

#if __EMBY__
    [DisplayName("Enable ratings")]
    [Description("Display community ratings from the original website.")]
#endif
    public bool EnableRatings { get; set; } = true;

#if __EMBY__
    [DisplayName("Enable trailers")]
    [Description("Generate online video trailers in strm format.")]
#endif
    public bool EnableTrailers { get; set; } = false;

#if __EMBY__
    [DisplayName("Enable real actor names")]
    [Description("Search and replace with real actor names from AVBASE.")]
#endif
    public bool EnableRealActorNames { get; set; } = false;

#if __EMBY__
    [DisplayName("Enable badges")]
    [Description("Add Chinese subtitle badges to primary images.")]
#endif
    public bool EnableBadges { get; set; } = false;

#if __EMBY__
    [DisplayName("Badge url")]
    [Description("Custom badge url, PNG format is recommended. (default: zimu.png)")]
#endif
    public string BadgeUrl { get; set; } = "zimu.png";

#if __EMBY__
    [DisplayName("Primary image ratio")]
    [Description("Aspect ratio for primary images, set a negative value to use the default.")]
#endif
    public double PrimaryImageRatio { get; set; } = -1;

#if __EMBY__
    [DisplayName("Default image quality")]
    [Description("Default compression quality for JPEG images, set between 0 and 100. (default: 90)")]
    [MinValue(0)]
    [MaxValue(100)]
    [Required]
#endif
    public int DefaultImageQuality { get; set; } = 90;

#if __EMBY__
    [DisplayName("Enable movie provider filter")]
    [Description("Filter and reorder search results from movie providers.")]
#endif
    public bool EnableMovieProviderFilter { get; set; } = false;

#if __EMBY__
    [DisplayName("Movie provider filter")]
    [Description(
        "Provider names are case-insensitive, with decreasing precedence from left to right, separated by commas.")]
#endif
    public string RawMovieProviderFilter
    {
        get => _movieProviderFilter?.Any() == true ? string.Join(',', _movieProviderFilter) : string.Empty;
        set => _movieProviderFilter = value?.Split(',').Select(s => s.Trim()).Where(s => s.Any())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<string> GetMovieProviderFilter()
    {
        return _movieProviderFilter;
    }

    private List<string> _movieProviderFilter;

#if __EMBY__
    [DisplayName("Enable template")]
#endif
    public bool EnableTemplate { get; set; } = false;

#if __EMBY__
    [DisplayName("Name template")]
#endif
    public string NameTemplate { get; set; } = DefaultNameTemplate;

#if __EMBY__
    [DisplayName("Tagline template")]
#endif
    public string TaglineTemplate { get; set; } = DefaultTaglineTemplate;

    public static string DefaultNameTemplate => "{number} {title}";

    public static string DefaultTaglineTemplate => "配信開始日 {date}";

#if __EMBY__
    [DisplayName("Translation mode")]
#endif
    public TranslationMode TranslationMode { get; set; } = TranslationMode.Disabled;

#if __EMBY__
    [DisplayName("Translation engine")]
#endif
    public TranslationEngine TranslationEngine { get; set; } = TranslationEngine.Baidu;

#if __EMBY__
    [DisplayName("Baidu app id")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppId { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Baidu app key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Google api key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Google api url")]
    [Description("Custom Google translate api url. (optional)")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL api key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL api url")]
    [Description("Custom DeepL-compatible api url. (optional)")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI api key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI api url")]
    [Description("Custom OpenAI-compatible api url. (optional)")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI model")]
    [Description("Custom OpenAI-compatible api model. (optional)")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiModel { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI extra params (JSON)")]
    [Description("Extra JSON fields merged into the chat completion request body. To cut cost/latency on SiliconFlow reasoning models use {\"thinking_budget\": 1} (verified: reasoning tokens 1154 -> 2, 13s -> 1.3s). Other gateways differ, e.g. Zhipu {\"thinking\": {\"type\": \"disabled\"}}, vLLM {\"chat_template_kwargs\": {\"enable_thinking\": false}}.")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiExtraParams { get; set; } = string.Empty;

    // 单次请求最多生成的 token 数（含思维链）。默认 4096，0 表示使用默认值。
#if __EMBY__
    [DisplayName("OpenAI max completion tokens")]
    [Description("Upper bound of generated tokens per request (including reasoning). Default 4096.")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public int OpenAiMaxCompletionTokens { get; set; } = 4096;

    // 翻译时最大传输的文字个数，避免一次性传太多需要AI翻译很久
#if __EMBY__
    [DisplayName("Translation MaxChunkSize")]
#endif
    public int MaxChunkSize { get; set; } = 2000;

    /// <summary>
    /// 翻译时同时发起的请求数上限（旧版本写死为 5）。
    ///
    /// 调大能缩短多分片评论的整体耗时（整体耗时由最慢的一片决定），
    /// 但会同时压到翻译服务的速率限制；调小则更稳。
    /// 改动后**无需重启**，下一次翻译请求就会按新值限流。
    /// </summary>
#if __EMBY__
    [DisplayName("Translation concurrency")]
    [Description(
        "Max concurrent translation requests. Default 5. Takes effect without restart. Higher is faster but may hit provider rate limits.")]
    [MinValue(1)]
    [MaxValue(32)]
#endif
    public int MaxTranslationConcurrency { get; set; } = 5;
    
    // 标题已经包含中文时，认为已经翻译过了
#if __EMBY__
    [DisplayName("Translation EnableCheck")]
#endif
    public bool EnableCheck { get; set; } = false;
    
    // 是否要获取评论（只支持FANZA）
#if __EMBY__
    [DisplayName("Show Reviews")]
#endif
    public bool ReviewSwitch { get; set; } = false;

    /// <summary>
    /// 评论更新的「全量刷新」开关，默认关闭。
    ///
    /// 仅在 <see cref="ReviewSwitch"/> 打开时才生效（<see cref="ReviewSwitch"/> 仍是总开关，
    /// 刮削与定时任务都读它）：
    ///   * 关闭（默认）：定时任务只刷新「曾被详情页按钮手动拉取过评论」的番号，
    ///     这些番号的 Overview 里带有 <c>ReviewAppender.ManualPullMarker</c> 标记；
    ///   * 打开：恢复原有行为——对所有番号都抓取评论。
    ///
    /// 之所以默认关闭：全库抓评论（每条都要翻译）非常慢且烧翻译额度，
    /// 而绝大多数番号用户并不会去看评论。
    /// </summary>
#if __EMBY__
    [DisplayName("Full review refresh")]
    [Description(
        "Update task refreshes reviews for all items. When off, only items whose reviews were pulled manually from the detail page are refreshed.")]
#endif
    public bool FullReviewRefresh { get; set; } = false;

    /// <summary>
    /// 打开后，"刷新元数据"（以及补齐任务发现资料缺失时）会携带 update: true，
    /// 强制服务端回源站重抓，而不是用服务端缓存（lazy=true）。
    /// </summary>
#if __EMBY__
    [DisplayName("Enable force refetch")]
    [Description("Refresh metadata forces the server to refetch from the source site instead of using its cache.")]
#endif
    public bool EnableForceRefetch { get; set; } = false;
    
#if __EMBY__
    [DisplayName("Enable title substitution")]
#endif
    public bool EnableTitleSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("Title substitution table")]
    [Description(
        "One record per line, separated by equal signs. Leave the target substring blank to delete the source substring.")]
    [EditMultiline(5)]
#endif
    public string TitleRawSubstitutionTable
    {
        get => _titleSubstitutionTable?.ToString();
        set => _titleSubstitutionTable = SubstitutionTable.Parse(value);
    }

    public SubstitutionTable GetTitleSubstitutionTable()
    {
        return _titleSubstitutionTable;
    }

    private SubstitutionTable _titleSubstitutionTable;

#if __EMBY__
    [DisplayName("Enable actor substitution")]
#endif
    public bool EnableActorSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("Actor substitution table")]
    [Description(
        "One record per line, separated by equal signs. Leave the target actor blank to delete the source actor.")]
    [EditMultiline(5)]
#endif
    public string ActorRawSubstitutionTable
    {
        get => _actorSubstitutionTable?.ToString();
        set => _actorSubstitutionTable = SubstitutionTable.Parse(value);
    }

    public SubstitutionTable GetActorSubstitutionTable()
    {
        return _actorSubstitutionTable;
    }

    private SubstitutionTable _actorSubstitutionTable;

#if __EMBY__
    [DisplayName("Enable genre substitution")]
#endif
    public bool EnableGenreSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("Title substitution table")]
    [Description(
        "One record per line, separated by equal signs. Leave the target genre blank to delete the source genre.")]
    [EditMultiline(5)]
#endif
    public string GenreRawSubstitutionTable
    {
        get => _genreSubstitutionTable?.ToString();
        set => _genreSubstitutionTable = SubstitutionTable.Parse(value);
    }

    public SubstitutionTable GetGenreSubstitutionTable()
    {
        return _genreSubstitutionTable;
    }

    private SubstitutionTable _genreSubstitutionTable;
}