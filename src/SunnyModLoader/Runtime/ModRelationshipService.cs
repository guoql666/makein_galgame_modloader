using System;
using System.Collections.Generic;
using System.Linq;

namespace SunnyModLoader;

internal static class ModRelationshipService
{
    internal static List<ModPackage> ResolveActivePackages(
        IReadOnlyList<ModPackage> all,
        out List<ModScanIssue> issues)
    {
        issues = new List<ModScanIssue>();
        List<ModPackage> packages = (all ?? Array.Empty<ModPackage>()).Where(package => package != null).ToList();
        Dictionary<string, ModPackage> byId = packages.ToDictionary(
            package => package.Id,
            StringComparer.OrdinalIgnoreCase);
        HashSet<ModPackage> blocked = new HashSet<ModPackage>();

        foreach (ModPackage package in packages)
        {
            package.RuntimeBlocked = false;
            package.RuntimeBlockReason = null;
        }

        List<ModPackage> requested = packages.Where(package => package.RuntimeEnabled).ToList();
        foreach (ModPackage package in requested)
        {
            foreach (ModDependencyManifest dependency in package.Manifest.dependencies ?? Array.Empty<ModDependencyManifest>())
            {
                if (!byId.TryGetValue(dependency.id, out ModPackage target))
                {
                    Block(
                        package,
                        blocked,
                        issues,
                        "缺少依赖 Mod '" + dependency.id + "'" + FormatConstraint(dependency.version) + "。");
                    continue;
                }

                if (!target.RuntimeEnabled)
                {
                    Block(
                        package,
                        blocked,
                        issues,
                        "依赖 Mod '" + target.Id + "' 当前未启用。");
                    continue;
                }

                if (!Matches(dependency.version, target.Manifest.version))
                {
                    Block(
                        package,
                        blocked,
                        issues,
                        "依赖 Mod '" + target.Id + "' 的版本 " + target.Manifest.version +
                        " 不满足 " + FormatConstraint(dependency.version) + "。");
                }
            }
        }

        foreach (ModPackage package in requested)
        {
            foreach (ModConflictManifest conflict in package.Manifest.conflicts ?? Array.Empty<ModConflictManifest>())
            {
                if (!byId.TryGetValue(conflict.id, out ModPackage target) || !target.RuntimeEnabled ||
                    !Matches(conflict.version, target.Manifest.version))
                {
                    continue;
                }

                string message = "与已启用 Mod '" + target.Id + "' 声明冲突" +
                                 FormatConstraint(conflict.version) + "，双方均未进入运行时。";
                Block(package, blocked, issues, message);
                Block(target, blocked, issues, "与已启用 Mod '" + package.Id + "' 声明冲突，双方均未进入运行时。");
            }
        }

        PropagateBlockedDependencies(requested, byId, blocked, issues);
        HashSet<ModPackage> cycleNodes = FindCycleNodes(requested, byId, blocked);
        foreach (ModPackage cycleNode in cycleNodes)
        {
            Block(cycleNode, blocked, issues, "依赖关系形成循环，无法确定加载顺序。");
        }

        PropagateBlockedDependencies(requested, byId, blocked, issues);
        return TopologicalOrder(requested.Where(package => !blocked.Contains(package)).ToList(), byId);
    }

    internal static IReadOnlyList<ModScanIssue> AnalyzeOverrides(IReadOnlyList<ModPackage> active)
    {
        List<ModScanIssue> issues = new List<ModScanIssue>();
        Dictionary<string, OverrideClaim> claims = new Dictionary<string, OverrideClaim>(StringComparer.OrdinalIgnoreCase);
        foreach (ModPackage package in active ?? Array.Empty<ModPackage>())
        {
            foreach (DialoguePatchDefinition patch in package.DialoguePatches ?? Array.Empty<DialoguePatchDefinition>())
            {
                if (patch?.anchor == null || patch.set == null)
                {
                    continue;
                }

                string target = TryBuildAnchorKey(patch.anchor);
                if (target == null)
                {
                    continue;
                }

                if (patch.set.text != null)
                {
                    AddClaim(
                        package,
                        claims,
                        issues,
                        "dialogue-text|" + target,
                        "台词文本",
                        patch.set.text,
                        "台词锚点 " + target);
                }

                if (!string.IsNullOrWhiteSpace(patch.set.voice))
                {
                    AddClaim(
                        package,
                        claims,
                        issues,
                        "dialogue-voice|" + target,
                        "台词语音",
                        package.Id + "|" + patch.set.voice + "|" +
                        patch.set.volume.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        "台词锚点 " + target);
                }
            }

            foreach (OverlayDefinition overlay in package.Overlays ?? Array.Empty<OverlayDefinition>())
            {
                if (overlay == null || string.IsNullOrWhiteSpace(overlay.kind) || string.IsNullOrWhiteSpace(overlay.target))
                {
                    continue;
                }

                string kind = overlay.kind.ToLowerInvariant();
                string target = LoaderUtil.NormalizeResourceKey(overlay.target);
                AddClaim(
                    package,
                    claims,
                    issues,
                    "overlay|" + kind + "|" + target,
                    "全局 " + overlay.kind + " 资源替换",
                    package.Id + "|" + (overlay.source ?? string.Empty),
                    overlay.kind + " 资源 " + target);
            }
        }

        return issues;
    }

    private static void AddClaim(
        ModPackage package,
        Dictionary<string, OverrideClaim> claims,
        List<ModScanIssue> issues,
        string key,
        string field,
        string value,
        string targetDescription)
    {
        if (!claims.TryGetValue(key, out OverrideClaim previous))
        {
            claims[key] = new OverrideClaim(package, field, value, targetDescription);
            return;
        }

        if (string.Equals(previous.Package.Id, package.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(previous.Value, value, StringComparison.Ordinal))
        {
            return;
        }

        OverrideClaim winner = new OverrideClaim(package, field, value, targetDescription);
        OverrideClaim loser = previous;
        issues.Add(new ModScanIssue
        {
            RootPath = package.RootPath,
            SourceDescription = package.SourceDescription,
            IsRuntime = true,
            IsWarning = true,
            Message = "真实覆盖冲突：" + targetDescription + " 的" + field + "同时被 Mod '" +
                      previous.Package.Id + "' 和 Mod '" + package.Id + "' 修改；按当前加载顺序 " +
                      winner.Package.Id + " 生效，" + loser.Package.Id + " 被覆盖。"
        });
        claims[key] = winner;
    }

    private static string TryBuildAnchorKey(DialogueAnchorDefinition anchor)
    {
        if (anchor == null || string.IsNullOrWhiteSpace(anchor.scene) || string.IsNullOrWhiteSpace(anchor.afterLabel))
        {
            return null;
        }

        string scene = LoaderUtil.NormalizeResourceKey(anchor.scene);
        string label = anchor.afterLabel.Trim().ToLowerInvariant();
        if (anchor.dialogueOrdinal >= 0)
        {
            return scene + "|" + label + "|ordinal=" + anchor.dialogueOrdinal;
        }

        if (string.IsNullOrWhiteSpace(anchor.expectedText))
        {
            return null;
        }

        return scene + "|" + label + "|speaker=" + (anchor.expectedSpeaker ?? string.Empty) +
               "|text=" + anchor.expectedText;
    }

    private static bool Matches(string constraint, string version)
    {
        return string.IsNullOrWhiteSpace(constraint) ||
               (ModVersionRange.TryParse(constraint, out ModVersionRange range, out _) && range.Matches(version));
    }

    private static string FormatConstraint(string constraint)
    {
        return string.IsNullOrWhiteSpace(constraint) ? string.Empty : " (要求 " + constraint + ")";
    }

    private static void Block(
        ModPackage package,
        HashSet<ModPackage> blocked,
        List<ModScanIssue> issues,
        string reason)
    {
        if (package == null || !package.RuntimeEnabled)
        {
            return;
        }

        blocked.Add(package);
        package.RuntimeBlocked = true;
        package.RuntimeBlockReason ??= reason;
        string message = "Mod '" + package.Id + "' 未进入运行时：" + reason;
        if (issues.Any(issue => !issue.IsWarning && string.Equals(issue.Message, message, StringComparison.Ordinal)))
        {
            return;
        }

        issues.Add(new ModScanIssue
        {
            RootPath = package.RootPath,
            SourceDescription = package.SourceDescription,
            IsRuntime = true,
            Message = message
        });
    }

    private static void PropagateBlockedDependencies(
        IReadOnlyList<ModPackage> requested,
        Dictionary<string, ModPackage> byId,
        HashSet<ModPackage> blocked,
        List<ModScanIssue> issues)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (ModPackage package in requested)
            {
                if (blocked.Contains(package))
                {
                    continue;
                }

                foreach (ModDependencyManifest dependency in package.Manifest.dependencies ?? Array.Empty<ModDependencyManifest>())
                {
                    if (byId.TryGetValue(dependency.id, out ModPackage target) && blocked.Contains(target))
                    {
                        int count = blocked.Count;
                        Block(package, blocked, issues, "依赖 Mod '" + target.Id + "' 未能进入运行时。");
                        changed |= count != blocked.Count;
                        break;
                    }
                }
            }
        }
        while (changed);
    }

    private static HashSet<ModPackage> FindCycleNodes(
        IReadOnlyList<ModPackage> requested,
        Dictionary<string, ModPackage> byId,
        HashSet<ModPackage> blocked)
    {
        Dictionary<ModPackage, int> states = new Dictionary<ModPackage, int>();
        List<ModPackage> stack = new List<ModPackage>();
        HashSet<ModPackage> cycles = new HashSet<ModPackage>();
        foreach (ModPackage package in requested)
        {
            if (!blocked.Contains(package) && !states.ContainsKey(package))
            {
                Visit(package, byId, blocked, states, stack, cycles);
            }
        }

        return cycles;
    }

    private static void Visit(
        ModPackage package,
        Dictionary<string, ModPackage> byId,
        HashSet<ModPackage> blocked,
        Dictionary<ModPackage, int> states,
        List<ModPackage> stack,
        HashSet<ModPackage> cycles)
    {
        states[package] = 1;
        stack.Add(package);
        foreach (ModDependencyManifest dependency in package.Manifest.dependencies ?? Array.Empty<ModDependencyManifest>())
        {
            if (!byId.TryGetValue(dependency.id, out ModPackage target) || blocked.Contains(target))
            {
                continue;
            }

            if (!states.TryGetValue(target, out int state))
            {
                Visit(target, byId, blocked, states, stack, cycles);
            }
            else if (state == 1)
            {
                int start = stack.IndexOf(target);
                if (start >= 0)
                {
                    for (int index = start; index < stack.Count; index++)
                    {
                        cycles.Add(stack[index]);
                    }
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        states[package] = 2;
    }

    private static List<ModPackage> TopologicalOrder(
        List<ModPackage> packages,
        Dictionary<string, ModPackage> byId)
    {
        Dictionary<ModPackage, int> indegree = packages.ToDictionary(package => package, _ => 0);
        Dictionary<ModPackage, List<ModPackage>> dependents = packages.ToDictionary(
            package => package,
            _ => new List<ModPackage>());
        HashSet<ModPackage> eligible = new HashSet<ModPackage>(packages);
        foreach (ModPackage package in packages)
        {
            foreach (ModDependencyManifest dependency in package.Manifest.dependencies ?? Array.Empty<ModDependencyManifest>())
            {
                if (!byId.TryGetValue(dependency.id, out ModPackage target) || !eligible.Contains(target))
                {
                    continue;
                }

                indegree[package]++;
                dependents[target].Add(package);
            }
        }

        List<ModPackage> ready = indegree.Where(item => item.Value == 0).Select(item => item.Key).ToList();
        List<ModPackage> result = new List<ModPackage>(packages.Count);
        while (ready.Count > 0)
        {
            ModPackage next = ready
                .OrderBy(package => package.RuntimePriority)
                .ThenBy(package => package.Id, StringComparer.Ordinal)
                .First();
            ready.Remove(next);
            result.Add(next);
            foreach (ModPackage dependent in dependents[next])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        return result;
    }

    private sealed class OverrideClaim
    {
        internal readonly ModPackage Package;
        internal readonly string Field;
        internal readonly string Value;
        internal readonly string TargetDescription;

        internal OverrideClaim(ModPackage package, string field, string value, string targetDescription)
        {
            Package = package;
            Field = field;
            Value = value;
            TargetDescription = targetDescription;
        }
    }
}
