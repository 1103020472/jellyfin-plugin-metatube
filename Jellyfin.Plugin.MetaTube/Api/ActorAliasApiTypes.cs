#if __EMBY__
using System;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Jellyfin.Plugin.MetaTube.Api;

/// <summary>
/// 演员页「关联为常用名」按钮的后端接口。
///
/// 场景：很老的番号刮出来的演员名还是这位演员的旧艺名（别名），用户希望
/// 一次性把这个名字挂到常用名下面，以后刮到旧名自动换成常用名。
/// 前端只需要把「当前演员页上的名字」+「用户填的常用名」发过来，
/// 各种边界（已关联 / 挂在别人下面 / 本身是常用名）都由服务端判断并回话。
/// </summary>
[Route("/MetaTube/ActorAlias/Link", "POST", Summary = "把一个演员名（别名）挂到某个「常用名」下")]
[Authenticated]
public class LinkActorAlias : IReturn<LinkActorAliasResult>
{
    /// <summary>别名：通常是演员详情页上显示的那个名字。多个用逗号分隔。</summary>
    public string Alias { get; set; }

    /// <summary>要挂到的常用名。</summary>
    public string CommonName { get; set; }

    /// <summary>「别名现在挂在别的常用名下」时，用户确认改挂后传 true。</summary>
    public bool Overwrite { get; set; }

    /// <summary>「别名本身是一条常用名」时，用户确认整条并过来后传 true。</summary>
    public bool MergeGroup { get; set; }
}

/// <summary>关联结果。</summary>
public class LinkActorAliasResult
{
    /// <summary>
    /// ok       = 已保存
    /// exists   = 本来就挂在同一个常用名下，什么都没改
    /// confirm  = 需要用户确认（见 NeedOverwrite / NeedMerge），确认后带着对应标记重发
    /// invalid  = 参数问题
    /// error    = 服务端异常
    /// </summary>
    public string Status { get; set; }

    /// <summary>给用户看的说明文字。</summary>
    public string Message { get; set; }

    /// <summary>确认后重发时要带上的标记。</summary>
    public bool NeedOverwrite { get; set; }

    /// <summary>确认后重发时要带上的标记。</summary>
    public bool NeedMerge { get; set; }

    public string Alias { get; set; }

    public string CommonName { get; set; }

    /// <summary>关联完成后该常用名下的全部别名（方便前端提示）。</summary>
    public string[] Aliases { get; set; } = Array.Empty<string>();

    /// <summary>本次是否新建了常用名。</summary>
    public bool Created { get; set; }
}

/// <summary>
/// 查一个演员名在替换表里的身份：是常用名？还是某个常用名下的别名？
/// 演员页的按钮文案要靠它显示「当前常用名：xxx」/「当前别名：xxx、xxx」。
/// </summary>
[Route("/MetaTube/ActorAlias/Lookup", "POST", Summary = "查询演员名在替换表里的身份")]
[Authenticated]
public class LookupActorAlias : IReturn<LookupActorAliasResult>
{
    /// <summary>要查询的演员名。</summary>
    public string Name { get; set; }
}

/// <summary>查询结果。</summary>
public class LookupActorAliasResult
{
    /// <summary>
    /// common = 这个名字本身就是一条常用名（Aliases 是它下面挂的别名）
    /// alias  = 这个名字是某个常用名下的别名（CommonName 是所属常用名）
    /// none   = 表里没有这个名字
    /// invalid = 参数问题
    /// error  = 服务端异常
    /// </summary>
    public string Status { get; set; }

    public string Name { get; set; }

    /// <summary>所属（或自身即）常用名。</summary>
    public string CommonName { get; set; }

    /// <summary>该常用名下的全部别名。</summary>
    public string[] Aliases { get; set; } = Array.Empty<string>();
}
#endif
