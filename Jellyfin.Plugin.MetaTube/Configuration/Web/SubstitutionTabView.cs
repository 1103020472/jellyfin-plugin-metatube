#if __EMBY__
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using IEditableObject = MediaBrowser.Model.GenericEdit.IEditableObject;

namespace Jellyfin.Plugin.MetaTube.Configuration.Web;

/// <summary>
/// 替换表页面提交上来 / 下发下去的扁平数据。
/// 只挑需要的字段，避开 Emby 那些 POCO 控件类型的反序列化坑。
/// </summary>
internal sealed class SubstitutionFormDto
{
    public bool EnableActorSubstitution { get; set; }

    public bool EnableActorExclusion { get; set; } = true;

    public string ActorRawSubstitutionTable { get; set; } = string.Empty;

    public string ActorRawExclusionList { get; set; } = string.Empty;

    public bool EnableTitleSubstitution { get; set; }

    public string TitleRawSubstitutionTable { get; set; } = string.Empty;

    public bool EnableGenreSubstitution { get; set; }

    public string GenreRawSubstitutionTable { get; set; } = string.Empty;

    // ---- 编辑表单专属字段 ----

    public string OriginalCommonName { get; set; } = string.Empty;

    public string CommonName { get; set; } = string.Empty;

    public string Aliases { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static SubstitutionFormDto FromConfiguration()
    {
        var options = Plugin.Instance.Configuration.Substitution;
        return new SubstitutionFormDto
        {
            EnableActorSubstitution = options.EnableActorSubstitution,
            EnableActorExclusion = options.EnableActorExclusion,
            ActorRawSubstitutionTable = options.ActorRawSubstitutionTable,
            ActorRawExclusionList = options.ActorRawExclusionList,
            EnableTitleSubstitution = options.EnableTitleSubstitution,
            TitleRawSubstitutionTable = options.TitleRawSubstitutionTable,
            EnableGenreSubstitution = options.EnableGenreSubstitution,
            GenreRawSubstitutionTable = options.GenreRawSubstitutionTable
        };
    }

    public static SubstitutionFormDto TryParse(string json, SubstitutionFormDto fallback = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback ?? FromConfiguration();

        try
        {
            var parsed = JsonSerializer.Deserialize<SubstitutionFormDto>(json, JsonOptions);
            return parsed ?? fallback ?? FromConfiguration();
        }
        catch (JsonException)
        {
            return fallback ?? FromConfiguration();
        }
    }

    /// <summary>把当前 dto 写回插件配置并落盘。</summary>
    public void Save()
    {
        var configuration = Plugin.Instance.Configuration;
        var options = configuration.Substitution;

        options.EnableActorSubstitution = EnableActorSubstitution;
        options.EnableActorExclusion = EnableActorExclusion;
        options.ActorRawSubstitutionTable = ActorRawSubstitutionTable;
        options.ActorRawExclusionList = ActorRawExclusionList;
        options.EnableTitleSubstitution = EnableTitleSubstitution;
        options.TitleRawSubstitutionTable = TitleRawSubstitutionTable;
        options.EnableGenreSubstitution = EnableGenreSubstitution;
        options.GenreRawSubstitutionTable = GenreRawSubstitutionTable;

        Plugin.Instance.SaveConfiguration(configuration);
    }

    public ActorSubstitutionMap BuildMap()
    {
        return ActorSubstitutionMap.Parse(ActorRawSubstitutionTable, ActorRawExclusionList);
    }

    /// <summary>整理（去重/冲突处理）后写回文本字段。</summary>
    public ActorSubstitutionMap NormalizeMap()
    {
        var map = BuildMap();
        var normalized = map.Normalize();
        ActorRawSubstitutionTable = normalized.ToGroupsText();
        ActorRawExclusionList = normalized.ToExclusionsText();
        return normalized;
    }
}

/// <summary>替换表主页：演员列表 + 排除名单 + 高级文本框。</summary>
internal sealed class SubstitutionTabView : PluginTabPageView
{
    private const string CmdActorAdd = "actor.add";
    private const string CmdActorEdit = "actor.edit";
    private const string CmdActorDelete = "actor.del";
    private const string CmdActorTidy = "actor.tidy";
    private const string CmdExclusionAdd = "exclusion.add";
    private const string CmdExclusionDelete = "exclusion.del";

    private SubstitutionTabView(IEditableObject content) : base(content)
    {
    }

    /// <summary>按当前配置生成列表页。</summary>
    public static SubstitutionTabView FromConfiguration(string message = null)
    {
        return Build(SubstitutionFormDto.FromConfiguration(), message);
    }

    public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        var dto = SubstitutionFormDto.TryParse(data);
        var map = dto.NormalizeMap();
        dto.Save();

        return Task.FromResult(RefreshList(dto, DescribeWarnings(map, "已保存。")));
    }

    public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        // 底部「保存」按钮走的是 PageSave：不同 Emby 版本对它的分发层级不一样，
        // 这里显式接住，保证一定会走到 OnSaveCommand。
        if (commandId == "PageSave")
            return OnSaveCommand(itemId, commandId, data);

        var dto = SubstitutionFormDto.TryParse(data);

        switch (commandId)
        {
            case CmdActorAdd:
                return Task.FromResult<IPluginUIView>(SubstitutionFormTabView.ForActor(dto, null));

            case CmdActorEdit:
                return Task.FromResult<IPluginUIView>(
                    SubstitutionFormTabView.ForActor(dto, dto.BuildMap().Find(itemId)));

            case CmdActorDelete:
            {
                var map = dto.BuildMap();
                var removed = map.Remove(itemId);
                dto.ActorRawSubstitutionTable = map.ToGroupsText();
                dto.ActorRawExclusionList = map.ToExclusionsText();
                dto.Save();
                return Task.FromResult(RefreshList(dto,
                    removed ? $"已删除「{itemId}」。" : $"没找到「{itemId}」，可能已经被删掉了。"));
            }

            case CmdActorTidy:
            {
                var map = dto.NormalizeMap();
                dto.Save();
                return Task.FromResult(RefreshList(dto, DescribeWarnings(map, "已整理。")));
            }

            case CmdExclusionAdd:
                return Task.FromResult<IPluginUIView>(SubstitutionFormTabView.ForExclusion(dto));

            case CmdExclusionDelete:
            {
                var map = dto.BuildMap();
                var target = map.Exclusions.FirstOrDefault(e => string.Equals(e, itemId, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                    map.Exclusions.Remove(target);

                dto.ActorRawSubstitutionTable = map.ToGroupsText();
                dto.ActorRawExclusionList = map.ToExclusionsText();
                dto.Save();
                return Task.FromResult(RefreshList(dto,
                    target != null ? $"已移出排除名单：「{itemId}」。" : $"排除名单里没有「{itemId}」。"));
            }

            default:
                return Task.FromResult<IPluginUIView>(this);
        }
    }

    private static SubstitutionTabView Build(SubstitutionFormDto dto, string message)
    {
        return new SubstitutionTabView(CreateContent(dto, message));
    }

    /// <summary>
    /// 原地刷新列表：复用同一个视图实例（ViewId 不变），前端只会增量更新
    /// 列表内容，不会像换页那样把长列表的滚动位置弹回顶部。
    /// </summary>
    private IPluginUIView RefreshList(SubstitutionFormDto dto, string message)
    {
        ContentData = CreateContent(dto, message);
        RaiseUIViewInfoChanged();
        return this;
    }

    private static SubstitutionEditModel CreateContent(SubstitutionFormDto dto, string message)
    {
        var map = dto.BuildMap();

        var model = new SubstitutionEditModel
        {
            EnableActorSubstitution = dto.EnableActorSubstitution,
            EnableActorExclusion = dto.EnableActorExclusion,
            ActorRawSubstitutionTable = dto.ActorRawSubstitutionTable,
            ActorRawExclusionList = dto.ActorRawExclusionList,
            EnableTitleSubstitution = dto.EnableTitleSubstitution,
            TitleRawSubstitutionTable = dto.TitleRawSubstitutionTable,
            EnableGenreSubstitution = dto.EnableGenreSubstitution,
            GenreRawSubstitutionTable = dto.GenreRawSubstitutionTable,

            ActorRows = map.Groups.Select(BuildActorRow).ToList(),
            ExclusionRows = map.Exclusions.Select(BuildExclusionRow).ToList(),

            AddActorButton = new ButtonItem
            {
                Caption = "新增演员",
                Icon = IconNames.add,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdActorAdd
            },
            TidyActorButton = new ButtonItem
            {
                Caption = "整理 / 去重",
                Icon = IconNames.autorenew,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdActorTidy
            },
            AddExclusionButton = new ButtonItem
            {
                Caption = "新增排除项",
                Icon = IconNames.add,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdExclusionAdd
            },
            ActorSummary = new LabelItem
            {
                IsVisible = true,
                Text = map.Groups.Count == 0
                    ? "还没有配置任何演员。点下面的「新增演员」开始，或者直接把现成的表粘到底部的高级文本框里再点保存。"
                    : $"共 {map.Groups.Count} 位演员、{map.AliasCount} 个别名。列表按添加时间排列。"
            },
            ActorHint = new LabelItem
            {
                IsVisible = true,
                Text = message ?? "删除一行 = 不再做这个替换；想彻底屏蔽某个演员，请用下面的排除名单。"
            },
            ExclusionSummary = new LabelItem
            {
                IsVisible = true,
                Text = map.Exclusions.Count == 0
                    ? "排除名单是空的（名字命中后该演员不会出现在演职员里）。"
                    : $"排除名单共 {map.Exclusions.Count} 项。"
            }
        };

        return model;
    }

    private static GenericListItem BuildActorRow(ActorSubstitutionGroup group)
    {
        var aliases = group.Aliases.Count == 0
            ? "（暂无别名）"
            : $"{group.Aliases.Count} 个别名：{string.Join("、", group.Aliases)}";

        return new GenericListItem
        {
            PrimaryText = group.CommonName,
            SecondaryText = aliases,
            Button1 = new ButtonItem
            {
                Icon = IconNames.edit,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdActorEdit,
                Data2 = group.CommonName
            },
            Button2 = new ButtonItem
            {
                Icon = IconNames.delete,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdActorDelete,
                Data2 = group.CommonName,
                ConfirmationPrompt = $"确定删除「{group.CommonName}」这一行吗？（不会影响已经刮削好的条目）"
            }
        };
    }

    private static GenericListItem BuildExclusionRow(string name)
    {
        return new GenericListItem
        {
            PrimaryText = name,
            Button2 = new ButtonItem
            {
                Icon = IconNames.delete,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdExclusionDelete,
                Data2 = name,
                ConfirmationPrompt = $"确定把「{name}」移出排除名单吗？"
            }
        };
    }

    private static string DescribeWarnings(ActorSubstitutionMap map, string prefix)
    {
        if (map.Warnings.Count == 0)
            return prefix;

        var shown = string.Join("；", map.Warnings.Take(3));
        var more = map.Warnings.Count > 3 ? $"（还有 {map.Warnings.Count - 3} 条）" : string.Empty;
        return $"{prefix}{shown}{more}";
    }
}

/// <summary>演员 / 排除项的编辑表单页。</summary>
internal sealed class SubstitutionFormTabView : PluginTabPageView
{
    private const string CmdActorSave = "form.actor.save";
    private const string CmdExclusionSave = "form.exclusion.save";
    private const string CmdCancel = "form.cancel";

    private SubstitutionFormTabView(IEditableObject content) : base(content)
    {
        ShowSave = false;
        ShowBack = false;
    }

    public static SubstitutionFormTabView ForActor(SubstitutionFormDto dto, ActorSubstitutionGroup group, string notice = null)
    {
        var model = new ActorEditFormModel
        {
            CopyFrom = dto,
            OriginalCommonName = group?.CommonName ?? string.Empty,
            CommonName = group?.CommonName ?? string.Empty,
            Aliases = group == null ? string.Empty : string.Join("\n", group.Aliases),
            SaveButton = new ButtonItem
            {
                Caption = group == null ? "添加" : "保存",
                Icon = IconNames.add,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdActorSave
            },
            CancelButton = new ButtonItem
            {
                Caption = "取消",
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdCancel
            },
            Notice = new LabelItem
            {
                IsVisible = true,
                Text = notice ?? (group == null
                    ? "别名一行一个（也可以逗号分隔）；同一个别名只能属于一位演员。"
                    : "改名会重建这条记录，别名也一起更新。")
            }
        };

        model.LoadFrom(dto);
        return new SubstitutionFormTabView(model);
    }

    public static SubstitutionFormTabView ForExclusion(SubstitutionFormDto dto)
    {
        var model = new ExclusionEditFormModel
        {
            CopyFrom = dto,
            Name = string.Empty,
            SaveButton = new ButtonItem
            {
                Caption = "添加",
                Icon = IconNames.add,
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdExclusionSave
            },
            CancelButton = new ButtonItem
            {
                Caption = "取消",
                IsVisible = true,
                IsEnabled = true,
                Data1 = CmdCancel
            },
            Notice = new LabelItem
            {
                IsVisible = true,
                Text = "可以一次粘贴多个名字（逗号或换行分隔）。"
            }
        };

        model.LoadFrom(dto);
        return new SubstitutionFormTabView(model);
    }

    public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        // 表单页不显示全局保存按钮，这里只是兜底。
        return Task.FromResult<IPluginUIView>(this);
    }

    public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        var dto = SubstitutionFormDto.TryParse(data, ContentData is SubstitutionFormBase form ? form.CopyFrom : null);

        switch (commandId)
        {
            case CmdCancel:
                return Task.FromResult<IPluginUIView>(SubstitutionTabView.FromConfiguration("已取消。"));

            case CmdActorSave:
            {
                var commonName = (dto.CommonName ?? string.Empty).Trim();
                var aliases = SplitAliases(dto.Aliases);

                // 没别名的记录不起作用，直接把用户填的内容留在表单上让他补
                if (commonName.Length == 0)
                    return Task.FromResult<IPluginUIView>(ForActor(dto, DraftGroup(dto), "常用名不能为空。"));

                if (aliases.Count == 0)
                    return Task.FromResult<IPluginUIView>(
                        ForActor(dto, DraftGroup(dto), "至少要填一个别名（一行一个，或用逗号分隔）。"));

                var map = dto.BuildMap();
                map.Upsert(dto.OriginalCommonName, commonName, aliases);
                var normalized = map.Normalize();
                dto.ActorRawSubstitutionTable = normalized.ToGroupsText();
                dto.ActorRawExclusionList = normalized.ToExclusionsText();
                dto.Save();

                return Task.FromResult<IPluginUIView>(
                    SubstitutionTabView.FromConfiguration(DescribeSave(normalized, commonName)));
            }

            case CmdExclusionSave:
            {
                var names = SplitAliases(dto.Name);
                if (names.Count == 0)
                    return Task.FromResult<IPluginUIView>(ForExclusion(dto));

                var map = dto.BuildMap();
                foreach (var name in names)
                {
                    // 排除名单里的名字如果还在替换表里当别名，先把那条别名摘掉，避免两处打架
                    var owner = map.Groups.FirstOrDefault(g => g.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));
                    if (owner != null)
                    {
                        owner.Aliases.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

                        // 摘完别名这条就空了，说明它本来就只为这个名字服务，一并清掉
                        if (owner.Aliases.Count == 0)
                            map.Groups.Remove(owner);
                    }

                    map.AddExclusion(name);
                }

                var normalized = map.Normalize();
                dto.ActorRawSubstitutionTable = normalized.ToGroupsText();
                dto.ActorRawExclusionList = normalized.ToExclusionsText();
                dto.Save();

                return Task.FromResult<IPluginUIView>(
                    SubstitutionTabView.FromConfiguration($"已加入排除名单：{string.Join("、", names)}。"));
            }

            default:
                return Task.FromResult<IPluginUIView>(this);
        }
    }

    private static ActorSubstitutionGroup FindGroup(SubstitutionFormDto dto, string commonName)
    {
        if (string.IsNullOrWhiteSpace(commonName))
            return null;

        var group = dto.BuildMap().Find(commonName);
        if (group == null)
            return null;

        // 用表单里当前填的别名重建一遍，避免用户改到一半又点保存时看到旧值
        return new ActorSubstitutionGroup
        {
            CommonName = commonName,
            Aliases = SplitAliases(dto.Aliases)
        };
    }

    /// <summary>把表单里当前填的内容原样包成一条记录，用于校验失败时回显。</summary>
    private static ActorSubstitutionGroup DraftGroup(SubstitutionFormDto dto)
    {
        return new ActorSubstitutionGroup
        {
            CommonName = (dto.CommonName ?? string.Empty).Trim(),
            Aliases = SplitAliases(dto.Aliases)
        };
    }

    private static List<string> SplitAliases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Replace("\r\n", "\n")
            .Split(new[] { '\n', ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    private static string DescribeSave(ActorSubstitutionMap map, string commonName)
    {
        if (map.Warnings.Count == 0)
            return $"已保存「{commonName}」。";

        return $"已保存「{commonName}」，但有问题需要注意：{string.Join("；", map.Warnings.Take(3))}";
    }
}
#endif
