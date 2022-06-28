using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Logging;
using Dalamud.Hooking;
using System;
using System.Linq;
using System.Collections.Generic;

namespace Gi
{
    public class UseActionArgs {
        public IntPtr actionManager;
        public uint actionType;
        public uint actionID;
        public long targetedActorID;
        public uint param;
        public uint useType;
        public int pvp;
        public IntPtr a8;
    }

    class GamepadSelection : IDisposable
    {
        public static Dictionary<string, ushort> ButtonMap = new Dictionary<string, ushort> {
            {"up", (ushort)GamepadButtons.DpadUp},
            {"down", (ushort)GamepadButtons.DpadDown},
            {"left", (ushort)GamepadButtons.DpadLeft},
            {"right", (ushort)GamepadButtons.DpadRight},
            {"y", (ushort)GamepadButtons.North},
            {"a", (ushort)GamepadButtons.South},
            {"x", (ushort)GamepadButtons.West},
            {"b", (ushort)GamepadButtons.East}
        };

        private Dictionary<uint, string> actions;
    
        public bool inGamepadSelectionMode = false;

        private int savedButtonsPressed;
        private UseActionArgs gsAction;

        private ClientState clientState;
        private GamepadState gamepad;
        private PartyList partyList;
        private Configuration config;
        // private BuddyList buddyList;

        public GamepadSelection(ClientState clientState, GamepadState gamepad, PartyList partyList, BuddyList buddyList, Configuration config) {
            this.clientState = clientState;
            this.gamepad = gamepad;
            this.partyList = partyList;
            this.config = config;

            this.actions = config.GetActionsInMonitor();
            // this.buddyList = buddyList;
            this.gsAction = new UseActionArgs();

            this.config.UpdateActionsInMonitor += this.UpdateActionsInMonitor;

            var Signature = "E8 ?? ?? ?? ?? EB 64 B1 01";
            var useAction = (new SigScanner()).ScanText(Signature);
            this.useActionHook = new Hook<UseActionDelegate>(useAction, this.UseActionDetour);

            this.useActionHook.Enable();
        }

        private Hook<UseActionDelegate> useActionHook;
        private delegate byte UseActionDelegate(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, IntPtr a8);
        private byte UseActionDetour(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, IntPtr a8)
        {
            byte ret = 0;
            var a = this.gsAction;
            bool inParty = this.partyList.Length > 0 || this.config.debug;  // <---
            // bool inParty = true;
            var pmap = this.GetSortedPartyMemberIDs();
        
            // PluginLog.Log($"ActionID: {actionID}, SavedActionID: {a.actionID}, TargetID: {targetedActorID}, inGSM: {this.inGamepadSelectionMode}");
            // PluginLog.Log($"Me: {pmap[0]}, ClassJob: {this.clientState.LocalPlayer.ClassJob.Id} inParty: {inParty}");

            if (this.inGamepadSelectionMode) {
                try {
                    unsafe {
                        var ginput = (GamepadInput*)this.gamepad.GamepadInputAddress;

                        // 1. Only use [up down left right y a x b]
                        // 2. Resolve buttons priority
                        // 目前GSM只在lt/rt按下, 即激活十字热键栏预备施放技能时可用.
                        int buttons = (ginput->ButtonsPressed & 0x00ff);
                        // 1 0 1 0 Prev
                        // 1 1 0 0 Now
                        // 0 1 0 0 True Buttons
                        // 如果上一次状态和本次相同, 我们不能判断到底是哪个按键触发了Action.
                        // 多个按键同时按下, 选择优先级高的按键
                        if (buttons != this.savedButtonsPressed)
                            buttons = (buttons ^ this.savedButtonsPressed) & buttons;
                        
                        var order = this.actions[a.actionID].ToLower().Trim().Split(" ").Where((a) => a != "").ToList();
                        var gsTargetedActorIndex = order.FindIndex((b) => ButtonMap.ContainsKey(b) ? (ButtonMap[b] & buttons) > 0 : false);
                        
                        if (pmap.Count > 0) {
                            gsTargetedActorIndex = gsTargetedActorIndex == -1 ? 0 : (gsTargetedActorIndex >= pmap.Count - 1 ? pmap.Count - 1 : gsTargetedActorIndex);
                            
                            var gsTargetedActorID = targetedActorID;
                            if (!pmap.Contains((uint)targetedActorID)) {   // Disable GSM if we already selected a member.
                                gsTargetedActorID = (long)(uint)pmap[gsTargetedActorIndex];
                            }
                            a.targetedActorID = gsTargetedActorID;
                        }
                        
                        PluginLog.Log($"[Party] ID: {this.partyList.PartyId}, Length: {this.partyList.Length}, index: {gsTargetedActorIndex}, btn: {Convert.ToString(buttons, 2)}, savedBtn: {Convert.ToString(this.savedButtonsPressed, 2)}, origBtn: {Convert.ToString((ginput->ButtonsPressed & 0x00ff), 2)}, Action: {a.actionID} Target: {a.targetedActorID}");

                        // PluginLog.Log($"[Buddy] ID: {0}, Length: {this.buddyList.Length}, index: {gsTargetedActorIndex}");
                        // if (this.buddyList.Length > 0) {
                        //     var gsTargetedActorID = this.buddyList[gsTargetedActorIndex % this.buddyList.Length].ObjectId;
                        //     a.targetedActorID = gsTargetedActorID;
                        // }
                    }
                } catch(Exception e) {
                    PluginLog.Log($"Exception: {e}");
                }

                ret = this.useActionHook.Original(a.actionManager, a.actionType, a.actionID, a.targetedActorID, a.param, a.useType, a.pvp, a.a8);
                
                this.savedButtonsPressed = 0;
                this.inGamepadSelectionMode = false;
            } else {
                // Cast normally if:
                //  1. We are not in party
                //  2. Action not in monitor
                //  3. We already target a party member
                if (!inParty || !this.actions.ContainsKey(actionID) || pmap.Contains((uint)targetedActorID)) {
                    ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
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
                var ginput = (GamepadInput*)this.gamepad.GamepadInputAddress;
                this.savedButtonsPressed = (ushort)(ginput->ButtonsPressed & 0x00ffu);
            }

            return ret;
        }

        // sort order eg: [s] t h m r. always place oneself in the 1st place.
        // ranged dps sort order: r = [pr mr]
        // [H] WHM: 24  SCH: 28 AST: 33 SGE: 40
        // [T] PLD: 19  WAR: 21 DRK: 32 GNB: 37
        // [M] MNK: 20  DRG: 22 NIN: 30 SAM: 34 RPR: 39
        // [PR] BRD: 23 MCH: 31 DNC: 38
        // [MR] BLM: 25 SMN: 27 RDM: 35
        private List<uint> GetSortedPartyMemberIDs(string order = "thmr")
        {
            uint selfId = 0;
            string selfName = "";
            if (this.clientState.LocalPlayer is not null) {
                selfId = this.clientState.LocalPlayer.ObjectId;
                selfName = this.clientState.LocalPlayer.Name.ToString();
            }

            var me = new List<(uint, uint, string)>() {(0, selfId, selfName)};

            var t = new List<(uint, uint, string)>();
            var h = new List<(uint, uint, string)>();
            var m = new List<(uint, uint, string)>();
            var pr = new List<(uint, uint, string)>();
            var mr = new List<(uint, uint, string)>();
            
            foreach (PartyMember p in this.partyList) {
                var pid = p.ObjectId;
                if (pid == selfId) continue;
                
                var classId = p.ClassJob.Id;
                var name = p.Name.ToString();
                
                switch (classId)
                {
                    case 24:
                    case 33:
                    case 28:
                    case 40:
                        h.Add((classId, pid, name));
                        break;
                    case 19:
                    case 21:
                    case 32:
                    case 37:
                        t.Add((classId, pid, name));
                        break;
                    case 20:
                    case 22:
                    case 30:
                    case 34:
                    case 39:
                        m.Add((classId, pid, name));
                        break;
                    case 23:
                    case 31:
                    case 38:
                        pr.Add((classId, pid, name));
                        break;
                    case 25:
                    case 27:
                    case 35:
                        mr.Add((classId, pid, name));
                        break;
                    default:
                        break;
                }
            }

            t = t.OrderBy(x => x.Item1).ThenBy(x => x.Item3).ToList();
            h = h.OrderBy(x => x.Item1).ThenBy(x => x.Item3).ToList();
            m = m.OrderBy(x => x.Item1).ThenBy(x => x.Item3).ToList();
            pr = pr.OrderBy(x => x.Item1).ThenBy(x => x.Item3).ToList();
            mr = mr.OrderBy(x => x.Item1).ThenBy(x => x.Item3).ToList();
            
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

            return me.Select(t => t.Item2).ToList();
        }

        public void UpdateActionsInMonitor(Dictionary<uint, string> actions)
        {
            this.actions = actions;
        }

        public void Dispose()
        {
            this.useActionHook.Disable();
            this.useActionHook.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}