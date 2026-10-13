using System;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 识别「不是错误」的接口失败。
///
/// 背景：MetaTube 服务端在两种完全正常的业务情况下也返回 404：
///   1. <c>/v1/reviews/{provider}/{id}</c> —— 该番号在源站没有评论（实测 ofje00614 即如此，
///      服务端模型里评论允许为空，但 DB 查不到记录时会走 <c>info not found</c>）；
///   2. <c>/v1/actors/search</c> —— 搜不到任何演员（未刮削过的演员），
///      服务端在结果数为 0 时主动返回 404（见 route/search.go 的 resultsLength == 0 分支）。
///
/// 插件此前把这两种情况一律按 <c>Error</c> 记录，日志里看起来像故障，
/// 实际是正常的「没有数据」。这类日志应该降级为 Info/Warn，并且把原因写清楚，
/// 否则真正的服务端故障会被淹没在这些噪音里。
/// </summary>
public static class ApiErrorClassifier
{
    /// <summary>服务端表示「资源不存在」的固定文案。</summary>
    private const string InfoNotFound = "info not found";

    private const string ImageNotFound = "image not found";

    private const string ProviderNotFound = "provider not found";

    private const string PlainNotFound = "Not Found";

    /// <summary>
    /// 是否属于「目标资源不存在」——即业务上的空结果，而非请求失败。
    ///
    /// 匹配服务端 <c>{"error":{"code":404,...}}</c> 序列化到 <see cref="Exception.Message"/>
    /// 后的几种固定写法（见 SDK provider/errors.go 与 errors.FromCode）。
    /// </summary>
    public static bool IsNotFound(Exception e)
    {
        var message = e?.Message;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains(InfoNotFound, StringComparison.OrdinalIgnoreCase)
               || message.Contains(ImageNotFound, StringComparison.OrdinalIgnoreCase)
               || message.Contains(ProviderNotFound, StringComparison.OrdinalIgnoreCase)
               || message.Contains($"404 ({PlainNotFound})", StringComparison.OrdinalIgnoreCase)
               // 服务端 errors.FromCode(404) 只有裸 "Not Found"
               || message.Trim().EndsWith(PlainNotFound, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否属于「番号在源站没有评论」。与 <see cref="IsNotFound"/> 的区别在于
    /// 这里明确排除了「provider 不支持」等其它 404，便于调用方给出准确文案。
    /// </summary>
    public static bool IsNoReviews(Exception e)
    {
        return IsNotFound(e);
    }
}
