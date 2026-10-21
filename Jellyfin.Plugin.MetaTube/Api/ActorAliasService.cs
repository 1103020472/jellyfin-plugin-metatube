#if __EMBY__
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Jellyfin.Plugin.MetaTube.Api;

/// <summary>
/// 演员页「关联为常用名」按钮的后端实现。
///
/// 全部逻辑都在服务端：前端只发「别名 + 常用名」，遇到需要用户拍板的情况
/// （别名已挂在别人下面 / 别名本身是一条常用名）返回 <c>confirm</c>，
/// 前端确认后带标记重发即可，避免前端复制一份判断逻辑。
/// </summary>
public class ActorAliasService : IService
{
    private static ILogger Logger => Plugin.Instance.logger;

    public object Post(LinkActorAlias request)
    {
        try
        {
            return Link(request);
        }
        catch (Exception ex)
        {
            Logger.Error("[MetaTube] 关联演员别名失败: {0}", ex.Message);
            return new LinkActorAliasResult
            {
                Status = "error",
                Message = $"服务端出错：{ex.Message}"
            };
        }
    }

    /// <summary>
    /// 查这个名字在替换表里的身份，供演员页按钮显示
    /// 「当前常用名：xxx」/「当前别名：xxx、xxx」。
    /// </summary>
    public object Get(LookupActorAlias request)
    {
        var name = (request.Name ?? string.Empty).Trim();

        try
        {
            if (name.Length == 0)
                return new LookupActorAliasResult { Status = "invalid" };

            var options = Plugin.Instance.Configuration.Substitution;
            var map = ActorSubstitutionMap.Parse(options.ActorRawSubstitutionTable, options.ActorRawExclusionList);

            // 1. 它本身就是一条常用名
            var self = map.Find(name);
            if (self != null)
            {
                return new LookupActorAliasResult
                {
                    Status = "common",
                    Name = name,
                    CommonName = self.CommonName,
                    Aliases = self.Aliases.ToArray()
                };
            }

            // 2. 它是某个常用名下的别名
            var owner = map.Groups.FirstOrDefault(g => g.Aliases.Any(a => Same(a, name)));
            if (owner != null)
            {
                return new LookupActorAliasResult
                {
                    Status = "alias",
                    Name = name,
                    CommonName = owner.CommonName,
                    Aliases = owner.Aliases.ToArray()
                };
            }

            return new LookupActorAliasResult { Status = "none", Name = name };
        }
        catch (Exception ex)
        {
            Logger.Error("[MetaTube] 查询演员别名失败: {0}", ex.Message);
            return new LookupActorAliasResult { Status = "error", Name = name };
        }
    }

    private static LinkActorAliasResult Link(LinkActorAlias request)
    {
        var aliases = SplitAliases(request.Alias);
        var common = (request.CommonName ?? string.Empty).Trim();

        if (aliases.Count == 0)
            return Fail("invalid", "别名是空的，没法关联。");

        if (common.Length == 0)
            return Fail("invalid", "常用名是空的，没法关联。");

        aliases.RemoveAll(a => Same(a, common));
        if (aliases.Count == 0)
            return Fail("invalid", $"「{common}」不需要关联到它自己。");

        var configuration = Plugin.Instance.Configuration;
        var options = configuration.Substitution;
        var map = ActorSubstitutionMap.Parse(options.ActorRawSubstitutionTable, options.ActorRawExclusionList);

        // ---------- 1. 逐个别名判断会发生什么 ----------
        var toAdd = new List<string>();
        var already = new List<string>();
        var moves = new List<string>();          // 「别名现在挂在 X 下面」的说明
        var merges = new List<ActorSubstitutionGroup>(); // 别名本身是常用名的那些组

        foreach (var alias in aliases)
        {
            var current = map.Find(common);
            if (current != null && current.Aliases.Any(a => Same(a, alias)))
            {
                already.Add(alias);
                continue;
            }

            var owner = map.Groups.FirstOrDefault(g => g.Aliases.Any(a => Same(a, alias)));
            if (owner != null && !Same(owner.CommonName, common))
                moves.Add($"「{alias}」现在挂在「{owner.CommonName}」下面");

            var own = map.Find(alias);
            if (own != null && merges.All(g => !Same(g.CommonName, own.CommonName)))
                merges.Add(own);

            toAdd.Add(alias);
        }

        if (toAdd.Count == 0)
        {
            return new LinkActorAliasResult
            {
                Status = "exists",
                Message = $"{Quote(already)}已经挂在「{common}」下面了，不用再加。",
                Alias = string.Join(", ", already),
                CommonName = common,
                Aliases = map.Find(common)?.Aliases.ToArray() ?? Array.Empty<string>()
            };
        }

        // ---------- 2. 需要用户确认的情况 ----------
        if (!request.Overwrite && moves.Count > 0)
        {
            return new LinkActorAliasResult
            {
                Status = "confirm",
                Message = $"{string.Join("；", moves)}，确定要改挂到「{common}」下面吗？",
                NeedOverwrite = true,
                NeedMerge = merges.Count > 0,
                Alias = string.Join(", ", toAdd),
                CommonName = common
            };
        }

        if (!request.MergeGroup && merges.Count > 0)
        {
            var text = string.Join("；", merges.Select(g => g.Aliases.Count > 0
                ? $"「{g.CommonName}」本身就是一条常用名（下面还有 {g.Aliases.Count} 个别名）"
                : $"「{g.CommonName}」本身就是一条常用名"));

            return new LinkActorAliasResult
            {
                Status = "confirm",
                Message = $"{text}，把它整条并到「{common}」下面吗？并进来以后原来的名字会变成别名。",
                NeedMerge = true,
                Alias = string.Join(", ", toAdd),
                CommonName = common
            };
        }

        // ---------- 3. 动手改 ----------
        foreach (var group in merges)
        {
            foreach (var alias in group.Aliases.Where(a => !Same(a, common)).ToList())
            {
                if (toAdd.All(a => !Same(a, alias)))
                    toAdd.Add(alias);
            }

            map.Groups.RemoveAll(g => Same(g.CommonName, group.CommonName));
        }

        // 这些名字原来挂在别的常用名下（改挂场景），必须先摘干净，
        // 否则两条记录都声明同一个别名，去重后会保留旧的那条，等于没改。
        foreach (var alias in toAdd)
        {
            foreach (var group in map.Groups)
            {
                if (Same(group.CommonName, common))
                    continue;

                group.Aliases.RemoveAll(a => Same(a, alias));
            }
        }

        // 被摘空的记录留着也没意义
        map.Groups.RemoveAll(g => !Same(g.CommonName, common) && g.Aliases.Count == 0);

        var created = false;
        var destination = map.Groups.FirstOrDefault(g => Same(g.CommonName, common));
        if (destination == null)
        {
            destination = new ActorSubstitutionGroup { CommonName = common };
            map.Groups.Add(destination);
            created = true;
        }

        foreach (var alias in toAdd)
        {
            if (!destination.Aliases.Any(a => Same(a, alias)))
                destination.Aliases.Add(alias);
        }

        // 这两个名字要是在排除名单里，映射永远不生效，顺手移出去
        var removedExclusions = new List<string>();
        foreach (var name in toAdd.Concat(new[] { common }))
        {
            var hit = map.Exclusions.FirstOrDefault(e => Same(e, name));
            if (hit == null)
                continue;

            map.Exclusions.Remove(hit);
            removedExclusions.Add(hit);
        }

        var normalized = map.Normalize();
        var final = normalized.Find(common);

        options.ActorRawSubstitutionTable = normalized.ToGroupsText();
        options.ActorRawExclusionList = normalized.ToExclusionsText();
        Plugin.Instance.SaveConfiguration(configuration);

        // ---------- 4. 回话 ----------
        var parts = new List<string>();
        parts.Add(created
            ? $"已新建常用名「{common}」，并把{Quote(toAdd)}作为它的别名"
            : $"已把{Quote(toAdd)}加到「{common}」的别名里");

        if (already.Count > 0)
            parts.Add($"{Quote(already)}本来就在里面");

        if (final != null && final.Aliases.Count > 0)
            parts.Add($"它现在共 {final.Aliases.Count} 个别名：{string.Join("、", final.Aliases)}");

        if (removedExclusions.Count > 0)
            parts.Add($"同时把{Quote(removedExclusions)}移出了演员排除名单（不移出的话这个名字不会被替换）");

        if (!options.EnableActorSubstitution)
            parts.Add("注意：演员替换开关是关着的，去「替换表」页打开后才会生效");

        if (normalized.Warnings.Count > 0)
            parts.Add("提示：" + string.Join("；", normalized.Warnings.Take(3)));

        Logger.Info("[MetaTube] 演员别名已关联: {0} => {1}", string.Join(", ", toAdd), common);

        return new LinkActorAliasResult
        {
            Status = "ok",
            Message = string.Join("。", parts) + "。",
            Alias = string.Join(", ", toAdd),
            CommonName = common,
            Aliases = final?.Aliases.ToArray() ?? Array.Empty<string>(),
            Created = created
        };
    }

    private static LinkActorAliasResult Fail(string status, string message)
    {
        return new LinkActorAliasResult { Status = status, Message = message };
    }

    private static string Quote(IEnumerable<string> names)
    {
        return string.Join("、", names.Select(n => $"「{n}」"));
    }

    private static bool Same(string a, string b)
    {
        return string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>别名可以是多个（前端理论上用不到，留着方便以后批量补）。</summary>
    private static List<string> SplitAliases(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var part in text.Split(new[] { ',', '，', '、', ';', '；', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length > 0 && result.All(r => !Same(r, name)))
                result.Add(name);
        }

        return result;
    }
}
#endif
