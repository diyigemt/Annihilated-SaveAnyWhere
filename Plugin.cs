using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace SaveAnywhere;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "local.annihilated.saveanywhere";
    public const string Name = "SaveAnywhere";
    public const string Version = "0.4.2";

    internal new static ManualLogSource Log;
    internal static ConfigEntry<bool> RestoreGlobalCounters;
    internal static ConfigEntry<int> SlotCount;
    internal static ConfigEntry<bool> ClearSlotsOnGameSaveLoad;
    internal static ConfigEntry<Key> SaveKey;
    internal static ConfigEntry<Key> LoadKey;
    internal static ConfigEntry<Key> PrevSlotKey;
    internal static ConfigEntry<Key> NextSlotKey;
    internal static ConfigEntry<Key> ToggleCountersKey;
    internal static ConfigEntry<bool> DiagnosticsEnabled;
    internal static ConfigEntry<bool> DevConsoleEnabled;
    internal static ConfigEntry<bool> BattleCheckpointEnabled;
    internal static ConfigEntry<Key> BattleCheckpointKey;

    public override void Load()
    {
        Log = base.Log;

        RestoreGlobalCounters = Config.Bind("Counters", "RestoreGlobalCounters", true,
            "读档时是否把 G_gameLoopCount / G_gameOverCount 回退到存档时刻的值。" +
            "开启后在存档之后死亡、再读档，这次死亡不会计入结局判定。");
        SlotCount = Config.Bind("Slots", "SlotCount", 9,
            new ConfigDescription("mod 专用存档槽数量", new AcceptableValueRange<int>(1, 99)));
        ClearSlotsOnGameSaveLoad = Config.Bind("Slots", "ClearOnGameSaveLoad", true,
            "游戏自身存档/读档完成后、以及每次启动游戏时，清空全部 mod 存档槽位和战斗检查点（mod 存档只在两次游戏存读档之间有效）");
        SaveKey = Config.Bind("Keys", "Save", Key.F5, "存档到当前槽（按住 Ctrl 为强制存档，忽略安全检查；战斗中只能在选指令时存档，不在时会排队等待，Ctrl 无效）");
        LoadKey = Config.Bind("Keys", "Load", Key.F9, "读取当前槽（会弹出确认框，再按一次或 Enter 确认，Esc 取消）");
        PrevSlotKey = Config.Bind("Keys", "PrevSlot", Key.F6, "切换到上一个槽");
        NextSlotKey = Config.Bind("Keys", "NextSlot", Key.F7, "切换到下一个槽");
        DiagnosticsEnabled = Config.Bind("Debug", "Diagnostics", false,
            "存/读档前后截图到 BepInEx/SaveAnywhere-diag，并在读档后逐帧记录脚本状态（排查用）");
        DevConsoleEnabled = Config.Bind("Debug", "DevConsole", false,
            "开发用：监视 BepInEx/SaveAnywhere-cmd/*.cmd 并执行其中的调试命令");
        BattleCheckpointEnabled = Config.Bind("Battle", "AutoCheckpoint", true,
            "每场战斗开始时自动存一个检查点（槽位 ModBattleStart）");
        BattleCheckpointKey = Config.Bind("Keys", "LoadBattleCheckpoint", Key.F10, "读取最近一场战斗开始时的检查点（从头重打）");
        ToggleCountersKey = Config.Bind("Keys", "ToggleRestoreCounters", Key.F8, "切换 RestoreGlobalCounters 开关");

        var harmony = new Harmony(Guid);
        TryPatch(harmony, typeof(SaveBlockBypassPatch), "存档阻断放行（失败时只能在游戏允许的时刻存档）");
        AddComponent<SaveAnywhereBehaviour>();
        Log.LogInfo($"{Name} {Version} loaded. RestoreGlobalCounters={RestoreGlobalCounters.Value}");
    }

    private static void TryPatch(Harmony harmony, Type patch, string what)
    {
        try
        {
            harmony.PatchAll(patch);
            Log.LogInfo($"Patched: {patch.Name}");
        }
        catch (Exception e)
        {
            Log.LogError($"Patch {patch.Name} failed — {what}: {e}");
        }
    }
}
