using System;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Naninovel;
using Naninovel.Async;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SaveAnywhere;

/// <summary>
/// 热键 → 等待安全点 → 调用 Naninovel 原生 SaveGameAsync/LoadGameAsync → 每帧轮询任务结果。
/// 同一时刻只进行一个操作；操作开始时冻结槽位、StateManager 实例和计数开关。
/// </summary>
public class SaveAnywhereBehaviour : MonoBehaviour
{
    public SaveAnywhereBehaviour(IntPtr ptr) : base(ptr) { }

    private const string SlotPrefix = "ModSave";
    private const string BattleSlotId = "ModBattleStart";
    private const string BattleLabel = "战斗检查点";
    // Battle.nani 开头 "# Start" 之后的第一条命令所在行（0 起算）；所有战斗都经 @gosub Battle 从这里进入。
    private const int BattleEntryMaxLine = 3;
    private const float SafePointTimeout = 30f;
    private const float SlowWarningAfter = 30f;

    private enum Phase { Idle, WaitingForSafePoint, Saving, Loading, SavingGlobals, Restoring }

    private Phase phase = Phase.Idle;
    private string opSlotId;
    private string opLabel;
    private string prevCommandScript;
    private int prevCommandLine = -1;
    private bool checkpointMissed;
    private const float StuckAfter = 90f;
    private IntPtr subscribedPlayer;
    private StateManager subscribedStateObj;
    private Il2CppSystem.Action<GameSaveLoadArgs> gameSavedHandler;
    private Il2CppSystem.Action<GameSaveLoadArgs> gameLoadedHandler;
    private string pendingClearReason = "游戏启动"; // 待清理（启动时、或操作进行中收到游戏自身存读档）
    private bool pendingClearQuiet = true;
    private float nextClearAttempt;
    private Il2CppSystem.Action<Command> commandStartHandler;
    private IntPtr opStatePtr;
    private bool restoreThisLoad;
    private string[] counterUndo;
    private string[] counterSnapshot;
    private bool countersTouched;
    private float phaseSince;
    private bool slowWarned;
    private bool stuckWarned;
    private UniTask<GameStateMap> stateTask;
    private UniTask globalTask;
    private string loadResultNote;

    // 战斗中存读档
    private bool battleWait;                 // 正在等待战斗里的选指令点
    private BattleState.Sidecar pendingSidecar;
    private BattleState.SaveData restoreSave;
    private BattleState.Sidecar restoreSidecar;
    private string restoreDoneMessage;
    private int restoreStep;
    private int restoreWaitFrames;
    private NaniRunner restoreRunner;
    private System.Text.StringBuilder restoreLog;
    private float restoreStepSince;
    private bool restoreBroken;              // 重建失败后的半成品场景：禁止存档，直到重新读档

    private int slot = 1;

    private string toast;
    private float toastUntil;
    private GUIStyle toastStyle;

    private void Update()
    {
        try
        {
            ProcessPendingClear();
            Diagnostics.Tick(GetStateManager());
            DevConsole.Tick(this);
            EnsureSubscribed();
            Advance();
            ProcessPendingClear(); // Advance 可能刚回到空闲：先清理，再接受本帧热键
            if (confirmSlotId != null && GetStateManager()?.Pointer != confirmStatePtr)
            {
                CloseConfirm(); // 引擎已重建：确认失效，释放旧门禁
                Show("引擎已重建，读档确认已取消");
            }
            ApplyConfirmDecision(); // 上一帧做出的决定
            if (confirmSlotId != null && confirmDecision == 0 && (Time.realtimeSinceStartup > confirmUntil || phase != Phase.Idle))
                CancelConfirm("读档确认已超时取消");
            HandleInput();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Update error in phase {phase}: {e}");
            Show($"内部错误：{e.Message}");
            // Saving/Loading 阶段的引擎任务可能还在跑，不能丢掉；其余阶段直接回到空闲。
            if (phase == Phase.WaitingForSafePoint || phase == Phase.SavingGlobals) Reset();
        }
    }

    // ---------- input ----------

    private void HandleInput()
    {
        var kb = Keyboard.current;
        if (kb is null || !Application.isFocused) return;
        if (DevConsole.Busy) return; // 调试命令执行期间不接受存读档热键

        if (confirmSlotId != null)
        {
            if (confirmDecision != 0) return;
            if (Pressed(kb, confirmKey) || kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) ConfirmLoad();
            else if (kb.escapeKey.wasPressedThisFrame) CancelConfirm("已取消读档");
            return; // 确认框打开时不处理其他热键
        }

        if (Pressed(kb, Plugin.PrevSlotKey.Value)) ChangeSlot(-1);
        else if (Pressed(kb, Plugin.NextSlotKey.Value)) ChangeSlot(+1);
        else if (Pressed(kb, Plugin.ToggleCountersKey.Value))
        {
            Plugin.RestoreGlobalCounters.Value = !Plugin.RestoreGlobalCounters.Value;
            Show($"读档回退死亡计数：{(Plugin.RestoreGlobalCounters.Value ? "开" : "关")}" +
                 (phase == Phase.Loading ? "（对正在进行的读档不生效）" : ""));
        }
        else if (Pressed(kb, Plugin.SaveKey.Value))
        {
            var force = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            if (phase == Phase.WaitingForSafePoint && !force) { Reset(); Show("已取消等待中的存档"); }
            else RequestSave(force);
        }
        else if (Pressed(kb, Plugin.LoadKey.Value))
        {
            if ((kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) && IsStuck)
            {
                Plugin.Log.LogWarning($"User force-unlocked stuck {phase} {opSlotId}");
                Reset();
                Show("已解除卡住的操作；若游戏表现异常，请重启游戏");
            }
            else AskLoad(SlotId(slot), $"槽位 {slot}", Plugin.LoadKey.Value);
        }
        else if (Pressed(kb, Plugin.BattleCheckpointKey.Value))
            AskLoad(BattleSlotId, checkpointMissed ? $"{BattleLabel}（本场未保存，读取的是上一场）" : BattleLabel, Plugin.BattleCheckpointKey.Value);
    }

    // ---------- load confirmation ----------

    private string confirmSlotId;
    private string confirmLabel;
    private string confirmDetail;
    private Key confirmKey;
    private float confirmUntil;
    private IntPtr confirmStatePtr;
    private const float ConfirmTimeout = 15f;

    /// <summary>读档前弹出确认框，防止误触。确认框打开期间屏蔽游戏输入。</summary>
    private void AskLoad(string slotId, string label, Key key)
    {
        if (phase != Phase.Idle) { Show("上一个操作还没完成"); return; }
        if (SlotsPendingClear) { Show("mod 存档正在清空，暂不能读档"); return; }
        var state = GetStateManager();
        if (!EngineIdle(state, out var why)) { Show($"现在不能读档：{why}"); return; }
        if (!SlotExists(state, slotId)) { Show($"{label} 是空的"); return; }

        var detail = "";
        try
        {
            var path = SlotPath(slotId);
            detail = $"存于 {System.IO.File.GetLastWriteTime(path):MM-dd HH:mm:ss}";
            var sc = slotId == BattleSlotId ? null : BattleState.ReadSidecar(path, out _);
            if (sc != null) detail += $"，战斗中第 {sc.TurnCount} 回合";
            else if (slotId == BattleSlotId) detail += "，战斗开始时";
        }
        catch (Exception e) { Plugin.Log.LogWarning($"confirm detail: {e.Message}"); }
        detail += Plugin.RestoreGlobalCounters.Value ? "；死亡计数将回退到存档时" : "；死亡计数不回退";

        confirmSlotId = slotId;
        confirmLabel = label;
        confirmDetail = detail;
        confirmKey = key;
        confirmUntil = Time.realtimeSinceStartup + ConfirmTimeout;
        confirmDecision = 0;
        confirmStatePtr = state.Pointer;
        InputGate.AcquireUi();
        InputGate.AcquireNaninovel();
    }

    private int confirmDecision; // 0 未决定，1 读档，-1 取消；下一帧才执行，避免确认用的 Enter/Esc 漏给游戏
    private string confirmCancelMessage;

    private void ConfirmLoad() => confirmDecision = 1;

    private void CancelConfirm(string message)
    {
        confirmDecision = -1;
        confirmCancelMessage = message;
    }

    private void ApplyConfirmDecision()
    {
        if (confirmSlotId == null || confirmDecision == 0) return;
        var slotId = confirmSlotId;
        var label = confirmLabel;
        var decision = confirmDecision;
        confirmDecision = 0;
        CloseConfirm();
        if (decision > 0) RequestLoad(slotId, label);
        else Show(confirmCancelMessage);
    }

    /// <summary>先释放输入门禁再读档：读档会写入存档里的输入状态，不能在之后被门禁覆盖。</summary>
    private void CloseConfirm()
    {
        confirmSlotId = null;
        InputGate.Release();
    }

    private static bool Pressed(Keyboard kb, Key key)
    {
        if (key == Key.None) return false;
        var control = kb[key];
        return control is not null && control.wasPressedThisFrame;
    }

    private void ChangeSlot(int delta)
    {
        if (phase != Phase.Idle) { Show("操作进行中，不能切换槽位"); return; }
        var count = Math.Max(1, Plugin.SlotCount.Value);
        slot = ((slot - 1 + delta) % count + count) % count + 1;
        Show($"当前槽位：{slot}{(SlotExists(GetStateManager(), SlotId(slot)) ? "" : "（空）")}");
    }

    private static string SlotId(int n) => $"{SlotPrefix}{n:00}";

    private static string SlotPath(string slotId)
        => System.IO.Path.Combine(Application.persistentDataPath, "NaninovelData", "Saves", slotId + ".nson");

    // ---------- services ----------

    private static T GetService<T>() where T : Il2CppObjectBase
    {
        if (!Engine.Initialized) return null;
        return Engine.GetService(Il2CppType.Of<T>())?.TryCast<T>();
    }

    private static StateManager GetStateManager() => GetService<StateManager>();

    private static CustomScriptPlayer GetPlayer(StateManager state)
        => state?.scriptPlayer?.TryCast<CustomScriptPlayer>();

    private static bool SlotExists(StateManager state, string slotId)
        => state?.GameSlotManager?.TryCast<ISaveSlotManager>()?.SaveSlotExists(slotId) ?? false;

    private static bool InGame(ICustomVariableManager vars)
        => string.Equals(vars.GetVariableValue("gameStarted"), "True", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 以脚本位置判定战斗生命周期：当前在 Battle 脚本里，或 gosub 返回栈里有 Battle 帧
    /// （战斗中调用的 Action/剧情脚本）。覆盖开战初始化、胜利结算、逃跑与死亡清理；
    /// 死亡后 @goto GameOver 不再有 Battle 帧，探索阶段也不会误判。
    /// </summary>
    private static bool InBattle(StateManager state)
    {
        var player = GetPlayer(state);
        if (player is null) return false;
        var spot = player.PlaybackSpot;
        if (spot is not null && spot.scriptName == "Battle") return true;

        var stack = player.GosubReturnSpots;
        if (stack is null) return false;
        var items = stack._array;
        var size = Math.Min(stack._size, items?.Length ?? 0);
        for (var i = 0; i < size; i++)
        {
            var frame = items[i];
            if (frame is not null && frame.scriptName == "Battle") return true;
        }
        return false;
    }


    /// <summary>所有操作的共同前置条件：引擎就绪且没有其他存/读档、回滚在进行。</summary>
    private static bool EngineIdle(StateManager state, out string reason)
    {
        if (state is null || GetPlayer(state) is null || state.customVariableManager is null) { reason = "引擎尚未就绪"; return false; }
        if (state.inFlightSave || state.inFlightLoad || state.replacingPersistentData) { reason = "游戏正在存/读档"; return false; }
        if (state.RollbackInProgress) { reason = "游戏正在回滚"; return false; }
        reason = null;
        return true;
    }

    // ---------- save ----------

    private void RequestSave(bool force)
    {
        if (phase != Phase.Idle && !(force && phase == Phase.WaitingForSafePoint))
        {
            Show("上一个操作还没完成");
            return;
        }

        var state = GetStateManager();
        if (!EngineIdle(state, out var why)) { Show($"现在不能存档：{why}"); return; }
        if (!InGame(state.customVariableManager)) { Show("未开始游戏，不能存档"); return; }
        if (restoreBroken) { Show("上次战斗画面重建失败，当前场景不完整，请先读档"); return; }
        if (SlotsPendingClear) { Show("mod 存档正在清空，暂不能存档"); return; }

        opLabel = $"槽位 {slot}";
        opSlotId = SlotId(slot);
        opStatePtr = state.Pointer;

        if (InBattle(state))
        {
            // 战斗中只能在选指令的时刻存档（读档后要据此重建画面），Ctrl 也不能绕过。
            if (IsBattleSavePoint(state, out var battleReason)) { StartSave(state, false); return; }
            battleWait = true;
            Enter(Phase.WaitingForSafePoint);
            Plugin.Log.LogInfo($"Battle save to {opSlotId} queued: {battleReason}");
            Show($"{opLabel}：将在下一次选指令时存档（{battleReason}），再按 {Plugin.SaveKey.Value} 取消");
            return;
        }
        if (force) { StartSave(state, true); return; }

        if (IsSafePoint(state, out var reason)) { StartSave(state, false); return; }

        Enter(Phase.WaitingForSafePoint);
        Plugin.Log.LogInfo($"Save to {opSlotId} queued: {reason}");
        Show($"{opLabel}：等待安全点（{reason}），再按 {Plugin.SaveKey.Value} 取消，Ctrl+{Plugin.SaveKey.Value} 强制");
    }

    /// <summary>
    /// 安全点：没有阻断存档的交互命令在执行、不在攻击条流程中，且脚本处于停住/等待输入的状态
    /// （战斗选择用 @stop 停住，对白用等待输入）。结算动画进行中不算安全点。
    /// </summary>
    private static bool IsSafePoint(StateManager state, out string reason)
    {
        if (!EngineIdle(state, out reason)) return false;
        var player = GetPlayer(state);

        var blocker = player.GetSaveBlocker();
        if (blocker is not null && blocker.BlocksSave) { reason = $"{blocker.CommandName} 进行中"; return false; }

        // 攻击条在一条命令里启动、在之后的 waitOnly 命令里等待，中间可能插入对白（Battle.nani 2757-2800）。
        if (state.customVariableManager.GetVariableValue("xSwitchAttackBarProcessing") == "1")
        { reason = "攻击条流程尚未结束"; return false; }

        // PlaybackSpot 是带 == 重载的 IL2CPP 值类型，必须用 is null 做引用判空，否则会调到原生 op_Equality。
        var spot = player.PlaybackSpot;
        if (spot is null || string.IsNullOrEmpty(spot.scriptName))
        { reason = "没有有效的播放位置"; return false; }

        if (player.Playing && !player.WaitingForInput) { reason = "脚本正在执行"; return false; }

        reason = null;
        return true;
    }

    private static bool IsBattleSavePoint(StateManager state, out string reason)
    {
        if (!EngineIdle(state, out reason)) return false;
        var player = GetPlayer(state);
        var blocker = player.GetSaveBlocker();
        if (blocker is not null && blocker.BlocksSave) { reason = $"{blocker.CommandName} 进行中"; return false; }
        return BattleState.IsChoicePoint(player, state.customVariableManager, out reason);
    }

    private void StartSave(StateManager state, bool force)
    {
        var player = GetPlayer(state);
        var spot = player.PlaybackSpot;
        var blocker = player.GetSaveBlocker();
        Plugin.Log.LogInfo(
            $"Saving {opSlotId} at {spot?.scriptName}:{spot?.lineIndex} force={force} " +
            $"playing={player.Playing} waitingInput={player.WaitingForInput} " +
            $"attackBar={state.customVariableManager.GetVariableValue("xSwitchAttackBarProcessing")} " +
            $"blocker={(blocker is not null && blocker.BlocksSave ? $"{blocker.CommandName}@{blocker.LineIndex} ({blocker.SaveBlockReason})" : "none")}");

        Plugin.Log.LogInfo($"[diag] save state: {Diagnostics.Describe(state)}");
        Diagnostics.Screenshot($"save_{opSlotId}");
        Enter(Phase.Saving);
        try
        {
            pendingSidecar = null;
            if (opSlotId != BattleSlotId && InBattle(state))
            {
                pendingSidecar = BattleState.Capture(player, state.customVariableManager);
                InputGate.AcquireUi(); // 捕获与原生序列化之间不让玩家操作
            }
            CounterSnapshot.Capture(state.customVariableManager);
            SaveBlockBypassPatch.BypassSlotId = opSlotId;
            SaveBlockBypassPatch.BypassInstance = state.Pointer;
            stateTask = state.SaveGameAsync(opSlotId);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"SaveGameAsync failed to start: {e}");
            Show($"存档失败：{e.Message}");
            Reset();
            return;
        }
        Show($"正在存档到{opLabel}…");
    }

    // ---------- battle checkpoint ----------

    private void EnsureSubscribed()
    {
        var state = GetStateManager();
        if (state is not null && (subscribedStateObj is null || state.Pointer != subscribedStateObj.Pointer))
        {
            UnsubscribeGameEvents();
            var source = state.Pointer; // 回调只认订阅时的实例
            gameSavedHandler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameSaveLoadArgs>>(new Action<GameSaveLoadArgs>(a => OnGameSaveLoad(a, "存档", source)));
            gameLoadedHandler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameSaveLoadArgs>>(new Action<GameSaveLoadArgs>(a => OnGameSaveLoad(a, "读档", source)));
            state.add_OnGameSaveFinished(gameSavedHandler);
            state.add_OnGameLoadFinished(gameLoadedHandler);
            subscribedStateObj = state;
            Plugin.Log.LogInfo("Subscribed to game save/load events");
        }

        if (!Plugin.BattleCheckpointEnabled.Value) return;
        var player = GetPlayer(GetStateManager());
        if (player is null || player.Pointer == subscribedPlayer) return;
        commandStartHandler ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Command>>(new Action<Command>(OnCommandStart));
        player.add_OnCommandExecutionStart(commandStartHandler);
        subscribedPlayer = player.Pointer;
        Plugin.Log.LogInfo("Subscribed to OnCommandExecutionStart for battle checkpoints");
    }

    /// <summary>
    /// 战斗脚本第一条命令开始执行前同步存一个检查点。读它会重新进入战斗，画面由游戏正常搭建。
    /// </summary>
    private void OnCommandStart(Command command)
    {
        try
        {
            var spot = command?.PlaybackSpot;
            if (spot is null) return;

            // 只在"从别处进入 Battle 开头"的第一条命令触发；开头几行连续执行时不重复触发。
            var prevWasEntry = prevCommandScript == "Battle" && prevCommandLine <= BattleEntryMaxLine;
            prevCommandScript = spot.scriptName;
            prevCommandLine = spot.lineIndex;
            if (spot.scriptName != "Battle" || spot.lineIndex > BattleEntryMaxLine || prevWasEntry) return;

            // 读检查点后恢复播放会再次经过入口；此时正在 Loading，跳过即可（存的就是同一个状态）。
            if (phase == Phase.Loading || phase == Phase.SavingGlobals || phase == Phase.Restoring) return;

            if (phase == Phase.WaitingForSafePoint && !battleWait) { Plugin.Log.LogInfo("Battle entry cancels pending F5 save"); Reset(); }
            if (SlotsPendingClear)
            {
                checkpointMissed = true;
                Plugin.Log.LogWarning("Battle entry while mod slots are pending clear; checkpoint skipped");
                return;
            }
            if (confirmSlotId != null) { CloseConfirm(); Show("已进入战斗，读档确认已取消"); }

            var state = GetStateManager();
            if (phase != Phase.Idle || !EngineIdle(state, out _) || !InGame(state.customVariableManager))
            {
                checkpointMissed = true;
                Plugin.Log.LogWarning($"Battle entry at {spot.scriptName}:{spot.lineIndex} but cannot save checkpoint (phase={phase})");
                Show("本场战斗检查点未能保存（F10 将读取上一场的检查点）");
                return;
            }

            checkpointMissed = false;
            opLabel = BattleLabel;
            opSlotId = BattleSlotId;
            opStatePtr = state.Pointer;
            Plugin.Log.LogInfo($"Battle entry detected at {spot.scriptName}:{spot.lineIndex} ({command.GetIl2CppType().Name}), saving checkpoint");
            StartSave(state, true);
        }
        catch (Exception e)
        {
            checkpointMissed = true;
            Plugin.Log.LogError($"battle checkpoint failed: {e}");
        }
    }

    // ---------- session-scoped slots ----------

    private void UnsubscribeGameEvents()
    {
        if (subscribedStateObj is null) return;
        try
        {
            if (gameSavedHandler != null) subscribedStateObj.remove_OnGameSaveFinished(gameSavedHandler);
            if (gameLoadedHandler != null) subscribedStateObj.remove_OnGameLoadFinished(gameLoadedHandler);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"unsubscribe game events: {e.Message}"); }
        subscribedStateObj = null;
    }

    private void OnDestroy() => UnsubscribeGameEvents();

    /// <summary>游戏自身的存/读档完成（不是 mod 槽位）：mod 槽位只在两次游戏存读档之间有效，清空。</summary>
    private void OnGameSaveLoad(GameSaveLoadArgs args, string what, IntPtr source)
    {
        try
        {
            var state = GetStateManager();
            if (state is null || state.Pointer != source) return; // 旧引擎实例的迟到回调
            var id = args?.SlotId ?? "";
            if (id.StartsWith(SlotPrefix, StringComparison.Ordinal) || id == BattleSlotId) return;
            if (!Plugin.ClearSlotsOnGameSaveLoad.Value) return;
            Plugin.Log.LogInfo($"Game's own {what} finished ({id}); clearing mod slots");
            if (phase == Phase.WaitingForSafePoint) Reset();
            pendingClearReason = $"游戏{what}";
            pendingClearQuiet = false;
            nextClearAttempt = 0;
            ProcessPendingClear();
        }
        catch (Exception e) { Plugin.Log.LogError($"game save/load hook: {e}"); }
    }

    /// <summary>有待清理且空闲时执行；删除失败的文件下次重试，期间 mod 槽位不可用。</summary>
    private void ProcessPendingClear()
    {
        if (pendingClearReason == null || phase != Phase.Idle) return;
        if (!Plugin.ClearSlotsOnGameSaveLoad.Value) { pendingClearReason = null; return; }
        if (Time.realtimeSinceStartup < nextClearAttempt) return;
        if (ClearModSlots(pendingClearReason, pendingClearQuiet)) pendingClearReason = null;
        else nextClearAttempt = Time.realtimeSinceStartup + 2f;
    }

    private bool SlotsPendingClear => pendingClearReason != null && Plugin.ClearSlotsOnGameSaveLoad.Value;

    /// <summary>删除全部 mod 存档（槽位、战斗检查点、战斗附加数据）。返回是否全部删除成功。</summary>
    private bool ClearModSlots(string reason, bool quiet)
    {
        var dir = System.IO.Path.Combine(Application.persistentDataPath, "NaninovelData", "Saves");
        var removed = 0;
        var failed = 0;
        string[] files;
        try
        {
            var found = new System.Collections.Generic.List<string>();
            if (System.IO.Directory.Exists(dir))
                foreach (var pattern in new[] { $"{SlotPrefix}*.nson", $"{BattleSlotId}.nson", "*.battle.json", "*.battle.json.tmp" })
                    foreach (var f in System.IO.Directory.GetFiles(dir, pattern))
                        if (!found.Contains(f)) found.Add(f);
            files = found.ToArray();
        }
        catch (Exception e) { Plugin.Log.LogError($"list mod slots: {e}"); return false; }

        foreach (var f in files)
        {
            try { System.IO.File.Delete(f); removed++; }
            catch (Exception e) { failed++; Plugin.Log.LogError($"delete {f}: {e.Message}"); }
        }
        checkpointMissed = false;
        Plugin.Log.LogInfo($"Cleared {removed} mod save files, {failed} failed ({reason})");
        if (failed > 0) { Show($"{reason}后清空 mod 存档失败（{failed} 个文件），稍后重试"); return false; }
        if (!quiet && removed > 0) Show($"{reason}，已清空 mod 存档槽位与战斗检查点");
        return true;
    }

    // ---------- load ----------

    private void RequestLoad(string slotId, string label)
    {
        if (phase != Phase.Idle) { Show("上一个操作还没完成"); return; }
        if (SlotsPendingClear) { Show("mod 存档正在清空，暂不能读档"); return; }

        // 允许在标题界面读档（与游戏自带读档菜单一致），只要求引擎空闲。
        var state = GetStateManager();
        if (!EngineIdle(state, out var why)) { Show($"现在不能读档：{why}"); return; }

        opLabel = label;
        opSlotId = slotId;
        if (!SlotExists(state, opSlotId)) { Show($"{opLabel} 是空的"); return; }

        // 战斗中的存档：先校验附加数据，不通过就不读，保持当前游戏不变。
        restoreSave = null;
        restoreSidecar = null;
        if (opSlotId != BattleSlotId)
        {
            try
            {
                var path = SlotPath(opSlotId);
                var save = BattleState.ReadSave(path);
                if (save.SpotScript == "Battle")
                {
                    var sc = BattleState.ReadSidecar(path, out var scErr);
                    if (sc == null) { Show($"{opLabel} 是战斗中的存档，但{scErr ?? "缺少战斗附加数据"}，无法读取"); return; }
                    restoreSave = save;
                    restoreSidecar = sc;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[restore] read {opSlotId}: {e}");
                Show($"读取存档数据失败：{e.Message}");
                return;
            }
        }

        var vars = state.customVariableManager;
        opStatePtr = state.Pointer;
        restoreThisLoad = Plugin.RestoreGlobalCounters.Value;
        loadResultNote = null;

        // 读档前先从存档文件预读计数快照，在调用 LoadGameAsync 之前写回全局变量，
        // 保证脚本恢复执行时看到的已经是存档时刻的计数。
        if (restoreThisLoad)
        {
            counterSnapshot = CounterSnapshot.ReadFromSaveFile(SlotPath(opSlotId), out var snapErr);
            if (counterSnapshot == null)
            {
                Plugin.Log.LogWarning($"[counters] {opSlotId}: {snapErr}");
                loadResultNote = $"{snapErr}，计数未回退";
            }
        }

        Plugin.Log.LogInfo($"Loading {opSlotId} restoreCounters={restoreThisLoad} snapshot={(counterSnapshot == null ? "none" : string.Join(",", counterSnapshot))}");
        Plugin.Log.LogInfo($"[diag] before-load state: {Diagnostics.Describe(state)}");
        Diagnostics.Screenshot($"beforeload_{opSlotId}");
        Diagnostics.StartTrace(20f, $"load {opSlotId}");
        Enter(Phase.Loading);
        if (restoreSave != null) InputGate.AcquireUi();
        try
        {
            counterUndo = CounterSnapshot.ReadGlobals(vars);
            if (counterSnapshot != null)
            {
                countersTouched = true; // 先置位，覆盖写到一半抛错的情况
                CounterSnapshot.ApplyGlobals(vars, counterSnapshot, "before-load");
            }
            stateTask = state.LoadGameAsync(opSlotId);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"LoadGameAsync failed to start: {e}");
            Show($"读档失败：{e.Message}");
            UndoCountersIfTouched(state);
            Reset();
            return;
        }
        Show($"正在读取{opLabel}…");
    }

    // ---------- state machine ----------

    private void Advance()
    {
        switch (phase)
        {
            case Phase.WaitingForSafePoint:
            {
                var state = GetStateManager();
                if (state is null || state.Pointer != opStatePtr) { Reset(); Show("引擎已重置，存档取消"); return; }
                if (state.customVariableManager is null || !InGame(state.customVariableManager)) { Reset(); Show("已离开游戏，存档取消"); return; }
                if (battleWait)
                {
                    if (!InBattle(state)) { Reset(); Show("战斗已结束，存档取消"); return; }
                    if (IsBattleSavePoint(state, out _)) StartSave(state, false);
                    return;
                }
                if (InBattle(state)) { Reset(); Show("已进入战斗，存档取消"); return; }
                if (IsSafePoint(state, out _)) { StartSave(state, false); return; }
                if (Elapsed > SafePointTimeout) { Reset(); Show($"{SafePointTimeout:0} 秒内没有等到安全点，存档取消"); }
                return;
            }
            case Phase.Saving:
            case Phase.Loading:
                if (EngineReplaced()) return;
                if (stateTask.Status == AwaiterStatus.Pending) { WarnIfSlow(); return; }
                try { FinishStateTask(); }
                catch (Exception e)
                {
                    // 任务结果已经消费，不能回到 Saving/Loading 再处理一次。
                    Plugin.Log.LogError($"post-processing failed: {e}");
                    Show($"后处理出错：{e.Message}");
                    Reset();
                }
                return;
            case Phase.SavingGlobals:
                if (EngineReplaced()) return;
                if (globalTask.Status == AwaiterStatus.Pending) { WarnIfSlow(); return; }
                var ok = Observe(() => globalTask.GetAwaiter().GetResult(), "SaveGlobalAsync", out var err);
                EndLoad(ok ? $"读档成功：{opLabel}（{loadResultNote}）" : $"已读档，但计数未能写入全局存档：{err}");
                return;
            case Phase.Restoring:
                if (EngineReplaced()) return;
                AdvanceRestore();
                return;
        }
    }

    private void FinishStateTask()
    {
        var wasSaving = phase == Phase.Saving;
        var ok = Observe(() => stateTask.GetAwaiter().GetResult(), wasSaving ? "SaveGameAsync" : "LoadGameAsync", out var err);

        if (wasSaving)
        {
            if (!ok && opSlotId == BattleSlotId) checkpointMissed = true;
            var note = "";
            if (ok && opSlotId != BattleSlotId)
            {
                // 原生存档成功后才动附加文件：非战斗档删掉旧的，战斗档写新的（带新存档的哈希）。
                if (pendingSidecar == null) BattleState.DeleteSidecar(SlotPath(opSlotId));
                else
                {
                    try { BattleState.WriteSidecar(SlotPath(opSlotId), pendingSidecar); note = "（战斗中）"; }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"write battle sidecar failed: {e}");
                        BattleState.DeleteSidecar(SlotPath(opSlotId));
                        ok = false;
                        err = $"战斗附加数据写入失败（{e.Message}），这个槽位的存档读取后无法重建战斗画面";
                    }
                }
            }
            if (ok) restoreBroken = false;
            if (ok && opSlotId == BattleSlotId) note = $"（按 {Plugin.BattleCheckpointKey.Value} 读取；{Plugin.LoadKey.Value} 读的是当前槽位 {slot}）";
            Show(ok ? $"存档成功：{opLabel}{note}" : $"存档失败：{err}");
            Reset();
            return;
        }

        var state = GetStateManager();
        if (state is null || state.Pointer != opStatePtr)
        {
            // 旧引擎实例的任务：不向新实例写快照或撤销值。
            Show(ok ? $"读档完成，但引擎已重建，计数未处理" : $"读档失败：{err}");
            Reset();
            return;
        }
        if (ok)
        {
            Plugin.Log.LogInfo($"[diag] after-load state: {Diagnostics.Describe(state)}");
            Diagnostics.Screenshot($"afterload0_{opSlotId}", 0.3f);
            Diagnostics.Screenshot($"afterload3_{opSlotId}", 3f);
        }
        if (!ok)
        {
            UndoCountersIfTouched(state);
            Show($"读档失败：{err}");
            Reset();
            return;
        }

        if (counterSnapshot == null || state?.customVariableManager is null)
        {
            EndLoad(loadResultNote == null ? $"读档成功：{opLabel}" : $"读档成功：{opLabel}（{loadResultNote}）");
            return;
        }

        // 读档流程如果重新装载了全局变量，再写一次（正常情况下这里不会有变化）。
        var vars = state.customVariableManager;
        if (!CounterSnapshot.GlobalsEqual(vars, counterSnapshot))
        {
            Plugin.Log.LogWarning("[counters] 读档后计数被改回，重新写入");
            CounterSnapshot.ApplyGlobals(vars, counterSnapshot, "after-load");
        }
        loadResultNote = "死亡计数已回退";

        try { globalTask = state.SaveGlobalAsync(); }
        catch (Exception e)
        {
            Plugin.Log.LogError($"SaveGlobalAsync failed to start: {e}");
            EndLoad("已读档并回退计数，但写入全局存档失败（下次游戏保存时会写入）");
            return;
        }
        Enter(Phase.SavingGlobals);
    }

    private void UndoCountersIfTouched(StateManager state)
    {
        if (!countersTouched || counterUndo == null || state?.customVariableManager is null) return;
        try { CounterSnapshot.ApplyGlobals(state.customVariableManager, counterUndo, "undo"); }
        catch (Exception e) { Plugin.Log.LogError($"counter undo failed: {e}"); }
    }

    private static bool Observe(Action getResult, string what, out string error)
    {
        try { getResult(); error = null; Plugin.Log.LogInfo($"{what} succeeded"); return true; }
        catch (Exception e) { error = e.Message; Plugin.Log.LogError($"{what} failed: {e}"); return false; }
    }

    /// <summary>引擎被重建时，旧操作的任务不再属于当前会话，结束跟踪。</summary>
    private bool EngineReplaced()
    {
        var state = GetStateManager();
        if (state is not null && state.Pointer == opStatePtr) return false;
        Plugin.Log.LogWarning($"Engine replaced during {phase} {opSlotId}; dropping operation");
        Show("引擎已重建，之前的存/读档操作已放弃");
        Reset();
        return true;
    }

    private bool IsStuck => (phase == Phase.Saving || phase == Phase.Loading || phase == Phase.SavingGlobals || phase == Phase.Restoring) && Elapsed > StuckAfter;

    private void WarnIfSlow()
    {
        if (IsStuck && !stuckWarned)
        {
            stuckWarned = true;
            Plugin.Log.LogWarning($"{phase} {opSlotId} stuck for {StuckAfter}s");
            Show($"{OpText} 超过 {StuckAfter:0} 秒未完成；如游戏已正常，可按 Ctrl+{Plugin.LoadKey.Value} 解除锁定");
            return;
        }
        if (slowWarned || Elapsed < SlowWarningAfter) return;
        slowWarned = true;
        Plugin.Log.LogWarning($"{phase} {opSlotId} still pending after {SlowWarningAfter}s");
        Show($"{OpText} 仍未完成，继续等待…");
    }

    private string OpText => phase switch
    {
        Phase.Saving => $"存档到{opLabel}",
        Phase.Loading => $"读取{opLabel}",
        Phase.SavingGlobals => "写入全局存档",
        Phase.Restoring => "重建战斗画面",
        _ => phase.ToString(),
    };

    private float Elapsed => Time.realtimeSinceStartup - phaseSince;

    private void Enter(Phase next)
    {
        phase = next;
        phaseSince = Time.realtimeSinceStartup;
        slowWarned = false;
        stuckWarned = false;
    }

    private void Reset()
    {
        phase = Phase.Idle;
        opSlotId = null;
        opStatePtr = IntPtr.Zero;
        stateTask = null;
        globalTask = null;
        counterUndo = null;
        counterSnapshot = null;
        countersTouched = false;
        battleWait = false;
        pendingSidecar = null;
        restoreSave = null;
        restoreSidecar = null;
        restoreRunner = null;
        restoreLog = null;
        confirmSlotId = null;
        confirmDecision = 0;
        InputGate.Release();
        SaveBlockBypassPatch.Clear();
    }

    // ---------- battle restore ----------

    private const float RestoreStepTimeout = 20f;

    private void EndLoad(string message)
    {
        restoreBroken = false;
        if (restoreSave == null || restoreSidecar == null) { Show(message); Reset(); return; }
        restoreDoneMessage = message;
        restoreStep = 0;
        restoreWaitFrames = 0;
        restoreLog = new System.Text.StringBuilder();
        InputGate.AcquireUi();
        InputGate.AcquireNaninovel(); // 读档已完成，此时的 ProcessInput 就是存档里的值
        Enter(Phase.Restoring);
        Show("正在重建战斗画面…");
    }

    private void NextRestoreStep(int step)
    {
        restoreStep = step;
        restoreStepSince = Time.realtimeSinceStartup;
        restoreWaitFrames = 0;
    }

    private bool RunRestoreScript()
    {
        if (!restoreRunner.Step())
        {
            if (Time.realtimeSinceStartup - restoreStepSince > RestoreStepTimeout)
                throw new InvalidOperationException($"{restoreRunner.Name} 超过 {RestoreStepTimeout:0} 秒未完成");
            return false;
        }
        restoreLog.Append(restoreRunner.Log);
        if (restoreRunner.Error != null) throw new InvalidOperationException(restoreRunner.Error);
        return true;
    }

    private void StartRestoreScript(string name, string text)
    {
        restoreLog.AppendLine(text);
        restoreRunner = new NaniRunner(name, text);
    }

    private void AdvanceRestore()
    {
        var state = GetStateManager();
        var player = GetPlayer(state);
        var vars = state?.customVariableManager;
        try
        {
            switch (restoreStep)
            {
                case 0: // 等读档后的播放器稳定停在存档位置
                {
                    var spot = player?.PlaybackSpot;
                    var settled = spot is not null && spot.scriptName == restoreSave.SpotScript && spot.lineIndex == restoreSave.SpotLine && !player.Playing && !state.inFlightLoad;
                    restoreWaitFrames = settled ? restoreWaitFrames + 1 : 0;
                    if (restoreWaitFrames < 5)
                    {
                        if (Elapsed > 10f) throw new InvalidOperationException($"播放器没有停在存档位置（{spot?.scriptName}:{spot?.lineIndex}）");
                        return;
                    }
                    var es = Probes.Service<EnemyService>();
                    var needDisplay = es is null || es.enemyDisplay == null || !es.enemyDisplay.activeInHierarchy;
                    StartRestoreScript("restoreEnemies", BattleState.BuildEnemyScript(restoreSave, restoreSidecar, needDisplay));
                    NextRestoreStep(1);
                    return;
                }
                case 1:
                    if (!RunRestoreScript()) return;
                    BattleState.FixPositions(restoreSidecar, restoreLog);
                    BattleState.RestoreLocals(vars, restoreSave, restoreLog, "after-enemies");
                    StartRestoreScript("restoreFinish", BattleState.BuildFinishScript(restoreSave, restoreSidecar));
                    NextRestoreStep(2);
                    return;
                case 2:
                    if (!RunRestoreScript()) return;
                    BattleState.RestoreEnemyRuntime(restoreSave, restoreSidecar, restoreLog);
                    BattleState.RestoreLocals(vars, restoreSave, restoreLog, "before-choices");
                    StartRestoreScript("restoreChoices", BattleState.BuildChoiceScript(restoreSave));
                    NextRestoreStep(3);
                    return;
                case 3:
                    if (!RunRestoreScript()) return;
                    NextRestoreStep(4);
                    return;
                case 4: // 等两帧让预告刷新，再做最终核对
                {
                    if (++restoreWaitFrames < 3) return;
                    var changed = BattleState.RestoreLocals(vars, restoreSave, restoreLog, "final");
                    var mismatches = BattleState.CheckActionTexts(restoreSave, restoreSidecar, restoreLog);
                    var endSpot = player?.PlaybackSpot;
                    if (endSpot is null || endSpot.scriptName != restoreSave.SpotScript || endSpot.lineIndex != restoreSave.SpotLine || player.Playing)
                        throw new InvalidOperationException($"重建后脚本位置变了（{endSpot?.scriptName}:{endSpot?.lineIndex}）");
                    restoreLog.AppendLine($"[restore] done: lateVarFixes={changed} actionTextMismatches={mismatches}");
                    Plugin.Log.LogInfo(restoreLog.ToString());
                    Show(mismatches == 0
                        ? $"{restoreDoneMessage}，战斗画面已重建"
                        : $"{restoreDoneMessage}，战斗画面已重建（{mismatches} 个敌人的行动预告与存档时不同，详见日志）");
                    Reset();
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[restore] failed at step {restoreStep}: {e}\n{restoreLog}");
            restoreBroken = true;
            Show($"战斗画面重建失败：{e.Message}。请重新读档（F10 读取的是最近一次进入战斗时的检查点，不一定是这一场）");
            Reset();
        }
    }

    // ---------- dev console hooks ----------

    internal bool IsIdle => phase == Phase.Idle && confirmSlotId == null;

    internal string DebugStatus() => $"phase={phase} op={opSlotId} slot={slot} inBattle={InBattle(GetStateManager())}";

    internal string DebugForceSave(string slotId)
    {
        if (phase != Phase.Idle) return $"busy: {phase}";
        var state = GetStateManager();
        if (!EngineIdle(state, out var why)) return $"not idle: {why}";
        opLabel = slotId;
        opSlotId = slotId;
        opStatePtr = state.Pointer;
        StartSave(state, true);
        return $"saving {slotId}";
    }

    internal string DebugLoad(string slotId)
    {
        if (phase != Phase.Idle) return $"busy: {phase}";
        RequestLoad(slotId, slotId);
        return phase == Phase.Loading ? $"loading {slotId}" : $"load not started: {toast}";
    }

    // ---------- toast ----------

    private void Show(string text)
    {
        toast = text;
        toastUntil = Time.realtimeSinceStartup + 3f;
        Plugin.Log.LogInfo($"[toast] {text}");
    }

    private GUIStyle dialogText;
    private GUIStyle dialogDetail;

    private void OnGUI()
    {
        if (confirmSlotId != null && confirmDecision == 0)
        {
            if (dialogText == null)
            {
                dialogText = new GUIStyle { fontSize = 24, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                dialogText.normal.textColor = Color.white;
                dialogDetail = new GUIStyle { fontSize = 18, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                dialogDetail.normal.textColor = new Color(1f, 0.9f, 0.6f);
            }
            const float w = 640, h = 230;
            var r = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.Box(r, GUIContent.none);
            GUI.Box(r, GUIContent.none); // 叠两层加深底色
            GUI.Label(new Rect(r.x + 20, r.y + 15, w - 40, 90), $"读取 {confirmLabel}？\n当前进度将丢失", dialogText);
            GUI.Label(new Rect(r.x + 20, r.y + 100, w - 40, 50), confirmDetail, dialogDetail);
            var left = (int)Math.Ceiling(confirmUntil - Time.realtimeSinceStartup);
            if (GUI.Button(new Rect(r.x + 60, r.y + 160, 240, 50), $"读档（{confirmKey}/Enter）")) ConfirmLoad();
            else if (GUI.Button(new Rect(r.x + w - 300, r.y + 160, 240, 50), $"取消（Esc，{left}s）")) CancelConfirm("已取消读档");
        }

        var waiting = phase == Phase.WaitingForSafePoint;
        if (toast == null || (!waiting && Time.realtimeSinceStartup > toastUntil)) return;
        if (toastStyle == null)
        {
            // 自建样式，不修改共享的 GUI.skin。
            toastStyle = new GUIStyle { fontSize = 22, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            toastStyle.normal.textColor = Color.white;
        }
        var rect = new Rect(20, 20, 760, 48);
        GUI.Box(rect, GUIContent.none);
        GUI.Label(rect, $"[SaveAnywhere] {toast}", toastStyle);
    }
}
