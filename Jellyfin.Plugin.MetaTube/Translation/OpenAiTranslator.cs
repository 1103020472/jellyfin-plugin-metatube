using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
/// 翻译时使用的 OpenAI 客户端实现（对应 SDK 的 openai-translator 参数）。
/// </summary>
public enum OpenAiTranslator
{
    /// <summary>
    /// 默认实现：github.com/xjasonlyu/openai-translator，保持历史行为不变。
    /// </summary>
    [Description("openai-translator（默认）")]
    OpenAiTranslator,

    /// <summary>
    /// github.com/sashabaranov/go-openai，支持额外参数（思考度）与最大 token 配置。
    /// </summary>
    [Description("go-openai（支持额外参数 / 最大 token）")]
    GoOpenAi
}

public static class OpenAiTranslatorExtensions
{
    /// <summary>
    /// 映射为 SDK <c>/v1/translate</c> 接口的 <c>openai-translator</c> 参数值。
    /// </summary>
    public static string ToSdkValue(this OpenAiTranslator translator) =>
        translator == OpenAiTranslator.GoOpenAi ? "go-openai" : "openai-translator";
}

/// <summary>
/// 提示词组装方式（对应 SDK 的 <c>openai-prompt-mode</c> 参数）。
///
/// 只影响消息结构，不改变翻译内容。本地小模型（如 qwen）在 default 的三段结构下，
/// 长分片里对"目标语言"的约束会被稀释，实测出现过把日文翻成英文的情况；
/// 改成 <see cref="Single"/> 后指令紧贴原文，问题消失。
/// </summary>
public enum OpenAiPromptMode
{
    /// <summary>三段：system + 指令 + 原文（历史行为，默认）。</summary>
    [Description("default（system + 指令 + 原文，三段）")]
    Default,

    /// <summary>
    /// 单条 user：指令与原文合并，不带 system。
    /// 本地翻译模型（Hunyuan-MT 等）只认这种格式；qwen 等长文本也更稳。
    /// </summary>
    [Description("single（指令 + 原文合并，无 system）")]
    Single,

    /// <summary>system + 单条 user：保留 system 规则，但指令与原文合并。</summary>
    [Description("system-single（system + 合并后的 user）")]
    SystemSingle
}

public static class OpenAiPromptModeExtensions
{
    /// <summary>
    /// 映射为 SDK <c>/v1/translate</c> 接口的 <c>openai-prompt-mode</c> 参数值。
    /// 非 default 时 SDK 会自动改用 go-openai 实现（legacy 库控制不了消息结构）。
    /// </summary>
    public static string ToSdkValue(this OpenAiPromptMode mode) => mode switch
    {
        OpenAiPromptMode.Single => "single",
        OpenAiPromptMode.SystemSingle => "system-single",
        _ => "default"
    };
}
