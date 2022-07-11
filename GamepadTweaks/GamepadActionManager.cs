using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Gui;
using Dalamud.Logging;
using Dalamud.Hooking;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace GamepadTweaks
{
    public class UseActionArgs {
        public IntPtr actionManager;
        public uint actionType;
        public uint actionID;
        public uint targetedActorID;
        public uint param;
        public uint useType;
        public uint pvp;
        public IntPtr a8;

        public bool ready;
        public bool done;
        public DateTime lastTime;

        private Random rnd = new Random();

        public UseActionArgs() {
            ready = false;
            done = false;
            lastTime = DateTime.Now;
        }

        public bool Equals(UseActionArgs o)
        {
            return actionType == o.actionType && actionID == o.actionID &&
                   targetedActorID == o.targetedActorID && param == o.param &&
                   useType == o.useType && pvp == o.pvp;
        }

        public bool IsValid() => this.actionID > 0;
    }

    public class Member {
        public string Name;
        public uint ID;
        public uint JobID;
    }

    public enum GamepadActionManagerState : int
    {
        Start = 0,
        EnteringGamepadSelection = 1,
        InGamepadSelection = 2,
        // ExitingGamepadSelection = 3,
        GsActionInQueue = 4,
        // ExecuteAction = 5,
        ActionExecuted = 6,
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

        private ushort savedButtonsPressed;
        private UseActionArgs gsAction;
        private IntPtr comboTimerPtr;
        private IntPtr lastComboActionPtr;

        private ActionMap ActionMap = new ActionMap();

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
            this.gsAction = new UseActionArgs();

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

        private Hook<UseActionDelegate> useActionHook;
        private delegate bool UseActionDelegate(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8);
        private bool UseActionDetour(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8)
        {
            var a = new UseActionArgs();
            bool ret = false;
            
            // original slot action which combo-chains place at. (eg: comboGroup)
            var originalActionID = actionID;
            
            var adjustedID = this.AdjustedActionID(actionManager, actionID);
            var status = this.GetActionStatus(actionManager, actionType, adjustedID, targetedActorID);
            
            // update real base actionID using adjustedID
            actionID = ActionMap.BaseActionID(adjustedID);

            var pmap = this.GetSortedPartyMembers();
            var target = TargetManager.Target;
            var softTarget = TargetManager.SoftTarget;
            bool inParty = pmap.Count > 1 || Config.alwaysInParty;  // <---

            //
            // BEGIN: 显式忽略某些类型的情况
            //
            // var targetID = softTarget is not null ? softTarget.ObjectId : (target is not null ? target.ObjectId : 3758096384U);  // default value
            PluginLog.Debug($"[UseAction]: {actionID} {adjustedID} {targetedActorID} status: {status}, state: {this.state}");

            // if (targetID != targetedActorID) {
            // if (status == ActionStatus.Done) {
            //     ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
            //     this.state = GamepadActionManagerState.Start;
            //     goto MainReturn;
            // }
            
            // if (this.gsAction.done && this.gsAction.actionID == adjustedID && (DateTime.Now - this.gsAction.lastTime).Milliseconds < 400) {
            //     return ret;
            // }
            //
            // END
            //

        MainLoop:
            switch (this.state)
            {
                // handle ActionStatus.Done
                case GamepadActionManagerState.Start:
                    if (Config.IsGtoffAction(actionID) || Config.IsGtoffAction(adjustedID)) {
                        unsafe {
                            var am = (ActionManager*)actionManager;
                            var tgt = target ?? softTarget;
                            if (tgt is not null) {
                                try {
                                    var p = tgt.Position;
                                    var ap = new Vector3() {
                                        X = p.X, Y = p.Y, Z = p.Z
                                    };
                                    if (am is not null)
                                        am->UseActionLocation((ActionType)actionType, adjustedID, targetedActorID, &ap);
                                } catch(Exception e) {
                                    PluginLog.Log($"Exception: {e}");
                                }
                            } else {
                                // Will enter ground targeting mode if action uses ground targeting.
                                ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);

                                // Cast again to emulate <gtoff>
                                ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
                            }
                        }

                        this.state = GamepadActionManagerState.ActionExecuted;
                    } else if (Config.IsGsAction(actionID) || Config.IsGsAction(adjustedID)) {
                        if (status == ActionStatus.Done || pmap.Any(x => x.ID == targetedActorID)) {
                            ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
                            this.state = GamepadActionManagerState.ActionExecuted;
                        } else {
                            a.actionManager = actionManager;
                            a.actionType = actionType;
                            a.actionID = adjustedID;
                            a.targetedActorID = targetedActorID;
                            a.param = param;
                            a.useType = useType;
                            a.pvp = pvp;
                            a.a8 = a8;
                            // a.ready = false;
                            // a.done = false;

                            this.state = GamepadActionManagerState.EnteringGamepadSelection;
                        }
                    } else {
                        if (status != ActionStatus.Done) {
                            // Auto-targeting only for normal actions.
                            target = target ?? (Config.autoTargeting ? NearestTarget() : null);

                            // Cast normally if:
                            //  1. We are not in party
                            //  2. We already target a party member
                            //  3. Action not in monitor (any action could be a combo action)
                            if (softTarget is not null) {
                                targetedActorID = softTarget.ObjectId;
                            } else if (target is not null) {
                                targetedActorID = target.ObjectId;
                            }
                        }

                        ret = this.useActionHook.Original(actionManager, actionType, adjustedID, targetedActorID, param, useType, pvp, a8);
                        
                        this.state = GamepadActionManagerState.ActionExecuted;
                    }
                    goto MainLoop;
                case GamepadActionManagerState.EnteringGamepadSelection:
                    this.gsAction = a;

                    // 未满足发动条件?
                    // 未满足发动条件的GsAction直接跳过, 不进行目标选择.
                    if (status != ActionStatus.Done && status != ActionStatus.Locking && status != ActionStatus.Pending)  {
                        this.state = GamepadActionManagerState.ActionExecuted;
                        goto MainLoop;
                    }

                    this.state = GamepadActionManagerState.InGamepadSelection;
                    break;
                case GamepadActionManagerState.InGamepadSelection:
                    var o = this.gsAction;
                    
                    adjustedID = this.AdjustedActionID(actionManager, o.actionID);
                    
                    // update real base actionID using adjustedID
                    actionID = ActionMap.BaseActionID(adjustedID);
                    
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

                            var order = Config.SelectOrder(a.actionID).ToLower().Trim().Split(" ").Where((a) => a != "").ToList();
                            var gsTargetedActorIndex = order.FindIndex((b) => ButtonMap.ContainsKey(b) ? (ButtonMap[b] & buttons) > 0 : false);

                            PluginLog.Debug($"[Party] originalIndex: {gsTargetedActorIndex}");

                            if (pmap.Count > 0) {
                                gsTargetedActorIndex = gsTargetedActorIndex == -1 ? 0 : (gsTargetedActorIndex >= pmap.Count - 1 ? pmap.Count - 1 : gsTargetedActorIndex);

                                var gsTargetedActorID = targetedActorID;
                                if (!pmap.Any(x => x.ID == (uint)targetedActorID)) {   // Disable GSM if we already selected a member.
                                    gsTargetedActorID = pmap[gsTargetedActorIndex].ID;
                                    // TargetManager.SetSoftTarget(Objects.SearchById(gsTargetedActorID));
                                }
                                this.gsAction.targetedActorID = gsTargetedActorID;
                            }

                            Func<int, string> _S = (x) => Convert.ToString(x, 2).PadLeft(8, '0');
                            PluginLog.Debug($"[Party] ID: {PartyList.PartyId}, Length: {pmap.Count}, index: {gsTargetedActorIndex}, btn: {_S(buttons)}, savedBtn: {_S(this.savedButtonsPressed)}, origBtn: {_S(ginput->ButtonsPressed & 0xff)}, reptBtn: {_S((ginput->ButtonsRepeat & 0xff))}");
                            PluginLog.Debug($"[Party] ActionID: {o.actionID}, AdjustedID: {o.actionID}, TargetID: {o.targetedActorID}, ready?: {o.ready}, state: {this.state}, status: {status}, ret: {ret}");
                        }
                    } catch(Exception e) {
                        PluginLog.Error($"Exception: {e}");
                    }

                    // a.actionID = adjustedID;

                    // TargetManager.ClearSoftTarget();
                    // TargetManager.SetTarget(previousTarget);
                    // TargetManager.SetSoftTarget(previousTarget);

                    targetedActorID = o.targetedActorID;
                    
                    status = this.GetActionStatus(actionManager, o.actionType, adjustedID, targetedActorID);

                    // this.gsAction.ready = true;
                    // this.gsAction.lastTime = DateTime.Now;
                    ret = this.useActionHook.Original(o.actionManager, o.actionType, o.actionID, o.targetedActorID, o.param, o.useType, o.pvp, o.a8);

                    this.savedButtonsPressed = 0;
                    
                    // 如果能力技资源可用, 只执行一次.
                    // GsAction需要两步操作完成执行(1. 触发Action, 2. 选中目标)
                    // code 580: 进入队列之后, 如果当时资源不可以, 会执行第二次. 第二次执行应该自动使用第一次选中的目标.
                    // 问题: 这两次执行之间有没有可能存在其它Action的执行? 还是说一定顺序执行完当前能力技之后再说?
                    // 可以存在
                    if (status == ActionStatus.Locking) {
                        this.gsAction.ready = true;
                        this.state = GamepadActionManagerState.GsActionInQueue;
                        break;
                    }
                    
                    this.state = GamepadActionManagerState.ActionExecuted;
                    goto MainLoop;
                case GamepadActionManagerState.GsActionInQueue:
                    o = this.gsAction;
                    var ostatus = this.GetActionStatus(actionManager, o.actionType, o.actionID, o.targetedActorID);
                    
                    // 先执行完成这个技能再考虑其它
                    // if (o.ready && !o.done) {
                    //     // var tgt = Objects.SearchById(o.targetedActorID);
                    //     // var originalTgt = TargetManager.Target;
                    //     // TargetManager.SetTarget(tgt);
                    //     ret = this.useActionHook.Original(actionManager, o.actionType, o.actionID, o.targetedActorID, param, useType, pvp, a8);
                    //     // if (originalTgt is not null)
                    //     //     TargetManager.SetTarget(originalTgt);
                    //     // o.ready = false;
                    // }

                    // 魔法 + 能力 + 魔法2. 如果[能力]当前不可插入, 系统会自动在其可以插入的时刻再次调用UseAction
                    // 魔法 + 能力 + 魔法2 with Gs. 触发能力之后需要再次按下按键以选中目标. 但这样也会触发原本处于该按键上的Action.
                    // 魔法2需要自己掌控时机手动插入. 当资源可用的时候, status应该为582或0? 意味着能力已经被系统自动插入了.
                    // 因此, 此时不再屏蔽这个Action输入, 调用UseAction以手动插施放魔法2.
                    // 如不处理, 则将需要再次点击一次按键以完成魔法2的施放.
                    // 1 + (1 + 1) + 1
                    if (status == ActionStatus.Done || status == ActionStatus.Pending) {
                        ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                        o.done = true;
                        o.lastTime = DateTime.Now;
                    }
                    
                    PluginLog.Debug($"[Party] ActionID: {o.actionID}, AdjustedID: {o.actionID}, TargetID: {o.targetedActorID}, ready?: {o.ready}, state: {this.state}, status: {status}, ret: {ret}");

                    if (!o.done) break;

                    // this.gsAction = new UseActionArgs();
                    
                    actionID = o.actionID;
                    adjustedID = this.AdjustedActionID(actionManager, o.actionID);
                    targetedActorID = o.targetedActorID;

                    this.state = GamepadActionManagerState.ActionExecuted;
                    goto MainLoop;
                case GamepadActionManagerState.ActionExecuted:
                    unsafe {
                        var ginput = (GamepadInput*)GamepadState.GamepadInputAddress;
                        this.savedButtonsPressed = (ushort)(ginput->ButtonsPressed & 0xff);

                        PluginLog.Debug($"[DONE] ActionID: {actionID}, AdjustedID: {adjustedID}, Orig: {originalActionID}, ActionType: {actionType}, TargetID: {targetedActorID}, ready?: {this.gsAction.ready}, state: {this.state}, status: {status}, ret: {ret}");
                    }
    
                    this.state = GamepadActionManagerState.Start;
                    break;
            }

        MainReturn:

            // Update combo action icon.
            if (Config.IsComboAction(actionID) && Config.UpdateComboState(actionID, status));
            else if (Config.IsComboAction(adjustedID) && Config.UpdateComboState(adjustedID, status));

            return ret;
        }

        public Hook<IsIconReplaceableDelegate> isIconReplaceableHook;
        public delegate ulong IsIconReplaceableDelegate(uint actionID);
        private ulong IsIconReplaceableDetour(uint actionID) => 1;

        public Hook<GetIconDelegate> getIconHook;
        public delegate uint GetIconDelegate(IntPtr actionManager, uint actionID);
        public uint GetIconDetour(IntPtr actionManager, uint actionID)
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

                    var chocoboName = addonPartyList->Chocobo.Name->NodeText.ToString()
                                                                            .Split(" ", 2)
                                                                            .Last()
                                                                            .Trim();

                    me.Add(new Member() {
                        Name = chocoboName,
                        ID = objectMap.ContainsKey(chocoboName) ? objectMap[chocoboName] : 0,
                        JobID = 0,
                    });

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

        private GameObject? NearestTarget(BattleNpcSubKind type = BattleNpcSubKind.Enemy)
        {
            var me = ClientState.LocalPlayer;
            
            if (me is null)
                return null;
            
            var pm = me.Position;
            
            GameObject o = null;
            var md = Double.PositiveInfinity;
            foreach (var x in Objects) {
                if (!x.IsValid() ||
                    x.ObjectKind != ObjectKind.BattleNpc ||
                    x.SubKind != (byte)type ||
                    x.ObjectId == me.ObjectId)
                    continue;
                if (((BattleNpc)x).CurrentHp <= 0) {
                    continue;
                }
                var px = x.Position;
                var d = Math.Pow(px.X - pm.X, 2) + Math.Pow(px.Y - pm.Y, 2) + Math.Pow(px.Z - pm.Z, 2);
                if (d < md) {
                    md = d;
                    o = x;
                }
            }

            // GameObject o = Objects.ToList().Min(x => {
            //     if (x.ObjectId == me.ObjectId)
            //         return Double.PositiveInfinity;
            //     var px = x.Position;
            //     return Math.Pow(px.X - pm.X, 2) + Math.Pow(px.Y - pm.Y, 2) + Math.Pow(px.Z - pm.Z, 2);
            // });
            if (o is not null) {
                TargetManager.SetTarget(o);
                PluginLog.Debug($"Nearest Target: {o.ObjectId} {o.Name.ToString()}, SubKind: {type}");
            }

            return o;
        }

        public uint AdjustedActionID(IntPtr actionManager, uint actionID)
        {
            var adjustedID = actionID;
            unsafe {
                var am = (ActionManager*)actionManager;
                if (am != null) {
                    // real action id
                    adjustedID = am->GetAdjustedActionId(actionID);
                }
            }
            return adjustedID;
        }

        private ActionStatus GetActionStatus(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID = 3758096384U)
        {
            // 以下均为猜测:
            // 000: 资源可用, 可以马上被执行(技能队列中的Action资源可用时, 游戏会调用UseAction, 此时status为0)
            // 572: 未满足发动条件
            // 580: 资源暂时不可用(加入技能队列当其可用时), 因为上一个技能的动画锁还未解除(通常需要等待~0.5s)?
            // 582: 资源可用, 可以马上插入技能队列
            uint status = 0;
            unsafe {
                var am = (ActionManager*)actionManager;
                if (am != null) {
                    // code 580: 如果资源可以访问, 则尝试将Action加入技能队列?
                    status = am->GetActionStatus((ActionType)actionType, actionID, targetedActorID);
                }
            }
            
            try {
                return (ActionStatus)status;
            } catch(Exception e) {
                PluginLog.Debug($"Exception: {e}");
                return ActionStatus.Invalid;
            }
        }

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