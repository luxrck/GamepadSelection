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

        // private Hook<ControllerPoll> controllerPoll;
        // private delegate int ControllerPoll(IntPtr controllerInput);
        // private int ControllerPollDetour(IntPtr controllerInput)
        // {
        //     var original = this.gamepadPoll.Original(gamepadInput);
        //     try {
        //         var input = (GamepadInput*)gamepadInput;
        //         this.ButtonsPressed = input->ButtonsPressed;
        //         this.ButtonsReleased = input->ButtonsReleased;
        //         this.ButtonsRepeat = input->ButtonsRepeat;
        // }
     
        private Hook<UseActionDelegate> useActionHook;
        private delegate byte UseActionDelegate(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, IntPtr a8);
        private byte UseActionDetour(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, IntPtr a8)
        {
            byte ret = 0;
            var a = this.gsAction;
            bool inParty = this.partyList.Length > 0;
            var pmap = this.GetSortedPartyMemberIDs();
        
            PluginLog.Log($"ActionID: {actionID}, SavedActionID: {a.actionID}, TargetID: {targetedActorID}, inGSM: {this.inGamepadSelectionMode}");
            // PluginLog.Log($"Me: {pmap[0]}, ClassJob: {this.clientState.LocalPlayer.ClassJob.Id} inParty: {inParty}");

            if (this.inGamepadSelectionMode) {
                try {
                    unsafe {
                        var ginput = (GamepadInput*)this.gamepad.GamepadInputAddress;

                        // 1. Only use [up down left right y a x b]
                        // 2. Resolve buttons priority
                        // 目前GSM只在lt/rt按下, 即激活十字热键栏预备施放技能时可用.
                        // 好处是省去了判断上一时刻和现在的button state的异同(eg: 是不是一直在hold button???).
                        // 坏处是不够灵活?
                        int buttons = (ginput->ButtonsPressed & 0x00ff);
                        // 1 0 1 0 Prev
                        // 1 1 0 0 Now
                        // 0 1 0 0 True Buttons
                        // 如果上一次状态和本次相同, 我们不能判断到底是哪个按键触发了Action.
                        // 同一时刻只能施放一个技能, 那么UseAction被调用的时候应该每次只有一个按键状态出现变动
                        buttons = (buttons ^ this.savedButtonsPressed) & buttons;
                        
                        var order = this.actions[a.actionID].ToLower().Trim().Split(" ").Where((a) => a != "").ToList();
                        var gsTargetedActorIndex = order.FindIndex((b) => ButtonMap.ContainsKey(b) ? (ButtonMap[b] & buttons) > 0 : false);
                        
                        PluginLog.Log($"[Party] ID: {this.partyList.PartyId}, Length: {this.partyList.Length}, index: {gsTargetedActorIndex}");
                        if (pmap.Count > 0) {
                            gsTargetedActorIndex %= pmap.Count;
                            
                            var gsTargetedActorID = targetedActorID;
                            if (!pmap.Contains((uint)targetedActorID)) {   // Disable GSM if we already selected a member.
                                gsTargetedActorID = (long)(uint)pmap[gsTargetedActorIndex];
                            }
                            a.targetedActorID = gsTargetedActorID;
                        }
                        
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

        // sort order: [s] t h m r. always place oneself in the 1st place.
        // WHM: 24  AST: 33 SCH: 28 SGE: 40
        // WAR: 21  PLD: 19 DRK: 32 GNB: 37
        // MNK: 20  DRG: 22 NIN: 30 SAM: 34 RPR: 39
        private List<uint> GetSortedPartyMemberIDs(string order = "thmr")
        {
            uint selfId = 0;
            if (this.clientState.LocalPlayer)
                selfId = this.clientState.LocalPlayer.ObjectId;

            var me = new List<(uint, uint)>() {(0, selfId)};

            var t = new List<(uint, uint)>();
            var h = new List<(uint, uint)>();
            var m = new List<(uint, uint)>();
            var r = new List<(uint, uint)>();
            
            foreach (PartyMember p in this.partyList) {
                var pid = p.ObjectId;
                if (pid == selfId) continue;
                
                var classId = p.ClassJob.Id;
                
                switch (classId)
                {
                    case 24:
                    case 33:
                    case 28:
                    case 40:
                        h.Add((classId, pid));
                        break;
                    case 19:
                    case 21:
                    case 32:
                    case 37:
                        t.Add((classId, pid));
                        break;
                    case 20:
                    case 22:
                    case 30:
                    case 34:
                    case 39:
                        m.Add((classId, pid));
                        break;
                    default:
                        r.Add((classId, pid));
                        break;
                }
            }

            t.Sort((a,b) => a.Item1.CompareTo(b.Item1));
            h.Sort((a,b) => a.Item1.CompareTo(b.Item1));
            m.Sort((a,b) => a.Item1.CompareTo(b.Item1));
            r.Sort((a,b) => a.Item1.CompareTo(b.Item1));

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
                        me.AddRange(r); break;
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