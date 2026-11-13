using System;
using HarmonyLib;
using Naninovel;

namespace SaveAnywhere;

/// <summary>
/// 只在 mod 自己发起的存档进行期间、同一个 StateManager 实例、且槽位与本次请求一致时，
/// 跳过 Naninovel 的存档阻断检查；游戏自己的存档行为保持原样。
/// 放行持续到本次存档的异步任务完成为止。
/// 注意：不要给返回 UniTask（IL2CPP 值类型）的方法打 Harmony 补丁，返回值会被破坏。
/// </summary>
[HarmonyPatch(typeof(StateManager), nameof(StateManager.ThrowIfGameSaveBlocked))]
internal static class SaveBlockBypassPatch
{
    internal static string BypassSlotId;
    internal static IntPtr BypassInstance;

    private static bool Prefix(StateManager __instance, string slotId)
    {
        if (BypassSlotId == null || slotId != BypassSlotId || __instance.Pointer != BypassInstance) return true;
        Plugin.Log.LogInfo($"[bypass] ThrowIfGameSaveBlocked skipped for {slotId}");
        return false;
    }

    internal static void Clear()
    {
        BypassSlotId = null;
        BypassInstance = IntPtr.Zero;
    }
}
