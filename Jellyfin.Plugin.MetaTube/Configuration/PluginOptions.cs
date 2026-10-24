using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Translation;
#if __EMBY__
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
#endif

namespace Jellyfin.Plugin.MetaTube.Configuration;

// ============================================================
// 配置分组
//
// Emby 的插件设置页不是手写 HTML，而是 Emby.Web.GenericEdit 按反射
// 从 PluginConfiguration 自动生成的：只有「属性类型本身是可编辑子对象」
// （继承 EditableOptionsBase）的属性，才会渲染成一个可编辑页面/tab。
// 之前所有配置项都平铺在 PluginConfiguration 上，于是一整页全是散装字段。
//
// 现在按用途把配置拆到下面这些分组类里，PluginConfiguration 里每个分组
// 对应一个属性 => 对应 Emby 设置页上的一个 tab 页。
//
// 界面文案（EditorTitle / DisplayName / Description）统一用中文，
// 枚举下拉项的中文来自各枚举成员上的 [Description]（见 Translation 命名空间）。
//
// 兼容性：配置项名字一个都没改，PluginConfiguration 上仍保留同名的
// 「兼容访问器」（读写都落到分组对象里），所以：
//   * 插件内部所有 Configuration.Xxx 的现有代码不用改；
//   * Jellyfin 端的手写 configPage.html（按扁平字段名读写）不用改；
//   * 旧版配置文件（扁平结构的 MetaTube.json / XML）在反序列化时
//     会被这些同名访问器自动写进分组对象，无需手写迁移逻辑。
// ============================================================

/// <summary>基础设置：服务端地址、令牌与刮削行为开关。</summary>
public class GeneralOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "常规";
#endif

#if __EMBY__
    [DisplayName("服务器地址")]
    [Description("MetaTube 服务端的完整地址，建议使用 HTTPS 协议。")]
    [Required]
#endif
    public string Server { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("访问令牌")]
    [Description("MetaTube 服务端的访问令牌；若服务端未设置令牌则留空。")]
#endif
    public string Token { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("启用合集")]
    [Description("按系列自动创建合集。")]
#endif
    public bool EnableCollections { get; set; } = false;

#if __EMBY__
    [DisplayName("启用导演")]
    [Description("为对应影片的元数据添加导演信息。")]
#endif
    public bool EnableDirectors { get; set; } = true;

#if __EMBY__
    [DisplayName("启用评分")]
    [Description("显示原站点的社区评分。")]
#endif
    public bool EnableRatings { get; set; } = true;

#if __EMBY__
    [DisplayName("启用预告片")]
    [Description("生成 strm 格式的在线视频预告片。")]
#endif
    public bool EnableTrailers { get; set; } = false;

#if __EMBY__
    [DisplayName("启用真实演员名")]
    [Description("从 AVBASE 搜索并替换为真实演员名。")]
#endif
    public bool EnableRealActorNames { get; set; } = false;
}

/// <summary>角标与图片：中文字幕角标、主图比例、默认压缩质量。</summary>
public class BadgeImageOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "角标 / 图片";
#endif

#if __EMBY__
    [DisplayName("启用中文字幕角标")]
    [Description("在主图上叠加中文字幕角标。")]
#endif
    public bool EnableBadges { get; set; } = false;

#if __EMBY__
    [DisplayName("角标图片地址")]
    [Description("自定义角标图片地址，建议使用 PNG 格式。（默认：zimu.png）")]
#endif
    public string BadgeUrl { get; set; } = "zimu.png";

#if __EMBY__
    [DisplayName("主图宽高比")]
    [Description("主图使用的宽高比，填负数表示使用默认值。")]
#endif
    public double PrimaryImageRatio { get; set; } = -1;

#if __EMBY__
    [DisplayName("默认图片质量")]
    [Description("JPEG 图片的默认压缩质量，取值范围 0~100。（默认：90）")]
    [MinValue(0)]
    [MaxValue(100)]
    [Required]
#endif
    public int DefaultImageQuality { get; set; } = 90;
}

/// <summary>数据源：影片提供者过滤、JavDB 会话 cookie。</summary>
public class ProviderOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "数据源 / JavDB";
#endif

#if __EMBY__
    [DisplayName("启用数据源过滤")]
    [Description("对影片数据源返回的搜索结果进行过滤和重新排序。")]
#endif
    public bool EnableMovieProviderFilter { get; set; } = false;

#if __EMBY__
    [DisplayName("数据源过滤顺序")]
    [Description("数据源名称不区分大小写，从左到右优先级依次降低，用逗号分隔。")]
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
    [DisplayName("JavDB Cookie")]
    [Description(
        "JavDB 会话 cookie（如 _jdb_session=...），用于访问需要登录的条目（如 FC2 素人番号）。" +
        "从浏览器登录 JavDB 后，在开发者工具(F12) → Network 里复制请求的 Cookie 头整个填入即可。留空则不携带。")]
#endif
    public string JavdbCookie { get; set; } = string.Empty;

    /// <summary>解析后的 JavDB cookie；空白返回空字符串。</summary>
    public string GetJavdbCookie()
    {
        return JavdbCookie?.Trim() ?? string.Empty;
    }
}

/// <summary>文本模板：标题 / 副标题模板。</summary>
public class TemplateOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "文本模板";
#endif

#if __EMBY__
    [DisplayName("启用模板")]
    [Description("启用下面的标题 / 副标题模板。")]
#endif
    public bool EnableTemplate { get; set; } = false;

#if __EMBY__
    [DisplayName("标题模板")]
    [Description("可用变量：{number} 番号、{title} 标题、{series} 系列、{maker} 片商、{label} 厂牌、{director} 导演、{actors} 演员、{first_actor} 首位演员、{runtime} 时长、{year}/{month}/{date} 发行日期。")]
#endif
    public string NameTemplate { get; set; } = PluginConfiguration.DefaultNameTemplate;

#if __EMBY__
    [DisplayName("副标题模板")]
    [Description("可用变量与标题模板相同，默认拼接发行日期。")]
#endif
    public string TaglineTemplate { get; set; } = PluginConfiguration.DefaultTaglineTemplate;
}

/// <summary>翻译：翻译模式、引擎与各引擎的凭据 / 参数。</summary>
public class TranslationOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "翻译";
#endif

#if __EMBY__
    [DisplayName("翻译模式")]
    [Description("禁用 / 仅标题 / 仅简介 / 标题和简介。开启后会显示下面引擎相关的配置。")]
#endif
    public TranslationMode Mode { get; set; } = TranslationMode.Disabled;

#if __EMBY__
    [DisplayName("翻译引擎")]
    [Description("百度 / Google / Google（免费）/ DeepL / OpenAI。只会使用当前引擎对应的凭据。")]
#endif
    public TranslationEngine Engine { get; set; } = TranslationEngine.Baidu;

#if __EMBY__
    [DisplayName("百度 App ID")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppId { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("百度 App Key")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Google API Key")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Google API 地址")]
    [Description("自定义 Google 翻译接口地址。（可选）")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL API Key")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL API 地址")]
    [Description("自定义兼容 DeepL 的接口地址。（可选）")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI API Key")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI API 地址")]
    [Description("自定义兼容 OpenAI 的接口地址。（可选）")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI 模型")]
    [Description("自定义兼容 OpenAI 的模型名。（可选）")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiModel { get; set; } = string.Empty;

    // 翻译时使用的 OpenAI 客户端实现。默认（第一个枚举值）是 openai-translator，
    // 保持历史行为；只有 go-openai 才会应用下面的额外参数与最大 token 配置。
#if __EMBY__
    [DisplayName("OpenAI 翻译组件")]
    [Description("openai-translator（默认）或 go-openai。只有 go-openai 会应用下面的「额外参数」和「最大生成 token」。")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public OpenAiTranslator OpenAiTranslator { get; set; }

#if __EMBY__
    [DisplayName("OpenAI 额外参数 (JSON)")]
    [Description("合并进 chat completion 请求体的额外 JSON 字段（仅 go-openai 生效）。SiliconFlow 推理模型降低开销/延迟可用 {\"thinking_budget\": 1}（实测思维 token 1154 -> 2，13s -> 1.3s）。其他网关写法不同：智谱 {\"thinking\": {\"type\": \"disabled\"}}，vLLM {\"chat_template_kwargs\": {\"enable_thinking\": false}}。")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiExtraParams { get; set; } = string.Empty;

    // 单次请求最多生成的 token 数（含思维链）。默认 4096，0 表示使用默认值；仅 go-openai 生效。
#if __EMBY__
    [DisplayName("OpenAI 最大生成 token")]
    [Description("单次请求生成 token 数的上限（含思维链）。默认 4096，0 表示使用默认值。仅 go-openai 生效。")]
    [VisibleCondition(nameof(Engine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public int OpenAiMaxCompletionTokens { get; set; } = 4096;

    // 翻译时最大传输的文字个数，避免一次性传太多需要AI翻译很久
#if __EMBY__
    [DisplayName("翻译分片长度")]
    [Description("单次请求发送给大模型的最大字符数。（默认：2000）")]
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
    [DisplayName("翻译并发数")]
    [Description(
        "同时发起的翻译请求数上限，默认 5。修改后无需重启即可生效。数值越大越快，但更容易触发服务商的速率限制。")]
    [MinValue(1)]
    [MaxValue(32)]
#endif
    public int MaxTranslationConcurrency { get; set; } = 5;

    /// <summary>
    /// 单次请求 MetaTube 服务端的超时时间（秒），默认 600（10 分钟）。
    ///
    /// 调用的主要是翻译接口，而翻译走大模型：单次摘要/评论分片在推理模型上
    /// 可能要几十秒到数分钟。HttpClient 默认 100 秒会导致请求被中途取消而整段翻译失败。
    /// 改动后**无需重启**，下一次请求就按新值计时。
    /// </summary>
#if __EMBY__
    [DisplayName("API 请求超时（秒）")]
    [Description(
        "单次请求 MetaTube 服务端的超时时间（秒），默认 600。翻译走大模型，单次可能需要数分钟。")]
    [MinValue(1)]
    [MaxValue(7200)]
#endif
    public int RequestTimeoutSeconds { get; set; } = 600;

    // 标题已经包含中文时，认为已经翻译过了
#if __EMBY__
    [DisplayName("跳过已翻译的标题")]
    [Description("标题中已经包含中文时跳过翻译。")]
#endif
    public bool EnableCheck { get; set; } = false;

    // ============================================================
    // OpenAI 多 Provider（DSH 风格）
    //
    // 这里的字段不参与 Emby 的反射表单（翻译页是自定义视图），
    // 但会原样序列化到配置里，供后端与 Jellyfin 手写配置页使用。
    // ============================================================

    /// <summary>所有 OpenAI 兼容通道。</summary>
    public List<TranslationProvider> Providers { get; set; } = new();

    /// <summary>主通道（A）的 Provider.Id。</summary>
    public string PrimaryProviderId { get; set; } = string.Empty;

    /// <summary>遇到敏感词 / 拒答时使用的备用通道（B）的 Provider.Id；留空表示不降级。</summary>
    public string SensitiveFallbackProviderId { get; set; } = string.Empty;

    /// <summary>主通道超时时使用的备用通道（C）的 Provider.Id；留空表示不降级。</summary>
    public string TimeoutFallbackProviderId { get; set; } = string.Empty;

    /// <summary>
    /// 是否已经执行过「旧版主 / 备用 OpenAI 配置 → Provider 列表」迁移。
    ///
    /// 这个标记必须落盘：用户把 Provider 全部删掉后，不能再从旧字段里把它们复活。
    /// </summary>
    public bool ProvidersInitialized { get; set; }

    /// <summary>旧版主通道迁移后使用的固定 Id。</summary>
    public const string LegacyPrimaryProviderId = "legacy-primary";

    /// <summary>旧版备用通道迁移后使用的固定 Id。</summary>
    public const string LegacyBackupProviderId = "legacy-backup";

    /// <summary>
    /// 确保 Provider 列表已初始化：
    ///   * 第一次运行时把旧版主 / 备用 OpenAI 配置迁成两个 Provider；
    ///   * 补齐缺失 / 重复的 Id，清理指向已删除 Provider 的场景路由。
    ///
    /// 幂等，可以在每次组装翻译请求 / 打开设置页时调用。
    /// </summary>
    public void EnsureProviders(BackupTranslationOptions backup)
    {
        Providers ??= new List<TranslationProvider>();

        if (!ProvidersInitialized)
        {
            ProvidersInitialized = true;

            // 已经有 Provider（例如用户手工编辑了配置文件）就只补标记，不覆盖。
            if (Providers.Count == 0)
                MigrateLegacyProviders(backup ?? new BackupTranslationOptions());
        }

        NormalizeProviders();
    }

    private void MigrateLegacyProviders(BackupTranslationOptions backup)
    {
        var hasPrimary = !string.IsNullOrWhiteSpace(OpenAiApiKey)
                         || !string.IsNullOrWhiteSpace(OpenAiApiUrl)
                         || !string.IsNullOrWhiteSpace(OpenAiModel);

        if (hasPrimary)
        {
            Providers.Add(new TranslationProvider
            {
                Id = LegacyPrimaryProviderId,
                Name = "主通道（旧配置迁移）",
                ApiUrl = OpenAiApiUrl ?? string.Empty,
                ApiKey = OpenAiApiKey ?? string.Empty,
                Model = OpenAiModel ?? string.Empty,
                Translator = OpenAiTranslator,
                ExtraParams = OpenAiExtraParams ?? string.Empty,
                MaxCompletionTokens = OpenAiMaxCompletionTokens,
                TimeoutSeconds = 0
            });

            if (string.IsNullOrWhiteSpace(PrimaryProviderId))
                PrimaryProviderId = LegacyPrimaryProviderId;
        }

        var hasBackup = backup.EnableBackupTranslation
                        && (!string.IsNullOrWhiteSpace(backup.BackupOpenAiApiKey)
                            || !string.IsNullOrWhiteSpace(backup.BackupOpenAiApiUrl)
                            || !string.IsNullOrWhiteSpace(backup.BackupOpenAiModel));

        if (hasBackup)
        {
            Providers.Add(new TranslationProvider
            {
                Id = LegacyBackupProviderId,
                Name = "备用通道（旧配置迁移）",
                ApiUrl = backup.BackupOpenAiApiUrl ?? string.Empty,
                ApiKey = backup.BackupOpenAiApiKey ?? string.Empty,
                Model = backup.BackupOpenAiModel ?? string.Empty,
                Translator = backup.BackupOpenAiTranslator,
                ExtraParams = backup.BackupOpenAiExtraParams ?? string.Empty,
                MaxCompletionTokens = backup.BackupOpenAiMaxCompletionTokens,
                TimeoutSeconds = 0
            });

            if (string.IsNullOrWhiteSpace(SensitiveFallbackProviderId))
                SensitiveFallbackProviderId = LegacyBackupProviderId;
        }
    }

    /// <summary>去空、补 Id、去重、夹取数值，并清理失效的场景路由。</summary>
    public void NormalizeProviders()
    {
        Providers ??= new List<TranslationProvider>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = Providers.Count - 1; i >= 0; i--)
        {
            var provider = Providers[i];
            if (provider == null)
            {
                Providers.RemoveAt(i);
                continue;
            }

            provider.Id = (provider.Id ?? string.Empty).Trim();
            provider.Name = (provider.Name ?? string.Empty).Trim();
            provider.ApiUrl = (provider.ApiUrl ?? string.Empty).Trim();
            provider.ApiKey = (provider.ApiKey ?? string.Empty).Trim();
            provider.Model = (provider.Model ?? string.Empty).Trim();
            provider.ExtraParams = provider.ExtraParams ?? string.Empty;

            if (provider.MaxCompletionTokens < 0)
                provider.MaxCompletionTokens = 0;

            provider.TimeoutSeconds = Math.Clamp(provider.TimeoutSeconds, 0, 7200);

            // 没有 Id（手工编辑 JSON 时常见）或 Id 重复：生成一个稳定可用的新 Id。
            if (provider.Id.Length == 0 || !seen.Add(provider.Id))
            {
                provider.Id = Guid.NewGuid().ToString("N");
                seen.Add(provider.Id);
            }

            if (provider.Name.Length == 0)
                provider.Name = string.IsNullOrWhiteSpace(provider.Model)
                    ? $"Provider {i + 1}"
                    : provider.Model;
        }

        PrimaryProviderId = (PrimaryProviderId ?? string.Empty).Trim();
        SensitiveFallbackProviderId = (SensitiveFallbackProviderId ?? string.Empty).Trim();
        TimeoutFallbackProviderId = (TimeoutFallbackProviderId ?? string.Empty).Trim();

        if (FindProvider(PrimaryProviderId) == null)
            PrimaryProviderId = Providers.FirstOrDefault()?.Id ?? string.Empty;

        if (FindProvider(SensitiveFallbackProviderId) == null)
            SensitiveFallbackProviderId = string.Empty;

        if (FindProvider(TimeoutFallbackProviderId) == null)
            TimeoutFallbackProviderId = string.Empty;
    }

    /// <summary>按 Id 查找 Provider；找不到返回 null。</summary>
    public TranslationProvider FindProvider(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return Providers?.FirstOrDefault(p =>
            p != null && string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>主通道；未显式选择时退化为列表第一个。</summary>
    public TranslationProvider GetPrimaryProvider()
    {
        var provider = FindProvider(PrimaryProviderId);
        return provider ?? Providers?.FirstOrDefault(p => p != null);
    }

    /// <summary>敏感词备用通道；未配置或与主通道相同时返回 null（不降级）。</summary>
    public TranslationProvider GetSensitiveFallbackProvider(TranslationProvider primary)
    {
        return ResolveFallback(SensitiveFallbackProviderId, primary);
    }

    /// <summary>超时备用通道；未配置或与主通道相同时返回 null（不降级）。</summary>
    public TranslationProvider GetTimeoutFallbackProvider(TranslationProvider primary)
    {
        return ResolveFallback(TimeoutFallbackProviderId, primary);
    }

    private TranslationProvider ResolveFallback(string id, TranslationProvider primary)
    {
        var provider = FindProvider(id);
        if (provider == null)
            return null;

        if (primary != null && string.Equals(provider.Id, primary.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        return provider;
    }
}

/// <summary>备用翻译通道：主通道返回拒答文案时，按请求粒度自动切换。</summary>
public class BackupTranslationOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "备用翻译";
#endif

    /// <summary>
    /// 备用翻译开关。部分中转站偶尔对某条文本返回
    /// 「Sorry, I can't respond to this question」之类的拒答文案而不给译文；
    /// 打开后，检测到该文案的那一次请求会改用下面的备用 OpenAI 参数重试（请求粒度切换，
    /// 对上层调用者无感）。
    /// </summary>
#if __EMBY__
    [DisplayName("启用备用翻译")]
    [Description(
        "主通道返回拒答文案（如「Sorry, I can't respond to this question」）时，用下面的备用 OpenAI 配置重试该次请求。")]
#endif
    public bool EnableBackupTranslation { get; set; } = false;

#if __EMBY__
    [DisplayName("备用 API Key")]
    [VisibleCondition(nameof(EnableBackupTranslation), ValueCondition.IsEqual, true)]
#endif
    public string BackupOpenAiApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("备用 API 地址")]
    [Description("自定义兼容 OpenAI 的接口地址。（可选）")]
    [VisibleCondition(nameof(EnableBackupTranslation), ValueCondition.IsEqual, true)]
#endif
    public string BackupOpenAiApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("备用模型")]
    [Description("自定义兼容 OpenAI 的模型名。（可选）")]
    [VisibleCondition(nameof(EnableBackupTranslation), ValueCondition.IsEqual, true)]
#endif
    public string BackupOpenAiModel { get; set; } = string.Empty;

    // 备用通道使用的 OpenAI 客户端实现，含义与主通道 OpenAiTranslator 相同。
#if __EMBY__
    [DisplayName("备用翻译组件")]
    [Description("openai-translator（默认）或 go-openai。只有 go-openai 会应用下面的「备用额外参数」和「备用最大生成 token」。")]
    [VisibleCondition(nameof(EnableBackupTranslation), ValueCondition.IsEqual, true)]
#endif
    public OpenAiTranslator BackupOpenAiTranslator { get; set; }

#if __EMBY__
    [DisplayName("备用额外参数 (JSON)")]
    [Description("合并进 chat completion 请求体的额外 JSON 字段（仅 go-openai 生效）。")]
    [VisibleCondition(nameof(EnableBackupTranslation), ValueCondition.IsEqual, true)]
#endif
    public string BackupOpenAiExtraParams { get; set; } = string.Empty;

    // 备用通道单次请求最多生成的 token 数（含思维链）。默认 4096，0 表示使用默认值；仅 go-openai 生效。
#if __EMBY__
    [DisplayName("备用最大生成 token")]
    [Description("单次请求生成 token 数的上限（含思维链）。默认 4096，0 表示使用默认值。仅 go-openai 生效。")]
    [VisibleCondition(nameof(EnableBackupTranslation), ValueCondition.IsEqual, true)]
#endif
    public int BackupOpenAiMaxCompletionTokens { get; set; } = 4096;

    /// <summary>
    /// 触发备用翻译的拒答文案列表（原始多行文本，每行一条，忽略空行）。
    /// 主通道返回的译文里出现**任意一条**（忽略大小写的子串匹配）即视为拒答、转备用通道。
    /// 留空时使用内置默认文案 <see cref="PluginConfiguration.DefaultBackupRefusalPatterns"/>。
    /// </summary>
#if __EMBY__
    [DisplayName("拒答文案")]
    [Description(
        "每行一条拒答文案。译文中出现任意一条（忽略大小写）时，该次请求改用备用通道重试。留空则使用内置默认文案（\"Sorry, I can't respond to this question\"）。")]
    [EditMultiline(5)]
    [VisibleCondition(nameof(EnableBackupTranslation), ValueCondition.IsEqual, true)]
#endif
    public string BackupRefusalPatterns { get; set; } = string.Empty;

    /// <summary>解析后的拒答文案列表；空列表表示用内置默认。</summary>
    public List<string> GetBackupRefusalPatterns()
    {
        var list = (BackupRefusalPatterns ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list;
    }
}

/// <summary>评论与元数据刷新：评论抓取、全量刷新、强制回源。</summary>
public class MetadataOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "评论 / 刷新";
#endif

    // 是否要获取评论（只支持FANZA）
#if __EMBY__
    [DisplayName("获取观众评论")]
    [Description("刮削时从 FANZA 抓取观众评论。")]
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
    [DisplayName("全量刷新评论")]
    [Description(
        "定时任务为所有条目刷新评论。关闭时只刷新「从详情页手动拉取过评论」的条目。")]
#endif
    public bool FullReviewRefresh { get; set; } = false;

    /// <summary>
    /// 打开后，"刷新元数据"（以及补齐任务发现资料缺失时）会携带 update: true，
    /// 强制服务端回源站重抓，而不是用服务端缓存（lazy=true）。
    /// </summary>
#if __EMBY__
    [DisplayName("强制回源重抓")]
    [Description("刷新元数据时强制服务端回源站重新抓取，而不使用服务端缓存。")]
#endif
    public bool EnableForceRefetch { get; set; } = false;
}

/// <summary>替换表：标题 / 演员 / 类型的关键字替换。</summary>
public class SubstitutionOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "替换表";
#endif

#if __EMBY__
    [DisplayName("启用标题替换")]
#endif
    public bool EnableTitleSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("标题替换表")]
    [Description(
        "每行一条记录，用等号分隔。等号右侧留空表示删除左侧的关键字。")]
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
    [DisplayName("启用演员替换")]
#endif
    public bool EnableActorSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("启用演员排除名单")]
    [Description("刮削结果里出现下面名单中的名字时，直接从演员列表里剔除。")]
#endif
    public bool EnableActorExclusion { get; set; } = true;

#if __EMBY__
    [DisplayName("演员替换表（高级 / 纯文本）")]
    [Description(
        "一行一位演员：常用名 = 别名1, 别名2。等号左边是最终显示的名字。老格式「别名=常用名」仍可识别。")]
    [EditMultiline(8)]
#endif
    public string ActorRawSubstitutionTable
    {
        get => _actorRawSubstitutionTable;
        set => _actorRawSubstitutionTable = value ?? string.Empty;
    }

    private string _actorRawSubstitutionTable = string.Empty;

#if __EMBY__
    [DisplayName("演员排除名单（纯文本）")]
    [Description("一行一个名字。命中后该演员不会出现在影片的演职员里。")]
    [EditMultiline(4)]
#endif
    public string ActorRawExclusionList
    {
        get => _actorRawExclusionList;
        set => _actorRawExclusionList = value ?? string.Empty;
    }

    private string _actorRawExclusionList = string.Empty;

    /// <summary>
    /// 解析后的演员替换表（含可选排除名单）。
    /// </summary>
    public ActorSubstitutionMap GetActorSubstitutionMap()
    {
        return ActorSubstitutionMap.Parse(
            ActorRawSubstitutionTable,
            EnableActorExclusion ? ActorRawExclusionList : string.Empty);
    }

#if __EMBY__
    [DisplayName("启用类型替换")]
#endif
    public bool EnableGenreSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("类型替换表")]
    [Description(
        "每行一条记录，用等号分隔。等号右侧留空表示删除左侧的类型名。")]
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

// ===== Merge Multi Versions（多版本合并，自 StrmAssistant 迁移）=====

/// <summary>多版本合并：扫描后按 MetaTube ID 合并同一番号的多个版本。</summary>
public class MergeOptions
#if __EMBY__
    : EditableOptionsBase
#endif
{
#if __EMBY__
    public override string EditorTitle => "多版本合并";
#endif

    /// <summary>
    /// 多版本合并总开关。打开后：媒体库扫描完成时自动合并同一番号的多个版本
    /// （按 MetaTube ProviderId 分组），也可在计划任务里手动运行。
    /// 从详情页「拆分版本」手动拆开过的条目（Overview 里带
    /// <see cref="ManualSplitMarker.OverviewMarker"/>）不参与合并。
    /// </summary>
#if __EMBY__
    [DisplayName("合并多版本")]
    [Description("媒体库扫描完成后，自动合并同一影片（MetaTube ID 相同）的多个版本。从详情页手动拆分过的条目不参与合并。也可在计划任务中手动运行。")]
    [Required]
#endif
    public bool MergeMultiVersion { get; set; } = false;

#if __EMBY__
    [DisplayName("合并范围")]
    [Description("仅当前库：只在正在扫描的媒体库内合并；全局：在所有电影库范围内合并。")]
    [VisibleCondition(nameof(MergeMultiVersion), ValueCondition.IsEqual, true)]
#endif
    public PluginConfiguration.MergeMoviesScopeOption MergeMoviesPreference { get; set; } =
        PluginConfiguration.MergeMoviesScopeOption.LibraryScope;

    /// <summary>
    /// 参与合并的媒体库名单（逗号分隔的库名）。留空 = 所有电影库都参与（默认全选）。
    /// 只有名单内的库才会被合并任务扫描；名单外即使有同番号多版本也不合并。
    /// Emby 设置页里渲染成「媒体库多选列表」：选项来自 <see cref="LibraryList"/>，
    /// 勾选结果由 Emby 自动写回本属性（逗号分隔的库名），存盘格式与旧版完全一致，
    /// 所以旧配置不用迁移；后端 <c>GetMergeMoviesLibraries()</c> 照旧按库名过滤。
    /// </summary>
#if __EMBY__
    [DisplayName("参与合并的媒体库")]
    [Description("勾选允许合并的电影库（可多选）。一个都不勾 = 所有电影库都参与合并（默认）。")]
    [EditMultilSelect]
    [SelectItemsSource(nameof(LibraryList))]
    [VisibleCondition(nameof(MergeMultiVersion), ValueCondition.IsEqual, true)]
#endif
    public string MergeMoviesLibraryList { get; set; } = string.Empty;

#if __EMBY__
    /// <summary>
    /// 上面那个多选列表的数据源（UI 专用，不落盘）。
    /// 每次打开设置页时由 ConfigurationTabView 现读服务器媒体库，库增删后立刻可见。
    /// </summary>
    [Browsable(false)]
    public IEnumerable<EditorSelectOption> LibraryList { get; set; }
#endif

    /// <summary>解析后的库名单；空集合表示不限制（全部库参与）。</summary>
    public List<string> GetMergeMoviesLibraries()
    {
        return (MergeMoviesLibraryList ?? string.Empty)
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 多 CD part 合并开关，默认关闭。
    ///
    /// * 关闭（默认）：待合并的番号组里只要任意一个条目存在额外的 CD part（如 -cd1/-cd2 拆分文件），
    ///   整组都不合并，避免把 part 结构打乱；
    /// * 打开：允许合并带 CD part 的条目，但要求组内每个番号的 part 数量完全一致
    ///   （每个条目按 1 + 额外 part 数计），不一致则该组跳过。
    /// </summary>
#if __EMBY__
    [DisplayName("合并含 CD 分片的条目")]
    [Description("关闭（默认）：只要组内任一条目存在额外 CD 分片，整组都不合并。打开：允许合并带 CD 分片的条目，但要求组内每个条目的分片数量一致。")]
    [VisibleCondition(nameof(MergeMultiVersion), ValueCondition.IsEqual, true)]
#endif
    public bool MergeMultiVersionWithParts { get; set; } = false;
}
