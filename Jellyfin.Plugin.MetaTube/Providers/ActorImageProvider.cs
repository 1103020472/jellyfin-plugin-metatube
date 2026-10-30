using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
#if __EMBY__
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Configuration;

#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

public class ActorImageProvider : BaseProvider, IRemoteImageProvider, IHasOrder
{
#if __EMBY__
    public ActorImageProvider(ILogManager logManager) : base(logManager.CreateLogger<ActorImageProvider>())
#else
    public ActorImageProvider( ILogger<ActorImageProvider> logger) : base(logger)
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

        var actorInfo = await ApiClient.GetActorInfoAsync(pid.Provider, pid.Id, cancellationToken);

        // 别名资料合并：替换表里这个演员的别名，只要在源站有数据就合并进来。
        // 头像规则：主记录的图在前（av-league 优先，沿用原行为），别名的图按表内顺序接在后；
        // 每张图记住自己的来源记录 —— 图片地址是 /v1/images/primary/{provider}/{id}?url=...，
        // 别名带来的图必须用别名那条记录的 provider/id 去拼。
        var merged = await ActorAliasMerger.MergeAsync(
            actorInfo, item.Name, message => Logger.Info(message), cancellationToken);

        if (merged.Images.Count == 0)
            return Enumerable.Empty<RemoteImageInfo>();

        return merged.Images.Select(image => new RemoteImageInfo
        {
            ProviderName = Name,
            Type = ImageType.Primary,
            Url = ApiClient.GetPrimaryImageApiUrl(image.Provider, image.Id, image.Url, 0.5, true)
        });
    }

    public bool Supports(BaseItem item)
    {
        return item is Person;
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new List<ImageType>
        {
            ImageType.Primary
        };
    }
}