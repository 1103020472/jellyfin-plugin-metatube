#if __EMBY__
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace Jellyfin.Plugin.MetaTube.Configuration.Web;

/// <summary>
/// 插件设置页（tab）的视图基类。
/// Emby 的 Dashboard 会拿 ContentData 走 GenericEdit 反射生成表单，
/// 所以这里只需要把“某个分组选项对象”交出去，并在保存时写回配置即可。
/// </summary>
internal abstract class PluginTabPageView : IPluginPageView, IPluginViewWithOptions
{
    protected PluginTabPageView(IEditableObject contentData)
    {
        ContentData = contentData;
    }

    public virtual string Caption => ContentData?.EditorTitle;

    public virtual string SubCaption => ContentData?.EditorDescription;

    public string PluginId => Plugin.Instance.Id.ToString("N");

    public IEditableObject ContentData { get; set; }

    public UserDto User { get; set; }

    public string RedirectViewUrl { get; set; }

    public bool ShowSave { get; set; } = true;

    public bool ShowBack { get; set; }

    public bool AllowSave { get; set; } = true;

    public bool AllowBack { get; set; } = true;

    public PluginViewOptions ViewOptions { get; } = new();

    public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;

    public virtual bool IsCommandAllowed(string commandKey) => true;

    public virtual Task<IPluginUIView> RunCommand(string itemId, string commandId, string data) =>
        Task.FromResult<IPluginUIView>(null);

    public abstract Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data);

    public virtual Task Cancel() => Task.CompletedTask;

    public virtual void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data)
    {
    }

    protected void RaiseUIViewInfoChanged() =>
        UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
}

/// <summary>
/// 通用的“一个分组 = 一个 tab 页”视图：ContentData 直接就是分组选项对象，
/// 保存时把回传的对象写回 <see cref="PluginConfiguration"/> 并落盘。
/// </summary>
internal sealed class ConfigurationTabView<TGroup> : PluginTabPageView
    where TGroup : EditableOptionsBase
{
    private readonly Action<PluginConfiguration, TGroup> _setter;

    public ConfigurationTabView(Func<PluginConfiguration, TGroup> getter,
        Action<PluginConfiguration, TGroup> setter)
        : base(getter(Plugin.Instance.Configuration))
    {
        _setter = setter;

        // 多版本合并页里的「媒体库」多选列表：每次打开都现读一次服务器媒体库，
        // 这样新增/删除/改名的库能立刻反映到界面上。
        if (ContentData is MergeOptions merge)
            merge.LibraryList = BuildLibraryOptions();
    }

    public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        if (ContentData is TGroup group)
        {
            var configuration = Plugin.Instance.Configuration;
            _setter(configuration, group);
            Plugin.Instance.SaveConfiguration(configuration);
        }

        return Task.FromResult<IPluginUIView>(this);
    }

    /// <summary>
    /// 构造「媒体库」多选项：筛选规则与合并任务
    /// （<c>MergeMultiVersionsTask.GetMovieLibraries()</c>）保持一致 ——
    /// 电影库 + 未指定类型的混合库。Value 用库名，因为后端配置里存的就是库名。
    /// </summary>
    private static List<EditorSelectOption> BuildLibraryOptions()
    {
        try
        {
            var libraryManager = Plugin.Instance.ApplicationHost?.Resolve<ILibraryManager>();
            if (libraryManager == null)
                return new List<EditorSelectOption>();

            return libraryManager.GetVirtualFolders()
                .Where(folder => string.IsNullOrWhiteSpace(folder.CollectionType)
                                 || string.Equals(folder.CollectionType, "movies",
                                     StringComparison.OrdinalIgnoreCase))
                .OrderBy(folder => folder.Name, StringComparer.CurrentCulture)
                .Select(folder => new EditorSelectOption
                {
                    Value = folder.Name,
                    Name = folder.Name,
                    IsEnabled = true
                })
                .ToList();
        }
        catch (Exception e)
        {
            Plugin.Instance.logger?.ErrorException("Build merge library options failed", e);
            return new List<EditorSelectOption>();
        }
    }
}

/// <summary>
/// 单个 tab 页的控制器。
/// </summary>
internal sealed class PluginTabPageController : IPluginUIPageController
{
    private readonly Func<IPluginUIView> _viewFactory;

    public PluginTabPageController(string name, string displayName, Func<IPluginUIView> viewFactory)
    {
        _viewFactory = viewFactory;
        PageInfo = new PluginPageInfo
        {
            Name = name,
            DisplayName = displayName,
            IsMainConfigPage = false,
            EnableInMainMenu = false
        };
    }

    public PluginPageInfo PageInfo { get; }

    public Task Initialize(CancellationToken token) => Task.CompletedTask;

    public Task<IPluginUIView> CreateDefaultPageView() => Task.FromResult(_viewFactory());
}

/// <summary>
/// 设置主页。它本身是第一个 tab（常规），并通过 <see cref="IHasTabbedUIPages"/>
/// 把其余分组注册成 Emby Dashboard 顶部的一排 tab 页。
/// </summary>
internal sealed class MetaTubeMainPageController : IPluginUIPageController, IHasTabbedUIPages
{
    private readonly List<IPluginUIPageController> _tabPages = new();

    public MetaTubeMainPageController()
    {
        // 除了插件卡片的“设置”，再在左侧菜单里给一个固定入口（同 StrmAssistant），
        // 点进来永远是第一个 tab（General）。
        PageInfo = new PluginPageInfo
        {
            Name = "Settings",
            DisplayName = "MetaTube",
            IsMainConfigPage = true,
            EnableInMainMenu = true,
            MenuIcon = "video_library"
        };

        _tabPages.Add(CreateTab("BadgeImageOptions", "角标 / 图片",
            c => c.BadgeImage, (c, v) => c.BadgeImage = v));
        _tabPages.Add(CreateTab("ProviderOptions", "数据源 / JavDB",
            c => c.Provider, (c, v) => c.Provider = v));
        _tabPages.Add(CreateTab("TemplateOptions", "文本模板",
            c => c.Template, (c, v) => c.Template = v));
        // 翻译页是自定义视图：Provider 列表 + 新增/编辑表单 + 三个场景下拉。
        // 旧的「备用翻译」tab 已合并进翻译页的「场景路由」。
        _tabPages.Add(new PluginTabPageController("TranslationOptions", "翻译",
            () => TranslationTabView.FromConfiguration()));
        _tabPages.Add(CreateTab("MetadataOptions", "评论 / 刷新",
            c => c.Metadata, (c, v) => c.Metadata = v));
        // 替换表用自定义界面（列表 + 编辑表单），不走反射表单
        _tabPages.Add(new PluginTabPageController("SubstitutionOptions", "替换表",
            () => SubstitutionTabView.FromConfiguration()));
        _tabPages.Add(CreateTab("MergeOptions", "多版本合并",
            c => c.Merge, (c, v) => c.Merge = v));
    }

    public PluginPageInfo PageInfo { get; }

    public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabPages.AsReadOnly();

    public Task Initialize(CancellationToken token) => Task.CompletedTask;

    public Task<IPluginUIView> CreateDefaultPageView() =>
        Task.FromResult<IPluginUIView>(
            new ConfigurationTabView<GeneralOptions>(c => c.General, (c, v) => c.General = v));

    private static IPluginUIPageController CreateTab<TGroup>(string name, string displayName,
        Func<PluginConfiguration, TGroup> getter, Action<PluginConfiguration, TGroup> setter)
        where TGroup : EditableOptionsBase =>
        new PluginTabPageController(name, displayName,
            () => new ConfigurationTabView<TGroup>(getter, setter));
}
#endif
