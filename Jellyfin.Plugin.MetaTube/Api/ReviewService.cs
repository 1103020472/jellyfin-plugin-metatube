#if __EMBY__
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaTube;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Jellyfin.Plugin.MetaTube.Api;

/// <summary>
/// Emby 详情页「拉取评论」按钮的后端接口。
///
/// 设计要点：
///   * 翻译可能耗时几十秒到几分钟，所以 <see cref="Any(PullReviews)"/> 只负责启动后台任务并
///     立即返回，绝不同步等待（否则会挂住请求线程直到超时）。
///   * 同一个条目在途时重复点击直接复用既有状态，不会重复抓取 / 重复翻译（避免重复消耗额度）。
///   * 只追加评论，不重新抓取或重写简介。
/// </summary>
public class ReviewService : IService
{
    /// <summary>
    /// Emby 的 ServiceStack 容器对插件自定义服务的构造注入不保证可用，
    /// 因此这里不依赖构造函数注入，而是在服务解析时从应用宿主取依赖。
    /// </summary>
    private static ILibraryManager LibraryManager =>
        Plugin.Instance.ApplicationHost.Resolve<ILibraryManager>();

    private static ILogger Logger => Plugin.Instance.logger;

    /// <summary>
    /// 进度状态。后台任务在写、Status 接口在读，字段用 volatile 语义的 Interlocked
    /// 更新，避免序列化时读到撕裂的对象；字符串是不可变引用，直接赋值即可。
    /// </summary>
    private sealed class PullState
    {
        public volatile string Status = "pending";
        public volatile string Stage = "fetching";
        public volatile string Message = "已开始处理…";
        public volatile bool Appended;
        public int NewReviewCount;
        public int TotalReviewCount;
        public int ChunkCount;
        public int FinishedChunks;
        public long ElapsedMs;

        public ReviewsStatusResult Snapshot() => new()
        {
            Status = Status,
            Stage = Stage,
            Message = Message,
            Appended = Appended,
            NewReviewCount = Volatile.Read(ref NewReviewCount),
            TotalReviewCount = Volatile.Read(ref TotalReviewCount),
            ChunkCount = Volatile.Read(ref ChunkCount),
            FinishedChunks = Volatile.Read(ref FinishedChunks),
            ElapsedMs = Volatile.Read(ref ElapsedMs)
        };
    }

    /// <summary>条目键（内部数字 Id 字符串）-> 进度状态。</summary>
    private static readonly ConcurrentDictionary<string, PullState> States = new();

    /// <summary>条目键 -> 在途标记，用于同一条目去重。</summary>
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();

    /// <summary>
    /// 把前端传来的 Id 解析成条目。
    ///
    /// Emby Web 的 <c>item.Id</c> 有两种形态：
    ///   * 内部数字 Id（如 "84684"）—— 详情页最常见的形态，用 GetItemById(long)；
    ///   * GUID 字符串 —— 用 GetItemById(Guid)。
    /// 两者都尝试，避免因形态不同而 500。
    /// </summary>
    private static BaseItem ResolveItem(string rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
            return null;

        var id = rawId.Trim();

        // 1. 内部数字 Id（Emby 4.x 详情页的 item.Id 常见形态）
        if (long.TryParse(id, out var numericId))
        {
            var byLong = LibraryManager.GetItemById(numericId);
            if (byLong != null)
                return byLong;
        }

        // 2. GUID
        if (Guid.TryParse(id, out var guid))
            return LibraryManager.GetItemById(guid);

        return null;
    }

    /// <summary>
    /// POST /emby/MetaTube/Reviews/Pull
    ///
    /// Emby 按 HTTP 动词分派到同名方法（Get/Post/Put/Delete），
    /// 所以这里必须叫 <c>Post</c> 而不是 <c>Any</c> —— 后者不会被调用。
    /// </summary>
    public object Post(PullReviews request)
    {
        var itemId = request.ItemId;
        if (string.IsNullOrWhiteSpace(itemId))
            return new PullReviewsResult { Status = "invalid", Message = "缺少 ItemId" };

        var item = ResolveItem(itemId);
        if (item == null)
            return new PullReviewsResult { Status = "invalid", Message = $"找不到条目：{itemId}" };

        // 用条目的内部 Id 作为状态键：前端每次可能传不同形态的 Id，
        // 但同一个条目必须归一到同一个键，否则去重和进度查询都会失效。
        var key = item.InternalId != 0 ? item.InternalId.ToString() : item.Id.ToString();

        // 已在跑 → 直接复用，不重复启动
        if (!InFlight.TryAdd(key, 1))
            return new PullReviewsResult { Status = "running", Message = "该条目正在处理中…" };

        var state = new PullState();
        States[key] = state;

        // 后台执行，接口立即返回
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            state.Status = "running";

            try
            {
                void OnProgress(string stage, int finished, int total, int newCount)
                {
                    state.Stage = stage;
                    Volatile.Write(ref state.FinishedChunks, finished);
                    Volatile.Write(ref state.ChunkCount, total);
                    if (newCount > 0) Volatile.Write(ref state.NewReviewCount, newCount);
                }

                // 后台任务没有请求级 CancellationToken，用一个长超时的令牌兜底
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

                var result = await ReviewAppender.AppendAsync(
                    item, Logger, LibraryManager, OnProgress, cts.Token, markAsManuallyPulled: true);

                state.Appended = result.Appended;
                Volatile.Write(ref state.NewReviewCount, result.NewReviewCount);
                Volatile.Write(ref state.TotalReviewCount, result.TotalReviewCount);
                Volatile.Write(ref state.ChunkCount, result.ChunkCount);
                state.Message = result.Message;
                state.Status = "done";
                state.Stage = "done";
            }
            catch (Exception e)
            {
                state.Status = "failed";
                state.Stage = "failed";
                state.Message = e.Message;
                Logger.Error("Pull reviews for {0} failed: {1}", item.Name, e.Message);
            }
            finally
            {
                Volatile.Write(ref state.ElapsedMs, sw.ElapsedMilliseconds);
                InFlight.TryRemove(key, out _);
            }
        });

        return new PullReviewsResult { Status = "started", Message = "已开始拉取评论…" };
    }

    /// <summary>GET /emby/MetaTube/Reviews/Status?ItemId=...</summary>
    public object Get(ReviewsStatus request)
    {
        // 与 Post 用同一种方式归一 Id，否则进度查询会查不到
        var item = ResolveItem(request.ItemId);
        var key = item != null
            ? (item.InternalId != 0 ? item.InternalId.ToString() : item.Id.ToString())
            : (request.ItemId ?? string.Empty).Trim();

        if (States.TryGetValue(key, out var state))
            return state.Snapshot();

        return new ReviewsStatusResult
        {
            Status = "none",
            Stage = "none",
            Message = "尚未触发过"
        };
    }
}
#endif
