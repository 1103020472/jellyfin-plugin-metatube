using Jellyfin.Plugin.MetaTube.Configuration;
using MediaBrowser.Common.Plugins;
#if __EMBY__
using Jellyfin.Plugin.MetaTube.Configuration.Web;
using MediaBrowser.Common;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;

#else
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Configuration;
#endif

namespace Jellyfin.Plugin.MetaTube;

#if __EMBY__
public class Plugin : BasePluginSimpleUI<PluginConfiguration>, IHasThumbImage, IHasUIPages
{
    public readonly ILogger logger;

    /// <summary>
    /// 设置页控制器（第一次访问时创建）。
    /// 重新实现 <see cref="IHasUIPages"/> 是为了用我们自己的“多 tab”页面
    /// 取代 BasePluginSimpleUI 默认的单页平铺设置页。
    /// </summary>
    private IReadOnlyCollection<IPluginUIPageController> uiPageControllers;

    /// <summary>
    /// 应用宿主。自定义 API 服务（Api/ReviewService）需要通过它解析
    /// ILibraryManager 等依赖，而 Emby 对插件自定义服务不保证构造注入。
    /// </summary>
    public IApplicationHost ApplicationHost { get; }

    public Plugin(IApplicationHost applicationHost) : base(applicationHost)
    {
        Instance = this;
        ApplicationHost = applicationHost;
        this.logger = applicationHost.Resolve<ILogManager>().GetLogger(this.Name);
    }
#else
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths,
        xmlSerializer)
    {
        Instance = this;
    }
#endif

    public override string Name => "MetaTube";

    public override string Description => "MetaTube Plugin for Jellyfin/Emby";

    public override Guid Id => Guid.Parse("01cc53ec-c415-4108-bbd4-a684a9801a32");

    public static Plugin Instance { get; private set; }

#if !__EMBY__
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
#endif

#if __EMBY__
    public PluginConfiguration Configuration => GetOptions();

    /// <summary>
    /// Emby Dashboard 的设置页（首个 tab + 其余 tab 由 MetaTubeMainPageController 提供）。
    /// </summary>
    IReadOnlyCollection<IPluginUIPageController> IHasUIPages.UIPageControllers =>
        uiPageControllers ??= new IPluginUIPageController[] {new MetaTubeMainPageController()};

    /// <summary>
    /// 保存配置（供 tab 页视图调用）。
    /// </summary>
    internal void SaveConfiguration(PluginConfiguration configuration) => SaveOptions(configuration);

    public Stream GetThumbImage()
    {
        return GetType().Assembly.GetManifestResourceStream($"{GetType().Namespace}.thumb.png");
    }

    public ImageFormat ThumbImageFormat => ImageFormat.Png;
#endif
}