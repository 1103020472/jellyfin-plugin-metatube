using Jellyfin.Plugin.MetaTube.Translation;

namespace Jellyfin.Plugin.MetaTube.Configuration;

/// <summary>
/// 一个 OpenAI 兼容翻译通道（Provider）。
///
/// 设计参考 DSH 的 provider 配置：把「接口地址 / 模型 / Key / 翻译组件 /
/// 额外请求体参数 / 最大生成 token」打包成一个可复用的命名配置，
/// 翻译时从列表里选一个作为主通道，再分别配置敏感词与超时两条降级链路。
///
/// 注意：这里只描述 OpenAI 兼容接口。百度 / Google / DeepL 仍是单套凭据，
/// 不参与 Provider 列表。
/// </summary>
public class TranslationProvider
{
    /// <summary>
    /// 稳定标识。场景路由（主通道 / 敏感词备用 / 超时备用）保存的是这个值，
    /// 所以改名不会影响路由。旧配置迁移时使用固定值，避免重启后路由失效。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称，仅用于设置页与日志。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>兼容 OpenAI 的接口地址；留空表示使用 SDK / 官方默认地址。</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>接口 Key。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名；留空表示使用 SDK 默认模型。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>翻译组件：openai-translator（默认）/ go-openai。</summary>
    public OpenAiTranslator Translator { get; set; } = OpenAiTranslator.OpenAiTranslator;

    /// <summary>
    /// 提示词组装方式（默认 default，三段结构）。
    ///
    /// 本地小模型建议选 single：指令与原文合并成一条消息，长分片下目标语言约束更强。
    /// 实测 qwen3.5:9b 在 default 下会把某些 900+ 字符的日文评论分片翻成英文，
    /// 同一分片换 single 后稳定输出中文。非 default 时 SDK 会自动改用 go-openai 实现。
    /// </summary>
    public OpenAiPromptMode PromptMode { get; set; } = OpenAiPromptMode.Default;

    /// <summary>合并进 chat completion 请求体的额外 JSON 字段（仅 go-openai 生效）。</summary>
    public string ExtraParams { get; set; } = string.Empty;

    /// <summary>单次请求最大生成 token（含思维链），0 表示 SDK 默认值。</summary>
    public int MaxCompletionTokens { get; set; } = 4096;

    /// <summary>
    /// 本 Provider 的单次请求超时（秒）。
    /// 0 = 使用全局「API 请求超时」；大于 0 时覆盖全局值。
    /// 只有配置了独立的超时，主通道超时后才能及时切到备用通道。
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>给日志 / 设置页用的简短描述。</summary>
    public string Describe()
    {
        var model = string.IsNullOrWhiteSpace(Model) ? "默认模型" : Model.Trim();
        var timeout = TimeoutSeconds > 0 ? $"{TimeoutSeconds}s" : "全局超时";
        // 只有非默认提示词模式才标出来，避免列表里全是噪音。
        var mode = PromptMode == OpenAiPromptMode.Default ? string.Empty : $" · {PromptMode.ToSdkValue()}";
        return $"{model} · {timeout}{mode}";
    }
}
