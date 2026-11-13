using System;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Naninovel;
using UnityEngine;

namespace SaveAnywhere;

/// <summary>调试控制台的 C# 状态探针（eval &lt;name&gt;）。</summary>
internal static class Probes
{
    internal static T Service<T>() where T : Il2CppObjectBase
        => Engine.Initialized ? Engine.GetService(Il2CppType.Of<T>())?.TryCast<T>() : null;

    public static string Run(string name, string body)
    {
        try
        {
            return name switch
            {
                "enemy" => Enemy(),
                "chars" => Chars(),
                _ => $"unknown probe '{name}' (enemy, chars)",
            };
        }
        catch (Exception e) { return $"probe {name} failed: {e}"; }
    }

    private static string Enemy()
    {
        var es = Service<EnemyService>();
        if (es is null) return "EnemyService null";
        var sb = new StringBuilder();
        sb.AppendLine($"enemyDisplay={(es.enemyDisplay == null ? "null" : es.enemyDisplay.name + " active=" + es.enemyDisplay.activeInHierarchy)}");
        var enemies = es.enemies;
        sb.AppendLine($"enemies[]={(enemies == null ? "null" : enemies.Length.ToString())}");
        if (enemies != null)
            for (var i = 0; i < enemies.Length; i++)
            {
                var g = enemies[i];
                sb.AppendLine($"  [{i}] {(g == null ? "null" : $"{g.name} active={g.activeInHierarchy} pos={g.transform.position}")}");
            }

        var ctrls = es.enemyControllerList;
        sb.AppendLine($"controllers={(ctrls == null ? "null" : ctrls.Count.ToString())}");
        if (ctrls != null)
            for (var i = 0; i < ctrls.Count; i++)
            {
                var c = ctrls[i];
                if (c == null) { sb.AppendLine($"  [{i}] null"); continue; }
                sb.AppendLine($"  [{i}] id={c.enemyId} go={c.gameObject.name} active={c.gameObject.activeInHierarchy} logicalX={c.logicalSceneX} pos={c.transform.position} prefix={c.enemyVariablePrefix}");
            }

        var next = es.enemyNextActionSetDataList;
        sb.AppendLine($"nextActions={(next == null ? "null" : next.Count.ToString())}");
        if (next != null)
            for (var i = 0; i < next.Count; i++)
            {
                var a = next[i];
                if (a == null) { sb.AppendLine($"  [{i}] null"); continue; }
                var vals = a.actionValues;
                var names = a.actionNames;
                var parts = new StringBuilder();
                for (var k = 0; names != null && k < names.Length; k++)
                    parts.Append($"{names[k]}:{(vals != null && k < vals.Length ? vals[k] : 0)} ");
                sb.AppendLine($"  [{i}] {a.name} {parts}");
            }

        var texts = es.enemyNextActionTextList;
        if (texts != null)
            for (var i = 0; i < texts.Count; i++)
                sb.AppendLine($"  text[{i}] {(texts[i] == null ? "null" : texts[i].text?.Replace("\n", "\\n"))}");
        return sb.ToString();
    }

    private static string Chars()
    {
        var sb = new StringBuilder();
        foreach (var c in UnityEngine.Object.FindObjectsByType<BattleCharacterSpriteController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            sb.AppendLine($"{c.gameObject.name} active={c.gameObject.activeInHierarchy} pos={c.transform.position}");
        return sb.Length == 0 ? "no BattleCharacterSpriteController" : sb.ToString();
    }
}
