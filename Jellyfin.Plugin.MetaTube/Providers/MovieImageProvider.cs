using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
#if __EMBY__
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

public class MovieImageProvider : BaseProvider, IRemoteImageProvider, IHasOrder
{
#if __EMBY__
    public MovieImageProvider(ILogManager logManager) : base(logManager.CreateLogger<MovieImageProvider>())
#else
    public MovieImageProvider(ILogger<MovieImageProvider> logger) : base(logger)
#endif
    {
    }

#if __EMBY__
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions,
        CancellationToken cancellationToken)
#else
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
#endif
    {
        var pid = item.GetPid(Name);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
            return Enumerable.Empty<RemoteImageInfo>();

        var m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken);
        var images = new List<RemoteImageInfo>
        {
            new()
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, pid.Position ?? -1)
            },
            new()
            {
                ProviderName = Name,
                Type = ImageType.Thumb,
                Url = ApiClient.GetThumbImageApiUrl(m.Provider, m.Id)
            }
        };

        // 背景图：
        // Emby 对 Backdrop 是「给多少就下多少」，完全不看条目里已经存了几张 —— 实测条目已经有 13 张
        // 背景图，照样把 13 个候选全下了一遍（每张走服务端图片代理约 300ms），下回来又按「重复图」丢掉。
        // SkipExistingBackdrop 打开后，条目已有背景图就不再提供候选，省掉每条 2~4 秒的纯浪费。
        //
        // 这个开关只决定「这次要不要把候选交出去」，不碰条目已有的图片，也不改变候选的数量和种类：
        // 需要提供候选时，给的就是基础背景图 + 全部预览图，与旧版本逐字一致。
        // 已有图片只有在 Emby 执行「替换所有图片」时才会被删除，那是 Emby 自己的行为，与本开关无关。
        //
        // Primary / Thumb 各只有一个槽位，条目已有就不会触发下载，所以两种情况都照常提供、不受影响。
        var provideBackdrop = !Configuration.SkipExistingBackdrop
                              || !item.HasImage(ImageType.Backdrop, 0);

        if (provideBackdrop)
        {
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Backdrop,
                Url = ApiClient.GetBackdropImageApiUrl(m.Provider, m.Id)
            });

            foreach (var imageUrl in m.PreviewImages ?? Enumerable.Empty<string>())
            {
                images.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Type = ImageType.Backdrop,
                    Url = ApiClient.GetBackdropImageApiUrl(m.Provider, m.Id, imageUrl)
                });
            }
        }

        // 预览图仍然作为 Primary / Thumb 的备选（各只有一个槽位，已有图时不会产生下载）。
        foreach (var imageUrl in m.PreviewImages ?? Enumerable.Empty<string>())
        {
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, imageUrl, pid.Position ?? -1)
            });

            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Thumb,
                Url = ApiClient.GetThumbImageApiUrl(m.Provider, m.Id, imageUrl)
            });
        }

        return images;
    }

    public bool Supports(BaseItem item)
    {
        return item is Movie;
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new List<ImageType>
        {
            ImageType.Primary,
            ImageType.Thumb,
            ImageType.Backdrop
        };
    }
}