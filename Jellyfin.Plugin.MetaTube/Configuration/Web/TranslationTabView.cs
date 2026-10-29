#if __EMBY__
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using IEditableObject = MediaBrowser.Model.GenericEdit.IEditableObject;

namespace Jellyfin.Plugin.MetaTube.Configuration.Web;

// ============================================================
// 「翻译」tab 的自定义界面。
//
// Emby 的 GenericEdit 只能一个属性一个控件，做不出「列表 + 新增/编辑」
// 这种结构，所以这里自己拼编辑器树：
//   1. 基础字段仍交给基类反射生成；
//   2. Provider 列表用 EditorItemList + 按钮组；
//   3. 主通道 / 敏感词备用 / 超时备用用 EditorSelectSingle，
//      选项在每次打开页面时按当前 Provider 列表现生成。
// ============================================================

internal static class TranslationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>翻译 tab 提交上来 / 下发下去的普通字段。</summary>
internal sealed class TranslationFormDto
{
    public TranslationMode Mode { get; set; }

    public TranslationEngine Engine { get; set; }

    public int MaxChunkSize { get; set; } = 2000;

    public int MaxTranslationConcurrency { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 600;

    public bool EnableCheck { get; set; }

    public string LocalProviderId { get; set; } = string.Empty;

    public string PrimaryProviderId { get; set; } = string.Empty;

    public string SensitiveFallbackProviderId { get; set; } = string.Empty;

    public string TimeoutFallbackProviderId { get; set; } = string.Empty;

    /// <summary>沿用旧的配置名，实际含义是「敏感词 / 拒答文案」列表。</summary>
    public string BackupRefusalPatterns { get; set; } = string.Empty;

    public static TranslationFormDto FromConfiguration()
    {
        var configuration = Plugin.Instance.Configuration;
        configuration.EnsureTranslationProvidersMigrated();
        var options = configuration.Translation;

        return new TranslationFormDto
        {
            Mode = options.Mode,
            Engine = options.Engine,
            MaxChunkSize = options.MaxChunkSize,
            MaxTranslationConcurrency = options.MaxTranslationConcurrency,
            RequestTimeoutSeconds = options.RequestTimeoutSeconds,
            EnableCheck = options.EnableCheck,
            LocalProviderId = options.LocalProviderId,
            PrimaryProviderId = options.PrimaryProviderId,
            SensitiveFallbackProviderId = options.SensitiveFallbackProviderId,
            TimeoutFallbackProviderId = options.TimeoutFallbackProviderId,
            BackupRefusalPatterns = configuration.BackupTranslation.BackupRefusalPatterns ?? string.Empty
        };
    }

    public static TranslationFormDto TryParse(string json, TranslationFormDto fallback = null)
    {
        // 不能直接把任意 JSON 反序列化成 TranslationFormDto：缺字段会变成默认值，
        // 例如把 Provider 表单的数据误当成翻译页数据保存，就会把翻译模式 / 引擎 / 超时全部重置。
        // 因此先确认它确实是翻译页的数据，再只覆盖 payload 里真实出现的字段。
        var result = fallback ?? FromConfiguration();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return result;

            var looksLikeTranslation = root.TryGetProperty(nameof(Mode), out _)
                                       || root.TryGetProperty(nameof(Engine), out _)
                                       || root.TryGetProperty(nameof(MaxChunkSize), out _)
                                       || root.TryGetProperty(nameof(LocalProviderId), out _)
                                       || root.TryGetProperty(nameof(PrimaryProviderId), out _)
                                       || root.TryGetProperty(nameof(SensitiveFallbackProviderId), out _)
                                       || root.TryGetProperty(nameof(TimeoutFallbackProviderId), out _);
            if (!looksLikeTranslation)
                return result;

            var parsed = JsonSerializer.Deserialize<TranslationFormDto>(json, TranslationJson.Options);
            if (parsed == null)
                return result;

            if (root.TryGetProperty(nameof(Mode), out _)) result.Mode = parsed.Mode;
            if (root.TryGetProperty(nameof(Engine), out _)) result.Engine = parsed.Engine;
            if (root.TryGetProperty(nameof(MaxChunkSize), out _)) result.MaxChunkSize = parsed.MaxChunkSize;
            if (root.TryGetProperty(nameof(MaxTranslationConcurrency), out _))
                result.MaxTranslationConcurrency = parsed.MaxTranslationConcurrency;
            if (root.TryGetProperty(nameof(RequestTimeoutSeconds), out _))
                result.RequestTimeoutSeconds = parsed.RequestTimeoutSeconds;
            if (root.TryGetProperty(nameof(EnableCheck), out _)) result.EnableCheck = parsed.EnableCheck;
            if (root.TryGetProperty(nameof(LocalProviderId), out _))
                result.LocalProviderId = parsed.LocalProviderId ?? string.Empty;
            if (root.TryGetProperty(nameof(PrimaryProviderId), out _))
                result.PrimaryProviderId = parsed.PrimaryProviderId ?? string.Empty;
            if (root.TryGetProperty(nameof(SensitiveFallbackProviderId), out _))
                result.SensitiveFallbackProviderId = parsed.SensitiveFallbackProviderId ?? string.Empty;
            if (root.TryGetProperty(nameof(TimeoutFallbackProviderId), out _))
                result.TimeoutFallbackProviderId = parsed.TimeoutFallbackProviderId ?? string.Empty;
            if (root.TryGetProperty(nameof(BackupRefusalPatterns), out _))
                result.BackupRefusalPatterns = parsed.BackupRefusalPatterns ?? string.Empty;

            return result;
        }
        catch (JsonException)
        {
            return result;
        }
    }

    public void Save()
    {
        var configuration = Plugin.Instance.Configuration;
        configuration.EnsureTranslationProvidersMigrated();

        var options = configuration.Translation;
        options.Mode = Mode;
        options.Engine = Engine;
        options.MaxChunkSize = MaxChunkSize;
        options.MaxTranslationConcurrency = MaxTranslationConcurrency;
        options.RequestTimeoutSeconds = RequestTimeoutSeconds;
        options.EnableCheck = EnableCheck;
        options.LocalProviderId = LocalProviderId ?? string.Empty;
        options.PrimaryProviderId = PrimaryProviderId ?? string.Empty;
        options.SensitiveFallbackProviderId = SensitiveFallbackProviderId ?? string.Empty;
        options.TimeoutFallbackProviderId = TimeoutFallbackProviderId ?? string.Empty;

        // 用户在这里保存过，说明 Provider 列表已经由用户掌控：
        // 以后即使删空，也不能再从旧版扁平字段里自动迁移回来。
        options.ProvidersInitialized = true;

        // 敏感词文案沿用旧字段，后端读取逻辑不用改，旧配置也天然兼容。
        configuration.BackupTranslation.BackupRefusalPatterns = BackupRefusalPatterns ?? string.Empty;

        configuration.EnsureTranslationProvidersMigrated();
        Plugin.Instance.SaveConfiguration(configuration);
    }
}

/// <summary>翻译 tab 的数据模型。</summary>
internal sealed class TranslationEditModel : EditableOptionsBase
{
    public override string EditorTitle => "翻译";

    public override string EditorDescription =>
        "OpenAI 兼容通道支持配置多个 Provider：可选的本地优先通道（L）、主通道（A），以及敏感词备用（B）与超时备用（C）。";

    // ===== 基础字段：交给基类反射生成 =====

    [DisplayName("翻译模式")]
    [Description("禁用 / 仅标题 / 仅简介 / 标题和简介。")]
    public TranslationMode Mode { get; set; }

    [DisplayName("翻译引擎")]
    [Description("百度 / Google / Google（免费）/ DeepL / OpenAI。OpenAI 才会使用下面的 Provider 列表。")]
    public TranslationEngine Engine { get; set; }

    [DisplayName("翻译分片长度")]
    [Description("单次请求发送给大模型的最大字符数。（默认：2000）")]
    public int MaxChunkSize { get; set; } = 2000;

    [DisplayName("翻译并发数")]
    [Description("同时发起的翻译请求数上限，默认 5。修改后无需重启即可生效。")]
    [MinValue(1)]
    [MaxValue(32)]
    public int MaxTranslationConcurrency { get; set; } = 5;

    [DisplayName("API 请求超时（秒）")]
    [Description("没有单独配置 Provider 超时时使用的全局超时。默认 600 秒。")]
    [MinValue(1)]
    [MaxValue(7200)]
    public int RequestTimeoutSeconds { get; set; } = 600;

    // ===== 敏感词：交给基类反射生成 =====

    [DisplayName("敏感词 / 拒答文案")]
    [Description(
        "每行一条。译文中出现任意一条（忽略大小写）时，该次请求改用「敏感词备用 B」重试。留空使用内置默认文案。")]
    [EditMultiline(5)]
    public string BackupRefusalPatterns { get; set; } = string.Empty;

    [DisplayName("跳过已翻译的标题")]
    [Description("标题中已经包含中文时跳过翻译。")]
    public bool EnableCheck { get; set; }

    // ===== 路由字段：通过自定义下拉控件渲染，禁止基类重复生成 =====

    [Browsable(false)] public string LocalProviderId { get; set; } = string.Empty;

    [Browsable(false)] public string PrimaryProviderId { get; set; } = string.Empty;

    [Browsable(false)] public string SensitiveFallbackProviderId { get; set; } = string.Empty;

    [Browsable(false)] public string TimeoutFallbackProviderId { get; set; } = string.Empty;

    // ===== 仅用于渲染的数据 =====

    [Browsable(false)] public List<GenericListItem> ProviderRows { get; set; } = new();

    [Browsable(false)] public ButtonItem AddProviderButton { get; set; } = new();

    [Browsable(false)] public LabelItem ProviderSummary { get; set; } = new();

    [Browsable(false)] public LabelItem RouteHint { get; set; } = new();

    [Browsable(false)] public IEnumerable<EditorSelectOption> ProviderSelectOptions { get; set; } =
        Array.Empty<EditorSelectOption>();

    public override IEditObjectContainer CreateEditContainer()
    {
        var container = base.CreateEditContainer();
        if (container is not EditObjectContainer editContainer || editContainer.EditorRoot == null)
            return container;

        var root = editContainer.EditorRoot;
        var parentId = root.EditorItems?.FirstOrDefault()?.ParentId ?? string.Empty;

        EditorBase Take(string name) => root.EditorItems?.FirstOrDefault(e => e.Name == name);

        var basicItems = new List<EditorBase>();
        AddIfNotNull(basicItems, Take(nameof(Mode)));
        AddIfNotNull(basicItems, Take(nameof(Engine)));
        AddIfNotNull(basicItems, Take(nameof(MaxChunkSize)));
        AddIfNotNull(basicItems, Take(nameof(MaxTranslationConcurrency)));
        AddIfNotNull(basicItems, Take(nameof(RequestTimeoutSeconds)));

        var providerItems = new List<EditorBase>
        {
            new EditorLabelItem(nameof(ProviderSummary), parentId),
            new EditorItemList(nameof(ProviderRows), parentId),
            new EditorButtonGroup(
                new EditorButtonItem[] {new EditorButtonItem(nameof(AddProviderButton), parentId)},
                "ProviderToolbar", parentId)
        };

        var routeItems = new List<EditorBase>
        {
            BuildRouteSelect(nameof(LocalProviderId), "本地 Provider（优先）",
                "配置后先走这个通道（例如本地 Ollama）。它报错、返回空或拒答文案时自动降级到主 Provider A，"
                + "再由 A 决定是否走 B / C。留空则不启用。", parentId),
            BuildRouteSelect(nameof(PrimaryProviderId), "主 Provider（A）",
                "正常翻译使用的通道；A 请求失败 / 超时时从这里开始判断。", parentId),
            BuildRouteSelect(nameof(SensitiveFallbackProviderId), "敏感词备用（B）",
                "A 返回拒答文案，或接口返回内容审核 / 敏感词错误时，用 B 重试一次。留空则不降级。", parentId),
            BuildRouteSelect(nameof(TimeoutFallbackProviderId), "超时备用（C）",
                "A 超时时用 C 重试一次。留空则不降级。B 和 C 是两条独立链路。", parentId),
            new EditorLabelItem(nameof(RouteHint), parentId)
        };

        var sensitiveItems = new List<EditorBase>();
        AddIfNotNull(sensitiveItems, Take(nameof(BackupRefusalPatterns)));
        AddIfNotNull(sensitiveItems, Take(nameof(EnableCheck)));

        root.EditorItems = new EditorBase[]
        {
            new EditorGroup("翻译基础", basicItems.ToArray(), "TranslationBasic", parentId, null),
            new EditorGroup("OpenAI Provider 列表", providerItems.ToArray(), "TranslationProviders", parentId, null),
            new EditorGroup("场景路由（仅 OpenAI 生效）", routeItems.ToArray(), "TranslationRouting", parentId, null),
            new EditorGroup("敏感词与其它", sensitiveItems.ToArray(), "TranslationSensitive", parentId, null)
        };

        return container;
    }

    private EditorSelectSingle BuildRouteSelect(string name, string displayName, string description, string parentId)
    {
        return new EditorSelectSingle(
            ProviderSelectOptions?.ToList() ?? new List<EditorSelectOption>(),
            name, parentId, string.Empty, string.Empty, false)
        {
            DisplayName = displayName,
            Description = description,
            // B / C 可以为空（不降级）；主通道也允许暂时为空，保存时后端会退回第一个 Provider。
            AllowEmpty = true
        };
    }

    private static void AddIfNotNull(List<EditorBase> target, EditorBase editor)
    {
        if (editor != null)
            target.Add(editor);
    }
}

/// <summary>翻译 tab：Provider 列表 + 场景路由。</summary>
internal sealed class TranslationTabView : PluginTabPageView
{
    private const string CmdProviderAdd = "provider.add";
    private const string CmdProviderEdit = "provider.edit";
    private const string CmdProviderDelete = "provider.del";

    private TranslationTabView(IEditableObject content) : base(content)
    {
    }

    public static TranslationTabView FromConfiguration(string message = null)
    {
        return Build(TranslationFormDto.FromConfiguration(), message);
    }

    public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        var dto = TranslationFormDto.TryParse(data);
        dto.Save();
        return Task.FromResult(Refresh(dto, "已保存。"));
    }

    public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        // 底部「保存」按钮在不同 Emby 版本里可能走 PageSave，显式接住。
        if (commandId == "PageSave")
            return OnSaveCommand(itemId, commandId, data);

        var dto = TranslationFormDto.TryParse(data);

        switch (commandId)
        {
            case CmdProviderAdd:
                return Task.FromResult<IPluginUIView>(
                    TranslationProviderFormTabView.ForProvider(dto, null));

            case CmdProviderEdit:
                return Task.FromResult<IPluginUIView>(
                    TranslationProviderFormTabView.ForProvider(dto, FindProvider(itemId)));

            case CmdProviderDelete:
            {
                var configuration = Plugin.Instance.Configuration;
                configuration.EnsureTranslationProvidersMigrated();

                var provider = configuration.Translation.FindProvider(itemId);
                if (provider != null)
                {
                    configuration.Translation.Providers.Remove(provider);
                    configuration.EnsureTranslationProvidersMigrated();
                    Plugin.Instance.SaveConfiguration(configuration);
                }

                return Task.FromResult(Refresh(TranslationFormDto.FromConfiguration(),
                    provider != null ? $"已删除 Provider「{provider.Name}」。" : "这个 Provider 已经不在了。"));
            }

            default:
                return Task.FromResult<IPluginUIView>(this);
        }
    }

    private static TranslationProvider FindProvider(string id)
    {
        var configuration = Plugin.Instance.Configuration;
        configuration.EnsureTranslationProvidersMigrated();
        return configuration.Translation.FindProvider(id);
    }

    private static TranslationTabView Build(TranslationFormDto dto, string message)
    {
        return new TranslationTabView(CreateContent(dto, message));
    }

    private IPluginUIView Refresh(TranslationFormDto dto, string message)
    {
        ContentData = CreateContent(dto, message);
        RaiseUIViewInfoChanged();
        return this;
    }

    private static TranslationEditModel CreateContent(TranslationFormDto dto, string message)
    {
        var configuration = Plugin.Instance.Configuration;
        configuration.EnsureTranslationProvidersMigrated();

        var providers = configuration.Translation.Providers.ToList();
        var options = BuildProviderOptions(providers);

        return new TranslationEditModel
        {
            Mode = dto.Mode,
            Engine = dto.Engine,
            MaxChunkSize = dto.MaxChunkSize,
            MaxTranslationConcurrency = dto.MaxTranslationConcurrency,
            RequestTimeoutSeconds = dto.RequestTimeoutSeconds,
            EnableCheck = dto.EnableCheck,
            BackupRefusalPatterns = dto.BackupRefusalPatterns,
            LocalProviderId = dto.LocalProviderId,
            PrimaryProviderId = dto.PrimaryProviderId,
            SensitiveFallbackProviderId = dto.SensitiveFallbackProviderId,
            TimeoutFallbackProviderId = dto.TimeoutFallbackProviderId,

            ProviderRows = providers.Select(BuildProviderRow).ToList(),
            ProviderSelectOptions = options,
            AddProviderButton = new ButtonItem
            {
                Caption = "新增 Provider",
                Icon = IconNames.add,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdProviderAdd
            },
            ProviderSummary = new LabelItem
            {
                IsVisible = true,
                Text = providers.Count == 0
                    ? "还没有配置任何 Provider。点下面的「新增 Provider」添加第一个 OpenAI 兼容通道。"
                    : $"共 {providers.Count} 个 Provider。配了「本地 Provider（优先）」就先走它，失败后回落主通道 A，再由 A 决定是否走 B / C。"
            },
            RouteHint = new LabelItem
            {
                IsVisible = true,
                Text = message ?? "L 失败 / 空结果一律回落 A；A 之后：敏感词只切 B，超时只切 C；B 或 C 再失败就直接报错。"
            }
        };
    }

    private static GenericListItem BuildProviderRow(TranslationProvider provider)
    {
        var api = string.IsNullOrWhiteSpace(provider.ApiUrl) ? "默认 API 地址" : provider.ApiUrl;
        var key = string.IsNullOrWhiteSpace(provider.ApiKey)
            ? "无 Key"
            : $"Key …{Tail(provider.ApiKey, 4)}";

        return new GenericListItem
        {
            PrimaryText = string.IsNullOrWhiteSpace(provider.Name) ? "(未命名 Provider)" : provider.Name,
            SecondaryText = $"{provider.Describe()} · {api} · {key}",
            Button1 = new ButtonItem
            {
                Icon = IconNames.edit,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdProviderEdit,
                Data2 = provider.Id
            },
            Button2 = new ButtonItem
            {
                Icon = IconNames.delete,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdProviderDelete,
                Data2 = provider.Id,
                ConfirmationPrompt = $"确定删除 Provider「{provider.Name}」吗？引用它的场景路由会自动清空。"
            }
        };
    }

    private static string Tail(string text, int length)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= length)
            return text ?? string.Empty;
        return text[^length..];
    }

    private static List<EditorSelectOption> BuildProviderOptions(IReadOnlyList<TranslationProvider> providers)
    {
        // 空选项由 EditorSelectSingle 的 AllowEmpty 自动补，这里不要再加，
        // 否则下拉里会出现两个空值。
        var options = new List<EditorSelectOption>();

        foreach (var provider in providers)
        {
            var model = string.IsNullOrWhiteSpace(provider.Model) ? "默认模型" : provider.Model;
            options.Add(new EditorSelectOption
            {
                Value = provider.Id,
                Name = $"{provider.Name}（{model}）",
                IsEnabled = true
            });
        }

        return options;
    }
}

/// <summary>Provider 编辑表单提交上来的字段。</summary>
internal sealed class TranslationProviderFormDto
{
    public string OriginalId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ApiUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public OpenAiTranslator Translator { get; set; }

    public OpenAiPromptMode PromptMode { get; set; }

    public string ExtraParams { get; set; } = string.Empty;

    public int MaxCompletionTokens { get; set; } = 4096;

    public int TimeoutSeconds { get; set; }

    public static TranslationProviderFormDto FromProvider(TranslationProvider provider)
    {
        if (provider == null)
            return new TranslationProviderFormDto();

        return new TranslationProviderFormDto
        {
            OriginalId = provider.Id,
            Name = provider.Name,
            ApiUrl = provider.ApiUrl,
            ApiKey = provider.ApiKey,
            Model = provider.Model,
            Translator = provider.Translator,
            PromptMode = provider.PromptMode,
            ExtraParams = provider.ExtraParams,
            MaxCompletionTokens = provider.MaxCompletionTokens,
            TimeoutSeconds = provider.TimeoutSeconds
        };
    }

    public static TranslationProviderFormDto TryParse(string json, TranslationProviderFormDto fallback = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback ?? new TranslationProviderFormDto();

        try
        {
            return JsonSerializer.Deserialize<TranslationProviderFormDto>(json, TranslationJson.Options)
                   ?? fallback ?? new TranslationProviderFormDto();
        }
        catch (JsonException)
        {
            return fallback ?? new TranslationProviderFormDto();
        }
    }

    /// <summary>把表单写回 Provider 列表。返回保存后的显示名。</summary>
    public string Save()
    {
        var configuration = Plugin.Instance.Configuration;
        configuration.EnsureTranslationProvidersMigrated();

        var options = configuration.Translation;
        options.ProvidersInitialized = true;

        var provider = options.FindProvider(OriginalId);
        if (provider == null)
        {
            provider = new TranslationProvider
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(Name) ? "OpenAI Provider" : Name.Trim()
            };
            options.Providers.Add(provider);
        }

        provider.Name = string.IsNullOrWhiteSpace(Name) ? provider.Name : Name.Trim();
        provider.ApiUrl = (ApiUrl ?? string.Empty).Trim();
        provider.ApiKey = (ApiKey ?? string.Empty).Trim();
        provider.Model = (Model ?? string.Empty).Trim();
        provider.Translator = Translator;
        provider.PromptMode = PromptMode;
        provider.ExtraParams = ExtraParams ?? string.Empty;
        provider.MaxCompletionTokens = MaxCompletionTokens < 0 ? 0 : MaxCompletionTokens;
        provider.TimeoutSeconds = Math.Clamp(TimeoutSeconds, 0, 7200);

        configuration.EnsureTranslationProvidersMigrated();
        Plugin.Instance.SaveConfiguration(configuration);
        return provider.Name;
    }
}

internal sealed class TranslationProviderFormModel : EditableOptionsBase
{
    public override string EditorTitle =>
        string.IsNullOrEmpty(OriginalId) ? "新增 Provider" : "编辑 Provider";

    public override string EditorDescription =>
        "一个 Provider = 一套 OpenAI 兼容接口参数。敏感词与超时降级链路由「翻译」页的路由选择决定。";

    [Browsable(false)] public string OriginalId { get; set; } = string.Empty;

    [DisplayName("名称")]
    [Description("仅用于设置页与日志，例如「主力」「备用-无审查」。")]
    public string Name { get; set; } = string.Empty;

    [DisplayName("API 地址")]
    [Description("兼容 OpenAI 的接口地址；留空表示使用默认地址。")]
    public string ApiUrl { get; set; } = string.Empty;

    [DisplayName("API Key")]
    [Description("接口 Key；本地无鉴权的接口可以留空。")]
    [IsPassword]
    public string ApiKey { get; set; } = string.Empty;

    [DisplayName("模型")]
    [Description("例如 deepseek-ai/DeepSeek-V3、gpt-4o-mini；留空表示使用默认模型。")]
    public string Model { get; set; } = string.Empty;

    [DisplayName("翻译组件")]
    [Description("openai-translator（默认）或 go-openai。只有 go-openai 会应用额外参数与最大 token。")]
    public OpenAiTranslator Translator { get; set; } = OpenAiTranslator.OpenAiTranslator;

    [DisplayName("提示词模式")]
    [Description(
        "default：system + 指令 + 原文三段（历史行为）。single：指令与原文合并成一条，无 system —— 本地小模型建议选它，"
        + "长分片下不容易翻错语言（实测 qwen 在 default 下会把日文翻成英文）。system-single：保留 system 但合并指令与原文。"
        + "非 default 会自动改用 go-openai 实现。")]
    public OpenAiPromptMode PromptMode { get; set; } = OpenAiPromptMode.Default;

    [DisplayName("额外参数 (JSON)")]
    [Description(
        "合并进 chat completion 请求体的额外 JSON 字段（仅 go-openai 生效）。SiliconFlow: {\"thinking_budget\": 1}；智谱: {\"thinking\": {\"type\": \"disabled\"}}。")]
    [EditMultiline(4)]
    public string ExtraParams { get; set; } = string.Empty;

    [DisplayName("最大生成 token")]
    [Description("单次请求生成 token 的上限（含思维链）。默认 4096，0 表示使用默认值。仅 go-openai 生效。")]
    public int MaxCompletionTokens { get; set; } = 4096;

    [DisplayName("超时（秒）")]
    [Description("本 Provider 的单次请求超时；0 表示沿用「翻译基础」里的全局 API 超时。")]
    [MinValue(0)]
    [MaxValue(7200)]
    public int TimeoutSeconds { get; set; }

    [Browsable(false)] public ButtonItem SaveButton { get; set; } = new();

    [Browsable(false)] public ButtonItem CancelButton { get; set; } = new();

    [Browsable(false)] public LabelItem Notice { get; set; } = new();

    public void LoadFrom(TranslationProviderFormDto dto)
    {
        OriginalId = dto.OriginalId;
        Name = dto.Name;
        ApiUrl = dto.ApiUrl;
        ApiKey = dto.ApiKey;
        Model = dto.Model;
        Translator = dto.Translator;
        PromptMode = dto.PromptMode;
        ExtraParams = dto.ExtraParams;
        MaxCompletionTokens = dto.MaxCompletionTokens;
        TimeoutSeconds = dto.TimeoutSeconds;
    }

    public override IEditObjectContainer CreateEditContainer()
    {
        var container = base.CreateEditContainer();
        if (container is not EditObjectContainer editContainer || editContainer.EditorRoot == null)
            return container;

        var root = editContainer.EditorRoot;
        var parentId = root.EditorItems?.FirstOrDefault()?.ParentId ?? string.Empty;
        var items = (root.EditorItems ?? Array.Empty<EditorBase>()).ToList();

        items.Add(new EditorLabelItem(nameof(Notice), parentId));
        items.Add(new EditorButtonGroup(
            new EditorButtonItem[]
            {
                new EditorButtonItem(nameof(SaveButton), parentId),
                new EditorButtonItem(nameof(CancelButton), parentId)
            }, "ProviderFormToolbar", parentId));

        root.EditorItems = items.ToArray();
        return container;
    }
}

/// <summary>Provider 新增 / 编辑表单页。</summary>
internal sealed class TranslationProviderFormTabView : PluginTabPageView
{
    private const string CmdSave = "form.provider.save";
    private const string CmdCancel = "form.cancel";

    private TranslationProviderFormTabView(IEditableObject content) : base(content)
    {
        ShowSave = false;
        ShowBack = false;
    }

    public static TranslationProviderFormTabView ForProvider(TranslationFormDto tabDto,
        TranslationProvider provider, string notice = null)
    {
        return ForDto(TranslationProviderFormDto.FromProvider(provider), notice);
    }

    /// <summary>用表单当前值重建页面（校验失败时保留用户已填内容）。</summary>
    public static TranslationProviderFormTabView ForDto(TranslationProviderFormDto dto, string notice = null)
    {
        var model = new TranslationProviderFormModel
        {
            SaveButton = new ButtonItem
            {
                Caption = string.IsNullOrEmpty(dto.OriginalId) ? "添加" : "保存",
                Icon = string.IsNullOrEmpty(dto.OriginalId) ? IconNames.add : IconNames.check,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdSave
            },
            CancelButton = new ButtonItem
            {
                Caption = "取消",
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdCancel
            },
            Notice = new LabelItem
            {
                IsVisible = true,
                Text = notice ?? "保存后会回到翻译页。Provider 的引用关系在「场景路由」里选择。"
            }
        };

        model.LoadFrom(dto);
        return new TranslationProviderFormTabView(model);
    }

    public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        // 表单页不显示全局保存按钮，这里只是兜底。
        return Task.FromResult<IPluginUIView>(this);
    }

    public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        var fallback = ContentData is TranslationProviderFormModel form
            ? TranslationProviderFormDto.FromProvider(new TranslationProvider
            {
                Id = form.OriginalId,
                Name = form.Name,
                ApiUrl = form.ApiUrl,
                ApiKey = form.ApiKey,
                Model = form.Model,
                Translator = form.Translator,
                PromptMode = form.PromptMode,
                ExtraParams = form.ExtraParams,
                MaxCompletionTokens = form.MaxCompletionTokens,
                TimeoutSeconds = form.TimeoutSeconds
            })
            : new TranslationProviderFormDto();

        var dto = TranslationProviderFormDto.TryParse(data, fallback);

        switch (commandId)
        {
            case CmdCancel:
                return Task.FromResult<IPluginUIView>(TranslationTabView.FromConfiguration("已取消。"));

            case CmdSave:
            {
                if (string.IsNullOrWhiteSpace(dto.Name))
                    return Task.FromResult<IPluginUIView>(
                        ForDto(dto, "名称不能为空。"));

                var name = dto.Save();
                return Task.FromResult<IPluginUIView>(
                    TranslationTabView.FromConfiguration($"已保存 Provider「{name}」。"));
            }

            default:
                return Task.FromResult<IPluginUIView>(this);
        }
    }
}
#endif
