#if __EMBY__
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace Jellyfin.Plugin.MetaTube.Configuration.Web;

// ============================================================
// 「替换表」tab 的自定义界面。
//
// Emby 的插件页是 GenericEdit 按反射生成的，一个属性只能对应一个控件，
// 所以演员替换表原来只能是一个小 textarea。这里重写 CreateEditContainer()：
//   1. 先让基类按反射把普通字段（开关、高级文本框）建出来 —— 这样控件 Id
//      与 JSON 属性名天然对齐，不用自己猜 Emby 的 Id 规则；
//   2. 再把「演员替换」「排除名单」两块改成 分组 + 富列表 + 按钮组 的布局；
//   3. 列表和按钮的数据都挂在 [Browsable(false)] 的 UI 属性上：基类不会为
//      它们建控件，但序列化时仍会带上，setFormValues 才能把值填进去。
// ============================================================

/// <summary>
/// 列表页的数据模型。8 个配置字段是真正的配置，其余属性只用于渲染。
/// </summary>
internal sealed class SubstitutionEditModel : EditableOptionsBase
{
    public override string EditorTitle => "替换表";

    public override string EditorDescription =>
        "演员替换：把源站返回的别名映射成常用名。一行一位演员，等号左边是最终显示的名字。";

    // ===== 真正的配置字段（顺序即界面命中顺序）=====

    [DisplayName("启用演员替换")]
    [Description("把别名替换成常用名；同一位演员的多个别名会合并成一个人。")]
    public bool EnableActorSubstitution { get; set; }

    [DisplayName("启用演员排除名单")]
    [Description("刮削结果里出现下面名单中的名字时，直接从演员列表里剔除。")]
    public bool EnableActorExclusion { get; set; } = true;

    [DisplayName("高级：演员替换表（纯文本）")]
    [Description("一行一位演员：常用名 = 别名1, 别名2。批量粘贴、备份、迁移都改这里；保存时会自动去重整理。")]
    [EditMultiline(10)]
    public string ActorRawSubstitutionTable { get; set; } = string.Empty;

    [DisplayName("高级：演员排除名单（纯文本）")]
    [Description("一行一个名字。")]
    [EditMultiline(4)]
    public string ActorRawExclusionList { get; set; } = string.Empty;

    [DisplayName("启用标题替换")]
    public bool EnableTitleSubstitution { get; set; }

    [DisplayName("标题替换表")]
    [Description("每行一条记录，用等号分隔。等号右侧留空表示删除左侧的关键字。")]
    [EditMultiline(5)]
    public string TitleRawSubstitutionTable { get; set; } = string.Empty;

    [DisplayName("启用类型替换")]
    public bool EnableGenreSubstitution { get; set; }

    [DisplayName("类型替换表")]
    [Description("每行一条记录，用等号分隔。等号右侧留空表示删除左侧的类型名。")]
    [EditMultiline(5)]
    public string GenreRawSubstitutionTable { get; set; } = string.Empty;

    // ===== 仅用于渲染的数据（不落盘到配置里）=====

    [Browsable(false)]
    public List<GenericListItem> ActorRows { get; set; } = new();

    [Browsable(false)]
    public List<GenericListItem> ExclusionRows { get; set; } = new();

    [Browsable(false)]
    public ButtonItem AddActorButton { get; set; } = new();

    [Browsable(false)]
    public ButtonItem TidyActorButton { get; set; } = new();

    [Browsable(false)]
    public ButtonItem AddExclusionButton { get; set; } = new();

    [Browsable(false)]
    public LabelItem ActorSummary { get; set; } = new();

    [Browsable(false)]
    public LabelItem ActorHint { get; set; } = new();

    [Browsable(false)]
    public LabelItem ExclusionSummary { get; set; } = new();

    // ===== 自定义界面 =====

    public override IEditObjectContainer CreateEditContainer()
    {
        var container = base.CreateEditContainer();
        if (container is not EditObjectContainer editContainer || editContainer.EditorRoot == null)
            return container;

        var root = editContainer.EditorRoot;
        var parentId = root.EditorItems?.FirstOrDefault()?.ParentId ?? string.Empty;

        EditorBase Take(string name) => root.EditorItems?.FirstOrDefault(e => e.Name == name);

        var actorItems = new List<EditorBase>();
        AddIfNotNull(actorItems, Take(nameof(EnableActorSubstitution)));
        actorItems.Add(new EditorLabelItem(nameof(ActorSummary), parentId));
        actorItems.Add(new EditorItemList(nameof(ActorRows), parentId));
        actorItems.Add(new EditorButtonGroup(
            new EditorButtonItem[]
            {
                new EditorButtonItem(nameof(AddActorButton), parentId),
                new EditorButtonItem(nameof(TidyActorButton), parentId)
            }, "ActorToolbar", parentId));
        actorItems.Add(new EditorLabelItem(nameof(ActorHint), parentId));
        AddIfNotNull(actorItems, Take(nameof(ActorRawSubstitutionTable)));

        var exclusionItems = new List<EditorBase>();
        AddIfNotNull(exclusionItems, Take(nameof(EnableActorExclusion)));
        exclusionItems.Add(new EditorLabelItem(nameof(ExclusionSummary), parentId));
        exclusionItems.Add(new EditorItemList(nameof(ExclusionRows), parentId));
        exclusionItems.Add(new EditorButtonGroup(
            new EditorButtonItem[] { new EditorButtonItem(nameof(AddExclusionButton), parentId) },
            "ExclusionToolbar", parentId));
        AddIfNotNull(exclusionItems, Take(nameof(ActorRawExclusionList)));

        var titleItems = new List<EditorBase>();
        AddIfNotNull(titleItems, Take(nameof(EnableTitleSubstitution)));
        AddIfNotNull(titleItems, Take(nameof(TitleRawSubstitutionTable)));

        var genreItems = new List<EditorBase>();
        AddIfNotNull(genreItems, Take(nameof(EnableGenreSubstitution)));
        AddIfNotNull(genreItems, Take(nameof(GenreRawSubstitutionTable)));

        root.EditorItems = new EditorBase[]
        {
            new EditorGroup("演员替换", actorItems.ToArray(), "ActorSection", parentId, null),
            new EditorGroup("演员排除名单", exclusionItems.ToArray(), "ExclusionSection", parentId, null),
            new EditorGroup("标题替换", titleItems.ToArray(), "TitleSection", parentId, null),
            new EditorGroup("类型替换", genreItems.ToArray(), "GenreSection", parentId, null)
        };

        return container;
    }

    private static void AddIfNotNull(List<EditorBase> target, EditorBase editor)
    {
        if (editor != null)
            target.Add(editor);
    }
}
#endif
