using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

public static class TranslationHelper
{
    private const string AutoLanguageCode = "auto";
    private const string JapaneseLanguageCode = "ja";

    private static readonly SemaphoreSlim Semaphore = new(1);

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    private static async Task<string> TranslateAsync(string q, string from, string to,
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
                millisecondsDelay = 1000;
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
                return (await ApiClient
                    .TranslateAsync(q, from, to, Configuration.TranslationEngine.ToString(), nv, cancellationToken)
                    .ConfigureAwait(false)).TranslatedText;
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
            titleTask = TranslateAsync(m.Title, AutoLanguageCode, to, cancellationToken);
        }

        // --- 2. 准备摘要分段任务 ---
        List<string> summaryChunks = new List<string>();
        if (Configuration.TranslationMode.HasFlag(TranslationMode.Summary) && !string.IsNullOrWhiteSpace(m.Summary))
        {
            summaryChunks = SplitText(m.Summary, 5000);
            foreach (var chunk in summaryChunks)
            {
                summaryTasks.Add(TranslateAsync(chunk, AutoLanguageCode, to, cancellationToken));
            }
        }

        // --- 3. 统一并行执行 ---
        // 构造一个包含所有任务的列表进行等待
        var allTasks = new List<Task>(summaryTasks);
        if (titleTask != Task.FromResult<string>(null)) allTasks.Add(titleTask);

        await Task.WhenAll(allTasks);

        // --- 4. 回写结果 ---
        if (titleTask != null && titleTask.Status == TaskStatus.RanToCompletion)
        {
            m.Title = await titleTask;
        }

        if (summaryTasks.Any())
        {
            // 按照原始顺序合并分段结果
            var translatedChunks = await Task.WhenAll(summaryTasks);
            m.Summary = string.Join("", translatedChunks);
        }
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