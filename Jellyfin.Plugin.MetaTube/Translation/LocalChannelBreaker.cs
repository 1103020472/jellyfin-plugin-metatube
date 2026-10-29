namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
/// 本地优先通道（L）的失败冷却，一个极简熔断器。
///
/// 为什么需要：本地模型常常跑在另一台机器上（Ollama 在主机、Emby 在 NAS），
/// 主机没开机时 L 连不通。一次批量刮削会翻译几十上百个分片，
/// 如果每个分片都先去撞一次，就是几十上百次连接失败 / 超时白等，日志也被刷满。
/// 冷却期内直接跳过 L 走主通道 A，主机开机后最多等一个冷却周期就自动恢复。
///
/// 状态机（按通道 Key 记账，换 Provider / 改地址即视为新通道）：
///   * <see cref="State.Closed"/>：正常，每个请求都先试 L；
///   * <see cref="State.Open"/>：冷却中，全部跳过 L 直接走 A，直到 <c>_retryAfterUtc</c> 到期；
///   * <see cref="State.HalfOpen"/>：冷却到期，只放**一个**请求去探活，其余照旧跳过。
///     探活成功 → Closed；失败 → 重新计时；被取消 → 退回 Open（下次再探）。
///
/// 只有「请求抛异常」才算故障。空结果 / 拒答文案是这一片的内容问题，
/// 不能拿来冷却，否则一条敏感评论就会让本地通道停用十分钟。
///
/// 状态是进程内的静态字段：进程重启（Emby 重启）后冷却清零，会重新试一次 L，
/// 这个行为是期望的——重启往往意味着用户刚调整过环境。
/// </summary>
internal static class LocalChannelBreaker
{
    private enum State
    {
        Closed,
        Open,
        HalfOpen
    }

    private static readonly object Sync = new();

    /// <summary>当前记账的通道身份；与传入 Key 不同就重置状态。</summary>
    private static string _key = string.Empty;

    private static State _state = State.Closed;

    /// <summary>Open 状态下允许重新探活的时间点。</summary>
    private static DateTime _retryAfterUtc = DateTime.MinValue;

    /// <summary>本次冷却是否已经打过「跳过 L」的日志（只在第一次跳过时打，避免刷屏）。</summary>
    private static bool _skipLogged;

    /// <summary>放行 / 拦截结论。</summary>
    internal readonly struct Ticket
    {
        /// <summary>本次是否应该尝试 L。</summary>
        public bool Allowed { get; init; }

        /// <summary>是否为冷却到期后的探活请求：结束时必须回报结果。</summary>
        public bool Probe { get; init; }

        /// <summary>本次是否应该打一条「冷却中，跳过 L」的日志。</summary>
        public bool AnnounceSkip { get; init; }

        /// <summary>剩余冷却时长（仅 <see cref="AnnounceSkip"/> 为 true 时有意义）。</summary>
        public TimeSpan Remaining { get; init; }
    }

    /// <summary>
    /// 判断本次是否该尝试 L。返回 <c>Allowed == false</c> 时调用方应直接走主通道 A。
    /// </summary>
    /// <param name="key">通道身份（Provider.Id + 地址 + 模型）。</param>
    /// <param name="cooldown">冷却时长；<see cref="TimeSpan.Zero"/> 表示不冷却、永远放行。</param>
    public static Ticket Enter(string key, TimeSpan cooldown)
    {
        // 没配冷却（0）：永远放行，并顺手把状态复位，避免之后改成非 0 时用到过期状态。
        if (cooldown <= TimeSpan.Zero)
        {
            lock (Sync)
            {
                Reset(key);
            }

            return new Ticket { Allowed = true };
        }

        lock (Sync)
        {
            if (!string.Equals(_key, key, StringComparison.Ordinal))
                Reset(key);

            var now = DateTime.UtcNow;

            switch (_state)
            {
                case State.Closed:
                    return new Ticket { Allowed = true };

                case State.HalfOpen:
                    // 已经有一个探活请求在路上，别再挤一个。
                    return new Ticket { Allowed = false };

                default:
                    if (now < _retryAfterUtc)
                    {
                        var announce = !_skipLogged;
                        _skipLogged = true;
                        return new Ticket
                        {
                            Allowed = false,
                            AnnounceSkip = announce,
                            Remaining = _retryAfterUtc - now
                        };
                    }

                    _state = State.HalfOpen;
                    return new Ticket { Allowed = true, Probe = true };
            }
        }
    }

    /// <summary>L 请求成功（拿到响应，哪怕是空串）：端点可用，关闭熔断。</summary>
    public static void ReportSuccess(string key)
    {
        lock (Sync)
        {
            Reset(key);
        }
    }

    /// <summary>L 请求抛异常（连接失败 / 超时 / 404 model not found）：进入冷却。</summary>
    public static void ReportFailure(string key, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
            return;

        lock (Sync)
        {
            if (!string.Equals(_key, key, StringComparison.Ordinal))
                Reset(key);

            _state = State.Open;
            _retryAfterUtc = DateTime.UtcNow + cooldown;
            _skipLogged = false;
        }
    }

    /// <summary>
    /// 放弃本次探活（用户取消 / 内容审核错误）：不算端点故障，但要把探活位让出来，
    /// 否则状态会永远停在 HalfOpen，L 再也不会被尝试。
    /// </summary>
    public static void Abandon(string key)
    {
        lock (Sync)
        {
            if (!string.Equals(_key, key, StringComparison.Ordinal))
                return;

            if (_state != State.HalfOpen)
                return;

            // 保持 Open 且 _retryAfterUtc 已在过去 → 下一个请求立刻成为新的探活。
            _state = State.Open;
            _skipLogged = false;
        }
    }

    private static void Reset(string key)
    {
        _key = key ?? string.Empty;
        _state = State.Closed;
        _retryAfterUtc = DateTime.MinValue;
        _skipLogged = false;
    }
}
