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

    private static readonly SemaphoreSlim Semaphore = new(5);

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

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

        await Semaphore.WaitAsync(cancellationToken);

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
            return await RetryAsync(TranslateWithDelay, 5, context, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
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

                // 2. 星级 + 标题
                int score = Math.Clamp(c.Score, 0, 5);
                string filledStar = new string('⭐', score);
                string emptyStar = new string('☆', 5 - score);
                string starIcon = $"{filledStar}{emptyStar}";
                string commentTitle = string.IsNullOrWhiteSpace(c.Title) ? "" : c.Title;
                if (!string.IsNullOrWhiteSpace(starIcon) || !string.IsNullOrWhiteSpace(commentTitle))
                    sb.Append($"<b>{starIcon} {commentTitle}</b><br>");

                // 3. 评论内容（原文，暂不翻译）
                if (!string.IsNullOrWhiteSpace(c.Comment))
                    sb.Append($"{c.Comment}<br>");

                sb.Append("<br><br>");
            }

            reviewHtmlContent = sb.ToString();
        }

        // ==============================================
        // 【第二步：准备翻译任务】
        // ==============================================

        // --- 1. 标题翻译 ---
        if (Configuration.TranslationMode.HasFlag(TranslationMode.Title) && !string.IsNullOrWhiteSpace(m.Title))
        {
            titleTask = TranslateAsync(m.Title, AutoLanguageCode, to, m.Id, 0, cancellationToken);
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
                summaryTasks.Add(TranslateAsync(chunk, AutoLanguageCode, to, m.Id, index + 1, cancellationToken));
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
                reviewTasks.Add(TranslateAsync(chunk, AutoLanguageCode, to, m.Id, 5000 + index, cancellationToken));
            }

            // 等待评论所有分片完成，合并成完整翻译后的 HTML
            var translatedReviewChunks = await Task.WhenAll(reviewTasks);
            reviewHtmlTask = Task.FromResult(string.Join("<br>", translatedReviewChunks));
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
    
    #region 演员简介

    /// <summary>
    /// 演员简介翻译的目标语言：优先用条目的元数据语言，没有则用简体中文
    /// （与 UpdateReviewsTask 保持一致）。
    /// </summary>
    private const string DefaultActorTargetLanguage = "zh-CN";

    /// <summary>
    /// 日文假名（平假名 / 片假名）。av-wiki 的演员简介原文是日文，
    /// 翻译成中文后一般不再含假名。
    /// </summary>
    private static readonly Regex JapaneseKanaRegex =
        new(@"[\u3040-\u309F\u30A0-\u30FF]", RegexOptions.Compiled);

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
                actor.Summary = await TranslateAsync(actor.Summary, AutoLanguageCode, to, actor.Id, 0,
                    cancellationToken);
                actor.SummaryTranslated = true;
                continue;
            }

            // 形如：[[1]] 简介1\n[[2]] 简介2
            var payload = string.Join("\n",
                batch.Select((actor, index) => $"[[{index + 1}]] {actor.Summary}"));
            var translated = await TranslateAsync(payload, AutoLanguageCode, to, "actor-summaries", 0,
                cancellationToken);

            if (!TryApplyBatchResult(translated, batch))
            {
                // 标记被翻译引擎改坏时，退回逐个翻译，避免串行 / 丢内容。
                foreach (var actor in batch)
                {
                    actor.Summary = await TranslateAsync(actor.Summary, AutoLanguageCode, to, actor.Id, 0,
                        cancellationToken);
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
            catch (OperationCanceledException)
            {
                // 用户取消刮削，继续重试没有意义。
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