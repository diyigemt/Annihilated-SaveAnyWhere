using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using Il2CppInterop.Runtime;
using Naninovel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SaveAnywhere;

/// <summary>
/// 调试用文件命令控制台（Debug.DevConsole 开启时生效）。
/// 往 BepInEx/SaveAnywhere-cmd/ 写入 *.cmd 文件，首行为命令，其余行为参数；
/// 处理后删除命令文件，结果写入同名 .out。异步命令（nani/load/save）完成后才写结果。
/// </summary>
internal static class DevConsole
{
    private static string dir;
    private static float nextPoll;

    // 正在执行的命令
    private static string currentId;
    private static StringBuilder currentOut;
    private static Func<bool> currentStep; // 返回 true 表示完成

    public static bool Enabled => Plugin.DevConsoleEnabled.Value;
    public static bool Busy => currentStep != null;

    private static string Dir
    {
        get
        {
            if (dir != null) return dir;
            dir = Path.Combine(Paths.BepInExRootPath, "SaveAnywhere-cmd");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Tick(SaveAnywhereBehaviour owner)
    {
        if (!Enabled) return;
        if (currentStep != null)
        {
            bool done;
            try { done = currentStep(); }
            catch (Exception e) { currentOut.AppendLine($"EXCEPTION {e}"); done = true; }
            if (done) Finish();
            return;
        }

        if (Time.realtimeSinceStartup < nextPoll) return;
        nextPoll = Time.realtimeSinceStartup + 0.25f;
        var file = Directory.GetFiles(Dir, "*.cmd").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        if (file == null) return;

        string text;
        try { text = File.ReadAllText(file, Encoding.UTF8); File.Delete(file); }
        catch (IOException) { return; } // 还在写入

        currentId = Path.GetFileNameWithoutExtension(file);
        currentOut = new StringBuilder();
        currentOut.AppendLine($"# {currentId} frame={Time.frameCount} t={Time.realtimeSinceStartup:0.00}");
        try { Dispatch(owner, text.Replace("\r\n", "\n")); }
        catch (Exception e) { currentOut.AppendLine($"EXCEPTION {e}"); currentStep = null; }
        if (currentStep == null) Finish();
    }

    private static void Finish()
    {
        currentOut.AppendLine($"# done frame={Time.frameCount} t={Time.realtimeSinceStartup:0.00}");
        try { File.WriteAllText(Path.Combine(Dir, currentId + ".out"), currentOut.ToString(), new UTF8Encoding(false)); }
        catch (Exception e) { Plugin.Log.LogError($"[console] write output failed: {e}"); }
        currentId = null;
        currentOut = null;
        currentStep = null;
    }

    private static void Dispatch(SaveAnywhereBehaviour owner, string text)
    {
        var nl = text.IndexOf('\n');
        var head = (nl < 0 ? text : text[..nl]).Trim();
        var body = nl < 0 ? "" : text[(nl + 1)..];
        var sp = head.IndexOf(' ');
        var cmd = sp < 0 ? head : head[..sp];
        var arg = sp < 0 ? "" : head[(sp + 1)..].Trim();
        var o = currentOut;

        var state = Engine.Initialized ? Engine.GetService(Il2CppType.Of<StateManager>())?.TryCast<StateManager>() : null;
        var vars = state?.customVariableManager;

        if (!owner.IsIdle && (cmd == "set" || cmd == "nani" || cmd == "save" || cmd == "load"))
        {
            o.AppendLine($"busy: {owner.DebugStatus()}");
            return;
        }

        switch (cmd)
        {
            case "state":
                o.AppendLine(Diagnostics.Describe(state));
                o.AppendLine(GosubStack(state));
                o.AppendLine(owner.DebugStatus());
                break;

            case "var": // var name1 name2 ...
                foreach (var n in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    o.AppendLine($"{n} = {vars?.GetVariableValue(n)}");
                break;

            case "vars": // vars <regex>
            {
                var re = new Regex(arg.Length == 0 ? "." : arg);
                foreach (var (n, v) in AllVariables(vars).Where(p => re.IsMatch(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal))
                    o.AppendLine($"{n} = {v}");
                break;
            }

            case "set": // set name value
            {
                var i = arg.IndexOf(' ');
                vars.SetVariableValue(i < 0 ? arg : arg[..i], i < 0 ? "" : arg[(i + 1)..]);
                o.AppendLine("ok");
                break;
            }

            case "shot":
            {
                var path = Path.Combine(Dir, $"{currentId}_{(arg.Length == 0 ? "shot" : arg)}.png");
                ScreenCapture.CaptureScreenshot(path, 1);
                o.AppendLine(path);
                var until = Time.frameCount + 3; // 截图在帧末写出
                currentStep = () => Time.frameCount > until;
                break;
            }

            case "tree": // tree [regex on path] ; body 行可写 depth=N
                DumpTree(o, arg, body);
                break;

            case "nani":
                StartNani(body.Length > 0 ? body : arg);
                break;

            case "save": // save <slotId>  —— 强制存档，跳过战斗限制与安全点（调试用）
                o.AppendLine(owner.DebugForceSave(arg));
                currentStep = () => owner.IsIdle;
                break;

            case "load":
                o.AppendLine(owner.DebugLoad(arg));
                currentStep = () => owner.IsIdle;
                break;

            case "eval": // eval <C# 预设探针名>
                o.AppendLine(Probes.Run(arg, body));
                break;

            default:
                o.AppendLine($"unknown command '{cmd}'");
                break;
        }
    }

    // ---------- nani ----------

    private static void StartNani(string scriptText)
    {
        var runner = new NaniRunner("console", scriptText, stopOnError: false);
        currentStep = () =>
        {
            if (!runner.Step()) return false;
            currentOut.Append(runner.Log);
            currentOut.AppendLine(runner.Error == null ? "ok" : $"first error: {runner.Error}");
            return true;
        };
    }

    // ---------- helpers ----------

    internal static IEnumerable<(string Name, string Value)> AllVariables(ICustomVariableManager vars)
    {
        var result = new List<(string, string)>();
        if (vars is null) return result;
        var all = vars.GetAllVariables();
        var enumerable = all.Cast<Il2CppSystem.Collections.Generic.IEnumerable<CustomVariable>>();
        var e = enumerable.GetEnumerator();
        var basic = e.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (basic.MoveNext())
        {
            var v = e.Current;
            result.Add((v.Name, v.Value));
        }
        return result;
    }

    private static string GosubStack(StateManager state)
    {
        var player = state?.scriptPlayer?.TryCast<CustomScriptPlayer>();
        var stack = player?.GosubReturnSpots;
        if (stack is null) return "gosub: null";
        var sb = new StringBuilder("gosub:");
        var items = stack._array;
        var size = Math.Min(stack._size, items?.Length ?? 0);
        for (var i = 0; i < size; i++) sb.Append($" [{i}]{items[i]?.scriptName}:{items[i]?.lineIndex}");
        return sb.ToString();
    }

    private static void DumpTree(StringBuilder o, string filter, string body)
    {
        var maxDepth = 12;
        var m = Regex.Match(body, @"depth=(\d+)");
        if (m.Success) maxDepth = int.Parse(m.Groups[1].Value);
        var includeInactive = body.Contains("inactive");
        var re = filter.Length == 0 ? null : new Regex(filter);

        var roots = new List<GameObject>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
            foreach (var g in SceneManager.GetSceneAt(i).GetRootGameObjects()) roots.Add(g);
        // DontDestroyOnLoad 场景：借一个临时对象拿到
        var probe = new GameObject("SaveAnywhereProbe");
        UnityEngine.Object.DontDestroyOnLoad(probe);
        foreach (var g in probe.scene.GetRootGameObjects()) if (g.Pointer != probe.Pointer) roots.Add(g);
        UnityEngine.Object.Destroy(probe);

        var count = 0;
        foreach (var r in roots) Walk(r.transform, r.name, 0);
        o.AppendLine($"({count} lines)");

        void Walk(Transform t, string path, int depth)
        {
            var go = t.gameObject;
            if (!includeInactive && !go.activeInHierarchy) return;
            if (re == null || re.IsMatch(path))
            {
                count++;
                o.AppendLine($"{(go.activeInHierarchy ? "+" : "-")} {path} {LocalInfo(t)}");
            }
            if (depth >= maxDepth) return;
            for (var i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                Walk(c, path + "/" + c.name, depth + 1);
            }
        }
    }

    private static string LocalInfo(Transform t)
    {
        var p = t.localPosition;
        var s = t.localScale;
        return $"pos=({p.x:0.#},{p.y:0.#},{p.z:0.#}) scale=({s.x:0.##},{s.y:0.##})";
    }
}
