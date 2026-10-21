using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

public enum TranslationMode
{
    [Description("不翻译")]
    Disabled,

    [Description("仅标题")]
    Title,

    [Description("仅简介")]
    Summary,

    [Description("标题和简介")]
    Both
}
