using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

public static class TranslationHelper
{
    private const string AutoLanguageCode = "auto";
    private const string JapaneseLanguageCode = "ja";

    /// <summary>
    /// 翻译并发闸门。并发上限来自配置项 <c>MaxTranslationConcurrency</c>，
    /// 并且**支持运行时动态调整**（改配置后无需重启，见 <see cref="ApplyConcurrencyLimit"/>）。
    ///
    /// 之所以不用 <c>SemaphoreSlim</c>：它的并发上限在构造时固定，无法缩小；
    /// 并发数调小后必须等旧请求自然跑完才能生效，期间"配置已改但没生效"很难排查。
    /// 这里用一个可增可减的计数器：调大立刻放行更多，调小则等已发出的请求返回后自然收敛。
    /// </summary>
    private static readonly TranslationGate Gate = new();

    /// <summary>上一次生效的并发值，用于检测配置变化（0 表示尚未初始化）。</summary>
    private static int _appliedConcurrency;

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    /// <summary>
    /// 按当前配置刷新并发上限。每次发起翻译前都会调用，
    /// 因此用户在配置页改完并发数后，下一次翻译就生效（无需重启）。
    /// </summary>
    private static void ApplyConcurrencyLimit()
    {
        var limit = Configuration.MaxTranslationConcurrency;
        if (limit <= 0)
            limit = DefaultMaxTranslationConcurrency;

        if (Interlocked.Exchange(ref _appliedConcurrency, limit) != limit)
            Gate.SetLimit(limit);
    }

    /// <summary>配置留空/非法时使用的默认并发数（与旧版本写死的 5 一致）。</summary>
    public const int DefaultMaxTranslationConcurrency = 5;

    /// <summary>
    /// 可动态调整上限的并发闸门。
    ///
    /// 实现要点：<see cref="SemaphoreSlim"/> 无法缩小上限，所以自己用计数器实现。
    /// 调大时直接抬高上限并唤醒等待者；调小时只降低上限，不打断已获得许可的请求，
    /// 它们释放后 <see cref="Release"/> 会让占用数自然降到新上限以下。
    /// </summary>
    private sealed class TranslationGate
    {
        private readonly object _sync = new();
        private int _limit;
        private int _current;
        private readonly LinkedList<TaskCompletionSource<bool>> _waiters = new();

        public TranslationGate()
        {
            _limit = DefaultMaxTranslationConcurrency;
        }

        public void SetLimit(int limit)
        {
            if (limit < 1)
                limit = 1;

            LinkedList<TaskCompletionSource<bool>> toRelease = null;

            lock (_sync)
            {
                _limit = limit;

                // 上限调大后，可能有等待者已经能进：按 FIFO 逐个放行。
                while (_waiters.Count > 0 && _current < _limit)
                {
                    var waiter = _waiters.First.Value;
                    _waiters.RemoveFirst();
                    _current++;
                    (toRelease ??= new LinkedList<TaskCompletionSource<bool>>()).AddLast(waiter);
                }
            }

            // 在锁外完成，避免唤醒后的续体在持锁线程上同步跑起来造成死锁。
            if (toRelease == null)
                return;

            foreach (var waiter in toRelease)
                waiter.TrySetResult(true);
        }

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_current < _limit)
                {
                    _current++;
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var node = _waiters.AddLast(tcs);
                if (cancellationToken.CanBeCanceled)
                {
                    // 取消时把等待者从队列摘掉，否则会永久占位导致并发数慢慢"泄漏"到 0。
                    cancellationToken.Register(() =>
                    {
                        lock (_sync)
                        {
                            if (node.List != null)
                            {
                                _waiters.Remove(node);
                                tcs.TrySetCanceled(cancellationToken);
                            }
                        }
                    });
                }

                return tcs.Task;
            }
        }

        public void Release()
        {
            TaskCompletionSource<bool> next = null;
            lock (_sync)
            {
                if (_current > 0)
                    _current--;

                if (_waiters.Count > 0 && _current < _limit)
                {
                    next = _waiters.First.Value;
                    _waiters.RemoveFirst();
                    _current++;
                }
            }

            next?.TrySetResult(true);
        }
    }

    #region 源语言判定

    /// <summary>
    /// 汉字 / (汉字 + 假名) 达到该比例时，认为是「无假名的中文」。
    /// 与 <see cref="ChineseRatioThreshold"/> 同源，用于识别已翻译文本。
    /// </summary>
    private const double HanOnlyChineseThreshold = 0.9;

    /// <summary>
    /// 推断请求正文的源语言，替代一律使用 "auto"。
    ///
    /// 背景（实测于 SDK + SiliconFlow DeepSeek-V4-Flash）：
    /// "auto" 会让 SDK 走 "Please translate the following text into Chinese:" 分支
    /// （见 SDK openai.go 的 lookupLanguage(source)=="auto"），不告诉模型源语言是什么，
    /// 且目标语言是唯一语境。模型因此把「REbecca STARS13-The marvels-」这类
    /// 「英文词 + 番号」混杂的短串当成一个待处理任务，进而：
    ///   1) 输出整段英文思维链（"The user wants me to translate..."）当作译文；
    ///   2) 把 The marvels 误判成漫威 IP，译成「漫威 / 惊奇队长2」。
    /// 实测给定源语言后行为显著收敛（from=en → 「奇迹」，from=ja → 直译）。
    ///
    /// 这里只做高置信度的粗判，拿不准时仍退回 "auto"（保持原有行为，不做无根据猜测）。
    /// </summary>
    private static string DetectSourceLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AutoLanguageCode;

        // 1. 有假名 → 日文。日文标题常混有大量汉字与拉丁字母，
        //    只有假名是日文独有的强特征，所以这一条必须排在最前。
        if (JapaneseKanaRegex.IsMatch(text)) return JapaneseLanguageCode;

        // 2. 有汉字且汉字占比很高、又无假名 → 视为中文（无需翻译，源语言与目标一致）。
        if (CountHan(text) > 0 && HanRatio(text) >= HanOnlyChineseThreshold) return "zh-CN";

        // 3. 形如番号（VRKM-01234 / 3DSVR00123 / MBDD-2076 / NO.1）的纯目录 token：
        //    没有自然语言可翻译，交回引擎自动识别，避免把番号当英文送去翻译
        //    （否则模型会试图解释 "STARS13" 的含义，产出思维链）。
        if (IsCatalogNumber(text)) return AutoLanguageCode;

        // 4. 有拉丁字母但无假名 → 多为英文标题 / 英文词混番号，按英文处理。
        if (text.Any(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z')) return "en";

        // 5. 其余（纯数字 / 纯符号 / 纯韩文等）无法判断，交回引擎自动识别。
        return AutoLanguageCode;
    }

    /// <summary>
    /// 判断整段文本是否只是一个"番号形状"的目录 token（VRKM-01234 / 3DSVR00123 /
    /// MBDD-2076 / STARS13 / NO.1）。
    ///
    /// 判定依据：整段只有一个「字母块」，且数字紧跟在它后面（可含 - _ 空格分隔）。
    /// 真正的自然语言标题总会有多个单词（如 "STARS13-The marvels-" 有 marvels），
    /// 因此不会被误判。用于避免把番号当英文送去翻译。
    /// </summary>
    private static bool IsCatalogNumber(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || !t.Any(char.IsDigit)) return false;

        // 统计"字母块"数量：连续的拉丁字母记作一块。
        var blocks = 0;
        var inBlock = false;
        var digitSeen = false;

        foreach (var c in t)
        {
            var isLatin = c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z';
            if (isLatin)
            {
                if (!inBlock) { blocks++; inBlock = true; }
            }
            else
            {
                inBlock = false;
                if (char.IsDigit(c)) digitSeen = true;
            }
        }

        // 只有一个字母块 + 含数字 → 番号形状（STARS13、VRKM-01234、NO.1）。
        // 注意 "STARS13-The marvels-" 有两个字母块，不在此列。
        return digitSeen && blocks <= 1;
    }

    #endregion

    private static async Task<string> TranslateAsync(string q, string from, string to, string title, int chunkIndex,
        CancellationToken cancellationToken)
    {
        int millisecondsDelay;
        var nv = new NameValueCollection();
        switch (Configuration.TranslationEngine)
        {
            case TranslationEngine.Baidu:
                millisecondsDelay = 1000; // Limit Baidu API request rate to 1 rps.
                nv.Add(new NameValueCollection
                {
                    { "baidu-app-id", Configuration.BaiduAppId },
                    { "baidu-app-key", Configuration.BaiduAppKey }
                });
                break;
            case TranslationEngine.Google:
                millisecondsDelay = 100; // Limit Google API request rate to 10 rps.
                nv.Add(new NameValueCollection
                {
                    { "google-api-key", Configuration.GoogleApiKey },
                    { "google-api-url", Configuration.GoogleApiUrl }
                });
                break;
            case TranslationEngine.GoogleFree:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection());
                break;
            case TranslationEngine.DeepL:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection
                {
                    { "deepl-api-key", Configuration.DeepLApiKey },
                    { "deepl-api-url", Configuration.DeepLApiUrl }
                });
                break;
            case TranslationEngine.OpenAi:
                millisecondsDelay = 10;
                nv.Add(new NameValueCollection
                {
                    { "openai-api-key", Configuration.OpenAiApiKey },
                    { "openai-api-url", Configuration.OpenAiApiUrl },
                    { "openai-model", Configuration.OpenAiModel },
                    { "openai-extra-params", Configuration.OpenAiExtraParams },
                    { "openai-max-completion-tokens", Configuration.OpenAiMaxCompletionTokens.ToString() }
                });
                break;
            default:
                throw new ArgumentException($"Invalid translation engine: {Configuration.TranslationEngine}");
        }

        // 每次翻译前同步一次并发配置，实现"改配置即生效"。
        ApplyConcurrencyLimit();
        await Gate.WaitAsync(cancellationToken);

        try
        {
            async Task<string> TranslateWithDelay()
            {
                await Task.Delay(millisecondsDelay, cancellationToken);
                var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var translationInfo = await ApiClient
                    .TranslateAsync(q, from, to, Configuration.TranslationEngine.ToString(), nv, cancellationToken)
                    .ConfigureAwait(false);
                LogInfo("Translating {0}#{1} end, cost:{2}ms, chars:{3}", title, chunkIndex,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, q?.Length ?? 0);
                return translationInfo.TranslatedText;
            }

            var context = $"{title}#{chunkIndex}({q?.Length ?? 0}chars)";
            return await RetryAsync(TranslateWithDelay, 1, context, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

     public static async Task TranslateAsync(MovieInfo m, string to, CancellationToken cancellationToken)
    {
        if (string.Equals(to, JapaneseLanguageCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"language not allowed: {to}");

        // 用于追踪所有的异步任务
        var titleTask = Task.FromResult<string>(null);
        var summaryTasks = new List<Task<string>>();
        var reviewHtmlTask = Task.FromResult<string>(null); // 评论整段HTML翻译任务

        // ==============================================
        // 【第一步：提前拼接评论 HTML（不翻译，只拼结构）】
        // ==============================================
        string reviewHtmlContent = string.Empty;
        if (m.Reviews != null && m.Reviews.Any())
        {
            var sb = new StringBuilder();

            foreach (var c in m.Reviews)
            {
                // 1. 作者 + 时间
                string author = string.IsNullOrWhiteSpace(c.Author) ? "匿名用户" : c.Author;
                string commentTime = c.Date != DateTime.MinValue ? $"{c.Date:yyyy-MM-dd HH:mm}" : "";
                sb.Append($"👤 {author}");
                if (!string.IsNullOrWhiteSpace(commentTime))
                    sb.Append($" · {commentTime}");
                sb.Append("<br>");

                // 2. 星级 + 标题（星级用占位符，翻译完再还原，见 StarTokenRegex）
                int score = Math.Clamp(c.Score, 0, 5);
                string starIcon = BuildStarToken(score);
                string commentTitle = string.IsNullOrWhiteSpace(c.Title) ? "" : c.Title;
                if (!string.IsNullOrWhiteSpace(starIcon) || !string.IsNullOrWhiteSpace(commentTitle))
                    sb.Append($"<b>{starIcon} {commentTitle}</b><br>");

                // 3. 评论内容（原文，暂不翻译）
                if (!string.IsNullOrWhiteSpace(c.Comment))
                    sb.Append($"{NormalizeReviewBody(c.Comment)}<br>");

                // 评论之间只留一个空行：上一条的 "…<br>" 已经换了一行，这里再补一个 <br>
                // 正好凑成 <br><br>（一个空行）。之前多补一个会变成 <br><br><br>，
                // 渲染出来就是两条评论之间空两行。
                sb.Append("<br>");
            }

            reviewHtmlContent = sb.ToString();
        }

        // ==============================================
        // 【第二步：准备翻译任务】
        // ==============================================

        // --- 1. 标题翻译 ---
        if (Configuration.TranslationMode.HasFlag(TranslationMode.Title) && !string.IsNullOrWhiteSpace(m.Title))
        {
            // 标题是短文本、且经常是「英文词 + 番号」混杂，最容易触发模型的"元思考"，
            // 因此这里必须给出明确的源语言，不能放任 auto（见 DetectSourceLanguage 注释）。
            var titleFrom = DetectSourceLanguage(m.Title);
            titleTask = TranslateAsync(m.Title, titleFrom, to, m.Id, 0, cancellationToken);
        }

        // --- 2. 摘要翻译（分片）---
        List<string> summaryChunks = new List<string>();
        if (Configuration.TranslationMode.HasFlag(TranslationMode.Summary) && !string.IsNullOrWhiteSpace(m.Summary))
        {
            summaryChunks = SplitText(m.Summary, Configuration.MaxChunkSize);
#if __EMBY__
            Plugin.Instance.logger.Info("摘要分片数量: {0}", summaryChunks.Count);
#endif
            foreach (var (chunk, index) in summaryChunks.Select((chunk, idx) => (chunk, idx)))
            {
                summaryTasks.Add(TranslateAsync(chunk, DetectSourceLanguage(chunk), to, m.Id, index + 1, cancellationToken));
            }
        }

        // --- 3. 评论整段 HTML 翻译（完全复用 Summary 分片逻辑）---
        List<string> reviewChunks = new List<string>();
        if (Configuration.TranslationMode.HasFlag(TranslationMode.Summary) && !string.IsNullOrWhiteSpace(reviewHtmlContent))
        {
            // 评论内容也按最大分片大小切割，和摘要完全一样
            reviewChunks = SplitText(reviewHtmlContent, Configuration.MaxChunkSize);
#if __EMBY__
            Plugin.Instance.logger.Info("评论HTML分片数量: {0}", reviewChunks.Count);
#endif

            var reviewTasks = new List<Task<string>>();
            foreach (var (chunk, index) in reviewChunks.Select((chunk, idx) => (chunk, idx)))
            {
                // taskId 从 5000 开始，避免和其他任务冲突
                reviewTasks.Add(TranslateAsync(chunk, DetectSourceLanguage(chunk), to, m.Id, 5000 + index, cancellationToken));
            }

            // 等待评论所有分片完成，合并成完整翻译后的 HTML
            var translatedReviewChunks = await Task.WhenAll(reviewTasks);
            reviewHtmlTask = Task.FromResult(RestoreStarTokens(RepairBoldTags(string.Join("<br>", translatedReviewChunks))));
        }

        // ==============================================
        // 【第三步：统一等待所有任务】
        // ==============================================
        var allTasks = new List<Task>(summaryTasks);
        if (titleTask != Task.FromResult<string>(null))
            allTasks.Add(titleTask);
        if (reviewHtmlTask != Task.FromResult<string>(null))
            allTasks.Add(reviewHtmlTask);

        await Task.WhenAll(allTasks);

        // ==============================================
        // 【第四步：回写翻译结果】
        // ==============================================

        // 1. 标题
        if (titleTask.Status == TaskStatus.RanToCompletion)
        {
            m.Title = await titleTask;
        }

        // 2. 摘要
        string finalSummary = m.Summary ?? "";
        if (summaryTasks.Any())
        {
            var translatedChunks = await Task.WhenAll(summaryTasks);
            finalSummary = string.Join("", translatedChunks);
        }

        // 3. 把【翻译后的评论HTML】追加到摘要末尾
        if (reviewHtmlTask.Status == TaskStatus.RanToCompletion)
        {
            var translatedReviewHtml = await reviewHtmlTask;
            if (!string.IsNullOrWhiteSpace(translatedReviewHtml))
            {
                var sb = new StringBuilder();

                // 评论区标题 + 分割线
                sb.Append("<br><br>");
                sb.Append("————————————————————————————<br>");
                sb.Append("<font color=\"#fce83a\" size=\"4\"><b>📝 观众评论</b></font><br>");
                sb.Append("<br>");
                finalSummary += sb.ToString();
                finalSummary += translatedReviewHtml;
            }
        }

        // 最终回写到 Summary
        m.Summary = finalSummary;
    }

    /// <summary>
    /// 只翻译评论的结果。
    /// </summary>
    public sealed class ReviewTranslationResult
    {
        /// <summary>翻译并排版好的评论 HTML（含 📝 观众评论 表头），失败时为空。</summary>
        public string Html { get; set; }

        /// <summary>实际切出的分片数。</summary>
        public int ChunkCount { get; set; }
    }

    /// <summary>
    /// 只翻译 <see cref="MovieInfo.Reviews"/>，返回可直接追加到 Overview 的 HTML 片段。
    ///
    /// 与 <see cref="TranslateAsync(MovieInfo, string, CancellationToken)"/> 里的评论分支保持
    /// 完全一致的拼接格式、分片方式与源语言判定，区别仅是：
    ///   * 不碰标题 / 摘要（按需拉评论时不应该重写简介）；
    ///   * 通过 <paramref name="onProgress"/> 回报分片完成情况，供 Emby 详情页按钮显示。
    ///
    /// 分片是并发翻译的（<c>Task.WhenAll</c>），所以进度是「已完成 N/M 片」的离散量，
    /// 不是时间比例 —— 可能长时间停在 0，然后一次性跳到 M。
    /// </summary>
    public static async Task<ReviewTranslationResult> TranslateReviewsAsync(
        MovieInfo m,
        string to,
        Action<string, int, int, int> onProgress,
        CancellationToken cancellationToken)
    {
        var result = new ReviewTranslationResult { Html = string.Empty };
        if (m?.Reviews == null || !m.Reviews.Any())
            return result;

        // ---- 第一步：先拼评论 HTML 结构（不翻译）----
        var sb = new StringBuilder();
        foreach (var c in m.Reviews)
        {
            var author = string.IsNullOrWhiteSpace(c.Author) ? "匿名用户" : c.Author;
            var commentTime = c.Date != DateTime.MinValue ? $"{c.Date:yyyy-MM-dd HH:mm}" : "";
            sb.Append($"👤 {author}");
            if (!string.IsNullOrWhiteSpace(commentTime))
                sb.Append($" · {commentTime}");
            sb.Append("<br>");

            // 星级用占位符，翻译完再还原（见 StarTokenRegex）
            var score = Math.Clamp(c.Score, 0, 5);
            var starIcon = BuildStarToken(score);
            var commentTitle = string.IsNullOrWhiteSpace(c.Title) ? "" : c.Title;
            if (!string.IsNullOrWhiteSpace(starIcon) || !string.IsNullOrWhiteSpace(commentTitle))
                sb.Append($"<b>{starIcon} {commentTitle}</b><br>");

            if (!string.IsNullOrWhiteSpace(c.Comment))
                sb.Append($"{NormalizeReviewBody(c.Comment)}<br>");

            // 评论之间只留一个空行（理由同 TranslateAsync 里的评论分支）。
            sb.Append("<br>");
        }

        var reviewHtmlContent = sb.ToString();

        // ---- 第二步：分片 + 并发翻译 ----
        var reviewChunks = SplitText(reviewHtmlContent, Configuration.MaxChunkSize);
        result.ChunkCount = reviewChunks.Count;
        LogInfo("按需拉取评论：分片数量 {0}", reviewChunks.Count);

        onProgress?.Invoke("translating", 0, reviewChunks.Count, m.Reviews.Count());

        var finished = 0;
        var totalChunks = reviewChunks.Count;
        var reviewTasks = new List<Task<string>>();
        foreach (var (chunk, index) in reviewChunks.Select((chunk, idx) => (chunk, idx)))
        {
            // taskId 从 5000 开始，避免和其他任务冲突（与批量翻译保持一致）
            reviewTasks.Add(TranslateChunkWithProgress(
                chunk, to, m.Id, 5000 + index, totalChunks,
                () =>
                {
                    var done = Interlocked.Increment(ref finished);
                    onProgress?.Invoke("translating", done, totalChunks, m.Reviews.Count());
                },
                cancellationToken));
        }

        var translatedReviewChunks = await Task.WhenAll(reviewTasks);
        var translatedReviewHtml = string.Join("<br>", translatedReviewChunks);
        translatedReviewHtml = RestoreStarTokens(RepairBoldTags(translatedReviewHtml));

        if (string.IsNullOrWhiteSpace(translatedReviewHtml))
            return result;

        // ---- 第三步：加上评论区表头（与 UpdateReviewsTask 完全一致）----
        var header = new StringBuilder();
        header.Append("<br><br>");
        header.Append("————————————————————————————<br>");
        header.Append("<font color=\"#fce83a\" size=\"4\"><b>📝 观众评论</b></font><br>");
        header.Append("<br>");

        result.Html = header + translatedReviewHtml;
        return result;
    }

    /// <summary>
    /// 翻译单个分片，并在完成后回调一次（无论成功失败），用于汇总「已完成 N/M 片」。
    /// </summary>
    private static async Task<string> TranslateChunkWithProgress(
        string chunk, string to, string title, int chunkIndex, int totalChunks,
        Action onFinished, CancellationToken cancellationToken)
    {
        try
        {
            return await TranslateAsync(chunk, DetectSourceLanguage(chunk), to, title, chunkIndex, cancellationToken);
        }
        finally
        {
            onFinished?.Invoke();
        }
    }

    #region 演员简介

    /// <summary>
    /// 星级占位符，形如 <c>[[S4]]</c>（4 星）。
    ///
    /// 为什么不直接把 ⭐⭐⭐⭐☆ 交给模型：这是一串高度重复的符号，模型在复制它时
    /// 容易数错个数、把标签断开（<c>&lt;b&gt;⭐⭐</c> + <c>&lt;b&gt;⭐⭐⭐ 标题&lt;/b&gt;</c>）
    /// 或者重复闭合标签（<c>&lt;/b&lt;/b&gt;</c>），进而导致标题跑到相邻评论下、甚至整条评论丢失。
    /// 换成短占位符后模型根本不需要碰这些符号，翻译质量与稳定性都更好，token 也更省。
    /// </summary>
    private static readonly Regex StarTokenRegex = new(@"\[\[\s*S\s*(\d{1,2})\s*\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>生成星级占位符（不直接输出 ⭐，翻译完成后再由 <see cref="RestoreStarTokens"/> 还原）。</summary>
    private static string BuildStarToken(int score)
    {
        score = Math.Clamp(score, 0, 5);
        return $"[[S{score}]]";
    }

    /// <summary>
    /// 把占位符还原成真实的星级符号。
    ///
    /// 兼容模型改写的几种形式：大小写、内部空格、以及把方括号写成全角或漏掉一侧。
    /// 无法识别的占位符按 0 星处理（宁可显示 ☆☆☆☆☆，也不要把 <c>[[S4]]</c> 这种
    /// 内部标记泄露到用户可见的简介里）。
    /// </summary>
    private static string RestoreStarTokens(string html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf("[[", StringComparison.Ordinal) < 0)
            return html;

        return StarTokenRegex.Replace(html, m =>
        {
            // 模型理论上不会改数字，但真改了就夹到 0-5，避免出现越界星级。
            var score = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 5);
            return new string('⭐', score) + new string('☆', 5 - score);
        });
    }


    /// <summary>
    /// 规范化单条评论的正文（在拼进 HTML 之前调用）。
    ///
    /// FANZA 的评论原文里带真实的换行符，作者会用它分段。这里把换行统一成
    /// &lt;br&gt;，并保证：
    ///   * 原文只有一个空行的地方，转换后也只有一个空行（<c>&lt;br&gt;&lt;br&gt;</c>）；
    ///   * 多余的空行（作者敲了 3 个以上换行）压缩成一个空行；
    ///   * 首尾的空行去掉 —— 评论之间的间距由拼接代码统一控制，
    ///     否则会和后面补的 &lt;br&gt; 叠成两个空行。
    ///
    /// 之所以在「拼进 HTML 之前」就转换，而不是等翻译完再转：
    /// 分片是按 &lt;br&gt; 边界切的，提前转换能保证分片边界落在标签上，
    /// 也不会让裸换行进入模型输入（模型可能把它吃掉或改写成别的形式）。
    /// </summary>
    private static string NormalizeReviewBody(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return string.Empty;

        var text = comment.Replace("\r\n", "\n").Replace("\r", "\n");

        // 换行 -> <br>，随后把 3 个以上连续的 <br> 压成 2 个（即最多一个空行）。
        text = text.Replace("\n", "<br>");
        text = ExcessiveBrRegex.Replace(text, "<br><br>");

        // 去掉首尾换行标签，评论之间的间距交给调用方控制。
        text = text.Trim();
        text = LeadingBrRegex.Replace(text, string.Empty);
        text = TrailingBrRegex.Replace(text, string.Empty);
        return text.Trim();
    }

    private static readonly Regex ExcessiveBrRegex =
        new(@"(?:<br\s*/?>){3,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingBrRegex =
        new(@"^(?:<br\s*/?>)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingBrRegex =
        new(@"(?:<br\s*/?>)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 修复翻译结果里的 &lt;b&gt; 标签。
    ///
    /// 模型偶尔会把评论标题写成一个完整句子，于是只输出 &lt;b&gt;…却忘了 &lt;/b&gt;，
    /// 一旦漏掉闭合标签，后面整段评论（甚至下一条评论）都会被渲染成粗体。
    /// 这里做三件事：
    ///   1. 保证 &lt;b&gt; / &lt;/b&gt; 成对；
    ///   2. 遇到换行时自动闭合，让加粗最多只覆盖到本行末尾
    ///      （评论标题本来就只有一行，这样即使模型漏写也不会污染正文）；
    ///   3. 丢弃空标签、清理嵌套。
    /// </summary>
    private static string RepairBoldTags(string html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf("<b", StringComparison.OrdinalIgnoreCase) < 0)
            return html;

        var sb = new StringBuilder(html.Length);
        var open = false;
        var lastIndex = 0;

        foreach (Match token in BoldTokenRegex.Matches(html))
        {
            if (token.Index > lastIndex)
                AppendPlainText(sb, html.Substring(lastIndex, token.Index - lastIndex), ref open);

            lastIndex = token.Index + token.Length;

            if (token.Value.StartsWith("</", StringComparison.Ordinal))
            {
                // 多余的闭合标签：只有真的处于加粗状态才输出。
                if (open)
                {
                    sb.Append("</b>");
                    open = false;
                }
            }
            else
            {
                // 前一个标签还没闭合就来了新的：先补上闭合。
                if (open)
                {
                    sb.Append("</b>");
                    open = false;
                }

                sb.Append("<b>");
                open = true;
            }
        }

        if (lastIndex < html.Length)
            AppendPlainText(sb, html.Substring(lastIndex), ref open);

        if (open)
            sb.Append("</b>");

        return sb.ToString();
    }

    // 匹配 <b> / </b>（含大写、带空格等变体）。
    private static readonly Regex BoldTokenRegex = new(@"</?[bB]\s*>", RegexOptions.Compiled);

    /// <summary>
    /// 原样写出普通文本，但遇到换行标签时把仍然打开的 &lt;b&gt; 关掉，
    /// 避免模型漏写 &lt;/b&gt; 时加粗范围一直蔓延下去。
    /// </summary>
    private static void AppendPlainText(StringBuilder sb, string text, ref bool open)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var start = 0;
        while (true)
        {
            var br = text.IndexOf("<br", start, StringComparison.OrdinalIgnoreCase);
            if (br < 0)
                break;

            var gt = text.IndexOf('>', br);
            if (gt < 0)
                break;

            sb.Append(text, start, br - start);
            if (open)
            {
                sb.Append("</b>");
                open = false;
            }

            sb.Append(text, br, gt + 1 - br);
            start = gt + 1;
        }

        sb.Append(text, start, text.Length - start);
    }

    /// <summary>
    /// 演员简介翻译的目标语言：优先用条目的元数据语言，没有则用简体中文
    /// （与 UpdateReviewsTask 保持一致）。
    /// </summary>
    private const string DefaultActorTargetLanguage = "zh-CN";

    /// <summary>
    /// 日文假名（平假名 / 片假名 / 半角片假名）。av-wiki 的演员简介原文是日文，
    /// 翻译成中文后一般不再含假名。
    /// 也用于 <see cref="DetectSourceLanguage"/> 判定源语言。
    /// </summary>
    private static readonly Regex JapaneseKanaRegex =
        new(@"[\u3040-\u309F\u30A0-\u30FF\u31F0-\u31FF\uFF66-\uFF9D]", RegexOptions.Compiled);

    /// <summary>
    /// 批量翻译时用来分隔不同演员简介的标记。纯符号标记翻译引擎 / 大模型都会原样保留，
    /// 万一被改坏（见 <see cref="TryApplyBatchResult"/>）会退回逐个翻译。
    /// </summary>
    private static readonly Regex BatchMarkerRegex = new(@"\[\[\s*(\d+)\s*\]\]", RegexOptions.Compiled);

    /// <summary>
    /// 简介是否需要翻译：空文本不需要；不像日文的文本（例如已经翻译好的中文）也不需要。
    /// 是否"已经翻译过"由 <see cref="ActorInfo.SummaryTranslated"/> 标记决定（更精确）。
    /// </summary>
    public static bool NeedsTranslation(string text)
    {
        return !string.IsNullOrWhiteSpace(text) && JapaneseKanaRegex.IsMatch(text);
    }

    /// <summary>
    /// 一条标题"算不算已经翻译成中文"的判定阈值：汉字占比达到 90% 即认为翻译过。
    /// </summary>
    public const double ChineseRatioThreshold = 0.9;

    /// <summary>
    /// 判断标题是否"已经是中文"（供 Translation EnableCheck 使用）。
    /// </summary>
    /// <remarks>
    /// 为什么用比例、而不是"出现假名就否决"：实际场景里翻译完仍可能残留日文，
    /// 因为演员名称通常不翻译，例如「VRKM-01234 美人妻 北野美奈」「新人 桜井あゆ」。
    /// 同时日文原文里也有大量汉字（「美人妻と温泉旅行」汉字占 88%），
    /// 所以必须靠"汉字 vs 假名"的比例来区分，而不是看有没有假名。
    ///
    /// 两个降噪处理（都是为了不让"非内容噪声"拉低占比导致反复重翻）：
    /// 1. 先剔除番号 token（形如 VRKM-01234 / 3DSVR00123 / NO.1，含数字的拉丁词），
    ///    否则番号里的字母会把「VRKM-01234 美人妻 北野美奈」压到 64%；
    /// 2. 再尝试忽略末尾的演员名段（纯 CJK 的尾段），
    ///    否则 kana 较多的演员名会把「新人 桜井あゆ」压到 67%。
    /// 两种处理都只在"剩余的汉字占比更高"时才采纳，不会把标题判空。
    /// </remarks>
    public static bool IsTranslatedText(string text)
    {
        return ChineseRatio(text) >= ChineseRatioThreshold;
    }

    /// <summary>
    /// 汉字占【字】总数的比例（0~1）。分母只统计汉字 + 假名，
    /// 番号字母、数字、空格、标点、符号都不计入。无可统计的字时返回 0。
    /// </summary>
    public static double ChineseRatio(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var stripped = StripCatalogToken(text);

        // 剥掉番号后如果已经没有汉字了（例如纯番号标题 VRKM-01234），
        // 就退回原文判断，避免把有意义的标题判成"空文本"。
        if (CountHan(stripped) == 0) stripped = text;

        // 候选：完整文本，以及忽略末尾演员名段后的文本，取更像"已翻译"的那个。
        var best = HanRatio(stripped);
        var trimmed = TrimTrailingActor(stripped);
        if (trimmed != null) best = Math.Max(best, HanRatio(trimmed));

        return best;
    }

    /// <summary>
    /// 汉字 / (汉字 + 假名)。
    /// </summary>
    private static double HanRatio(string text)
    {
        var han = 0;
        var total = 0;

        foreach (var c in text)
        {
            if (IsHan(c)) { han++; total++; }
            else if (IsKana(c)) total++;
        }

        return total == 0 ? 0 : (double)han / total;
    }

    private static int CountHan(string text)
    {
        var count = 0;
        foreach (var c in text)
            if (IsHan(c)) count++;
        return count;
    }

    private static bool IsHan(char c) => c >= '\u4E00' && c <= '\u9FFF';

    private static bool IsKana(char c) =>
        c >= '\u3040' && c <= '\u309F' ||   // 平假名
        c >= '\u30A0' && c <= '\u30FF' ||   // 片假名
        c >= '\u31F0' && c <= '\u31FF' ||   // 片假名语音扩展
        c >= '\uFF66' && c <= '\uFF9D';     // 半角片假名

    /// <summary>
    /// 剔除番号 token：按空格切分，丢掉任何含数字的片段
    /// （VRKM-01234、3DSVR00123、NO.1 等）。纯粹为了消除番号对占比的稀释。
    /// </summary>
    private static string StripCatalogToken(string text)
    {
        var kept = text.Split(' ')
            .Where(token => !token.Any(char.IsDigit))
            .ToArray();

        return kept.Length == 0 ? text : string.Join(" ", kept);
    }

    /// <summary>
    /// 尝试去掉末尾的演员名段（最后一个空格分隔、且只由汉字/假名构成的片段）。
    /// 演员名通常不翻译，会让已翻译标题的汉字占比偏低。
    /// 末尾不是纯 CJK、或去掉后就没有汉字了，都返回 null 表示不作此处理。
    /// </summary>
    private static string TrimTrailingActor(string text)
    {
        var tokens = text.Split(' ');
        if (tokens.Length < 2) return null;

        var last = tokens[^1];
        if (last.Length == 0 || !last.All(c => IsHan(c) || IsKana(c))) return null;

        var head = string.Join(" ", tokens.Take(tokens.Length - 1));
        return CountHan(head) > 0 ? head : null;
    }

    /// <summary>
    /// 是否开启了简介翻译（复用现有的 TranslationMode 开关）。
    /// </summary>
    private static bool ActorTranslationEnabled =>
        Configuration.TranslationMode.HasFlag(TranslationMode.Summary);

    /// <summary>
    /// 翻译单个演员的简介（刮削单个演员时使用，没有批量的余地）。
    /// </summary>
    public static async Task TranslateActorAsync(ActorInfo actor, string language,
        CancellationToken cancellationToken)
    {
        if (actor == null) return;
        await TranslateActorSummariesAsync(new[] { actor }, language, cancellationToken);
    }

    /// <summary>
    /// 批量翻译演员简介：把多个演员的简介拼成一次请求，节省（AI 翻译的）token。
    /// 只会翻译 <see cref="NeedsTranslation"/> 为真且有简介的演员，其余原样保留。
    /// </summary>
    public static async Task TranslateActorSummariesAsync(IReadOnlyList<ActorInfo> actors, string language,
        CancellationToken cancellationToken)
    {
        if (!ActorTranslationEnabled || actors == null || actors.Count == 0) return;

        var to = ResolveActorTargetLanguage(language);
        if (to == null) return; // 目标语言就是日文，无需翻译。

        var targets = actors
            .Where(actor => actor != null && !actor.SummaryTranslated && NeedsTranslation(actor.Summary))
            .ToList();
        if (targets.Count == 0) return;

        // 按 MaxChunkSize 分组：同一批演员的简介合并成一次请求。
        foreach (var batch in SplitActorBatches(targets, Configuration.MaxChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (batch.Count == 1)
            {
                var actor = batch[0];
                actor.Summary = await TranslateAsync(actor.Summary, DetectSourceLanguage(actor.Summary), to, actor.Id, 0,
                    cancellationToken);
                actor.SummaryTranslated = true;
                continue;
            }

            // 形如：[[1]] 简介1\n[[2]] 简介2
            var payload = string.Join("\n",
                batch.Select((actor, index) => $"[[{index + 1}]] {actor.Summary}"));
            var translated = await TranslateAsync(payload, DetectSourceLanguage(payload), to, "actor-summaries", 0,
                cancellationToken);

            if (!TryApplyBatchResult(translated, batch))
            {
                // 标记被翻译引擎改坏时，退回逐个翻译，避免串行 / 丢内容。
                foreach (var actor in batch)
                {
                    actor.Summary = await TranslateAsync(actor.Summary, DetectSourceLanguage(actor.Summary), to,
                        actor.Id, 0, cancellationToken);
                    actor.SummaryTranslated = true;
                }
            }
        }
    }

    /// <summary>
    /// 把演员简介按最大分片大小分组，保证单次请求不会过长。
    /// </summary>
    private static List<List<ActorInfo>> SplitActorBatches(IReadOnlyList<ActorInfo> actors, int maxChunkSize)
    {
        var limit = maxChunkSize > 0 ? maxChunkSize : 2000;
        var batches = new List<List<ActorInfo>>();
        var current = new List<ActorInfo>();
        var length = 0;

        foreach (var actor in actors)
        {
            var itemLength = actor.Summary.Length + 16; // 16 为标记与换行的余量。
            if (current.Count > 0 && length + itemLength > limit)
            {
                batches.Add(current);
                current = new List<ActorInfo>();
                length = 0;
            }

            current.Add(actor);
            length += itemLength;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>
    /// 按 [[n]] 标记把批量翻译结果拆回各个演员。标记数量或顺序对不上时返回 false，
    /// 由调用方退回逐个翻译。
    /// </summary>
    private static bool TryApplyBatchResult(string translated, IReadOnlyList<ActorInfo> batch)
    {
        var matches = BatchMarkerRegex.Matches(translated ?? string.Empty);
        if (matches.Count != batch.Count) return false;

        for (var i = 0; i < matches.Count; i++)
        {
            if (int.TryParse(matches[i].Groups[1].Value, out var index) && index != i + 1) return false;

            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : translated.Length;
            var text = translated.Substring(start, end - start).Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;

            batch[i].Summary = text;
            batch[i].SummaryTranslated = true;
        }

        return true;
    }

    /// <summary>
    /// 解析简介翻译的目标语言；为空或就是日文时返回 null（表示无需翻译）。
    /// </summary>
    private static string ResolveActorTargetLanguage(string language)
    {
        var to = string.IsNullOrWhiteSpace(language) ? DefaultActorTargetLanguage : language.Trim();
        return string.Equals(to, JapaneseLanguageCode, StringComparison.OrdinalIgnoreCase) ? null : to;
    }

    #endregion

    private static List<string> SplitText(string text, int maxChunkSize)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;

        // 正则匹配常见的 <br> 标签变体
        var brRegex = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        int offset = 0;

        while (offset < text.Length)
        {
            if (offset + maxChunkSize >= text.Length)
            {
                chunks.Add(text.Substring(offset));
                break;
            }

            // 1. 在当前窗口（offset 到 offset + maxChunkSize）内寻找所有的 <br>
            string currentWindow = text.Substring(offset, maxChunkSize);
            var matches = brRegex.Matches(currentWindow);

            int splitPoint = -1;
            if (matches.Count > 0)
            {
                // 2. 找到窗口中最后一个 <br> 的结束位置
                var lastMatch = matches[matches.Count - 1];
                splitPoint = offset + lastMatch.Index + lastMatch.Length;
            }

            // 3. 确定截断位置
            int nextOffset;
            if (splitPoint > offset)
            {
                // 如果找到了 <br>，就在其后截断
                chunks.Add(text.Substring(offset, splitPoint - offset).Trim());
                nextOffset = splitPoint;
            }
            else
            {
                // 如果 1000 字内没有 <br>，则强制按长度截断（避免死循环）
                chunks.Add(text.Substring(offset, maxChunkSize).Trim());
                nextOffset = offset + maxChunkSize;
            }

            offset = nextOffset;
        }
        return chunks;
    }

    // 服务端在响应被 max_completion_tokens 截断时返回的错误标记（见 SDK 的 openai.go）。
    private static readonly Regex TruncationErrorRegex =
        new(@"truncated by max_completion_tokens", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void LogInfo(string format, params object[] args)
    {
#if __EMBY__
        try { Plugin.Instance.logger.Info(format, args); } catch { /* logging must never break translation */ }
#endif
    }

    private static void LogWarn(string format, params object[] args)
    {
#if __EMBY__
        try { Plugin.Instance.logger.Warn(format, args); } catch { }
#endif
    }

    /// <summary>
    /// 带日志与指数退避的重试。截断类错误直接抛出（重试无意义），
    /// 取消请求立刻放弃，其余错误按退避重试。
    /// </summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> func, int retryCount, string context,
        CancellationToken cancellationToken)
    {
        var delayMs = 500;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await func();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 用户/上层主动取消（例如任务被停止），继续重试没有意义。
                throw;
            }
            catch (OperationCanceledException e)
            {
                // 走到这里说明不是外部取消，而是**请求超时**：
                // ApiClient 用 per-request CTS 的 CancelAfter 实现超时，
                // 超时表现为 OperationCanceledException，但外部 token 并没有被取消。
                // 这类错误必须打日志，否则超时会表现为"翻译无声无息地失败"，极难排查。
                // 不重试：超时通常意味着这一片就是慢，重试只会再等一个完整的超时。
                LogWarn("Translate timeout ({0}) after {1} attempt(s): {2}", context, attempt, e.Message);
                throw;
            }
            catch (Exception e) when (IsNonRetryable(e) || attempt >= retryCount)
            {
                LogWarn("Translate give up ({0}) after {1} attempt(s): {2}", context, attempt, e.Message);
                throw;
            }
            catch (Exception e)
            {
                LogWarn("Translate attempt {0}/{1} failed ({2}), retrying in {3}ms: {4}", attempt, retryCount, context,
                    delayMs, e.Message);
                await Task.Delay(delayMs, cancellationToken);
                delayMs = Math.Min(delayMs * 2, 8000); // cap backoff at 8s.
            }
        }
    }

    /// <summary>
    /// 判断是否为「重试也不会变好」的错误（例如被 max_completion_tokens 截断：
    /// 重试结果完全一样，只会白烧一遍 token）。
    /// </summary>
    private static bool IsNonRetryable(Exception e)
    {
        return e.Message != null && TruncationErrorRegex.IsMatch(e.Message);
    }
}