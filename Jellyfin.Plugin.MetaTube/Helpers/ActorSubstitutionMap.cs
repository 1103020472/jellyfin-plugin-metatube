using System.Text;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 一条演员映射记录：<c>常用名 = 别名1, 别名2, ...</c>
/// </summary>
public sealed class ActorSubstitutionGroup
{
    public string CommonName { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = new();

    public ActorSubstitutionGroup Clone()
    {
        return new ActorSubstitutionGroup
        {
            CommonName = CommonName,
            Aliases = new List<string>(Aliases)
        };
    }
}

/// <summary>
/// 演员替换表的解析结果。
///
/// 文本格式（v2，保存时写这个）：
/// <code>
/// # metatube-actor-map-v2
/// 三上悠亚 = 三上悠亜, Mikami Yua
/// 深田えいみ = 深田咏美
/// </code>
///
/// 兼容老格式（v1，没有上面那行表头时按老格式读）：
/// <code>
/// 三上悠亜=三上悠亚
/// 山岸逢花=
/// </code>
/// 老格式「等号右侧留空」表示删除该演员，解析时会自动并入排除名单。
///
/// 排除名单单独存放（一行一个名字），含义是：刮削结果里出现这个名字就直接丢掉。
/// </summary>
public sealed class ActorSubstitutionMap
{
    /// <summary>v2 格式的标记行。没有这一行就按老格式（别名=常用名）解析。</summary>
    public const string GroupsFormatHeader = "# metatube-actor-map-v2";

    /// <summary>分组列表，顺序即「添加时间」顺序。</summary>
    public List<ActorSubstitutionGroup> Groups { get; } = new();

    /// <summary>排除名单：命中就直接从演员列表里剔除。</summary>
    public List<string> Exclusions { get; } = new();

    /// <summary>最近一次解析/整理发现的问题（重复别名、环形映射等），用于界面提示。</summary>
    public List<string> Warnings { get; } = new();

    public int AliasCount => Groups.Sum(g => g.Aliases.Count);

    public bool IsEmpty => Groups.Count == 0 && Exclusions.Count == 0;

    // ================= 解析 =================

    public static ActorSubstitutionMap Parse(string groupsText, string exclusionsText)
    {
        var map = new ActorSubstitutionMap();

        var lines = SplitLines(groupsText).ToList();
        var isV2 = lines.Any(line => line.StartsWith('#') && line.Contains("actor-map-v2", StringComparison.OrdinalIgnoreCase));

        foreach (var rawLine in lines)
        {
            var line = UnifyPunctuation(rawLine).Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                // 没有等号：当成排除项（对用户手滑宽容一点）
                map.AddExclusion(line);
                continue;
            }

            var left = line[..eq].Trim();
            var right = line[(eq + 1)..].Trim();

            if (left.Length == 0)
                continue;

            if (right.Length == 0)
            {
                if (isV2)
                {
                    // 新格式里右侧留空 = 这条还没有别名（不能当成排除，否则会和排除名单打架）
                    map.AddGroup(left, new List<string>());
                    continue;
                }

                // 老格式「右侧留空 = 删除这个演员」
                map.AddExclusion(left);
                continue;
            }

            if (isV2)
            {
                map.AddGroup(left, SplitAliases(right));
            }
            else
            {
                // 老格式：别名 = 常用名
                var common = right;
                var group = map.Groups.FirstOrDefault(g => Same(g.CommonName, common));
                if (group == null)
                {
                    group = new ActorSubstitutionGroup { CommonName = common };
                    map.Groups.Add(group);
                }

                if (!group.Aliases.Any(a => Same(a, left)))
                    group.Aliases.Add(left);
            }
        }

        foreach (var name in SplitLines(exclusionsText))
        {
            var value = UnifyPunctuation(name).Trim().TrimStart('-', '!', '×', '*').Trim();
            if (value.Length == 0 || value.StartsWith('#'))
                continue;
            map.AddExclusion(value);
        }

        return map;
    }

    /// <summary>把内部结构写回 v2 文本（这是配置里真正存的东西）。</summary>
    public string ToGroupsText()
    {
        var groups = Groups.Where(g => !string.IsNullOrWhiteSpace(g.CommonName)).ToList();
        if (groups.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append(GroupsFormatHeader);
        foreach (var group in groups)
        {
            sb.Append('\n').Append(group.CommonName.Trim());
            if (group.Aliases.Count == 0)
                continue;
            sb.Append(" = ").Append(string.Join(", ", group.Aliases.Select(a => a.Trim())));
        }

        return sb.ToString();
    }

    public string ToExclusionsText()
    {
        return string.Join('\n', Exclusions.Select(e => e.Trim()).Where(e => e.Length > 0));
    }

    // ================= 编辑操作 =================

    public ActorSubstitutionGroup Find(string commonName)
    {
        return Groups.FirstOrDefault(g => Same(g.CommonName, commonName));
    }

    public bool Remove(string commonName)
    {
        var group = Find(commonName);
        return group != null && Groups.Remove(group);
    }

    /// <summary>新增或更新一条记录；新记录追加在末尾（＝按添加时间排序）。</summary>
    public void Upsert(string originalCommonName, string commonName, IEnumerable<string> aliases)
    {
        commonName = commonName.Trim();
        if (commonName.Length == 0)
            return;

        var aliasList = aliases.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

        if (!string.IsNullOrWhiteSpace(originalCommonName) && !Same(originalCommonName, commonName))
            Remove(originalCommonName);

        var group = Find(commonName);
        if (group == null)
        {
            group = new ActorSubstitutionGroup { CommonName = commonName };
            Groups.Add(group);
        }

        group.Aliases.Clear();
        group.Aliases.AddRange(aliasList);
    }

    public void AddExclusion(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Exclusions.Any(e => Same(e, name)))
            return;
        Exclusions.Add(name);
    }

    /// <summary>批量写入排除名单（逗号/换行分隔）。</summary>
    public void SetExclusions(string text)
    {
        Exclusions.Clear();
        foreach (var name in SplitAliases(text))
            AddExclusion(name);
    }

    // ================= 规范化 =================

    /// <summary>
    /// 去重与冲突处理，返回整理后的新对象（问题记在 Warnings 里）。
    /// 规则：
    ///   * 常用名重复的两条记录自动合并；
    ///   * 某个名字既是甲条记录的别名、又是乙条记录的常用名，说明两条其实是
    ///     同一个人（常见于「A=B」「B=A」这种互指），合并成一条，先出现的名字
    ///     当常用名；
    ///   * 剩下的别名冲突保留第一次出现的那条；
    ///   * 空常用名直接丢掉。
    /// </summary>
    public ActorSubstitutionMap Normalize()
    {
        var result = new ActorSubstitutionMap();
        var warnings = result.Warnings;

        // 1) 先按常用名归并（保持原有先后顺序）
        var source = new List<ActorSubstitutionGroup>();
        foreach (var group in Groups)
        {
            var common = group.CommonName?.Trim() ?? string.Empty;
            if (common.Length == 0)
                continue;

            var aliases = group.Aliases.Select(a => a?.Trim() ?? string.Empty).Where(a => a.Length > 0).ToList();
            var existing = source.FirstOrDefault(g => Same(g.CommonName, common));
            if (existing == null)
            {
                source.Add(new ActorSubstitutionGroup { CommonName = common, Aliases = aliases });
            }
            else
            {
                existing.Aliases.AddRange(aliases);
                warnings.Add($"常用名「{common}」出现了多次，已合并为一条。");
            }
        }

        // 2) 别名恰好是另一条的常用名 —— 同一个人，合并（可能是 A=B、B=A 这种环）
        var merged = true;
        while (merged)
        {
            merged = false;

            foreach (var group in source)
            {
                foreach (var alias in group.Aliases.ToList())
                {
                    if (Same(alias, group.CommonName))
                        continue;

                    var other = source.FirstOrDefault(g => !ReferenceEquals(g, group) && Same(g.CommonName, alias));
                    if (other == null)
                        continue;

                    var keep = source.IndexOf(group) <= source.IndexOf(other) ? group : other;
                    var drop = ReferenceEquals(keep, group) ? other : group;

                    keep.Aliases.Add(drop.CommonName);
                    keep.Aliases.AddRange(drop.Aliases);
                    keep.Aliases.RemoveAll(a => Same(a, keep.CommonName));
                    source.Remove(drop);

                    warnings.Add($"「{drop.CommonName}」是「{keep.CommonName}」的另一个名字，两条已合并成「{keep.CommonName}」。");
                    merged = true;
                    break;
                }

                if (merged)
                    break;
            }
        }

        // 3) 别名全局去重（同一个别名只保留第一次出现的那条）
        var usedAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in source)
        {
            var target = new ActorSubstitutionGroup { CommonName = group.CommonName };

            foreach (var alias in group.Aliases)
            {
                if (Same(alias, group.CommonName))
                    continue;

                if (usedAliases.TryGetValue(alias, out var owner))
                {
                    if (!Same(owner, group.CommonName))
                        warnings.Add($"别名「{alias}」同时属于「{owner}」和「{group.CommonName}」，已保留第一次出现的那条。");
                    continue;
                }

                usedAliases[alias] = group.CommonName;
                target.Aliases.Add(alias);
            }

            // 没有别名的记录不起任何作用，直接丢掉（界面上新增时会要求至少填一个别名）
            if (target.Aliases.Count == 0)
                continue;

            result.Groups.Add(target);
        }

        foreach (var name in Exclusions.Select(e => e?.Trim() ?? string.Empty).Where(e => e.Length > 0))
        {
            if (result.Exclusions.Any(e => Same(e, name)))
                continue;
            result.Exclusions.Add(name);
        }

        return result;
    }

    // ================= 替换 =================

    /// <summary>
    /// 把源站返回的演员名映射成常用名；排除名单里的名字直接丢弃，结果去重。
    /// </summary>
    public IEnumerable<string> Substitute(IEnumerable<string> actors)
    {
        if (IsEmpty || actors == null)
            return actors ?? Enumerable.Empty<string>();

        var aliasToCommon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in Groups)
        {
            if (string.IsNullOrWhiteSpace(group.CommonName))
                continue;

            commonNames.Add(group.CommonName.Trim());
            foreach (var alias in group.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                    continue;
                aliasToCommon[alias.Trim()] = group.CommonName.Trim();
            }
        }

        foreach (var name in Exclusions)
        {
            if (!string.IsNullOrWhiteSpace(name))
                excluded.Add(name.Trim());
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in actors)
        {
            var name = raw?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (excluded.Contains(name))
                continue;

            var target = name;
            if (!commonNames.Contains(name) && aliasToCommon.TryGetValue(name, out var common))
                target = common;

            if (seen.Add(target))
                result.Add(target);
        }

        return result;
    }

    // ================= 工具 =================

    private void AddGroup(string commonName, IEnumerable<string> aliases)
    {
        var group = Groups.FirstOrDefault(g => Same(g.CommonName, commonName));
        if (group == null)
        {
            group = new ActorSubstitutionGroup { CommonName = commonName.Trim() };
            Groups.Add(group);
        }

        foreach (var alias in aliases)
        {
            if (alias.Length == 0)
                continue;
            if (!group.Aliases.Any(a => Same(a, alias)))
                group.Aliases.Add(alias);
        }
    }

    private static bool Same(string a, string b)
    {
        return string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            yield return line;
    }

    private static List<string> SplitAliases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split(new[] { ',', '，', '、', ';', '；', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    /// <summary>全角标点统一成半角，避免中英文输入法混用导致解析失败。</summary>
    private static string UnifyPunctuation(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        return line
            .Replace('，', ',')
            .Replace('、', ',')
            .Replace('；', ',')
            .Replace('＝', '=')
            .Replace('\u3000', ' ');
    }
}
