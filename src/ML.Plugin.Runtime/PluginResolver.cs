namespace ML.Plugin.Runtime;

/// <summary>排序结果：能加载的按序，不能加载的给出原因。</summary>
public sealed record PluginLoadPlan(
    IReadOnlyList<PluginManifest> Order,
    IReadOnlyDictionary<string, string> Rejected);

/// <summary>
/// 把一堆清单排成加载顺序，顺手把冲突、缺依赖、循环依赖剔出去。
/// </summary>
/// <remarks>
/// 依赖的**版本区间**暂不校验（只校验"在不在"）。区间解析要么用 NuGet 的
/// <c>VersionRange</c>（等于引一个包），要么自己写一套语义化版本比较——两件事都该单独决定，
/// 所以这里先只做存在性校验，并在界面上把这件事说明白，而不是假装校验过了。
/// </remarks>
public static class PluginResolver
{
    public static PluginLoadPlan Resolve(IReadOnlyList<PluginManifest> plugins)
    {
        var rejected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);

        foreach (PluginManifest plugin in plugins)
        {
            if (!byId.TryAdd(plugin.Id, plugin))
            {
                rejected[plugin.Id] = $"有两个插件都叫 {plugin.Id}，两个都不会加载";
            }
        }

        // 冲突：任一方声明了另一方，双方都退出（不替用户做"谁更重要"的判断）。
        foreach (PluginManifest plugin in plugins)
        {
            foreach (string other in plugin.Conflicts)
            {
                if (byId.ContainsKey(other))
                {
                    rejected[plugin.Id] = $"与 {other} 冲突";
                    rejected.TryAdd(other, $"与 {plugin.Id} 冲突");
                }
            }
        }

        // 缺依赖：直接退出，而不是"先加载再让它自己炸"。
        foreach (PluginManifest plugin in plugins)
        {
            foreach (string dependency in plugin.Dependencies.Keys)
            {
                if (!byId.ContainsKey(dependency))
                {
                    rejected[plugin.Id] = $"缺少依赖 {dependency}";
                }
            }
        }

        var candidates = plugins
            .Where(p => !rejected.ContainsKey(p.Id))
            .ToList();

        // 拓扑排序：dependencies 与 loadAfter 都是"我在它后面"。
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (PluginManifest plugin in candidates)
        {
            edges[plugin.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            inDegree[plugin.Id] = 0;
        }

        foreach (PluginManifest plugin in candidates)
        {
            foreach (string after in plugin.Dependencies.Keys.Concat(plugin.LoadAfter))
            {
                if (edges.TryGetValue(after, out HashSet<string>? outgoing) && after != plugin.Id)
                {
                    if (outgoing.Add(plugin.Id))
                    {
                        inDegree[plugin.Id]++;
                    }
                }
            }

            // loadBefore：我要求在它前面 → 把它挂在我后面。
            foreach (string before in plugin.LoadBefore)
            {
                if (edges.TryGetValue(before, out HashSet<string>? outgoing) && before != plugin.Id)
                {
                    if (outgoing.Add(plugin.Id))
                    {
                        inDegree[plugin.Id]++;
                    }
                }
            }
        }

        var ready = new List<string>(inDegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var order = new List<PluginManifest>();

        while (ready.Count > 0)
        {
            // 同层的按 id 排，保证每次启动顺序一致（顺序不稳会让问题难以复现）。
            ready.Sort(StringComparer.OrdinalIgnoreCase);
            string current = ready[0];
            ready.RemoveAt(0);
            order.Add(byId[current]);

            foreach (string next in edges[current])
            {
                if (--inDegree[next] == 0)
                {
                    ready.Add(next);
                }
            }
        }

        if (order.Count != candidates.Count)
        {
            // 剩下的都在环里：整环禁用，并告诉他们跟谁绕住了。
            var remaining = candidates.Where(p => !order.Any(o => o.Id == p.Id)).ToList();
            string names = string.Join("、", remaining.Select(p => p.Id));

            foreach (PluginManifest plugin in remaining)
            {
                rejected[plugin.Id] = $"依赖成环（{names}）";
            }
        }

        return new PluginLoadPlan(order, rejected);
    }
}
