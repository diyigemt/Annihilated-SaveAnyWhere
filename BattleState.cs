using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Naninovel;
using UnityEngine;

namespace SaveAnywhere;

/// <summary>
/// 战斗中存读档：只允许在"玩家选指令"的 @stop 处存档；存档时把 Naninovel 不保存的
/// C# 战斗状态写进同名 .battle.json 附加文件；读档后据此生成 nani 片段重建战斗画面。
/// </summary>
internal static class BattleState
{
    private const int SidecarVersion = 3;
    private const string ChoiceLabel = "ChoiceDebug";
    private const string ChoiceLabelEnd = "HeroLinkLightningPlayerChoice";
    private const string BattleCommandButton = "Buttons/CustomChoiceHandlerButtonBattleCommand";

    // 选指令点不能存档的状态：教程演出、攻击条、阶段重启/结束请求、死亡复查等。
    private static readonly (string Var, string Bad)[] BlockingFlags =
    {
        ("xSwitchAttackBarProcessing", "1"),
        ("xSwitchBattlePhaseRestartPending", "1"),
        ("xSwitchBattleEndRequested", "1"),
        ("xSwitchEnemyDeathCheckRunning", "1"),
        ("xSwitchEnemyDeathRecheckRequested", "1"),
        ("xSwitchIsTutorial", "1"),
        ("xSwitchTutorialSlimeBattle", "1"),
        ("xSwitchAttackTutorial", "1"),
        ("xSwitchStealTutorial", "1"),
        ("xSwitchStealBossTutorial", "1"),
        ("xSwitchCurseTutorial", "1"),
        ("xSwitchPositionTutorial", "1"),
        ("xSwitchPositionTutorial", "2"),
    };

    // ---------- choice point ----------

    /// <summary>是否停在普通回合的选指令 @stop（Battle.nani 的 # ChoiceDebug 段内）。</summary>
    public static bool IsChoicePoint(CustomScriptPlayer player, ICustomVariableManager vars, out string reason)
    {
        var spot = player.PlaybackSpot;
        if (spot is null || spot.scriptName != "Battle") { reason = "不在战斗脚本中"; return false; }
        if (player.Playing) { reason = "战斗正在进行"; return false; }
        var cmd = player.PlayedCommand;
        if (cmd is null || cmd.GetIl2CppType().Name != "Stop") { reason = "还没到选指令的时刻"; return false; }

        var script = player.PlayedScript;
        if (script is null || !script.LabelExists(ChoiceLabel) || !script.LabelExists(ChoiceLabelEnd))
        { reason = "战斗脚本结构与预期不符"; return false; }
        var from = script.GetLineIndexForLabel(ChoiceLabel);
        var to = script.GetLineIndexForLabel(ChoiceLabelEnd);
        if (spot.lineIndex <= from || spot.lineIndex >= to) { reason = "当前是特殊指令选择，不支持存档"; return false; }

        foreach (var (name, bad) in BlockingFlags)
            if (vars.GetVariableValue(name) == bad) { reason = $"当前状态不支持存档（{name}）"; return false; }

        reason = null;
        return true;
    }

    // ---------- sidecar ----------

    internal sealed class EnemyInfo
    {
        public int Id { get; set; }
        public string Action { get; set; }
        public float LogicalX { get; set; }
        public float WorldX { get; set; }
        public float WorldY { get; set; }
        public float WorldZ { get; set; }
        public float ScaleX { get; set; }
        public float ScaleY { get; set; }
        public string ActionJson { get; set; }
        public List<PropInfo> ActionProps { get; set; }
        public string ActionText { get; set; }
    }

    internal sealed class UsageInfo
    {
        public int EnemyId { get; set; }
        public string ActionSet { get; set; }
        public int UsedCount { get; set; }
        public int LastUsedTurn { get; set; }
    }

    /// <summary>trackedCharacterStates 的一项：Who 为 player/partner，LookId/State 为当前显示的立绘与姿态。</summary>
    internal sealed class AllyInfo
    {
        public string Who { get; set; }
        public string LookId { get; set; }
        public string State { get; set; }
    }

    /// <summary>ActionSetData.customProperties 的一项（JsonUtility 不序列化字典，单独保存）。</summary>
    internal sealed class PropInfo
    {
        public string Key { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
        public int[] Ints { get; set; }
    }

    internal sealed class Sidecar
    {
        public int Version { get; set; }
        public string NsonSha256 { get; set; }
        public string Spot { get; set; }
        public int TurnCount { get; set; }
        public List<EnemyInfo> Enemies { get; set; } = new();
        public List<UsageInfo> Usage { get; set; } = new();
        public Dictionary<string, int> NoActTurns { get; set; } = new();
        public List<int> HiddenNextActionIds { get; set; } = new();
        public List<AllyInfo> AllyStates { get; set; } = new();
    }

    public static string SidecarPath(string nsonPath) => Path.ChangeExtension(nsonPath, ".battle.json");

    public static Sidecar Capture(CustomScriptPlayer player, ICustomVariableManager vars)
    {
        var es = Probes.Service<EnemyService>() ?? throw new InvalidOperationException("EnemyService 不可用");
        var spot = player.PlaybackSpot;
        var sc = new Sidecar
        {
            Version = SidecarVersion,
            Spot = $"{spot.scriptName}:{spot.lineIndex}",
            TurnCount = int.TryParse(vars.GetVariableValue("turnCount"), out var t) ? t : -1,
        };

        var count = int.Parse(vars.GetVariableValue("enemyMaxCount"), CultureInfo.InvariantCulture);
        var ctrls = es.enemyControllerList;
        var next = es.enemyNextActionSetDataList;
        var texts = es.enemyNextActionTextList;
        for (var id = 1; id <= count; id++)
        {
            var ctrl = ctrls != null && id < ctrls.Count ? ctrls[id] : null;
            if (ctrl is null) throw new InvalidOperationException($"敌人 {id} 没有控制器");
            var action = next != null && id < next.Count ? next[id] : null;
            var pos = ctrl.transform.position;
            sc.Enemies.Add(new EnemyInfo
            {
                Id = id,
                Action = action?.name,
                LogicalX = ctrl.logicalSceneX,
                WorldX = pos.x, WorldY = pos.y, WorldZ = pos.z,
                ScaleX = ctrl.transform.localScale.x,
                ScaleY = ctrl.transform.localScale.y,
                ActionJson = action is null ? null : JsonUtility.ToJson(action),
                ActionProps = action is null ? null : CaptureProps(action, id),
                ActionText = texts != null && id < texts.Count && texts[id] != null ? texts[id].text : null,
            });
        }

        sc.Usage = CaptureUsage(es, count);
        if (es.enemyNoActTurns != null)
            foreach (var kv in es.enemyNoActTurns) sc.NoActTurns[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
        if (es.hiddenEnemyNextActionDisplayIds != null)
            foreach (var id in es.hiddenEnemyNextActionDisplayIds) sc.HiddenNextActionIds.Add(id);

        var chars = UnityEngine.Object.FindObjectOfType<BattleCharacterSpriteController>();
        if (chars != null && chars.trackedCharacterStates != null)
            foreach (var kv in chars.trackedCharacterStates)
                sc.AllyStates.Add(new AllyInfo { Who = kv.Key, LookId = kv.Value.Item1, State = kv.Value.Item2 });

        // 特殊姿态（破防/超级支援）必须有记录才能重建，否则拒绝存档。
        foreach (var who in new[] { "player", "partner" })
        {
            if (!NeedsTrackedSprite(vars.GetVariableValue, who)) continue;
            var a = sc.AllyStates.FirstOrDefault(x => x.Who == who);
            if (a == null || string.IsNullOrEmpty(a.LookId) || string.IsNullOrEmpty(a.State))
                throw new InvalidOperationException($"{who} 处于特殊姿态但没有立绘记录，暂不支持在此时存档");
        }
        return sc;
    }

    private static bool NeedsTrackedSprite(Func<string, string> get, string who)
        => (float.TryParse(get($"{who}BreakCount"), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b >= 1)
           || (get("xSwitchMageSuperBuffActive") == "1" && who == "partner");

    /// <summary>
    /// enemyActionUsage 的键是 ValueTuple&lt;int,string&gt;，interop 读其中的 int 会得到垃圾值，
    /// 所以只读行动名和计数，再用公开查询接口匹配到敌人。
    /// </summary>
    private static List<UsageInfo> CaptureUsage(EnemyService es, int enemyCount)
    {
        var result = new List<UsageInfo>();
        var entries = new List<(string Name, int Count, int Turn)>();
        if (es.enemyActionUsage == null) return result;
        foreach (var kv in es.enemyActionUsage) entries.Add((kv.Key.Item2, kv.Value.UsedCount, kv.Value.LastUsedTurn));

        foreach (var group in entries.GroupBy(e => (e.Name, e.Turn)))
        {
            var ids = Enumerable.Range(1, enemyCount)
                .Where(id => es.HasEnemyUsedActionSetInBattle(id, group.Key.Name) && es.GetEnemyActionSetLastUsedTurn(id, group.Key.Name) == group.Key.Turn)
                .ToList();
            var counts = group.Select(g => g.Count).ToList();
            if (ids.Count != counts.Count)
                throw new InvalidOperationException($"行动使用记录无法对应到敌人（{group.Key.Name}@{group.Key.Turn}: {counts.Count} 条记录 / {ids.Count} 个敌人）");
            if (counts.Distinct().Count() > 1)
                Plugin.Log.LogWarning($"[capture] usage {group.Key.Name}@{group.Key.Turn} has differing counts {string.Join(",", counts)}; assigned in order");
            for (var i = 0; i < ids.Count; i++)
                result.Add(new UsageInfo { EnemyId = ids[i], ActionSet = group.Key.Name, UsedCount = counts[i], LastUsedTurn = group.Key.Turn });
        }
        return result;
    }

    private static List<PropInfo> CaptureProps(ActionSetData action, int enemyId)
    {
        var list = new List<PropInfo>();
        var props = action.customProperties;
        if (props == null) return list;
        foreach (var kv in props)
        {
            var v = kv.Value;
            var info = new PropInfo { Key = kv.Key };
            var type = v == null ? "null" : v.GetIl2CppType().FullName;
            switch (type)
            {
                case "null": info.Type = "null"; break;
                case "System.Int32": info.Type = "int"; info.Value = v.Unbox<int>().ToString(CultureInfo.InvariantCulture); break;
                case "System.Single": info.Type = "float"; info.Value = v.Unbox<float>().ToString("R", CultureInfo.InvariantCulture); break;
                case "System.Boolean": info.Type = "bool"; info.Value = v.Unbox<bool>() ? "1" : "0"; break;
                case "System.String": info.Type = "string"; info.Value = IL2CPP.Il2CppStringToManaged(v.Pointer); break;
                case "System.Int32[]":
                    info.Type = "int[]";
                    info.Ints = new Il2CppStructArray<int>(v.Pointer).ToArray();
                    break;
                default:
                    throw new InvalidOperationException($"敌人 {enemyId} 的行动带有暂不支持的属性 {kv.Key}（{type}）");
            }
            list.Add(info);
        }
        return list;
    }

    private static unsafe Il2CppSystem.Object Box<T>(T value) where T : unmanaged
        => new(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, (IntPtr)(&value)));

    private static void RestoreProps(ActionSetData action, List<PropInfo> props)
    {
        if (props == null) return;
        foreach (var p in props)
        {
            switch (p.Type)
            {
                case "null": action.SetProperty(p.Key, null); break;
                case "int": action.SetProperty(p.Key, Box(int.Parse(p.Value, CultureInfo.InvariantCulture))); break;
                case "float": action.SetProperty(p.Key, Box(float.Parse(p.Value, CultureInfo.InvariantCulture))); break;
                case "bool": action.SetProperty(p.Key, Box(p.Value == "1")); break;
                case "string": action.SetProperty(p.Key, new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(p.Value))); break;
                case "int[]":
                    var arr = new Il2CppStructArray<int>(p.Ints ?? Array.Empty<int>());
                    if (p.Key == ActionSetData.PlannedOutgoingDamageValuesPropertyName) action.SetPlannedOutgoingDamageValues(arr);
                    else action.SetProperty(p.Key, new Il2CppSystem.Object(arr.Pointer));
                    break;
            }
        }
    }

    public static void WriteSidecar(string nsonPath, Sidecar sc)
    {
        sc.NsonSha256 = Sha256(nsonPath);
        var path = SidecarPath(nsonPath);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sc, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static void DeleteSidecar(string nsonPath)
    {
        try { File.Delete(SidecarPath(nsonPath)); } catch (Exception e) { Plugin.Log.LogWarning($"delete sidecar: {e.Message}"); }
    }

    /// <summary>读取并校验与存档配对的附加文件；不存在返回 null（error 为 null）。</summary>
    public static Sidecar ReadSidecar(string nsonPath, out string error)
    {
        error = null;
        var path = SidecarPath(nsonPath);
        if (!File.Exists(path)) return null;
        try
        {
            var sc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path, Encoding.UTF8));
            if (sc == null || sc.Version != SidecarVersion) { error = "战斗附加数据版本不符"; return null; }
            if (!string.Equals(sc.NsonSha256, Sha256(nsonPath), StringComparison.OrdinalIgnoreCase))
            { error = "战斗附加数据与存档不匹配"; return null; }
            return sc;
        }
        catch (Exception e) { error = $"战斗附加数据损坏：{e.Message}"; return null; }
    }

    private static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(f));
    }

    // ---------- save file ----------

    internal sealed class SaveData
    {
        public Dictionary<string, string> Locals = new(StringComparer.Ordinal);
        public List<(string Summary, string Script)> BattleButtons = new();
        public string SpotScript;
        public int SpotLine;
    }

    public static SaveData ReadSave(string nsonPath)
    {
        string json;
        using (var file = File.OpenRead(nsonPath))
        using (var inflate = new DeflateStream(file, CompressionMode.Decompress))
        using (var reader = new StreamReader(inflate, detectEncodingFromByteOrderMarks: true))
            json = reader.ReadToEnd();

        var data = new SaveData();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var spot = root.GetProperty("playbackSpot");
        data.SpotScript = spot.GetProperty("scriptName").GetString();
        data.SpotLine = spot.GetProperty("lineIndex").GetInt32();

        var map = root.GetProperty("objectJsonMap");
        var keys = map.GetProperty("keys");
        var values = map.GetProperty("values");
        for (var i = 0; i < keys.GetArrayLength(); i++)
        {
            var key = keys[i].GetString() ?? "";
            if (key.StartsWith("Naninovel.CustomVariableManager+GameState,", StringComparison.Ordinal))
            {
                using var v = JsonDocument.Parse(values[i].GetString()!);
                var local = v.RootElement.GetProperty("LocalVariableMap");
                var names = local.GetProperty("keys");
                var vals = local.GetProperty("values");
                for (var k = 0; k < names.GetArrayLength(); k++)
                    data.Locals[names[k].GetString()!] = vals[k].ValueKind == JsonValueKind.String ? vals[k].GetString() : vals[k].ToString();
            }
            else if (key.StartsWith("Naninovel.UI.ChoiceHandlerPanel+GameState,", StringComparison.Ordinal))
            {
                using var v = JsonDocument.Parse(values[i].GetString()!);
                foreach (var b in v.RootElement.GetProperty("Buttons").EnumerateArray())
                {
                    if (b.GetProperty("buttonPath").GetString() != BattleCommandButton) continue;
                    data.BattleButtons.Add((b.GetProperty("summary").GetString() ?? "", b.GetProperty("onSelectScript").GetString() ?? ""));
                }
            }
        }
        return data;
    }

    /// <summary>把局部变量写回存档值；返回改动数量。全局变量（G_）不动。</summary>
    public static int RestoreLocals(ICustomVariableManager vars, SaveData save, StringBuilder log, string stage)
    {
        var changed = 0;
        foreach (var (name, value) in save.Locals)
        {
            if (name.StartsWith("G_", StringComparison.Ordinal)) continue;
            var current = vars.GetVariableValue(name);
            if (current == value) continue;
            if (changed < 40) log.AppendLine($"  [{stage}] {name}: '{current}' -> '{value}'");
            vars.SetVariableValue(name, value);
            changed++;
        }
        log.AppendLine($"[{stage}] restored {changed} locals");
        return changed;
    }

    // ---------- restore scripts ----------

    private static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string Var(SaveData s, string name) => s.Locals.TryGetValue(name, out var v) ? v : "";

    private static float Num(SaveData s, string name)
        => float.TryParse(Var(s, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

    /// <summary>第一段：重新生成全部敌人（含已死亡的，保持槽位编号）。</summary>
    public static string BuildEnemyScript(SaveData s, Sidecar sc, bool needDisplay)
    {
        var sb = new StringBuilder();
        if (needDisplay) sb.AppendLine("@showEnemyDisplay");
        var names = string.Join(",", sc.Enemies.Select(e => Var(s, $"enemy{e.Id}ResourceName")));
        var forms = string.Join(",", sc.Enemies.Select(e => string.IsNullOrEmpty(Var(s, $"enemy{e.Id}FormNum")) ? "0" : Var(s, $"enemy{e.Id}FormNum")));
        var xs = string.Join(",", sc.Enemies.Select(e => F(e.LogicalX)));
        sb.AppendLine("@set enemyMaxCountBefore=0");
        sb.AppendLine($"@set enemyMaxCount={sc.Enemies.Count}");
        sb.AppendLine($"@set addEnemyResourceNames=\"{names}\"");
        sb.AppendLine($"@battleAddEnemies deferAppearance:false enemyPositionsString:\"{xs}\" enemyPositionsY:{{enemyYPos}} enemyPositionsZ:{{enemyZPos}} enemyScales:{{enemyScale}} enemyFormNums:\"{forms}\" duration:0 enemyHealthAddValue:0 enemyHealthDecayStacks:0 applyMaxHealthAdjustmentToCurrent:false showUI:{{showEnemyUI}}");
        sb.AppendLine("@set _=\"{InitEnemyControllerList(enemyMaxCount)}\"");
        return sb.ToString();
    }

    /// <summary>第二段：变量写回后执行，恢复血条、死亡、下一步行动、我方立绘、指令与手牌。</summary>
    public static string BuildFinishScript(SaveData s, Sidecar sc)
    {
        var sb = new StringBuilder();
        foreach (var e in sc.Enemies)
        {
            var id = e.Id;
            if (Var(s, $"enemy{id}Death") == "1")
            {
                sb.AppendLine($"@enemyOpacity enemyId:{id} value:0 duration:0");
                sb.AppendLine($"@setEnemyNextActionText \"\" enemyId:{id}");
                sb.AppendLine($"@storeEnemyNextAction enemyId:{id} noAct:true");
                continue;
            }
            foreach (var g in new[] { "Health", "Break" })
            {
                var max = Num(s, $"enemy{id}Max{g}");
                if (max <= 0) continue;
                var ratio = Math.Clamp(Num(s, $"enemy{id}{g}") / max, 0f, 1f);
                sb.AppendLine($"@enemyChangeGaugeEffect {F(ratio)} gaugeName:\"{g}\" enemyId:{id} time:0");
            }
            if (string.IsNullOrEmpty(e.Action)) sb.AppendLine($"@storeEnemyNextAction enemyId:{id} noAct:true");
            else sb.AppendLine($"@storeEnemyNextActionSet enemyId:{id} actionSetName:\"{e.Action}\"");
        }

        // 我方立绘：照搬 Battle.nani # NextAction 里回合开始时的显示逻辑。
        sb.AppendLine("@battleCharacterSprite preload characterId:\"player\"");
        sb.AppendLine("@battleCharacterSprite preload characterId:\"partner\"");
        sb.AppendLine("@battleCharacterSprite move isAttackMove:false position:\"{turnCharacterPosition}\"");
        sb.AppendLine("@battleCharacterSprite setAlpha position:\"{turnCharacterPosition}\" alpha:1");
        sb.AppendLine("@battleCharacterSprite setAlpha position:\"{notTurnCharacterPosition}\" alpha:0.6");
        // 正常姿态按回合开始的逻辑显示；破防等特殊姿态用存档时记录的状态（trackedCharacterStates）补回。
        foreach (var (posVar, whoVar) in new[] { ("turnCharacterPosition", "turnCharacter"), ("notTurnCharacterPosition", "notTurnCharacter") })
        {
            var position = Var(s, posVar);
            var who = Var(s, whoVar);
            var normal = Num(s, $"{who}BreakCount") < 1 && !(Var(s, "xSwitchMageSuperBuffActive") == "1" && who == "partner");
            if (normal)
            {
                sb.AppendLine($"@battleCharacterSprite show characterId:\"{{GetLookIdByPosition({posVar})}}\" state:\"wait\" position:\"{{{posVar}}}\" preserveStates:\"guard\"");
                continue;
            }
            var tracked = sc.AllyStates.FirstOrDefault(a => a.Who == who);
            if (tracked == null || string.IsNullOrEmpty(tracked.LookId) || string.IsNullOrEmpty(tracked.State))
                throw new InvalidOperationException($"{who} 的特殊姿态没有记录");
            sb.AppendLine($"@battleCharacterSprite show characterId:\"{tracked.LookId}\" state:\"{tracked.State}\" position:\"{position}\"");
        }

        return sb.ToString();
    }

    /// <summary>第三段：变量最终写回后再生成指令与手牌。</summary>
    public static string BuildChoiceScript(SaveData s)
    {
        var sb = new StringBuilder();
        // 指令：物品由 buildBattleItemChoices 生成，其余（魔法/默认行动）按存档里的按钮原样补回。
        sb.AppendLine("@clearChoice * wait:false");
        sb.AppendLine("@prewarmBattleHand");
        sb.AppendLine("@buildBattleItemChoices");
        foreach (var (summary, script) in s.BattleButtons)
        {
            var m = Regex.Match(script, @"selectCommand=(-?\d+)");
            if (!m.Success) continue;
            var value = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (value > 0) continue;
            var text = summary.Replace("\"", "\\\"");
            sb.AppendLine($"@choiceR \"{text}\" button:{BattleCommandButton} handler:BattleCommand set:selectCommand=000 value:\"{value.ToString(CultureInfo.InvariantCulture)}\" goto:Battle.PlayerAttack isTrans:false");
        }
        sb.AppendLine("@operateHand y:-360 hoverUp:55 extraHoverUp:200 hoverScale:1 defaultScale:0.9 entrance:true");
        return sb.ToString();
    }

    /// <summary>敌人生成后：位置/缩放与存档时不同则直接对齐（特殊战斗里 Y/Z/缩放可能各不相同）。</summary>
    public static void FixPositions(Sidecar sc, StringBuilder log)
    {
        var ctrls = Probes.Service<EnemyService>()?.enemyControllerList;
        foreach (var e in sc.Enemies)
        {
            var c = ctrls != null && e.Id < ctrls.Count ? ctrls[e.Id] : null;
            if (c is null) throw new InvalidOperationException($"敌人 {e.Id} 重建后没有控制器");
            var t = c.transform;
            var p = t.position;
            var d = Math.Abs(p.x - e.WorldX) + Math.Abs(p.y - e.WorldY) + Math.Abs(p.z - e.WorldZ)
                    + Math.Abs(t.localScale.x - e.ScaleX) + Math.Abs(t.localScale.y - e.ScaleY);
            if (d <= 0.01f) continue;
            log.AppendLine($"  enemy{e.Id}: realign ({p.x},{p.y},{p.z},s{t.localScale.x}) -> ({e.WorldX},{e.WorldY},{e.WorldZ},s{e.ScaleX})");
            t.position = new Vector3(e.WorldX, e.WorldY, e.WorldZ);
            t.localScale = new Vector3(e.ScaleX, e.ScaleY, t.localScale.z);
        }
    }

    /// <summary>
    /// 把存档时的行动数据（含运行时改过的数值）覆盖回按名字重建的行动，恢复使用记录、禁行动回合与隐藏预告，
    /// 然后刷新预告。返回预告文字与存档时不一致的敌人数。
    /// </summary>
    public static int RestoreEnemyRuntime(SaveData s, Sidecar sc, StringBuilder log)
    {
        var es = Probes.Service<EnemyService>() ?? throw new InvalidOperationException("EnemyService 不可用");
        var actions = Probes.Service<ActionService>() ?? throw new InvalidOperationException("ActionService 不可用");
        var next = es.enemyNextActionSetDataList;
        var texts = es.enemyNextActionTextList;

        foreach (var e in sc.Enemies)
        {
            if (Var(s, $"enemy{e.Id}Death") == "1" || string.IsNullOrEmpty(e.ActionJson)) continue;
            var set = next != null && e.Id < next.Count ? next[e.Id] : null;
            if (set is null || set.name != e.Action) throw new InvalidOperationException($"敌人 {e.Id} 的行动 {e.Action} 未能重建");
            JsonUtility.FromJsonOverwrite(e.ActionJson, set);
            RestoreProps(set, e.ActionProps);
            var arr = actions.CreateActionDataArrayFromActionSet(set);
            es.SetEnemyNextActionInfo(e.Id, set, arr, texts != null && e.Id < texts.Count ? texts[e.Id] : null, "SaveAnywhereRestore");
        }

        es.ClearEnemyActionUsage();
        foreach (var u in sc.Usage)
            for (var i = 0; i < Math.Max(1, u.UsedCount); i++)
                es.MarkEnemyActionSetUsed(u.EnemyId, u.ActionSet, u.LastUsedTurn);
        var total = 0;
        foreach (var kv in es.enemyActionUsage) total += kv.Value.UsedCount;
        var expected = sc.Usage.Sum(u => Math.Max(1, u.UsedCount));
        foreach (var u in sc.Usage)
            if (!es.HasEnemyUsedActionSetInBattle(u.EnemyId, u.ActionSet) || es.GetEnemyActionSetLastUsedTurn(u.EnemyId, u.ActionSet) != u.LastUsedTurn)
                throw new InvalidOperationException($"行动使用记录恢复失败：enemy{u.EnemyId} {u.ActionSet}");
        if (total != expected) throw new InvalidOperationException($"行动使用次数恢复不一致（{total} / {expected}）");
        es.enemyNoActTurns.Clear();
        foreach (var kv in sc.NoActTurns) es.enemyNoActTurns[int.Parse(kv.Key, CultureInfo.InvariantCulture)] = kv.Value;
        es.hiddenEnemyNextActionDisplayIds.Clear();
        foreach (var id in sc.HiddenNextActionIds) es.hiddenEnemyNextActionDisplayIds.Add(id);

        es.RefreshAllEnemyNextActionDisplays();
        log.AppendLine($"[restore] actions/usage restored: usage={sc.Usage.Count} noAct={sc.NoActTurns.Count} hidden={sc.HiddenNextActionIds.Count}");
        return 0;
    }

    /// <summary>核对预告文字（刷新可能在下一帧生效，所以单独调用）。</summary>
    public static int CheckActionTexts(SaveData s, Sidecar sc, StringBuilder log)
    {
        var texts = Probes.Service<EnemyService>()?.enemyNextActionTextList;
        var mismatches = 0;
        foreach (var e in sc.Enemies)
        {
            if (Var(s, $"enemy{e.Id}Death") == "1") continue;
            var now = texts != null && e.Id < texts.Count && texts[e.Id] != null ? texts[e.Id].text : null;
            if ((now ?? "") == (e.ActionText ?? "")) continue;
            mismatches++;
            log.AppendLine($"  enemy{e.Id} next-action text differs: saved='{e.ActionText}' now='{now}'");
        }
        return mismatches;
    }
}
