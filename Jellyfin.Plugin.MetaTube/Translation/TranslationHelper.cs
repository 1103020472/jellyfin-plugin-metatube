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
        
        // --- 1. 准备标题任务 ---
        if (Configuration.TranslationMode.HasFlag(TranslationMode.Title) && !string.IsNullOrWhiteSpace(m.Title))
        {
            titleTask = TranslateAsync(m.Title, AutoLanguageCode, to, m.Id, 0, cancellationToken);
        }

        // --- 2. 准备摘要分段任务 ---
        List<string> summaryChunks = new List<string>();
        if (Configuration.TranslationMode.HasFlag(TranslationMode.Summary) && !string.IsNullOrWhiteSpace(m.Summary))
        {
            summaryChunks = SplitText(m.Summary, Configuration.MaxChunkSize);
            Plugin.Instance.logger.Info("Translated summary chunks size: {0}", summaryChunks.Count);
            foreach (var (chunk, index) in summaryChunks.Select((chunk, idx) => (chunk, idx)))
            {
                summaryTasks.Add(TranslateAsync(chunk, AutoLanguageCode, to, m.Id, index + 1, cancellationToken));
            }
        }
        
        // --- 3. 准备评论翻译任务（Title + Comment 都翻译）---
        var reviewTitleTasks = new List<Task<string>>();    // 评论标题翻译
        var reviewCommentTasks = new List<Task<string>>(); // 评论内容翻译
        if (m.Reviews != null && m.Reviews.Any())
        {
            Plugin.Instance.logger.Info("准备翻译 {0} 条评论的标题 + 内容", m.Reviews.Count);

            foreach (var (comment, index) in m.Reviews.Select((c, i) => (c, i)))
            {
                int taskId = 1000 + index;

                // 翻译评论标题
                if (!string.IsNullOrWhiteSpace(comment.Title))
                {
                    reviewTitleTasks.Add(TranslateAsync(comment.Title, AutoLanguageCode, to, m.Id, taskId, cancellationToken));
                }
                else
                {
                    reviewTitleTasks.Add(Task.FromResult<string>(null));
                }

                // 翻译评论内容
                if (!string.IsNullOrWhiteSpace(comment.Comment))
                {
                    reviewCommentTasks.Add(TranslateAsync(comment.Comment, AutoLanguageCode, to, m.Id, taskId + 100, cancellationToken));
                }
                else
                {
                    reviewCommentTasks.Add(Task.FromResult<string>(null));
                }
            }
        }

        // --- 3. 统一并行执行 ---
        // 构造一个包含所有任务的列表进行等待
        var allTasks = new List<Task>(summaryTasks);
        allTasks.AddRange(reviewTitleTasks);
        allTasks.AddRange(reviewCommentTasks);
        if (titleTask != Task.FromResult<string>(null)) allTasks.Add(titleTask);

        await Task.WhenAll(allTasks);

        // --- 4. 回写结果 ---
        if (titleTask != null && titleTask.Status == TaskStatus.RanToCompletion)
        {
            m.Title = await titleTask;
        }

        // --- 6. 回写摘要 ---
        string finalSummary = m.Summary ?? "";
        if (summaryTasks.Any())
        {
            var translatedChunks = await Task.WhenAll(summaryTasks);
            finalSummary = string.Join("", translatedChunks);
        }
        
        // --- 7. 回写评论（标题 + 内容）---
        if (reviewTitleTasks.Any() && reviewCommentTasks.Any())
        {
            var translatedTitles = await Task.WhenAll(reviewTitleTasks);
            var translatedComments = await Task.WhenAll(reviewCommentTasks);

            for (int i = 0; i < m.Reviews.Count; i++)
            {
                var c = m.Reviews[i];

                if (i < translatedTitles.Length && !string.IsNullOrWhiteSpace(translatedTitles[i]))
                    c.Title = translatedTitles[i];

                if (i < translatedComments.Length && !string.IsNullOrWhiteSpace(translatedComments[i]))
                    c.Comment = translatedComments[i];
            }

            Plugin.Instance.logger.Info("评论翻译完成：{0} 条", m.Reviews.Count);
        }

        // --- 8. 拼接评论到概述（匹配参考样式）---
        if (m.Reviews != null && m.Reviews.Any())
        {
            var sb = new StringBuilder();
    
            // 评论区标题 + 分割线
            sb.Append("<br><br>");
            sb.Append("————————————————————————————<br>");
            sb.Append("<font color=\"#fce83a\" size=\"4\"><b>📝 观众评论</b></font><br>");
            sb.Append("<br>");

            foreach (var c in m.Reviews)
            {
                // 1. 作者 + 时间（前置，普通文字）
                string author = string.IsNullOrWhiteSpace(c.Author) ? "匿名用户" : c.Author;
                // 假设你的评论实体有时间字段（无则删除）
                string commentTime = c.Date != DateTime.MinValue ? $"{c.Date:yyyy-MM-dd}" : "";
                sb.Append($"👤 {author}");
                if (!string.IsNullOrWhiteSpace(commentTime))
                {
                    sb.Append($" · {commentTime}");
                }
                sb.Append("<br>");

                // 2. 星级 + 标题（5星补全：实星+灰星，整体加粗）
                int score = Math.Clamp(c.Score, 0, 5); // 限制星级0-5
                string filledStar = new string('⭐', score); // 亮星（实星）
                string emptyStar = new string('☆', 5 - score); // 灰星（空星）
                string starIcon = $"{filledStar}{emptyStar}"; // 拼接成5颗星
        
                string commentTitle = string.IsNullOrWhiteSpace(c.Title) ? "" : c.Title;
                if (!string.IsNullOrWhiteSpace(starIcon) || !string.IsNullOrWhiteSpace(commentTitle))
                {
                    sb.Append($"<b>{starIcon} {commentTitle}</b><br>");
                }

                // 3. 评论内容（纯文本，无样式）
                if (!string.IsNullOrWhiteSpace(c.Comment))
                {
                    sb.Append($"{c.Comment}<br>");
                }

                // 4. 每条评论之间用两个<br>分隔
                sb.Append("<br><br>");
            }

            finalSummary += sb.ToString();
        }

        // 最终回写
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