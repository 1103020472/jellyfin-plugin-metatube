using System.ComponentModel;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Translation;
#if __EMBY__
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
#else
using MediaBrowser.Model.Plugins;
#endif

namespace Jellyfin.Plugin.MetaTube.Configuration;

// 配置项的真正存放位置已经按用途拆到 PluginOptions.cs 里的各个分组类中，
// 这里只保留「分组属性」+「同名兼容访问器」。
//
// 为什么要保留兼容访问器：插件内部（Providers / ScheduledTasks / ApiClient …）
// 以及 Jellyfin 端手写的 configPage.html 都按扁平字段名访问配置，
// 有了这些访问器就不必改动它们；旧配置文件里的扁平字段名也会被
// 这些访问器自动写回分组对象，无需额外的数据迁移代码。
#if __EMBY__
public class PluginConfiguration : EditableOptionsBase
#else
public class PluginConfiguration : BasePluginConfiguration
#endif
{
#if __EMBY__
    public override string EditorTitle => Plugin.Instance.Name;

    // ---- 分组（Emby 设置页上每个属性 => 一个折叠分组）----

    [DisplayName("常规")]
    [Description("服务端地址、访问令牌与基础刮削开关。")]
#endif
    public GeneralOptions General
    {
        get => _general ??= new GeneralOptions();
        set
        {
            if (value != null) _general = value;
        }
    }

    private GeneralOptions _general;

#if __EMBY__
    [DisplayName("角标 / 图片")]
    [Description("中文字幕角标与主图输出。")]
#endif
    public BadgeImageOptions BadgeImage
    {
        get => _badgeImage ??= new BadgeImageOptions();
        set
        {
            if (value != null) _badgeImage = value;
        }
    }

    private BadgeImageOptions _badgeImage;

#if __EMBY__
    [DisplayName("数据源 / JavDB")]
    [Description("数据源过滤与 JavDB 会话 Cookie。")]
#endif
    public ProviderOptions Provider
    {
        get => _provider ??= new ProviderOptions();
        set
        {
            if (value != null) _provider = value;
        }
    }

    private ProviderOptions _provider;

#if __EMBY__
    [DisplayName("文本模板")]
    [Description("标题与副标题模板。")]
#endif
    public TemplateOptions Template
    {
        get => _template ??= new TemplateOptions();
        set
        {
            if (value != null) _template = value;
        }
    }

    private TemplateOptions _template;

#if __EMBY__
    [DisplayName("翻译")]
    [Description("翻译模式、翻译引擎，以及当前引擎对应的凭据。")]
#endif
    public TranslationOptions Translation
    {
        get => _translation ??= new TranslationOptions();
        set
        {
            if (value != null) _translation = value;
        }
    }

    private TranslationOptions _translation;

#if __EMBY__
    [DisplayName("备用翻译")]
    [Description("主通道拒答时，用第二个 OpenAI 兼容接口重试该次请求。")]
#endif
    public BackupTranslationOptions BackupTranslation
    {
        get => _backupTranslation ??= new BackupTranslationOptions();
        set
        {
            if (value != null) _backupTranslation = value;
        }
    }

    private BackupTranslationOptions _backupTranslation;

#if __EMBY__
    [DisplayName("评论 / 刷新")]
    [Description("评论抓取与元数据刷新行为。")]
#endif
    public MetadataOptions Metadata
    {
        get => _metadata ??= new MetadataOptions();
        set
        {
            if (value != null) _metadata = value;
        }
    }

    private MetadataOptions _metadata;

#if __EMBY__
    [DisplayName("替换表")]
    [Description("标题 / 演员 / 类型的关键字替换表。")]
#endif
    public SubstitutionOptions Substitution
    {
        get => _substitution ??= new SubstitutionOptions();
        set
        {
            if (value != null) _substitution = value;
        }
    }

    private SubstitutionOptions _substitution;

#if __EMBY__
    [DisplayName("多版本合并")]
    [Description("媒体库扫描完成后合并同一影片的多个版本。")]
#endif
    public MergeOptions Merge
    {
        get => _merge ??= new MergeOptions();
        set
        {
            if (value != null) _merge = value;
        }
    }

    private MergeOptions _merge;

    // ============================================================
    // 兼容访问器：字段名与旧版完全一致，读写都落到上面的分组对象。
    // [Browsable(false)] 让它们不出现在 Emby 设置页里（否则每个字段会重复显示两次）。
    // ============================================================

    [Browsable(false)]
    public string Server
    {
        get => General.Server;
        set => General.Server = value;
    }

    [Browsable(false)]
    public string Token
    {
        get => General.Token;
        set => General.Token = value;
    }

    [Browsable(false)]
    public bool EnableCollections
    {
        get => General.EnableCollections;
        set => General.EnableCollections = value;
    }

    [Browsable(false)]
    public bool EnableDirectors
    {
        get => General.EnableDirectors;
        set => General.EnableDirectors = value;
    }

    [Browsable(false)]
    public bool EnableRatings
    {
        get => General.EnableRatings;
        set => General.EnableRatings = value;
    }

    [Browsable(false)]
    public bool EnableTrailers
    {
        get => General.EnableTrailers;
        set => General.EnableTrailers = value;
    }

    [Browsable(false)]
    public bool EnableRealActorNames
    {
        get => General.EnableRealActorNames;
        set => General.EnableRealActorNames = value;
    }

    [Browsable(false)]
    public bool EnableBadges
    {
        get => BadgeImage.EnableBadges;
        set => BadgeImage.EnableBadges = value;
    }

    [Browsable(false)]
    public string BadgeUrl
    {
        get => BadgeImage.BadgeUrl;
        set => BadgeImage.BadgeUrl = value;
    }

    [Browsable(false)]
    public double PrimaryImageRatio
    {
        get => BadgeImage.PrimaryImageRatio;
        set => BadgeImage.PrimaryImageRatio = value;
    }

    [Browsable(false)]
    public int DefaultImageQuality
    {
        get => BadgeImage.DefaultImageQuality;
        set => BadgeImage.DefaultImageQuality = value;
    }

    /// <summary>
    /// 条目已经有背景图时，是否不再提供背景图候选（避免每次刷新重复下载）。
    /// 默认 false：保持与旧版本一致的「照旧提供全部候选」行为。
    /// </summary>
    [Browsable(false)]
    public bool SkipExistingBackdrop
    {
        get => BadgeImage.SkipExistingBackdrop;
        set => BadgeImage.SkipExistingBackdrop = value;
    }

    [Browsable(false)]
    public bool EnableMovieProviderFilter
    {
        get => Provider.EnableMovieProviderFilter;
        set => Provider.EnableMovieProviderFilter = value;
    }

    [Browsable(false)]
    public string RawMovieProviderFilter
    {
        get => Provider.RawMovieProviderFilter;
        set => Provider.RawMovieProviderFilter = value;
    }

    public List<string> GetMovieProviderFilter()
    {
        return Provider.GetMovieProviderFilter();
    }

    [Browsable(false)]
    public string JavdbCookie
    {
        get => Provider.JavdbCookie;
        set => Provider.JavdbCookie = value;
    }

    /// <summary>解析后的 JavDB cookie；空白返回空字符串。</summary>
    public string GetJavdbCookie()
    {
        return Provider.GetJavdbCookie();
    }

    [Browsable(false)]
    public bool EnableTemplate
    {
        get => Template.EnableTemplate;
        set => Template.EnableTemplate = value;
    }

    [Browsable(false)]
    public string NameTemplate
    {
        get => Template.NameTemplate;
        set => Template.NameTemplate = value;
    }

    [Browsable(false)]
    public string TaglineTemplate
    {
        get => Template.TaglineTemplate;
        set => Template.TaglineTemplate = value;
    }

    public static string DefaultNameTemplate => "{number} {title}";

    public static string DefaultTaglineTemplate => "配信開始日 {date}";

    [Browsable(false)]
    public TranslationMode TranslationMode
    {
        get => Translation.Mode;
        set => Translation.Mode = value;
    }

    [Browsable(false)]
    public TranslationEngine TranslationEngine
    {
        get => Translation.Engine;
        set => Translation.Engine = value;
    }

    [Browsable(false)]
    public string BaiduAppId
    {
        get => Translation.BaiduAppId;
        set => Translation.BaiduAppId = value;
    }

    [Browsable(false)]
    public string BaiduAppKey
    {
        get => Translation.BaiduAppKey;
        set => Translation.BaiduAppKey = value;
    }

    [Browsable(false)]
    public string GoogleApiKey
    {
        get => Translation.GoogleApiKey;
        set => Translation.GoogleApiKey = value;
    }

    [Browsable(false)]
    public string GoogleApiUrl
    {
        get => Translation.GoogleApiUrl;
        set => Translation.GoogleApiUrl = value;
    }

    [Browsable(false)]
    public string DeepLApiKey
    {
        get => Translation.DeepLApiKey;
        set => Translation.DeepLApiKey = value;
    }

    [Browsable(false)]
    public string DeepLApiUrl
    {
        get => Translation.DeepLApiUrl;
        set => Translation.DeepLApiUrl = value;
    }

    [Browsable(false)]
    public string OpenAiApiKey
    {
        get => Translation.OpenAiApiKey;
        set => Translation.OpenAiApiKey = value;
    }

    [Browsable(false)]
    public string OpenAiApiUrl
    {
        get => Translation.OpenAiApiUrl;
        set => Translation.OpenAiApiUrl = value;
    }

    [Browsable(false)]
    public string OpenAiModel
    {
        get => Translation.OpenAiModel;
        set => Translation.OpenAiModel = value;
    }

    [Browsable(false)]
    public OpenAiTranslator OpenAiTranslator
    {
        get => Translation.OpenAiTranslator;
        set => Translation.OpenAiTranslator = value;
    }

    [Browsable(false)]
    public string OpenAiExtraParams
    {
        get => Translation.OpenAiExtraParams;
        set => Translation.OpenAiExtraParams = value;
    }

    [Browsable(false)]
    public int OpenAiMaxCompletionTokens
    {
        get => Translation.OpenAiMaxCompletionTokens;
        set => Translation.OpenAiMaxCompletionTokens = value;
    }

    [Browsable(false)]
    public int MaxChunkSize
    {
        get => Translation.MaxChunkSize;
        set => Translation.MaxChunkSize = value;
    }

    [Browsable(false)]
    public int MaxTranslationConcurrency
    {
        get => Translation.MaxTranslationConcurrency;
        set => Translation.MaxTranslationConcurrency = value;
    }

    [Browsable(false)]
    public int RequestTimeoutSeconds
    {
        get => Translation.RequestTimeoutSeconds;
        set => Translation.RequestTimeoutSeconds = value;
    }

    [Browsable(false)]
    public bool EnableCheck
    {
        get => Translation.EnableCheck;
        set => Translation.EnableCheck = value;
    }

    [Browsable(false)]
    public bool EnableBackupTranslation
    {
        get => BackupTranslation.EnableBackupTranslation;
        set => BackupTranslation.EnableBackupTranslation = value;
    }

    [Browsable(false)]
    public string BackupOpenAiApiKey
    {
        get => BackupTranslation.BackupOpenAiApiKey;
        set => BackupTranslation.BackupOpenAiApiKey = value;
    }

    [Browsable(false)]
    public string BackupOpenAiApiUrl
    {
        get => BackupTranslation.BackupOpenAiApiUrl;
        set => BackupTranslation.BackupOpenAiApiUrl = value;
    }

    [Browsable(false)]
    public string BackupOpenAiModel
    {
        get => BackupTranslation.BackupOpenAiModel;
        set => BackupTranslation.BackupOpenAiModel = value;
    }

    [Browsable(false)]
    public OpenAiTranslator BackupOpenAiTranslator
    {
        get => BackupTranslation.BackupOpenAiTranslator;
        set => BackupTranslation.BackupOpenAiTranslator = value;
    }

    [Browsable(false)]
    public string BackupOpenAiExtraParams
    {
        get => BackupTranslation.BackupOpenAiExtraParams;
        set => BackupTranslation.BackupOpenAiExtraParams = value;
    }

    [Browsable(false)]
    public int BackupOpenAiMaxCompletionTokens
    {
        get => BackupTranslation.BackupOpenAiMaxCompletionTokens;
        set => BackupTranslation.BackupOpenAiMaxCompletionTokens = value;
    }

    [Browsable(false)]
    public string BackupRefusalPatterns
    {
        get => BackupTranslation.BackupRefusalPatterns;
        set => BackupTranslation.BackupRefusalPatterns = value;
    }

    /// <summary>解析后的拒答文案列表；空列表表示用内置默认。</summary>
    public List<string> GetBackupRefusalPatterns()
    {
        return BackupTranslation.GetBackupRefusalPatterns();
    }

    /// <summary>
    /// 确保 OpenAI Provider 列表已完成迁移 / 规范化。
    /// 翻译请求与设置页在读取配置前都会调用，幂等。
    /// </summary>
    public void EnsureTranslationProvidersMigrated()
    {
        Translation.EnsureProviders(BackupTranslation);
    }

    /// <summary>未配置时的内置默认拒答文案。</summary>
    public static readonly string[] DefaultBackupRefusalPatterns =
    {
        "Sorry, I can't respond to this question"
    };

    [Browsable(false)]
    public bool ReviewSwitch
    {
        get => Metadata.ReviewSwitch;
        set => Metadata.ReviewSwitch = value;
    }

    [Browsable(false)]
    public bool FullReviewRefresh
    {
        get => Metadata.FullReviewRefresh;
        set => Metadata.FullReviewRefresh = value;
    }

    [Browsable(false)]
    public bool EnableForceRefetch
    {
        get => Metadata.EnableForceRefetch;
        set => Metadata.EnableForceRefetch = value;
    }

    [Browsable(false)]
    public bool EnableTitleSubstitution
    {
        get => Substitution.EnableTitleSubstitution;
        set => Substitution.EnableTitleSubstitution = value;
    }

    [Browsable(false)]
    public string TitleRawSubstitutionTable
    {
        get => Substitution.TitleRawSubstitutionTable;
        set => Substitution.TitleRawSubstitutionTable = value;
    }

    public SubstitutionTable GetTitleSubstitutionTable()
    {
        return Substitution.GetTitleSubstitutionTable();
    }

    [Browsable(false)]
    public bool EnableActorSubstitution
    {
        get => Substitution.EnableActorSubstitution;
        set => Substitution.EnableActorSubstitution = value;
    }

    [Browsable(false)]
    public string ActorRawSubstitutionTable
    {
        get => Substitution.ActorRawSubstitutionTable;
        set => Substitution.ActorRawSubstitutionTable = value;
    }

    [Browsable(false)]
    public bool EnableActorExclusion
    {
        get => Substitution.EnableActorExclusion;
        set => Substitution.EnableActorExclusion = value;
    }

    [Browsable(false)]
    public string ActorRawExclusionList
    {
        get => Substitution.ActorRawExclusionList;
        set => Substitution.ActorRawExclusionList = value;
    }

    public ActorSubstitutionMap GetActorSubstitutionMap()
    {
        return Substitution.GetActorSubstitutionMap();
    }

    // 演员别名资料合并（配置项挂在「评论 / 刷新」分组里：替换表那个 tab 是自定义界面，
    // 反射表单在那里不显示）。
    [Browsable(false)]
    public bool EnableActorAliasMerge
    {
        get => Metadata.EnableActorAliasMerge;
        set => Metadata.EnableActorAliasMerge = value;
    }

    [Browsable(false)]
    public int ActorAliasMergeMaxAttempts
    {
        get => Metadata.ActorAliasMergeMaxAttempts;
        set => Metadata.ActorAliasMergeMaxAttempts = value;
    }

    [Browsable(false)]
    public int ActorAliasMergeMaxHits
    {
        get => Metadata.ActorAliasMergeMaxHits;
        set => Metadata.ActorAliasMergeMaxHits = value;
    }

    [Browsable(false)]
    public bool EnableGenreSubstitution
    {
        get => Substitution.EnableGenreSubstitution;
        set => Substitution.EnableGenreSubstitution = value;
    }

    [Browsable(false)]
    public string GenreRawSubstitutionTable
    {
        get => Substitution.GenreRawSubstitutionTable;
        set => Substitution.GenreRawSubstitutionTable = value;
    }

    public SubstitutionTable GetGenreSubstitutionTable()
    {
        return Substitution.GetGenreSubstitutionTable();
    }

    [Browsable(false)]
    public bool MergeMultiVersion
    {
        get => Merge.MergeMultiVersion;
        set => Merge.MergeMultiVersion = value;
    }

    /// <summary>合并范围枚举：仅当前扫描的库，或全局所有电影库。</summary>
    public enum MergeMoviesScopeOption
    {
        [Description("仅当前库")]
        LibraryScope,

        [Description("全局")]
        GlobalScope
    }

    [Browsable(false)]
    public MergeMoviesScopeOption MergeMoviesPreference
    {
        get => Merge.MergeMoviesPreference;
        set => Merge.MergeMoviesPreference = value;
    }

    [Browsable(false)]
    public string MergeMoviesLibraryList
    {
        get => Merge.MergeMoviesLibraryList;
        set => Merge.MergeMoviesLibraryList = value;
    }

    /// <summary>解析后的库名单；空集合表示不限制（全部库参与）。</summary>
    public List<string> GetMergeMoviesLibraries()
    {
        return Merge.GetMergeMoviesLibraries();
    }

    [Browsable(false)]
    public bool MergeMultiVersionWithParts
    {
        get => Merge.MergeMultiVersionWithParts;
        set => Merge.MergeMultiVersionWithParts = value;
    }
}
