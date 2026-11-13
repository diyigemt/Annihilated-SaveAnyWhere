using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Naninovel;
using UnityEngine;

namespace SaveAnywhere;

/// <summary>
/// 调试用：存/读档前后截图，并在读档后逐帧记录脚本播放器状态的变化。
/// 由配置 Debug.Diagnostics 控制。
/// </summary>
internal static class Diagnostics
{
    private static readonly List<(float At, string Tag)> pendingShots = new();
    private static float traceUntil;
    private static string lastTrace;
    private static string dir;

    public static bool Enabled => Plugin.DiagnosticsEnabled.Value;

    private static string Dir
    {
        get
        {
            if (dir != null) return dir;
            dir = Path.Combine(Paths.BepInExRootPath, "SaveAnywhere-diag");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Screenshot(string tag, float delay = 0f)
    {
        if (!Enabled) return;
        pendingShots.Add((Time.realtimeSinceStartup + delay, tag));
    }

    public static void StartTrace(float seconds, string reason)
    {
        if (!Enabled) return;
        traceUntil = Time.realtimeSinceStartup + seconds;
        lastTrace = null;
        Plugin.Log.LogInfo($"[diag] trace start ({reason}, {seconds}s)");
    }

    public static void Tick(StateManager state)
    {
        if (!Enabled) return;
        var now = Time.realtimeSinceStartup;

        for (var i = pendingShots.Count - 1; i >= 0; i--)
        {
            if (pendingShots[i].At > now) continue;
            var file = Path.Combine(Dir, $"{DateTime.Now:HHmmss_fff}_{pendingShots[i].Tag}.png");
            pendingShots.RemoveAt(i);
            try { ScreenCapture.CaptureScreenshot(file, 1); Plugin.Log.LogInfo($"[diag] screenshot {file}"); }
            catch (Exception e) { Plugin.Log.LogError($"[diag] screenshot failed: {e.Message}"); }
        }

        if (now > traceUntil) return;
        var line = Describe(state);
        if (line == lastTrace) return;
        lastTrace = line;
        Plugin.Log.LogInfo($"[diag] f{Time.frameCount} {line}");
    }

    public static string Describe(StateManager state)
    {
        try
        {
            var player = state?.scriptPlayer?.TryCast<CustomScriptPlayer>();
            if (player is null) return "player=null";
            var spot = player.PlaybackSpot;
            var cmd = player.PlayedCommand;
            var cmdName = cmd is null ? "null" : cmd.GetIl2CppType().Name;
            var gosub = player.GosubReturnSpots;
            return $"spot={spot?.scriptName}:{spot?.lineIndex} idx={player.PlayedIndex} cmd={cmdName} " +
                   $"playing={player.Playing} waitInput={player.WaitingForInput} gosubDepth={(gosub is null ? -1 : gosub.Count)} " +
                   $"inFlightLoad={state.inFlightLoad}";
        }
        catch (Exception e)
        {
            return $"describe failed: {e.Message}";
        }
    }
}
