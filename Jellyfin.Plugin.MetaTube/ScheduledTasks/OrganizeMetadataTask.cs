using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;

#else
using MediaBrowser.Controller.Sorting;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class OrganizeMetadataTask : IScheduledTask
{
    private static ILibraryManager _libraryManager;
    private static ILogger _logger;

#if __EMBY__
    public OrganizeMetadataTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<OrganizeMetadataTask>();
        _libraryManager = libraryManager;
    }
#else
    public OrganizeMetadataTask(ILogger<OrganizeMetadataTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    public string Key => $"{Plugin.Instance.Name}OrganizeMetadata";

    public string Name => "Organize Metadata";

    public string Description => $"Organizes video metadata provided by {Plugin.Instance.Name} in library.";

    public string Category => Plugin.Instance.Name;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        await Task.Yield();

        progress?.Report(0);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
#if __EMBY__
            HasAnyProviderId = new[] { Plugin.Instance.Name },
            IncludeItemTypes = new[] { nameof(Movie) },
#else
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.Instance.Name, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        foreach (var (idx, item) in items.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double)idx / items.Count * 100);

            var genres = item.Genres?.ToList() ?? new List<string>();

            // 1. 处理中文字幕标签 (只负责增删 List 中的元素)
            try
            {
                bool hasZimu = HasEmbeddedChineseSubtitle(item.FileNameWithoutExtension) ||
                               HasExternalChineseSubtitle(item.Path);
                
                if (hasZimu && !genres.Contains(ChineseSubtitle))
                {
                    _logger.Info("添加中文字幕标签 for video: {0}", item.Name);
                    genres.Add(ChineseSubtitle);
                }
                else if (!hasZimu && genres.Contains(ChineseSubtitle))
                {
                    _logger.Info("删除中文字幕标签 for video: {0}", item.Name);
                    genres.RemoveAll(s => s.Equals(ChineseSubtitle));
                }
            }
            catch (Exception e)
            {
                _logger.Error("Update ChineseSubtitle for video {0}: {1}", item.Name, e.Message);
            }
            
            // 2. 处理无码标签 (只负责增删 List 中的元素)
            try
            {
                bool hasUmr = HasUmrFile(item.Path);
                if (hasUmr && !genres.Contains(UmrGenre))
                {
                    _logger.Info("添加无码标签 for video: {0}", item.Name);
                    genres.Add(UmrGenre);
                }
                else if (!hasUmr && genres.Contains(UmrGenre))
                {
                    _logger.Info("删除无码标签 for video: {0}", item.Name);
                    genres.RemoveAll(s => s.Equals(UmrGenre));
                }
            }
            catch (Exception e)
            {
                _logger.Error("Update WuMaGenre for video {0}: {1}", item.Name, e.Message);
            }

            // 3. 去重和应用替换规则
            var orderedGenres =
                (Plugin.Instance.Configuration.EnableGenreSubstitution
                    ? Plugin.Instance.Configuration.GetGenreSubstitutionTable().Substitute(genres)
                    : genres).Distinct().OrderByString(genre => genre).ToList();

            // 4. 检查是否需要更新标签
            // 注意：不能写成 orderedGenres.Any() && ...，否则当标签被全部移除时
            // orderedGenres 为空 -> genresChanged=false -> 提前 continue，导致旧标签
            // 和旧图片水印永远清不掉。这里直接比较两个集合是否一致。
            var currentGenres = item.Genres?.OrderByString(g => g).ToList() ?? new List<string>();
            bool genresChanged = !currentGenres.SequenceEqual(orderedGenres, StringComparer.OrdinalIgnoreCase);

            if (!genresChanged)
            {
                // 如果标签没变，直接跳过处理下一部影片
                continue;
            }

            // 更新标签
            item.Genres = orderedGenres.ToArray();
            _logger.Info("Organize metadata for video: {0}", item.Name);
            
            // 5. 【关键修复】基于最终确定的 orderedGenres 来决定水印变量！
            string zimu = orderedGenres.Contains(ChineseSubtitle) ? Plugin.Instance.Configuration.BadgeUrl : string.Empty;
            string umr = orderedGenres.Contains(UmrGenre) ? "umr.png" : string.Empty;

            // 6. 同步修改图片
            try
            {
                if (Plugin.Instance.Configuration.EnableBadges)
                {
                    _logger.Info("修改图片标签 for video: {0}", item.Name);
                    await SetPrimaryImage(item, zimu, umr, cancellationToken);
                }
            }
            catch (Exception e)
            {
                _logger.Error("SetPrimaryImage for video {0}: {1}", item.Name, e.Message);
            }

#if __EMBY__
            _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
#else
            await _libraryManager
                .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
#endif
        }

        progress?.Report(100);
    }

    #region Helper

    private const string ChineseSubtitle = "中文字幕";
    
    private const string UmrGenre = "无码破解";

    /// <summary>
    /// 提取文件名里所有「版本标记」段，例如：
    ///   "SSIS-696-UC"    -> ["UC"]
    ///   "SSIS-696-C-U"   -> ["C", "U"]      历史数据里分开写的组合
    ///   "SSIS-696-4K-UC" -> ["4K", "UC"]
    ///   "SSIS-696-U4K"   -> ["U4K"]
    ///   "SSIS-696"       -> ["SSIS", "696"]   注：prefix 与数字段会被 IsTagMatch 忽略
    /// 说明：IsTagMatch 采用白名单匹配，因此把番号前缀（如 SSIS）一并收集是安全的。
    /// </summary>
    private static readonly Regex TagSegmentRegex = new(
        @"(?:^|[-_\s])(?<tag>[A-Za-z0-9]{1,6})(?=$|[-_\s])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static List<string> GetSuffixTags(string basename)
    {
        if (string.IsNullOrWhiteSpace(basename))
            return new List<string>();

        return TagSegmentRegex.Matches(basename)
            .Select(m => m.Groups["tag"].Value)
            .ToList();
    }

    /// <summary>
    /// 判断文件名里是否含有指定的版本标记。支持两种写法：
    ///   1) 组合写法 "UC"（= 无码 + 中文字幕）
    ///   2) 历史数据里分开写的 "C-U" / "U-C"
    /// 例如：
    ///   HasSuffixTag("SSIS-696-U",  "U") -> true
    ///   HasSuffixTag("SSIS-696-UC", "U") -> true    （UC 里含 U）
    ///   HasSuffixTag("SSIS-696-UC", "C") -> true
    ///   HasSuffixTag("SSIS-696-C-U","U") -> true    （历史写法）
    ///   HasSuffixTag("SSIS-696-C-U","C") -> true
    ///   HasSuffixTag("SSIS-696-4K", "U") -> false
    ///   HasSuffixTag("SSIS-696-UCHIDA","U") -> false（不是版本标记）
    /// </summary>
    private static bool HasSuffixTag(string basename, params string[] tags)
    {
        var segments = GetSuffixTags(basename);
        if (segments.Count == 0)
            return false;

        var lowered = segments.Select(s => s.ToLowerInvariant()).ToList();

        // 单独出现的 C + 单独出现的 U 等价于 UC（历史数据 "-C-U" / "-U-C"）
        if (tags.Contains("C", StringComparer.OrdinalIgnoreCase) &&
            tags.Contains("U", StringComparer.OrdinalIgnoreCase) &&
            lowered.Contains("c") && lowered.Contains("u"))
        {
            return true;
        }

        return tags.Any(tag => lowered.Any(seg => IsTagMatch(seg, tag)));
    }

    /// <summary>
    /// 判断一个版本标记段是否命中目标标记。
    /// 白名单式匹配，避免 CHU / ABP 之类误命中，同时支持各种组合写法：
    ///   IsTagMatch("u",   "U") -> true
    ///   IsTagMatch("uc",  "U") -> true    （UC = 无码 + 中文字幕）
    ///   IsTagMatch("uc",  "C") -> true
    ///   IsTagMatch("un",  "U") -> true    （-UN = 无码）
    ///   IsTagMatch("u4k", "U") -> true    （-U4K = 无码 4K）
    ///   IsTagMatch("c",   "C") -> true
    ///   IsTagMatch("chu", "U") -> false   （CHU 是中文字幕拼音缩写，不是无码）
    ///   IsTagMatch("chu", "C") -> false
    ///   IsTagMatch("abp", "U") -> false
    /// </summary>
    private static bool IsTagMatch(string segment, string tag)
    {
        if (string.IsNullOrEmpty(segment) || string.IsNullOrEmpty(tag))
            return false;

        var s = segment.ToLowerInvariant();

        // 版本标记段的合法形态：
        //   u / c / uc           无码、中文字幕、两者
        //   un                   无码
        //   u4k                  无码 + 4K
        //   ch / chs / cht       中文字幕
        //   k / fps / uhd 之类画面标记不参与
        switch (tag.ToLowerInvariant())
        {
            case "u":
                return s == "u" || s == "uc" || s == "un" || s.StartsWith("u4k", StringComparison.Ordinal);
            case "c":
                return s == "c" || s == "uc" || s == "ch" || s == "chs" || s == "cht";
            case "ch":
                return s == "ch" || s == "chs" || s == "cht";
            default:
                return s.Equals(tag, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool HasEmbeddedChineseSubtitle(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return false;

        // "中文字幕" 直接写在文件名里，或者末尾标记为 -C / -UC / -ch
        return filename.Contains(ChineseSubtitle) || HasSuffixTag(filename, "C", "ch");
    }

    private static bool HasExternalChineseSubtitle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var basename = Path.GetFileNameWithoutExtension(path);
        var dir = Directory.GetParent(path);
        if (dir is null || !dir.Exists)
            return false;

        return HasExternalChineseSubtitle(basename, dir.GetFiles().Select(info => info.Name));
    }

    private static bool HasExternalChineseSubtitle(string basename, IEnumerable<string> files)
    {
        if (files is null)
            return false;

        var r = new Regex(@"\.(ass|srt|ssa|smi|sub|idx|psb|vtt)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        var subs = files.Where(name => r.IsMatch(name))
            .Select(name => r.Replace(name, string.Empty))
            .ToList();

        // 0) 只要字幕文件名以「原视频文件名」开头就算命中（本次放宽的核心规则）：
        //    SSIS-696-UC.mp4 配 SSIS-696-UC.ass / SSIS-696-UC.chs.ass / SSIS-696-UC.zh-CN.srt
        //    SSIS-696-UC.mp4 配 SSIS-696-UC-中文字幕.srt
        //    边界要求：basename 之后必须紧跟分隔符（. - _ 空格）或直接结束，
        //    避免 SSIS-696-U.mp4 误匹配 SSIS-696-U2.ass。
        if (subs.Any(s => IsPrefixedBy(s, basename)))
            return true;

        // 1) 与当前版本同名的字幕：SSIS-696-UC.mp4 配 SSIS-696-UC.ass
        if (subs.Any(s => s.Equals(basename, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 2) 主番号同名的字幕：SSIS-696-4K.mp4 配 SSIS-696.srt
        var main = GetMainCode(basename);
        if (main.Length > 0 && subs.Any(s => s.Equals(main, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 3) 字幕文件名带明确的中文标记
        return subs.Any(s =>
            s.EndsWith("-zh", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith("-chs", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith("-cht", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith("-uc", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith("-c", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断字幕文件名（去掉扩展名后）是否以原视频文件名开头。
    /// 只要字幕是「原文件名 + 语言/字幕标识」这种命名，都算命中：
    ///   IsPrefixedBy("SSIS-696-UC.chs",    "SSIS-696-UC") -> true
    ///   IsPrefixedBy("SSIS-696-UC.zh-CN",  "SSIS-696-UC") -> true
    ///   IsPrefixedBy("SSIS-696-UC-中文字幕","SSIS-696-UC") -> true
    ///   IsPrefixedBy("SSIS-696-UC",        "SSIS-696-UC") -> true
    ///   IsPrefixedBy("SSIS-696-U2",        "SSIS-696-U")  -> false（后面紧跟字母数字，属于另一个版本）
    ///   IsPrefixedBy("SSIS-696-U",         "SSIS-696-UC") -> false
    /// </summary>
    private static bool IsPrefixedBy(string subName, string basename)
    {
        if (string.IsNullOrWhiteSpace(subName) || string.IsNullOrWhiteSpace(basename))
            return false;

        if (!subName.StartsWith(basename, StringComparison.OrdinalIgnoreCase))
            return false;

        // 完全同名，或原名之后紧跟分隔符，才算同一部影片的字幕
        if (subName.Length == basename.Length)
            return true;

        var next = subName[basename.Length];
        return next is '.' or '-' or '_' or ' ' || char.IsWhiteSpace(next);
    }

    /// <summary>"SSIS-696-UC" / "SSIS-696-U" / "SSIS-696-4K" -> "SSIS-696"</summary>
    private static string GetMainCode(string basename)
    {
        if (string.IsNullOrWhiteSpace(basename))
            return string.Empty;

        var idx = basename.LastIndexOf('-');
        return idx > 0 ? basename[..idx] : basename;
    }

    private static async Task SetPrimaryImage(BaseItem item, string badge, string umr, CancellationToken cancellationToken)
    {
        var pid = item.GetPid(Plugin.Instance.Name);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
            return;

        var m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken);
        // Set first primary image.
        item.SetImage(new ItemImageInfo
        {
            Path = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, pid.Position ?? -1, badge, umr),
            Type = ImageType.Primary
        }, 0);
    }
    
    /// <summary>
    /// 判断当前视频本身是否为无码破解版本。
    /// 以“该条目自己的文件名”为准，避免同目录下其它版本（如 SSIS-696-U.mp4）
    /// 把普通版 / 4K 版也一起标记成无码。
    /// </summary>
    private static bool HasUmrFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return HasSuffixTag(Path.GetFileNameWithoutExtension(path), "U");
    }

    #endregion
}