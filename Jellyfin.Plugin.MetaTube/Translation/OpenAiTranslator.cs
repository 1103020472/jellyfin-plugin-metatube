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
    [Description("openai-translator (default)")]
    OpenAiTranslator,

    /// <summary>
    /// github.com/sashabaranov/go-openai，支持额外参数（思考度）与最大 token 配置。
    /// </summary>
    [Description("go-openai")]
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
