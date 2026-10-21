#if __EMBY__
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace Jellyfin.Plugin.MetaTube.Configuration.Web;

/// <summary>
/// 编辑表单的公共部分：8 个配置字段不显示，但要挂着一起来回走，
/// 否则「新增/编辑一条记录」时其它几个分组的值（标题表、类型表、各个开关）
/// 会在提交时丢失。
/// </summary>
internal abstract class SubstitutionFormBase : EditableOptionsBase
{
    [Browsable(false)] public bool EnableActorSubstitution { get; set; }

    [Browsable(false)] public bool EnableActorExclusion { get; set; } = true;

    [Browsable(false)] public string ActorRawSubstitutionTable { get; set; } = string.Empty;

    [Browsable(false)] public string ActorRawExclusionList { get; set; } = string.Empty;

    [Browsable(false)] public bool EnableTitleSubstitution { get; set; }

    [Browsable(false)] public string TitleRawSubstitutionTable { get; set; } = string.Empty;

    [Browsable(false)] public bool EnableGenreSubstitution { get; set; }

    [Browsable(false)] public string GenreRawSubstitutionTable { get; set; } = string.Empty;

    /// <summary>进入表单时的配置快照，供提交数据解析失败时兜底。</summary>
    [Browsable(false)] public SubstitutionFormDto CopyFrom { get; set; } = new();

    /// <summary>
    /// 把进入表单前的配置拷进模型。表单只显示其中一两个字段，其余字段必须
    /// 跟着来回走，否则提交时会把它们当成空值写回配置（会清空替换表）。
    /// </summary>
    public void LoadFrom(SubstitutionFormDto dto)
    {
        CopyFrom = dto;
        EnableActorSubstitution = dto.EnableActorSubstitution;
        EnableActorExclusion = dto.EnableActorExclusion;
        ActorRawSubstitutionTable = dto.ActorRawSubstitutionTable;
        ActorRawExclusionList = dto.ActorRawExclusionList;
        EnableTitleSubstitution = dto.EnableTitleSubstitution;
        TitleRawSubstitutionTable = dto.TitleRawSubstitutionTable;
        EnableGenreSubstitution = dto.EnableGenreSubstitution;
        GenreRawSubstitutionTable = dto.GenreRawSubstitutionTable;
    }

    /// <summary>表单底部的按钮。</summary>
    [Browsable(false)] public ButtonItem SaveButton { get; set; } = new();

    [Browsable(false)] public ButtonItem CancelButton { get; set; } = new();

    [Browsable(false)] public LabelItem Notice { get; set; } = new();

    /// <summary>把两个按钮插到编辑器树末尾（基类已经按反射建好普通字段）。</summary>
    protected IEditObjectContainer AppendToolbar(IEditObjectContainer container, string name)
    {
        if (container is not EditObjectContainer editContainer || editContainer.EditorRoot == null)
            return container;

        var root = editContainer.EditorRoot;
        var parentId = root.EditorItems?.FirstOrDefault()?.ParentId ?? string.Empty;
        var items = (root.EditorItems ?? Array.Empty<EditorBase>()).ToList();

        items.Add(new EditorLabelItem(nameof(Notice), parentId));
        items.Add(new EditorButtonGroup(
            new EditorButtonItem[]
            {
                new EditorButtonItem(nameof(SaveButton), parentId),
                new EditorButtonItem(nameof(CancelButton), parentId)
            }, name, parentId));

        root.EditorItems = items.ToArray();
        return container;
    }
}

/// <summary>新增 / 编辑一位演员。</summary>
internal sealed class ActorEditFormModel : SubstitutionFormBase
{
    public override string EditorTitle => string.IsNullOrEmpty(OriginalCommonName) ? "新增演员" : "编辑演员";

    public override string EditorDescription =>
        "左边填最终要显示的名字（常用名），右边填源站可能返回的各种写法（别名），一行一个或用逗号分隔。";

    /// <summary>被编辑的那条记录的常用名；新增时为空。</summary>
    [Browsable(false)] public string OriginalCommonName { get; set; } = string.Empty;

    [DisplayName("常用名")]
    [Description("Emby 里最终显示的演员名。留空表示放弃这次编辑。")]
    public string CommonName { get; set; } = string.Empty;

    [DisplayName("别名")]
    [Description("一行一个，或用逗号分隔。这些写法都会被替换成上面的常用名。")]
    [EditMultiline(8)]
    public string Aliases { get; set; } = string.Empty;

    public override IEditObjectContainer CreateEditContainer()
    {
        return AppendToolbar(base.CreateEditContainer(), "ActorFormToolbar");
    }
}

/// <summary>新增 / 编辑排除名单里的一项。</summary>
internal sealed class ExclusionEditFormModel : SubstitutionFormBase
{
    public override string EditorTitle => "新增排除项";

    public override string EditorDescription => "刮削结果里出现这个名字时，直接从演员列表里剔除。";

    [DisplayName("演员名")]
    [Description("要剔除的名字，可以一次粘贴多个（逗号或换行分隔）。")]
    [EditMultiline(6)]
    public string Name { get; set; } = string.Empty;

    public override IEditObjectContainer CreateEditContainer()
    {
        return AppendToolbar(base.CreateEditContainer(), "ExclusionFormToolbar");
    }
}
#endif
