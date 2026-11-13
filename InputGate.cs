using System;
using Naninovel;
using UnityEngine.EventSystems;

namespace SaveAnywhere;

/// <summary>
/// 战斗存读档与画面重建期间冻结玩家操作。
/// UI 门禁关闭 uGUI 的 EventSystem（点击/导航），不属于任何存档状态，存读档全程都可以开。
/// Naninovel 门禁改 InputManager.ProcessInput，它会被写进存档，所以只在读档完成后的重建阶段使用，
/// 并在释放时恢复为获取时（即读档结果）的值。
/// </summary>
internal static class InputGate
{
    private static EventSystem disabledSystem;
    private static InputManager input;
    private static bool inputWasProcessing;

    public static void AcquireUi()
    {
        if (disabledSystem != null) return;
        try
        {
            var es = EventSystem.current;
            if (es != null && es.enabled) { es.enabled = false; disabledSystem = es; }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[gate] ui acquire: {e.Message}"); }
    }

    public static void AcquireNaninovel()
    {
        if (input != null) return;
        try
        {
            input = Probes.Service<InputManager>();
            if (input != null) { inputWasProcessing = input.ProcessInput; input.ProcessInput = false; }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[gate] input acquire: {e.Message}"); }
    }

    public static void Release()
    {
        try
        {
            if (disabledSystem != null) disabledSystem.enabled = true;
            if (input != null) input.ProcessInput = inputWasProcessing;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[gate] release: {e.Message}"); }
        disabledSystem = null;
        input = null;
    }
}
