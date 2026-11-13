using System;
using System.Collections.Generic;
using System.Text;
using Naninovel;
using Naninovel.Async;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace SaveAnywhere;

/// <summary>
/// 在主播放器之外逐条执行一段 nani 文本（不改变主播放器位置与 gosub 栈，前提是片段里没有跳转命令）。
/// 每帧调用 <see cref="Step"/> 推进；默认遇到第一个错误即停止。
/// </summary>
internal sealed class NaniRunner
{
    private readonly List<Command> commands = new();
    private readonly bool stopOnError;
    private int index = -1;
    private UniTask task;

    public string Name { get; }
    public string Error { get; private set; }
    public bool Done { get; private set; }
    public StringBuilder Log { get; } = new();

    public NaniRunner(string name, string scriptText, bool stopOnError = true)
    {
        Name = name;
        this.stopOnError = stopOnError;
        var errors = new Il2CppSystem.Collections.Generic.List<ScriptParseError>();
        var script = Script.FromScriptText($"SaveAnywhere_{name}", scriptText,
            errors.Cast<Il2CppSystem.Collections.Generic.ICollection<ScriptParseError>>());
        for (var i = 0; i < errors.Count; i++)
            Fail($"parse error line {errors[i].LineIndex}: {errors[i].Description} | {errors[i].LineText}");
        if (Done) return;
        var list = script.ExtractCommands(null);
        for (var i = 0; i < list.Count; i++) commands.Add(list[i]);
        var lines = scriptText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Log.AppendLine($"[{name}] parsed {commands.Count} commands from {lines} lines");
    }

    public int CommandCount => commands.Count;

    /// <returns>true 表示已结束（成功或失败）。</returns>
    public bool Step()
    {
        if (Done) return true;
        if (task != null)
        {
            if (task.Status == AwaiterStatus.Pending) return false;
            try { task.GetAwaiter().GetResult(); }
            catch (Exception e) { Fail($"[{index}] {Describe(commands[index])} failed: {e.Message}"); if (Done) return true; }
            task = null;
        }

        while (true)
        {
            index++;
            if (index >= commands.Count) { Done = true; return true; }
            var c = commands[index];
            try
            {
                if (!c.ShouldExecute) continue;
                task = c.ExecuteAsync(new AsyncToken());
            }
            catch (Exception e)
            {
                Fail($"[{index}] {Describe(c)} threw: {e.Message}");
                if (Done) return true;
                continue;
            }
            // 同步完成的命令直接继续，减少帧数。
            if (task.Status == AwaiterStatus.Pending) return false;
            try { task.GetAwaiter().GetResult(); }
            catch (Exception e) { Fail($"[{index}] {Describe(c)} failed: {e.Message}"); if (Done) return true; }
            task = null;
        }
    }

    private void Fail(string message)
    {
        Log.AppendLine(message);
        Error ??= message;
        if (stopOnError) Done = true;
    }

    private static string Describe(Command c)
    {
        try { return c.GetIl2CppType().Name; } catch { return "?"; }
    }
}
