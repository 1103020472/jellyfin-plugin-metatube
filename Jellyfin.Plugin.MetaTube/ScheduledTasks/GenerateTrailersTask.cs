using System.Text;
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
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class GenerateTrailersTask : IScheduledTask
{
    // Emby: trailers can be stored in a trailers sub-folder.
    // https://support.emby.media/support/solutions/articles/44001159193-trailers
    private const string TrailersFolder = "trailers";

    // Uniform suffix for all trailer files.
    private const string TrailerFileSuffix = "-Trailer.strm";

    // StrmAssistant 会在预告片旁写 <预告片文件名去后缀>-mediainfo.json，改名时需一并同步。
    private const string MediaInfoSuffix = "-mediainfo.json";

    // 番号形态：字母前缀 + 可选连字符 + 数字（ABP-123 / DSVR-2015 / TEN1-22-2）。
    // 纯数字日期式番号（20150930、082413-415）和 "[Videos]"、"www.98T.la@" 这类片名
    // 都不满足该形态，会退化到 ProviderId 或媒体文件名，避免不同影片撞到同一个文件名。
    private static readonly Regex NumberPattern = new("^[A-Za-z]+-?\\d", RegexOptions.Compiled);

    // File name length guard (path segment safety).
    private const int MaxKeyLength = 100;

    // UTF-8 without BOM encoding.
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

#if __EMBY__
    public GenerateTrailersTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<GenerateTrailersTask>();
        _libraryManager = libraryManager;
    }
#else
    public GenerateTrailersTask(ILogger<GenerateTrailersTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    public string Key => $"{Plugin.Instance.Name}GenerateTrailers";

    public string Name => "Generate Trailers";

    public string Description => $"Generates video trailers provided by {Plugin.Instance.Name} in library.";

    public string Category => Plugin.Instance.Name;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        // Stop the task if disabled.
        if (!Plugin.Instance.Configuration.EnableTrailers)
            return;

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

            try
            {
                var trailersFolderPath = Path.Join(item.ContainingFolderPath, TrailersFolder);

                // Skip if contains .ignore file.
                if (File.Exists(Path.Join(trailersFolderPath, ".ignore")))
                    continue;

                // 一个目录只保留一个预告片文件，文件名以“影片标识”为准：
                // 同一部片的多 part / 多版本会解析出同一个 key，因此天然共享同一个
                // <key>-Trailer.strm；不同影片才会各自落到不同文件。
                var trailerKey = SanitizeFileName(GetTrailerKey(item));
                if (string.IsNullOrWhiteSpace(trailerKey))
                    continue;

                var trailerFilePath = Path.Join(trailersFolderPath, $"{trailerKey}{TrailerFileSuffix}");

                // 旧命名（片名首段）迁移：只在新文件缺失、旧文件存在时原地改名，
                // 内容与时间戳都不变，不会触发重新下载或重新生成。
                MigrateLegacyTrailerFile(item, trailersFolderPath, trailerFilePath);

                var trailerUrl = item.GetTrailerUrl();

                // Skip if no remote trailers.
                // 注意：这里不再删除同目录下的预告片文件。同目录可能存在多个影片条目，
                // 旧的“删掉 *-Trailer.strm 并删除空目录”逻辑会把别人的预告片一起删掉，
                // 造成两个条目每轮互相删除、反复重写（进而触发 Emby 的实时监控与重复刮削）。
                if (string.IsNullOrWhiteSpace(trailerUrl))
                    continue;

                // When trailer file already exists.
                // 幂等只比对内容：内容一致直接跳过，且不再回写文件时间戳——
                // 改 mtime 本身就会产生一次文件变更事件，反过来喂给 Emby 的 LibraryMonitor。
                if (File.Exists(trailerFilePath)
                    && string.Equals(await File.ReadAllTextAsync(trailerFilePath, cancellationToken), trailerUrl))
                    continue;

                // Create trailers folder if not exists.
                if (!Directory.Exists(trailersFolderPath))
                    Directory.CreateDirectory(trailersFolderPath);

                _logger.Info("Generate trailer for video {0} at {1}", item.Name, trailerFilePath);

                // Write .strm trailer file.
                await File.WriteAllTextAsync(trailerFilePath, trailerUrl, Utf8WithoutBom, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.Error("Generate trailer for video {0} error: {1}", item.Name, e.Message);
            }
        }

        progress?.Report(100);
    }

    /// <summary>
    /// 计算预告片文件名的 key（不含后缀）。
    /// 优先级：番号形态的片名首段 → MetaTube ProviderId(provider-id) → 媒体文件名。
    /// 同一部片的多个 part / 多份拷贝会得到相同 key，因此一个目录里只产出一个预告片文件。
    /// </summary>
    private static string GetTrailerKey(BaseItem item)
    {
        var token = FirstToken(item.Name);
        if (!string.IsNullOrWhiteSpace(token) && NumberPattern.IsMatch(token))
            return token;

        // ProviderId 才是影片的真正身份：同片不同分片、不同拷贝都一致，不同影片必然不同。
        var pid = item.GetPid(Plugin.Instance.Name);
        if (!string.IsNullOrWhiteSpace(pid.Provider) && !string.IsNullOrWhiteSpace(pid.Id))
            return $"{pid.Provider}-{pid.Id}";

        var fileName = Path.GetFileNameWithoutExtension(item.Path);
        return string.IsNullOrWhiteSpace(fileName) ? token : fileName;
    }

    private static string FirstToken(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : name.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// 旧版本用「片名首段」做文件名，且不区分同目录下的多个条目。
    /// 升级时把旧文件原地改名成新 key，避免整库重新生成预告片。
    /// </summary>
    private void MigrateLegacyTrailerFile(BaseItem item, string trailersFolderPath, string trailerFilePath)
    {
        try
        {
            var legacyKey = FirstToken(item.Name);
            if (string.IsNullOrWhiteSpace(legacyKey))
                return;

            var legacyFilePath = Path.Join(trailersFolderPath, $"{legacyKey}{TrailerFileSuffix}");
            if (string.Equals(legacyFilePath, trailerFilePath, StringComparison.Ordinal))
                return;
            if (File.Exists(trailerFilePath) || !File.Exists(legacyFilePath))
                return;

            File.Move(legacyFilePath, trailerFilePath);
            _logger.Info("Migrate trailer file name for video {0}: {1}", item.Name, trailerFilePath);

            // StrmAssistant 会在预告片旁写 <name>-mediainfo.json，一并改名保持对应。
            var legacyMediaInfo = Path.Join(trailersFolderPath,
                $"{Path.GetFileNameWithoutExtension(legacyFilePath)}{MediaInfoSuffix}");
            var mediaInfo = Path.Join(trailersFolderPath,
                $"{Path.GetFileNameWithoutExtension(trailerFilePath)}{MediaInfoSuffix}");
            if (File.Exists(legacyMediaInfo) && !File.Exists(mediaInfo))
                File.Move(legacyMediaInfo, mediaInfo);
        }
        catch (Exception e)
        {
            _logger.Warn("Migrate trailer file name for video {0} failed: {1}", item.Name, e.Message);
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(invalid.Contains(c) ? '-' : c);

        var result = builder.ToString().Trim();
        return result.Length > MaxKeyLength ? result[..MaxKeyLength] : result;
    }
}
