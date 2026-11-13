using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Naninovel;

namespace SaveAnywhere;

/// <summary>
/// 把全局死亡计数复制成局部变量随存档保存；读档前从存档文件预读快照并写回全局变量。
/// 每次 mod 存档都记录快照，这样之后再打开开关，旧的 mod 档也能用。
/// </summary>
internal static class CounterSnapshot
{
    private const string Schema = "1";
    private const string SchemaVar = "modSnap_schema";
    private const string VarStateKey = "Naninovel.CustomVariableManager+GameState";
    private static readonly (string Global, string Snap)[] Pairs =
    {
        ("G_gameLoopCount", "modSnap_gameLoopCount"),
        ("G_gameOverCount", "modSnap_gameOverCount"),
    };

    public static void Capture(ICustomVariableManager vars)
    {
        foreach (var (global, snap) in Pairs)
            vars.SetVariableValue(snap, vars.GetVariableValue(global) ?? "");
        vars.SetVariableValue(SchemaVar, Schema);
    }

    public static string[] ReadGlobals(ICustomVariableManager vars)
    {
        var values = new string[Pairs.Length];
        for (var i = 0; i < Pairs.Length; i++) values[i] = vars.GetVariableValue(Pairs[i].Global);
        return values;
    }

    /// <returns>是否有变量被实际修改。</returns>
    public static bool ApplyGlobals(ICustomVariableManager vars, string[] values, string stage)
    {
        var changed = false;
        for (var i = 0; i < Pairs.Length; i++)
        {
            var current = vars.GetVariableValue(Pairs[i].Global);
            if (values[i] == null || current == values[i]) continue;
            vars.SetVariableValue(Pairs[i].Global, values[i]);
            Plugin.Log.LogInfo($"[counters/{stage}] {Pairs[i].Global}: {current} -> {values[i]}");
            changed = true;
        }
        return changed;
    }

    public static bool GlobalsEqual(ICustomVariableManager vars, string[] values)
    {
        for (var i = 0; i < Pairs.Length; i++)
            if (vars.GetVariableValue(Pairs[i].Global) != values[i]) return false;
        return true;
    }

    /// <summary>
    /// 从存档文件（raw deflate 压缩的 JSON）里读出计数快照。
    /// 没有快照或格式不符时返回 null 并给出原因。
    /// </summary>
    public static string[] ReadFromSaveFile(string path, out string error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) { error = $"找不到存档文件 {path}"; return null; }

            string json;
            using (var file = File.OpenRead(path))
            using (var inflate = new DeflateStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(inflate, detectEncodingFromByteOrderMarks: true))
                json = reader.ReadToEnd();

            using var doc = JsonDocument.Parse(json);
            var map = doc.RootElement.GetProperty("objectJsonMap");
            var keys = map.GetProperty("keys");
            var values = map.GetProperty("values");

            string varState = null;
            for (var i = 0; i < keys.GetArrayLength(); i++)
            {
                if (keys[i].GetString()?.StartsWith(VarStateKey + ",", StringComparison.Ordinal) != true) continue;
                varState = values[i].GetString();
                break;
            }
            if (varState == null) { error = "存档里没有变量状态"; return null; }

            using var varDoc = JsonDocument.Parse(varState);
            var local = varDoc.RootElement.GetProperty("LocalVariableMap");
            var names = local.GetProperty("keys");
            var vals = local.GetProperty("values");

            string schema = null;
            var result = new string[Pairs.Length];
            for (var i = 0; i < names.GetArrayLength(); i++)
            {
                var name = names[i].GetString();
                if (name == SchemaVar) schema = vals[i].ToString();
                for (var p = 0; p < Pairs.Length; p++)
                    if (name == Pairs[p].Snap) result[p] = vals[i].ToString();
            }

            if (schema != Schema) { error = "存档里没有计数快照"; return null; }
            for (var p = 0; p < Pairs.Length; p++)
                if (!int.TryParse(result[p], out _)) { error = $"快照 {Pairs[p].Snap}='{result[p]}' 无效"; return null; }
            return result;
        }
        catch (Exception e)
        {
            error = $"解析存档失败：{e.Message}";
            Plugin.Log.LogError($"[counters] read {path}: {e}");
            return null;
        }
    }
}
