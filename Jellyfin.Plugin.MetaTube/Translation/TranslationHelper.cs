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

    private static readonly SemaphoreSlim Semaphore = new(10);

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
                    { "openai-model", Configuration.OpenAiModel }
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
                //Plugin.Instance.logger.Info("Translating start {0}: {1}", title, chunkIndex);
                var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var translationInfo = await ApiClient
                    .TranslateAsync(q, from, to, Configuration.TranslationEngine.ToString(), nv, cancellationToken)
                    .ConfigureAwait(false);
                //Plugin.Instance.logger.Info("Translating end, cost:{0}ms {1}: {2}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, title, chunkIndex);
                return translationInfo.TranslatedText;
            }

            return await RetryAsync(TranslateWithDelay, 5);
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

    private static async Task<T> RetryAsync<T>(Func<Task<T>> func, int retryCount)
    {
        while (true)
        {
            try
            {
                return await func();
            }
            catch when (--retryCount > 0)
            {
            }
        }
    }
}