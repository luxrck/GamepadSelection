using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Gui;
using Dalamud.Hooking;
using Dalamud.Logging;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace GamepadTweaks
{
    public class Member {
        public string Name = String.Empty;
        public uint ID;
        public uint JobID;
    }

    // https://github.com/SheepGoMeh/VisibilityPlugin/blob/master/Visibility/Enumerations.cs
    public enum InvisibleFlags : int
    {
        Model = 1 << 1,
        Nameplate = 1 << 11,
        Invisible =  Model | Nameplate,
    }

    public enum GamepadActionManagerState : int
    {
        Start = 0,
        EnteringGamepadSelection = 1,
        InGamepadSelection = 2,
        // ExitingGamepadSelection = 3,
        // GsActionInQueue = 4,
        // ExecuteAction = 5,
        ActionExecuted = 3,
    }

    public class GamepadActionManager : IDisposable
    {
        public static Dictionary<string, ushort> ButtonMap = new Dictionary<string, ushort> {
            {"up", (ushort)GamepadButtons.DpadUp},
            {"down", (ushort)GamepadButtons.DpadDown},
            {"left", (ushort)GamepadButtons.DpadLeft},
            {"right", (ushort)GamepadButtons.DpadRight},

            // Xobx
            {"y", (ushort)GamepadButtons.North},
            {"a", (ushort)GamepadButtons.South},
            {"x", (ushort)GamepadButtons.West},
            {"b", (ushort)GamepadButtons.East},

            // Direction
            {"n", (ushort)GamepadButtons.North},
            {"s", (ushort)GamepadButtons.South},
            {"w", (ushort)GamepadButtons.West},
            {"e", (ushort)GamepadButtons.East},
        };

        public GamepadActionManagerState state = GamepadActionManagerState.Start;
        // public bool inGamepadSelectionMode = false;
        // public Channel<GameAction> LastAction = Channel.CreateBounded<GameAction>(new BoundedChannelOptions(1) {
        //     FullMode = BoundedChannelFullMode.DropOldest
        // });
        public DateTime LastTime;
        public uint LastActionID;
        public GameAction LastAction = new GameAction();
        public bool InComboState => Marshal.PtrToStructure<float>(this.comboTimerPtr) > 0f;
        public uint LastComboAction => (uint)Marshal.ReadInt32(this.lastComboActionPtr);

        // private Channel<(uint, ActionStatus, bool)> executedActions = Channel.CreateUnbounded<(uint, ActionStatus, bool)>();
        private Channel<(GameAction, bool)> executedActions = Channel.CreateUnbounded<(GameAction, bool)>();
        private Channel<GameAction> pendingActions = Channel.CreateUnbounded<GameAction>();

        // private Dictionary<uint, uint> savedActionIconMap = new Dictionary<uint, uint>();

        private ushort savedButtonsPressed;
        private GameAction LastGsAction;
        private SemaphoreSlim actionLock = new SemaphoreSlim(1, 1);

        private IntPtr comboTimerPtr;
        private IntPtr lastComboActionPtr;

        private Actions Actions = Plugin.Actions;

        private Configuration Config = Plugin.Config;
        private SigScanner SigScanner = Plugin.SigScanner;
        private GamepadState GamepadState = Plugin.GamepadState;
        private PartyList PartyList = Plugin.PartyList;
        private GameGui GameGui = Plugin.GameGui;
        private ClientState ClientState = Plugin.ClientState;
        private Condition Condition = Plugin.Condition;
        private ObjectTable Objects = Plugin.Objects;
        private TargetManager TargetManager = Plugin.TargetManager;

        public GamepadActionManager()
        {
            this.LastGsAction = new GameAction();
            // this.lastTime = DateTime.Now;

            // var useActionLocation = SigScanner.ScanText("E8 ?? ?? ?? ?? 3C 01 0F 85 ?? ?? ?? ?? EB 46");
            // this.UseActionLocationHook = new Hook<UseActionLocationDelegate>(useActionLocation, this.UseActionLocationDetour);

            var useAction = SigScanner.ScanText("E8 ?? ?? ?? ?? EB 64 B1 01");
            this.UseActionHook = new Hook<UseActionDelegate>(useAction, this.UseActionDetour);

            var getIcon = SigScanner.ScanText("E8 ?? ?? ?? ?? 8B F8 3B DF");
            this.GetIconHook = new Hook<GetIconDelegate>(getIcon, this.GetIconDetour);

            var isIconReplaceable = SigScanner.ScanText("81 F9 ?? ?? ?? ?? 7F 35");
            this.IsIconReplaceableHook = new Hook<IsIconReplaceableDelegate>(isIconReplaceable, IsIconReplaceableDetour);

            // this.comboTimerPtr = SigScanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? 80 7E 21 00", 0x178) - 4;
            this.comboTimerPtr = SigScanner.GetStaticAddressFromSig("F3 0F 11 05 ?? ?? ?? ?? F3 0F 10 45 ?? E8");
            this.lastComboActionPtr = this.comboTimerPtr + 0x04;

            Enable();
        }

        public void UpdateFramework(Framework framework) {
            if (!Plugin.Ready || Plugin.Player is null) return;

            var me = Plugin.Player;

            // reset combo state if out of combat.
            // if (!me.StatusFlags.HasFlag(StatusFlags.InCombat) && !me.StatusFlags.HasFlag(StatusFlags.WeaponOut)) {
            //     Plugin.Config.ResetComboState(0);
            // }
            if (!Condition[ConditionFlag.BoundByDuty] &&
                !Condition[ConditionFlag.InCombat] &&
                !Condition[ConditionFlag.InDeepDungeon] &&
                !Condition[ConditionFlag.InDuelingArea])
            {
                Config.ResetComboState(0);
            }


            // macro
            Task.Run(async () => {
                var suc = this.pendingActions.Reader.TryRead(out var m);

                // 顺序执行命令
                await this.actionLock.WaitAsync();

                if (suc && m is not null) {
                    if (!Plugin.Ready) goto TaskRet;

                    var ret = false;
                    var act = Actions.ActionType(m.ID);
                    var adj = Actions.AdjustedActionID(m.ID);

                    var retry = Config.actionRetry;

                TaskRetry:
                    if (retry < 0) goto TaskRet;
                    retry -= 1;

                    var savedLastTime = this.LastTime;

                    if (Config.actionSchedule != "none")
                        await ActionDelay(m);

                    //Check Plugin status after delay.
                    if (!Plugin.Ready) goto TaskRet;

                    // PluginLog.Debug($"[ExecuteAction Async] {m.ID} Targeted: {m.Targeted} {m.DelayTo} {DateTime.Now} {(m.DelayTo - DateTime.Now).TotalMilliseconds}");
                    if (savedLastTime != this.LastTime) goto TaskRetry;

                    if (m.Status == ActionStatus.Delay) {
                        this.SendAction(m);
                    } else if (m.Status == ActionStatus.LocalDelay) {
                        ret = this.ExecuteAction(m);
                    }

                    // PluginLog.Debug($"[UseActionAsync] wait? {delay}ms {m.ID} {m.TargetID} {m.Status} ret: {ret}, lastID: {this.LastActionID}, lastTime: {this.LastTime}");

                    if (ret) {
                        this.LastTime = DateTime.Now;
                        this.LastActionID = m.ID;
                        this.LastAction = m;
                        // this.LastAction.Writer.TryWrite(m);
                    }

                    // status == NotSatisfied -> return false;
                    if (m.Type == ActionType.Spell) {
                        m.Status = ActionStatus.Ready;
                        m.LastTime = DateTime.Now;
                        this.executedActions.Writer.TryWrite((m, ret));
                    }
                }

            TaskRet:
                // releae lock?
                this.actionLock.Release();
            });

            // combo
            Task.Run(async () => {
                if (!Plugin.Ready) return;

                try {
                    if (this.executedActions.Reader.TryRead(out (GameAction Action, bool Result) a)) {
                        // PluginLog.Debug($"[UpdateComboStateAsync] update action: {a.Action.ID}, iscombo?: {Config.IsComboAction(a.Action.ID)} {Actions.Equals(25800, 7429)}");
                        if (Config.IsComboAction(a.Action.ID)) {
                            await Config.UpdateComboState(a.Action, a.Result);
                        }
                    } else {
                        await Task.Delay(50);
                        await Config.UpdateComboState(new GameAction() {ID = 0, Finished = true});
                    }
                } catch(Exception e) {
                    PluginLog.Error($"Exception: {e}");
                }
            });
            // Update combo action icon.
            // if (Config.IsComboAction(action) && Config.UpdateComboState(action, status)) {}
            // else if (Config.IsComboAction(adjusted) && Config.UpdateComboState(adjusted, status)) {}
        }

        // private Hook<UseActionLocationDelegate> UseActionLocationHook;
        // private delegate bool UseActionLocationDelegate(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, IntPtr vectorLocation, uint param);
        // private bool UseActionLocationDetour(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, IntPtr vectorLocation, uint param)
        // {
        //     return UseActionLocationHook.Original(actionManager, actionType, actionID, targetedActorID, vectorLocation, param);
        // }

        private Hook<UseActionDelegate> UseActionHook;
        private delegate bool UseActionDelegate(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8);
        private bool UseActionDetour(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8)
        {
            if (!Plugin.Ready || Plugin.Player is null) return false;

            bool ret = false;

            var me = Plugin.Player;

            // original slot action which combo-chains place at. (ComboGroup)
            var originalActionID = actionID;

            var adjustedID = Actions.AdjustedActionID(actionID);
            var status = Actions.ActionStatus(adjustedID, (ActionType)actionType, targetedActorID);

            // update real base actionID using adjustedID
            actionID = Actions.BaseActionID(adjustedID);

            var a = new GameAction() {
                ID = adjustedID,
                Status = status,
                Type = (ActionType)actionType,
                TargetID = targetedActorID,
                UseType = useType,
                param = param,
                pvp = pvp,
                a8 = a8
            };

            var pmap = this.GetSortedPartyMembers();
            var target = TargetManager.Target;
            var softTarget = TargetManager.SoftTarget;
            bool inParty = pmap.Count > 1 || Config.alwaysInParty;  // <---

            string actionName = Actions.Name(adjustedID);

            // PluginLog.Debug($"[UseAction]: {actionID} {adjustedID} {actionName} {targetedActorID} {softTarget ?? target} status: {status}, state: {this.state} {Actions.RecastTimeRemain(adjustedID)}");
            // PluginLog.Debug($"[UseAction][Args] am: {actionManager}, type: {actionType}, id: {actionID}, target: {targetedActorID}, param: {param}, useType: {useType}, pvp: {pvp}, a8: {a8}");

            // if (interval.TotalMilliseconds < 30) {
            //     // Task.Run(async () => {
            //     //     PluginLog.Debug("[UseAction] wait 20ms");
            //     //     await Task.Delay(20);
            //     //     this.UseAction(actionType, actionID, targetedActorID, param, useType, pvp, a8);
            //     // });
            //     return false;
            // }

            // PROCEDURE
            //  1. !Spell && !Ability -> exec
            //  2. AcCom : UseAction[UseType == 2] -> LocalDelay -> am->UseAction[UseType == 2] -> exec
            //  3. GtAct : UseAction[UseType == 0] -> Delay -> UseAction[UseType == 2] -> exec
            //  4. GsAct : UseAction[UseType == *] : Entering Gs -> UseAction[UseType == *] : In Gs -> exec
            //  5. Norma : UseAction[UseType == 0/1] -> exec
            // PRECEDURE END

            // Only handle Spell && Ability(?)
            if (a.Type != ActionType.Spell && a.Type != ActionType.Ability) {
                PluginLog.Debug($"[UseAction] Not a spell. {adjustedID} {actionType}");
                return UseActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, 1, pvp, a8);
            }

            // UseType == 2: macro
            // handle /ac <xx>
            if (a.UseType == 2) {
                // GtAction in macro should execute immediately!
                if (Config.IsGtoffAction(a.ID)) {
                    ret = UseActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                    PluginLog.Debug($"[UseAction][{this.state}] ret: {ret}, {status}, iscasting: {me.IsCasting}, casttime: {a.AdjustedCastTimeTotalMilliseconds}, action: {Plugin.Actions.Name(adjustedID)}, ID: {adjustedID}, UseType: {a.UseType}");
                    goto MainRet;
                }

                if (Config.actionSchedule != "none") {
                    a.Status = ActionStatus.LocalDelay;
                    ret = SendPendingAction(a);

                    PluginLog.Debug($"[UseAction] Macro local delay? {ret}");
                    return ret;
                }

                ret = UseActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                goto MainRet;
            }

        MainLoop:
            switch (this.state)
            {
                // handle ActionStatus.Ready
                case GamepadActionManagerState.Start:
                    if (Config.IsGtoffAction(actionID) || Config.IsGtoffAction(adjustedID)) {
                        // this.pendingActions.Writer.TryWrite(new GameAction() {
                        //     ActionManager = actionManager,
                        //     ID = adjustedID,
                        //     TargetID = targetedActorID,
                        //     Status = ActionStatus.Delay,
                        //     // DelayTo = this.CalculateDelay(adjustedID),
                        // });

                        // am->UseActionLocation有时会出现内存访问错误, 导致游戏崩溃
                        // 因此转而采用宏来实现
                        a.Status = ActionStatus.Delay;
                        SendPendingAction(a);
                        PluginLog.Debug($"[UseAction] GtoffAction delay.");
                        return false;
                    } else if (Config.IsGsAction(actionID) || Config.IsGsAction(adjustedID)) {
                        // if (status == ActionStatus.Ready && pmap.Any(x => x.ID == targetedActorID)) {
                        // 1. targetID不是队友, 进入GSM
                        // 2. GSM选中队友, 执行.
                        // 3. 如果出现Locking, Pending等状态, 系统会在Ready的时候再次执行UseAction
                        // 4. 再次执行, 又回到这里, 需要判断到底处于何种状态(是第一步, 还是第四步)
                        //    此时Action状态为Ready, 并且targetID应该也已经变成了队友?, 所以可能不需要特殊判断
                        if (a.IsTargetingPartyMember) {
                            ret = UseActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
                            this.state = GamepadActionManagerState.ActionExecuted;
                        } else {
                            this.state = GamepadActionManagerState.EnteringGamepadSelection;
                        }
                    } else {
                        // Auto-targeting only for normal actions.
                        // default targetID == 3758096384u == 0xe0000000
                        // 只有在没选中队友时才能选中最近的敌人
                        if (!a.IsTargetingPartyMember && (a.Info?.CanTargetHostile ?? false)) {
                            if (Config.targeting == "auto") {
                                target = softTarget ?? target ?? NearestTarget(a);
                            } else if (Config.targeting == "nearest") {
                                target = NearestTarget(a);
                            } else if (Config.targeting == "least-enmity") {
                                // 有可能选到友方战斗NPC
                                target = NearestTargetWithLeastEnmity(a);
                            }
                            if (target is not null)
                                targetedActorID = target.ObjectId;
                        }

                        // status == Ready: 可能是用户触发, 也可能是游戏触发(Pending -> Ready).
                        // 如果是游戏触发, 并且是GsAction, 那么除非target为空, 否则不应该更改target.
                        // 其它情况应该都可以更改.
                        // if (status != ActionStatus.Ready || targetedActorID == 3758096384u) {
                        // Cast normally if:
                        //  1. We are not in party
                        //  2. We already target a party member
                        //  3. Action not in monitor (any action could be a combo action)

                        ret = UseActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);

                        if (!ret && Config.targeting == "least-enmity") {
                            ret = UseActionHook.Original(actionManager, actionType, adjustedID, NearestTarget(a)?.ObjectId ?? Configuration.DefaultInvalidGameObjectID, param, useType, pvp, a8);
                        }

                        this.state = GamepadActionManagerState.ActionExecuted;
                    }
                    goto MainLoop;
                case GamepadActionManagerState.EnteringGamepadSelection:
                    this.LastGsAction = a;

                    // 未满足发动条件?
                    // 未满足发动条件的GsAction直接跳过, 不进行目标选择.
                    if (status != ActionStatus.Ready && status != ActionStatus.Locking && status != ActionStatus.Pending)  {
                        ret = UseActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
                        this.state = GamepadActionManagerState.ActionExecuted;
                        goto MainLoop;
                    }

                    this.state = GamepadActionManagerState.InGamepadSelection;
                    break;
                case GamepadActionManagerState.InGamepadSelection:
                    var o = this.LastGsAction;

                    adjustedID = Actions.AdjustedActionID(o.ID);

                    // update real base actionID using adjustedID
                    actionID = Actions.BaseActionID(adjustedID);

                    try {
                        unsafe {
                            var ginput = (GamepadInput*)GamepadState.GamepadInputAddress;

                            // Only use [up down left right y a x b]
                            // 目前GSM只在lt/rt按下, 即激活十字热键栏预备施放技能时可用.
                            ushort buttons = (ushort)(ginput->ButtonsPressed & 0xff);

                            // 1 0 1 0 Pre
                            // 1 1 0 0 Now
                            // 0 1 0 0 Strategy 1 : Config.ignoreRepeatedButtons == true
                            // 1 1 0 0 Strategy 2
                            // 如果上一次状态和本次相同, 不能判断到底是哪个按键触发了Action.
                            // 多个按键同时按下, 选择优先级高的按键
                            buttons = (ushort)((buttons ^ this.savedButtonsPressed) & buttons);

                            if (buttons == 0)
                                buttons = this.savedButtonsPressed;

                            var order = Config.SelectOrder(a.ID).ToLower().Trim().Split(" ").Where((a) => a != "").ToList();
                            var gsTargetedActorIndex = order.FindIndex((b) => ButtonMap.ContainsKey(b) ? (ButtonMap[b] & buttons) > 0 : false);

                            var gsRealTargetedActorIndex = gsTargetedActorIndex;
                            if (pmap.Count > 0) {
                                gsRealTargetedActorIndex = gsTargetedActorIndex == -1 ? 0 : (gsTargetedActorIndex >= pmap.Count - 1 ? pmap.Count - 1 : gsTargetedActorIndex);

                                var gsTargetedActorID = targetedActorID;
                                if (!a.IsTargetingPartyMember) {   // Disable GSM if we already selected a member.
                                    gsTargetedActorID = pmap[gsRealTargetedActorIndex].ID;
                                }
                                this.LastGsAction.TargetID = gsTargetedActorID;
                            }

                            Func<int, string> _S = (x) => Convert.ToString(x, 2).PadLeft(8, '0');
                            PluginLog.Debug($"[UseAction][{this.state}], PartyID: {PartyList.PartyId}, Length: {pmap.Count}, originalIndex: {gsTargetedActorIndex}, index: {gsRealTargetedActorIndex}, btn: {_S(buttons)}, savedBtn: {_S(this.savedButtonsPressed)}, origBtn: {_S(ginput->ButtonsPressed & 0xff)}, reptBtn: {_S((ginput->ButtonsRepeat & 0xff))}");
                        }
                    } catch(Exception e) {
                        PluginLog.Error($"Exception: {e}");
                    }

                    targetedActorID = o.TargetID;

                    status = Actions.ActionStatus(o.ID, o.Type, targetedActorID);

                    ret = UseActionHook.Original(actionManager, (uint)o.Type, o.ID, o.TargetID, o.param, o.UseType, o.pvp, o.a8);
                    PluginLog.Debug($"[UseAction][{this.state}] ret: {ret}, ActionID: {actionID}, AdjID: {adjustedID}, Orig: {originalActionID}, ActionType: {actionType}, TargetID: {targetedActorID}, status: {status}");

                    this.savedButtonsPressed = 0;

                    a = o;
                    this.state = GamepadActionManagerState.ActionExecuted;
                    goto MainLoop;
                case GamepadActionManagerState.ActionExecuted:
                    unsafe {
                        var ginput = (GamepadInput*)GamepadState.GamepadInputAddress;
                        this.savedButtonsPressed = (ushort)(ginput->ButtonsPressed & 0xff);

                        PluginLog.Debug($"[UseAction][{this.state}] ret: {ret}, {status}, iscasting: {me.IsCasting}, casttime: {a.AdjustedCastTimeTotalMilliseconds}, action: {Plugin.Actions.Name(adjustedID)}, ID: {adjustedID}, UseType: {a.UseType}");
                    }

                    this.state = GamepadActionManagerState.Start;
                    break;
            }

        MainRet:
            // PluginLog.Debug($"[cast] id: {a.ID}, casttime: {a.CastTimeTotalMilliseconds}, iscasting?:{me.IsCasting}, me.casttime: {me.CurrentCastTime}, {me.TotalCastTime}");

            // ActionExecuted -> Start
            // 如果Action没有被执行完成, 则不处理(例如GsAction的第一阶段)
            if (state != GamepadActionManagerState.Start) return false;

            if (ret && a.Status == ActionStatus.Ready) {
                this.LastTime = DateTime.Now;
                this.LastActionID = adjustedID;
                this.LastAction = a;
                // this.LastAction.Writer.TryWrite(a);
            }

            // status == NotSatisfied -> return false;
            if (a.Type == ActionType.Spell) {
                a.LastTime = DateTime.Now;
                if (a.CanCastImmediatly) a.Finished = true;
                bool suc = this.executedActions.Writer.TryWrite((a, ret));
                if (!suc) {
                    PluginLog.Debug($"[UseAction] executedActions write failed.");
                }
            }

            return ret;
        }

        private Hook<IsIconReplaceableDelegate> IsIconReplaceableHook;
        private delegate ulong IsIconReplaceableDelegate(uint actionID);
        private ulong IsIconReplaceableDetour(uint actionID) => 1;

        private Hook<GetIconDelegate> GetIconHook;
        private delegate uint GetIconDelegate(IntPtr actionManager, uint actionID);

        // actionID: 位于技能栏上的原本的Action的ID(不是replace icon之后的).
        private uint GetIconDetour(IntPtr actionManager, uint actionID)
        {
            var lastComboAction = Marshal.ReadInt32(this.lastComboActionPtr);
            var comboTimer = Marshal.PtrToStructure<float>(this.comboTimerPtr);

            // 小奥秘卡 -> 出王冠卡 : 出王冠卡
            var comboActionID = Config.CurrentComboAction(actionID, (uint)lastComboAction, comboTimer);
            return GetIconHook.Original(actionManager, comboActionID);
        }

        public List<Member> GetSortedPartyMembers(string order = "thmr")
        {
            try {
                unsafe {
                    var addonPartyList = (AddonPartyList*)GameGui.GetAddonByName("_PartyList", 1);
                    if (addonPartyList is null)
                        return this.GetDefaultSortedPartyMembers(order);

                    var pmap = new Dictionary<string, PartyMember>();

                    foreach (PartyMember p in PartyList) {
                        var name = p.Name.ToString();
                        pmap.Add(name, p);
                    }

                    uint selfID = 0;
                    uint selfJobID = 0;
                    string selfName = "";

                    if (ClientState.LocalPlayer is not null) {
                        selfID = ClientState.LocalPlayer.ObjectId;
                        selfJobID = ClientState.LocalPlayer.ClassJob.Id;
                        selfName = ClientState.LocalPlayer.Name.ToString();
                    }

                    var me = new List<Member>() {
                        new Member() {Name = selfName, ID = selfID, JobID = selfJobID}
                    };

                    for (var i = 1; i < addonPartyList->MemberCount; i++) {
                        // 90级 玩家2
                        // Lv90 Player Two
                        // 90级 [皇冠]玩家3
                        // 过场动画中
                        var name = addonPartyList->PartyMember[i].Name->NodeText.ToString();
                        // remove non-visible special characters.
                        name = Regex.Replace(name, @"[\x01-\x1f,\x7f]", "");
                        name = name.Split(" ", 2).Last().Trim();
                        me.Add(new Member() {
                            Name = name,
                            ID = pmap.ContainsKey(name) ? pmap[name].ObjectId : 0,
                            JobID = pmap.ContainsKey(name) ? pmap[name].ClassJob.Id : 0,
                        });
                    }

                    var objectMap = new Dictionary<string, uint>();

                    if (addonPartyList->TrustCount > 0 || addonPartyList->ChocoboCount > 0) {
                        foreach (var obj in Objects) {
                            var name = obj.Name.ToString().Trim();

                            if (String.IsNullOrEmpty(name)) continue;

                            var id = obj.ObjectId;
                            objectMap.TryAdd(name, id);
                        }
                    }

                    for (var i = 0; i < addonPartyList->TrustCount; i++) {
                        var name = addonPartyList->TrustMember[i].Name->NodeText.ToString()
                                                                                .Split(" ", 2)
                                                                                .Last()
                                                                                .Trim();
                        me.Add(new Member() {
                            Name = name,
                            ID = objectMap.ContainsKey(name) ? objectMap[name] : 0,
                            JobID = 0,  // ignored
                        });
                    }

                    if (addonPartyList->ChocoboCount > 0) {
                        var chocoboName = addonPartyList->Chocobo.Name->NodeText.ToString()
                                                                                .Split(" ", 2)
                                                                                .Last()
                                                                                .Trim();

                        if (!String.IsNullOrEmpty(chocoboName) && objectMap.ContainsKey(chocoboName)) {
                            me.Add(new Member() {
                                Name = chocoboName,
                                ID = objectMap[chocoboName],
                                JobID = 0,
                            });
                        }
                    }

                    return me;
                }
            } catch (Exception e) {
                PluginLog.Error($"Exception: {e}");

                return new List<Member>();
            }
        }

        // sort order eg: [s] t h m r. always place oneself in the 1st place.
        // ranged dps sort order: r = [pr mr]
        // [H] WHM: 24  SCH: 28 AST: 33 SGE: 40
        // [T] PLD: 19  WAR: 21 DRK: 32 GNB: 37
        // [M] MNK: 20  DRG: 22 NIN: 30 SAM: 34 RPR: 39
        // [PR] BRD: 23 MCH: 31 DNC: 38
        // [MR] BLM: 25 SMN: 27 RDM: 35
        // [DEPRECATED]: We could get the right order from PartyList UI in game.
        private List<Member> GetDefaultSortedPartyMembers(string order = "thmr")
        {
            uint selfID = 0;
            uint selfJobID = 0;
            string selfName = "";

            if (ClientState.LocalPlayer is not null) {
                selfID = ClientState.LocalPlayer.ObjectId;
                selfJobID = ClientState.LocalPlayer.ClassJob.Id;
                selfName = ClientState.LocalPlayer.Name.ToString();
            }

            var me = new List<Member>() {
                new Member() {Name = selfName, ID = selfID, JobID = selfJobID}
            };

            var t = new List<Member>();
            var h = new List<Member>();
            var m = new List<Member>();
            var pr = new List<Member>();
            var mr = new List<Member>();

            foreach (PartyMember p in PartyList) {
                var pid = p.ObjectId;
                if (pid == selfID) continue;

                var jobID = p.ClassJob.Id;
                var name = p.Name.ToString();

                var member = new Member() {
                    Name = name, ID = pid, JobID = jobID
                };

                switch (jobID)
                {
                    case 24:
                    case 33:
                    case 28:
                    case 40:
                        h.Add(member);
                        break;
                    case 19:
                    case 21:
                    case 32:
                    case 37:
                        t.Add(member);
                        break;
                    case 20:
                    case 22:
                    case 30:
                    case 34:
                    case 39:
                        m.Add(member);
                        break;
                    case 23:
                    case 31:
                    case 38:
                        pr.Add(member);
                        break;
                    case 25:
                    case 27:
                    case 35:
                        mr.Add(member);
                        break;
                    default:
                        break;
                }
            }

            t = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            h = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            m = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            pr = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            mr = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();

            foreach(char a in order) {
                switch (a)
                {
                    case 't':
                        me.AddRange(t); break;
                    case 'h':
                        me.AddRange(h); break;
                    case 'm':
                        me.AddRange(m); break;
                    case 'r':
                        me.AddRange(pr); me.AddRange(mr); break;
                    default:
                        break;
                }
            }

            return me.ToList();
        }

        private async Task<int> ActionDelay(GameAction a)
        {
            if (a.ID == LastAction.ID && Config.IsGtoffAction(a.ID) && !a.HasTarget) {
                return 0;
            }

            var lgroup = Actions.RecastGroup(this.LastActionID);
            var cgroup = Actions.RecastGroup(a.ID);

            // m1 + m2
            // m1 + a1
            // m1 + a1 + m2
            // a1 + a2
            var remain = 0;

            if (Config.actionSchedule == "non-preemptive") {
                if (LastAction.CanCastImmediatly) {
                    remain = (int)(DateTime.Now - this.LastTime).TotalMilliseconds;
                    remain = Math.Max(Configuration.GlobalCoolingDown.AnimationWindow - remain, 0);
                } else {
                    await LastAction.Wait();
                    // remain = 100;
                }
            } else if (Config.actionSchedule == "preemptive") {
                remain = Math.Min(LastAction.AdjustedReastTimeTotalMilliseconds, Actions.Cooldown(0, adjusted: true));
            }

            await Task.Delay(remain);

            PluginLog.Debug($"[DelayTime] id: {a.ID}, target: {a.TargetID} {a.HasTarget} {Config.IsGtoffAction(a.ID)}, lt: {LastAction.TargetID}, lgroup: {lgroup}, cgroup: {cgroup}, lastID: {LastAction.ID}, lastTime: {LastTime.Second}:{LastTime.Millisecond}, remain: {remain}ms");
            return remain;
        }

        private bool SendAction(GameAction a)
        {
            try {
                Plugin.Send($"/merror off");
                if (TargetManager.SoftTarget is not null) {
                    Plugin.Send($"/ac {a.Name} <t>");
                } else if (TargetManager.FocusTarget is not null) {
                    Plugin.Send($"/ac {a.Name} <f>");
                } else if (a.HasTarget) {
                    var s = a.IsTargetingSelf ? "<me>" : "<t>";
                    Plugin.Send($"/ac {a.Name} {s}");
                } else {
                    if (Config.IsGtoffAction(a.ID)) {
                        Plugin.Send($"/ac {a.Name}");
                    }
                    Plugin.Send($"/ac {a.Name}");
                }
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                return false;
            }
            return true;
        }

        private bool SendPendingAction(GameAction a)
        {
            return this.pendingActions.Writer.TryWrite(a);
        }

        public bool ExecuteAction(GameAction a, bool canTargetSelf = false)
        {
            bool ret = false;
            try {
                this.UseActionHook.Disable();
                unsafe {
                    var am = ActionManager.Instance();
                    if (am is not null) {
                        ret = UseActionHook.Original((IntPtr)am, (uint)a.Type, a.ID, a.TargetID, a.param, a.UseType, a.pvp, a.a8);
                    }
                }
                this.UseActionHook.Enable();
            } catch(Exception e) {
                PluginLog.Fatal($"Fatal: {e}");
            }
            return ret;
        }

        // Could choose wrong target cause some game objects are invisible.
        private GameObject? NearestTarget(GameAction a)
        {
            Plugin.Send("/tenemy");
            return TargetManager.Target;
        }

        private GameObject? NearestTargetWithLeastEnmity(GameAction a, BattleNpcSubKind type = BattleNpcSubKind.Enemy)
        {
            var me = Plugin.Player;

            if (me is null) return null;

            var actionRange = a.Info?.Range ?? 0;

            if (actionRange == 0) return NearestTarget(a);

            GameObject? o = null;
            var md = Double.PositiveInfinity;
            foreach (var x in Objects) {
                if (x.ObjectId == me.ObjectId) continue;
                unsafe {
                    var g = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)x.Address;
                    // TODO: 不出现在小队列表里的战斗npc的SubKind会被识别成Enemy, 目前无法区分
                    if (!g->GetIsTargetable() ||
                        !x.IsValid() ||
                        String.IsNullOrEmpty(x.Name.ToString().Trim()) ||
                        x.ObjectKind != ObjectKind.BattleNpc ||
                        x.SubKind != (byte)type ||
                        x.TargetObjectId == me.ObjectId)    // <---
                        continue;
                    if (((BattleNpc)x).CurrentHp <= 0)
                        continue;
                    var d = Math.Sqrt(Math.Pow(x.Position.X - me.Position.X, 2) + Math.Pow(x.Position.Y - me.Position.Y, 2) + Math.Pow(x.Position.Z - me.Position.Z, 2));
                    // PluginLog.Debug($"{x.IsValid()} {x.ObjectKind} {g->ObjectID} {x.Name.ToString()} {x.TargetObjectId} {me.ObjectId} {d}");
                    if ((int)d > actionRange) continue;
                    if (d < md) {
                        md = d;
                        o = x;
                    }
                }
            }

            if (o is not null) {
                TargetManager.SetTarget(o);
                PluginLog.Debug($"Nearest target with least enmity: {o.ObjectId} {o.Name.ToString()}, SubKind: {type}");
                return TargetManager.Target;
            } else {
                return NearestTarget(a);
            }
        }

        private double DistanceToLocation(Vector3 pos)
        {
            if (!Plugin.Ready || Plugin.Player is null) return 0;
            var me = Plugin.Player;
            var d = Math.Sqrt(Math.Pow(pos.X - me.Position.X, 2) + Math.Pow(pos.Y - me.Position.Y, 2) + Math.Pow(pos.Z - me.Position.Z, 2));
            return d;
        }

        public void Enable()
        {
            this.UseActionHook.Enable();
            this.IsIconReplaceableHook.Enable();
            this.GetIconHook.Enable();
            // make sure we don't have handler on this event.
            // it's ok even not registed.
            Plugin.Framework.Update -= this.UpdateFramework;
            Plugin.Framework.Update += this.UpdateFramework;
        }

        public void Disable()
        {
            this.UseActionHook.Disable();
            this.IsIconReplaceableHook.Disable();
            this.GetIconHook.Disable();
            Plugin.Framework.Update -= this.UpdateFramework;
        }

        public void Dispose()
        {
            Disable();
            this.UseActionHook.Dispose();
            this.IsIconReplaceableHook.Dispose();
            this.GetIconHook.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}