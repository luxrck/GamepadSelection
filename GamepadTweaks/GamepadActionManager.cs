using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.GamePad;
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
    public class GameAction {
        public IntPtr ActionManager = IntPtr.Zero;
        public ActionType Type = ActionType.Spell;
        public uint ID;
        public uint TargetID = Configuration.DefaultInvalidGameObjectID;
        public uint param = 0;

        // 0 : normal
        // 2 : macro
        public uint UseType = 0;

        public uint pvp = 0;
        public IntPtr a8 = IntPtr.Zero;
        public ActionStatus Status = ActionStatus.Ready;
        public DateTime DelayTo = DateTime.Now;

        public bool Targeted => TargetID != Configuration.DefaultInvalidGameObjectID;

        public bool IsValid => this.ID > 0;
    }

    // public class GameAction {
    //     public uint ID;
    //     public bool Targeted;
    //     public ActionStatus Status;
    //     public DateTime DelayTo = DateTime.Now;
    // }

    public class Member {
        public string Name = String.Empty;
        public uint ID;
        public uint JobID;
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

    class GamepadActionManager : IDisposable
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
        public bool inGamepadSelectionMode = false;

        private Channel<(uint, ActionStatus)> executedActions = Channel.CreateUnbounded<(uint, ActionStatus)>();
        private Channel<GameAction> pendingActions = Channel.CreateUnbounded<GameAction>();

        private ushort savedButtonsPressed;
        private GameAction savedAction;
        private DateTime lastTime;
        private uint lastActionID;
        private SemaphoreSlim actionLock = new SemaphoreSlim(1, 1);

        private IntPtr comboTimerPtr;
        private IntPtr lastComboActionPtr;

        private ActionMap Actions = new ActionMap();

        private Configuration Config = Plugin.Config;
        private SigScanner SigScanner = Plugin.SigScanner;
        private GamepadState GamepadState = Plugin.GamepadState;
        private PartyList PartyList = Plugin.PartyList;
        private GameGui GameGui = Plugin.GameGui;
        private ClientState ClientState = Plugin.ClientState;
        private ObjectTable Objects = Plugin.Objects;
        private TargetManager TargetManager = Plugin.TargetManager;

        public GamepadActionManager()
        {
            this.savedAction = new GameAction();
            // this.lastTime = DateTime.Now;

            var useAction = SigScanner.ScanText("E8 ?? ?? ?? ?? EB 64 B1 01");
            this.useActionHook = new Hook<UseActionDelegate>(useAction, this.UseActionDetour);

            var getIcon = SigScanner.ScanText("E8 ?? ?? ?? ?? 8B F8 3B DF");
            this.getIconHook = new Hook<GetIconDelegate>(getIcon, this.GetIconDetour);

            var isIconReplaceable = SigScanner.ScanText("81 F9 ?? ?? ?? ?? 7F 35");
            this.isIconReplaceableHook = new Hook<IsIconReplaceableDelegate>(isIconReplaceable, IsIconReplaceableDetour);

            // this.comboTimerPtr = SigScanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? 80 7E 21 00", 0x178) - 4;
            this.comboTimerPtr = SigScanner.GetStaticAddressFromSig("F3 0F 11 05 ?? ?? ?? ?? F3 0F 10 45 ?? E8");
            this.lastComboActionPtr = this.comboTimerPtr + 0x04;

            this.useActionHook.Enable();
            this.isIconReplaceableHook.Enable();
            this.getIconHook.Enable();
        }

        public void UpdateFramework(Framework framework) {
            if (!Plugin.Ready) return;

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

                    // PluginLog.Debug($"[ExecuteAction Async] {m.ID} Targeted: {m.Targeted} {m.DelayTo} {DateTime.Now} {(m.DelayTo - DateTime.Now).TotalMilliseconds}");

                TaskWait:
                    var savedLastTime = this.lastTime;
                    var delay = (int)CalculateDelay(m.ID).TotalMilliseconds;
                    delay = Math.Min(delay, 2600);  // 至多等待一个gcd
                    await Task.Delay(delay);

                    //Check Plugin status after delay.
                    if (!Plugin.Ready) goto TaskRet;

                    if (savedLastTime != this.lastTime) goto TaskWait;

                    if (m.Status == ActionStatus.Delay) {
                        if (m.Targeted) {
                            this.UseActionToTarget(m.Type, m.ID);
                        } else {
                            this.UseAction(m.Type, m.ID);
                        }
                    } else if (m.Status == ActionStatus.LocalDelay) {
                        this.useActionHook.Disable();
                        unsafe {
                            var am = ActionManager.Instance();
                            if (am is not null)
                                ret = am->UseAction(m.Type, m.ID, m.TargetID, m.param, m.UseType, m.pvp, (void*)m.a8);
                        }
                        this.useActionHook.Enable();
                    }

                    PluginLog.Debug($"[UseActionAsync] wait? {delay}ms {m.ID} {m.TargetID} {m.Status} ret: {ret}, lastID: {this.lastActionID}, lastTime: {this.lastTime}");

                    if (ret) {
                        this.lastTime = DateTime.Now;
                        this.lastActionID = m.ID;
                        var status = ActionStatus.Ready;
                        this.executedActions.Writer.TryWrite((m.ID, status));
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
                    if (this.executedActions.Reader.TryRead(out (uint ID, ActionStatus Status) a)) {
                        var adjusted = Actions.AdjustedActionID(a.ID);

                        if (Config.IsComboAction(a.ID)) {
                            await Config.UpdateComboState(a.ID, a.Status);
                        } else if (Config.IsComboAction(adjusted)) {
                            await Config.UpdateComboState(adjusted, a.Status);
                        }
                    } else {
                        await Config.UpdateComboState();
                    }
                } catch(Exception e) {
                    PluginLog.Error($"Exception: {e}");
                }
            });
            // Update combo action icon.
            // if (Config.IsComboAction(action) && Config.UpdateComboState(action, status)) {}
            // else if (Config.IsComboAction(adjusted) && Config.UpdateComboState(adjusted, status)) {}
        }

        private Hook<UseActionDelegate> useActionHook;
        private delegate bool UseActionDelegate(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8);
        private bool UseActionDetour(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8)
        {
            if (!Plugin.Ready) return false;

            var a = new GameAction();
            bool ret = false;

            // original slot action which combo-chains place at. (ComboGroup)
            var originalActionID = actionID;

            var adjustedID = Actions.AdjustedActionID(actionID);
            var status = Actions.ActionStatus(adjustedID, (ActionType)actionType, targetedActorID);

            // update real base actionID using adjustedID
            actionID = Actions.BaseActionID(adjustedID);

            var pmap = this.GetSortedPartyMembers();
            var target = TargetManager.Target;
            var softTarget = TargetManager.SoftTarget;
            bool inParty = pmap.Count > 1 || Config.alwaysInParty;  // <---

            var interval = DateTime.Now - this.lastTime;
            // PluginLog.Debug($"[UseAction]: {actionID} {adjustedID} {targetedActorID} {actionManager} {softTarget ?? target} interval: {interval}, status: {status}, state: {this.state} {Actions.RecastTimeRemain(adjustedID)}");
            // PluginLog.Debug($"[Args] AM: {actionManager}, AT: {actionType}, ID: {actionID}, TID: {targetedActorID}, param: {param}, useType: {useType}, pvp: {pvp}, a8: {a8}");

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
            //  2. Macro : UseAction[UseType == 2] -> Delay Local -> am->UseAction[UseType == 2] -> exec
            //  3. GtAct : UseAction[UseType == 0] -> Delay -> UseAction[UseType == 2] -> no delay -> exec
            //  4. GsAct : UseAction[UseType == 0] : In Gs -> UseAction[UseType == 0] : Gs -> exec
            //  5. Norma : UseAction[UseType == 0] -> exec
            // PRECEDURE END

            // Only handle Spell && Ability(?)
            if (actionType != (uint)ActionType.Spell && actionType != (uint)ActionType.Ability) {
                PluginLog.Debug($"[UseAction] not a spell. {adjustedID} {actionType}");
                ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                goto MainRet;
            }

            // useType == 2: macro
            if (useType == 2) {
                // GtAction in macro should execute immediately!
                if (Config.IsGtoffAction(actionID) || Config.IsGtoffAction(adjustedID)) {
                    ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                    goto MainRet;
                }

                var suc = false;
                if (Config.blockingAction) {
                    suc = this.pendingActions.Writer.TryWrite(new GameAction() {
                                    ActionManager = actionManager,
                                    ID = originalActionID,
                                    TargetID = targetedActorID,
                                    Status = ActionStatus.LocalDelay,
                                    // DelayTo = this.CalculateDelay(adjustedID),
                                });

                    PluginLog.Debug($"[UseAction] Macro Local Delay? {suc}");
                }

                if (suc) return false;

                ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
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

                        // 使用UseAction去执行GtAction不会进技能队列, 因为GT需要交互.
                        // 使用UseActionLocation执行则可以
                        var tgt = softTarget ?? target ?? Plugin.Player;

                        this.useActionHook.Disable();
                        unsafe {
                            var am = (ActionManager*)actionManager;
                            if (tgt is not null && am is not null) {
                                var p = new Vector3() {
                                    X = tgt.Position.X,
                                    Y = tgt.Position.Y,
                                    Z = tgt.Position.Z
                                };

                                ret = am->UseActionLocation((ActionType)actionType, actionID, targetedActorID, &p);
                            } else {
                                // Entering Gt Mode
                                ret = am->UseAction((ActionType)actionType, actionID, targetedActorID, param, useType, pvp, (void*)a8);
                                // Cast again to emulate <gtoff>
                                ret = am->UseAction((ActionType)actionType, actionID, targetedActorID, param, useType, pvp, (void*)a8);
                            }
                        }
                        this.useActionHook.Enable();

                        this.state = GamepadActionManagerState.ActionExecuted;
                    } else if (Config.IsGsAction(actionID) || Config.IsGsAction(adjustedID)) {
                        // if (status == ActionStatus.Ready && pmap.Any(x => x.ID == targetedActorID)) {
                        // 1. targetID不是队友, 进入GSM
                        // 2. GSM选中队友, 执行.
                        // 3. 如果出现Locking, Pending等状态, 系统会在Ready的时候再次执行UseAction
                        // 4. 再次执行, 又回到这里, 需要判断到底处于何种状态(是第一步, 还是第四步)
                        //    此时Action状态为Ready, 并且targetID应该也已经变成了队友?, 所以可能不需要特殊判断
                        if (pmap.Any(x => x.ID == targetedActorID)) {
                            ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
                            this.state = GamepadActionManagerState.ActionExecuted;
                        } else {
                            a.ActionManager = actionManager;
                            a.Type = (ActionType)actionType;
                            a.ID = adjustedID;
                            a.TargetID = targetedActorID;
                            a.param = param;
                            a.UseType = useType;
                            a.pvp = pvp;
                            a.a8 = a8;
                            // a.ready = false;
                            // a.done = false;

                            this.state = GamepadActionManagerState.EnteringGamepadSelection;
                        }
                    } else {
                        // Auto-targeting only for normal actions.
                        // default targetID == 3758096384u == 0xe0000000
                        // 只有在没选中队友时才能选中最近的敌人
                        if (!pmap.Any(x => x.ID == targetedActorID)) {
                            target = Config.alwaysTargetingNearestEnemy ? NearestTarget() : softTarget ?? target ?? (Config.autoTargeting ? NearestTarget() : null);
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

                        ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);

                        this.state = GamepadActionManagerState.ActionExecuted;
                    }
                    goto MainLoop;
                case GamepadActionManagerState.EnteringGamepadSelection:
                    this.savedAction = a;

                    // 未满足发动条件?
                    // 未满足发动条件的GsAction直接跳过, 不进行目标选择.
                    if (status != ActionStatus.Ready && status != ActionStatus.Locking && status != ActionStatus.Pending)  {
                        ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
                        this.state = GamepadActionManagerState.ActionExecuted;
                        goto MainLoop;
                    }

                    this.state = GamepadActionManagerState.InGamepadSelection;
                    break;
                case GamepadActionManagerState.InGamepadSelection:
                    var o = this.savedAction;

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
                                if (!pmap.Any(x => x.ID == (uint)targetedActorID)) {   // Disable GSM if we already selected a member.
                                    gsTargetedActorID = pmap[gsRealTargetedActorIndex].ID;
                                }
                                this.savedAction.TargetID = gsTargetedActorID;
                            }

                            Func<int, string> _S = (x) => Convert.ToString(x, 2).PadLeft(8, '0');
                            PluginLog.Debug($"[{this.state}], PartyID: {PartyList.PartyId}, Length: {pmap.Count}, originalIndex: {gsTargetedActorIndex}, index: {gsRealTargetedActorIndex}, btn: {_S(buttons)}, savedBtn: {_S(this.savedButtonsPressed)}, origBtn: {_S(ginput->ButtonsPressed & 0xff)}, reptBtn: {_S((ginput->ButtonsRepeat & 0xff))}");
                        }
                    } catch(Exception e) {
                        PluginLog.Error($"Exception: {e}");
                    }

                    targetedActorID = o.TargetID;

                    status = Actions.ActionStatus(o.ID, o.Type, targetedActorID);

                    ret = this.useActionHook.Original(actionManager, (uint)o.Type, o.ID, o.TargetID, o.param, o.UseType, o.pvp, o.a8);
                    PluginLog.Debug($"[{this.state}] ret: {ret}, ActionID: {actionID}, AdjID: {adjustedID}, Orig: {originalActionID}, ActionType: {actionType}, TargetID: {targetedActorID}, status: {status}");

                    this.savedButtonsPressed = 0;

                    this.state = GamepadActionManagerState.ActionExecuted;
                    goto MainLoop;
                case GamepadActionManagerState.ActionExecuted:
                    unsafe {
                        var ginput = (GamepadInput*)GamepadState.GamepadInputAddress;
                        this.savedButtonsPressed = (ushort)(ginput->ButtonsPressed & 0xff);

                        // PluginLog.Debug($"[{this.state}] ret: {ret}, ActionID: {actionID}, AdjID: {adjustedID}, Orig: {originalActionID}, ActionType: {actionType}, TargetID: {targetedActorID}, status: {status}");
                    }

                    this.state = GamepadActionManagerState.Start;
                    break;
            }

        MainRet:
            if (!ret) return false;

            if (status == ActionStatus.Ready) {
                this.lastTime = DateTime.Now;
                this.lastActionID = adjustedID;
            }

            this.executedActions.Writer.TryWrite((adjustedID, status));
            return ret;
        }

        private Hook<IsIconReplaceableDelegate> isIconReplaceableHook;
        private delegate ulong IsIconReplaceableDelegate(uint actionID);
        private ulong IsIconReplaceableDetour(uint actionID) => 1;

        private Hook<GetIconDelegate> getIconHook;
        private delegate uint GetIconDelegate(IntPtr actionManager, uint actionID);

        // actionID: 位于技能栏上的原本的Action的ID(不是replace icon之后的).
        private uint GetIconDetour(IntPtr actionManager, uint actionID)
        {
            var lastComboAction = Marshal.ReadInt32(this.lastComboActionPtr);
            var comboTimer = Marshal.PtrToStructure<float>(this.comboTimerPtr);

            // 小奥秘卡 -> 出王冠卡 : 出王冠卡
            var comboActionID = Config.CurrentComboAction(actionID, (uint)lastComboAction, comboTimer);
            return this.getIconHook.Original(actionManager, comboActionID);
        }

        private List<Member> GetSortedPartyMembers(string order = "thmr")
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

        // Could choose wrong target cause some game objects are invisible.
        private GameObject? NearestTarget()
        {
            Plugin.Send("/tenemy");
            return TargetManager.Target;
        }

        private TimeSpan CalculateDelay(uint actionID)
        {
            var lgroup = Actions.RecastGroup(this.lastActionID);
            var cgroup = Actions.RecastGroup(actionID);

            // m1 + m2
            // m1 + a1
            // m1 + a1 + m2
            // a1 + a2
            var remainMs = 0;
            var remain = 0;
            var me = Plugin.Player!;
            // if (status == ActionStatus.Locking) {
            if (me.IsCasting) {
                remain = (int)((me.TotalCastTime - me.CurrentCastTime) * 1000) + 100;
            } else {
                remain = (int)(DateTime.Now - this.lastTime).TotalMilliseconds;
                remain = Math.Max(690 - remain, 0);
            }

            var recast = Actions.RecastTimeRemain(actionID);
            // 不需要等到下一个gcd开始...No
            // if (lgroup == cgroup) recast -= 0.4f;
            if (recast > Configuration.GlobalCoolingDown.TotalSeconds) recast = Configuration.GlobalCoolingDown.TotalSeconds;

            remainMs = Math.Max((int)(recast * 1000), remain) + 10;

            PluginLog.Debug($"[DelayTime] id: {actionID}, lgroup: {lgroup}, cgroup: {cgroup}, isCasting: {Plugin.Player!.IsCasting}, lastID: {this.lastActionID}, lastTime: {this.lastTime}, recast: {recast}, remain: {remain}, remainMs: {remainMs}ms");
            return TimeSpan.FromMilliseconds(remainMs);
        }

        [HandleProcessCorruptedStateExceptions]
        private bool UseActionToTarget(ActionType actionType, uint actionID)
        {
            try {
                Plugin.Send($"/merror off");
                Plugin.Send($"/ac {Actions[actionID]} <t>");
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                return false;
            }
            return true;
        }

        [HandleProcessCorruptedStateExceptions]
        private bool UseAction(ActionType actionType, uint actionID)
        {
            try {
                Plugin.Send($"/merror off");
                Plugin.Send($"/ac {Actions[actionID]}");
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                return false;
            }
            return true;
        }

        // private GameObject? NearestTarget(BattleNpcSubKind type = BattleNpcSubKind.Enemy)
        // {
        //     var me = ClientState.LocalPlayer;

        //     if (me is null)
        //         return null;

        //     var pm = me.Position;

        //     GameObject? o = null;
        //     var md = Double.PositiveInfinity;
        //     foreach (var x in Objects) {
        //         // if (x.ObjectKind == ObjectKind.BattleNpc) {
        //         //     unsafe {
        //         //         var St = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)x.Address;
        //         //         PluginLog.Debug($"{x.IsValid()} {x.ObjectKind} {x.SubKind} {x.ObjectId} {x.OwnerId} {x.Name.ToString()} {((BattleNpc)x).CurrentHp}, {St->RenderFlags}");
        //         //     }
        //         // }
        //         if (!x.IsValid() ||
        //             x.ObjectKind != ObjectKind.BattleNpc ||
        //             x.SubKind != (byte)type ||
        //             x.ObjectId == me.ObjectId)
        //             continue;
        //         if (((BattleNpc)x).CurrentHp <= 0)
        //             continue;
        //         var px = x.Position;
        //         var d = Math.Pow(px.X - pm.X, 2) + Math.Pow(px.Y - pm.Y, 2) + Math.Pow(px.Z - pm.Z, 2);
        //         if (d < md) {
        //             md = d;
        //             o = x;
        //         }
        //     }

        //     // GameObject o = Objects.ToList().Min(x => {
        //     //     if (x.ObjectId == me.ObjectId)
        //     //         return Double.PositiveInfinity;
        //     //     var px = x.Position;
        //     //     return Math.Pow(px.X - pm.X, 2) + Math.Pow(px.Y - pm.Y, 2) + Math.Pow(px.Z - pm.Z, 2);
        //     // });
        //     if (o is not null) {
        //         TargetManager.SetTarget(o);
        //         PluginLog.Debug($"Nearest Target: {o.ObjectId} {o.Name.ToString()}, SubKind: {type}");
        //     }

        //     return o;
        // }

        // public uint AdjustedActionID(IntPtr actionManager, uint actionID)
        // {
        //     var adjustedID = actionID;
        //     unsafe {
        //         var am = (ActionManager*)actionManager;
        //         if (am != null) {
        //             // real action id
        //             adjustedID = am->GetAdjustedActionId(actionID);
        //         }
        //     }
        //     return adjustedID;
        // }

        // private ActionStatus GetActionStatus(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID = 3758096384U)
        // {
        //     uint status = 0;
        //     unsafe {
        //         var am = (ActionManager*)actionManager;
        //         if (am != null) {
        //             status = am->GetActionStatus((ActionType)actionType, actionID, targetedActorID);
        //         }
        //     }

        //     try {
        //         return (ActionStatus)status;
        //     } catch(Exception e) {
        //         PluginLog.Debug($"Exception: {e}");
        //         return ActionStatus.Invalid;
        //     }
        // }

        public void Enable()
        {
            this.useActionHook.Enable();
            this.isIconReplaceableHook.Enable();
            this.getIconHook.Enable();
        }

        public void Disable()
        {
            this.useActionHook.Disable();
            this.isIconReplaceableHook.Disable();
            this.getIconHook.Disable();
        }

        public void Dispose()
        {
            this.useActionHook.Disable();
            this.useActionHook.Dispose();
            this.isIconReplaceableHook.Disable();
            this.isIconReplaceableHook.Dispose();
            this.getIconHook.Disable();
            this.getIconHook.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}