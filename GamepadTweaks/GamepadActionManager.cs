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
    }

    public class Member {
        public string Name;
        public uint ID;
        public uint JobID;
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

        public bool inGamepadSelectionMode = false;

        private ushort savedButtonsPressed;
        private UseActionArgs gsAction;

        private Configuration Config = Plugin.Config;
        private GamepadState GamepadState = Plugin.GamepadState;
        private PartyList PartyList = Plugin.PartyList;
        private GameGui GameGui = Plugin.GameGui;
        private ClientState ClientState = Plugin.ClientState;
        private ObjectTable Objects = Plugin.Objects;
        private TargetManager TargetManager = Plugin.TargetManager;

        public GamepadActionManager() {
            this.gsAction = new UseActionArgs();

            var Signature = "E8 ?? ?? ?? ?? EB 64 B1 01";
            var useAction = Plugin.SigScanner.ScanText(Signature);
            this.useActionHook = new Hook<UseActionDelegate>(useAction, this.UseActionDetour);

            this.useActionHook.Enable();
        }

        private Hook<UseActionDelegate> useActionHook;
        private delegate bool UseActionDelegate(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8);
        private bool UseActionDetour(IntPtr actionManager, uint actionType, uint actionID, uint targetedActorID, uint param, uint useType, uint pvp, IntPtr a8)
        {
            bool ret = false;
            var adjustedID = actionID;
            var pmap = this.GetSortedPartyMembers();
            var target = TargetManager.Target ?? (Config.autoTargeting ? NearestTarget() : null);
            var softTarget = TargetManager.SoftTarget;
            bool inParty = pmap.Count > 1 || Config.alwaysInParty;  // <---

            unsafe {
                var am = (ActionManager*)actionManager;
                if (am != null)
                    adjustedID = am->GetAdjustedActionId(actionID);
            }

            if (this.inGamepadSelectionMode) {
                var a = this.gsAction;
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

                        if (pmap.Count > 0) {
                            gsTargetedActorIndex = gsTargetedActorIndex == -1 ? 0 : (gsTargetedActorIndex >= pmap.Count - 1 ? pmap.Count - 1 : gsTargetedActorIndex);

                            var gsTargetedActorID = targetedActorID;
                            if (!pmap.Any(x => x.ID == (uint)targetedActorID)) {   // Disable GSM if we already selected a member.
                                gsTargetedActorID = pmap[gsTargetedActorIndex].ID;
                            }
                            this.gsAction.targetedActorID = gsTargetedActorID;
                        }

                        Func<int, string> _S = (x) => Convert.ToString(x, 2).PadLeft(8, '0');
                        PluginLog.Debug($"[Party] ID: {PartyList.PartyId}, Length: {pmap.Count}, index: {gsTargetedActorIndex}, btn: {_S(buttons)}, savedBtn: {_S(this.savedButtonsPressed)}, origBtn: {_S(ginput->ButtonsPressed & 0xff)}, reptBtn: {_S((ginput->ButtonsRepeat & 0xff))}, Action: {a.actionID} Target: {a.targetedActorID}");
                    }
                } catch(Exception e) {
                    PluginLog.Error($"Exception: {e}");
                }

                ret = this.useActionHook.Original(a.actionManager, a.actionType, a.actionID, a.targetedActorID, a.param, a.useType, a.pvp, a.a8);

                this.savedButtonsPressed = 0;
                this.inGamepadSelectionMode = false;
            } else {
                if (Config.IsGtoffAction(actionID) || Config.IsGtoffAction(adjustedID)) {
                    // Will enter ground targeting mode if action uses ground targeting.
                    ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);

                    unsafe {
                        var am = (ActionManager*)actionManager;
                        if (target is not null) {
                            try {
                                var p = target.Position;
                                var ap = new Vector3() {
                                    X = p.X, Y = p.Y, Z = p.Z
                                };
                                if (am is not null)
                                    am->UseActionLocation((ActionType)actionType, actionID, targetedActorID, &ap);
                            } catch(Exception e) {
                                PluginLog.Log($"Exception: {e}");
                            }
                        } else {
                            // Cast again to emulate <gtoff>
                            ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                        }
                    }
                } else if (
                    !inParty ||
                    target is not null && pmap.Any(x => x.ID == target.ObjectId) ||
                    softTarget is not null && pmap.Any(x => x.ID == softTarget.ObjectId) || // SoftTarget support.
                    !(Config.ActionInMonitor(actionID) || Config.ActionInMonitor(adjustedID))
                ) {
                    // Cast normally if:
                    //  1. We are not in party
                    //  2. We already target a party member
                    //  3. Action not in monitor
                    if (softTarget is not null)
                        targetedActorID = softTarget.ObjectId;
                    ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);

                    if (!ret) {
                        if (target is not null)
                            targetedActorID = target.ObjectId;
                        ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                    }

                    var tgtID = target is not null ? target.ObjectId : 0;
                    var softTgtID = softTarget is not null ? softTarget.ObjectId : 0;
                    PluginLog.Debug($"ActionID: {actionID}, AdjustedID: {adjustedID}, TargetID: {tgtID}, SoftTargetID: {softTgtID}");
                } else {
                    this.gsAction.actionManager = actionManager;
                    this.gsAction.actionType = actionType;
                    this.gsAction.actionID = actionID;
                    this.gsAction.targetedActorID = targetedActorID;
                    this.gsAction.param = param;
                    this.gsAction.useType = useType;
                    this.gsAction.pvp = pvp;
                    this.gsAction.a8 = a8;
                    this.inGamepadSelectionMode = true;
                }
            }

            unsafe {
                var ginput = (GamepadInput*)GamepadState.GamepadInputAddress;
                this.savedButtonsPressed = (ushort)(ginput->ButtonsPressed & 0xff);
            }

            return ret;
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

                    if (addonPartyList->TrustCount > 0) {
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
                        var id = objectMap.ContainsKey(name) ? objectMap[name] : 0;
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

        public void Enable() => this.useActionHook.Enable();
        public void Disable() => this.useActionHook.Disable();

        public void Dispose()
        {
            this.useActionHook.Disable();
            this.useActionHook.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}