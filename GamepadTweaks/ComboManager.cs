using Dalamud.Logging;
using Dalamud.Game.ClientState.Objects;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game;


// l : 线性调用
// ls: 线性调用 (跳过后续不可用的技能)
// p : 优先级调用 (每次从上到下, 跳过不可用的技能)
// p : 以太 -> 星极 -> 耀 -> 风 -> 土 -> 火 -> 毁灭 -> 吸收 -> 龙神 -> 溃烂 -> 醒梦 : 毁灭
// p : 以太 -> 星极 -> 辉 -> 风 -> 土 -> 火 -> 迸裂 -> 抽取 -> 龙神 -> 痛苦 -> 醒梦 : 迸发

namespace GamepadTweaks
{
    public enum ComboType : int
    {
        // l
        Linear = 1,

        // ls
        // Skip non-ready actions
        LinearWithSkip = 2,

        // p
        // Acts just like execute macro.
        Priority = 3,

        // m
        Manual = 4,

        // s
        // 严格按照序列从头执行到尾
        Strict = 5,
    }

    public class ComboGroup
    {
        public uint GroupID;
        public int CurrentIndex;
        public List<uint> ComboActions;
        public ComboType Type;

        public ActionMap Actions = new ActionMap();

        private TargetManager TargetManager = Plugin.TargetManager;
        private Dictionary<uint, int> ActionPos = new Dictionary<uint, int>();

        public ComboGroup(uint id, List<uint> actions, string ctype = "l")
        {
            GroupID = id;
            CurrentIndex = 0;
            switch (ctype) {
                case "l":
                    Type = ComboType.Linear; break;
                case "ls":
                    Type = ComboType.LinearWithSkip; break;
                case "p":
                    Type = ComboType.Priority; break;
                case "m":
                    Type = ComboType.Manual; break;
                case "s":
                    Type = ComboType.Strict; break;
                 default:
                    Type = ComboType.Linear; break;
            }
            ComboActions = actions;
            
            if (Type != ComboType.Strict)
                for (var i=0; i<actions.Count; i++) {
                    ActionPos.Add(actions[i], i);
                }
        }

        public uint Current(uint lastComboAction = 0, float comboTimer = 0f, IntPtr actionManager = default(IntPtr))
        {
            return ComboActions[CurrentIndex];
            // PluginLog.Debug($"{lastComboAction} {comboTimer}");
            // if (lastComboAction == 0 || comboTimer <= 0) {
            //     return ComboActions[CurrentIndex];
            // }
            
            // int index = ActionPos.ContainsKey(lastComboAction) ? ActionPos[lastComboAction] : -1;
            // var actionID = index < 0 ? ComboActions[CurrentIndex] : ComboActions[(index+1)%ComboActions.Count];

            // return actionID;
        }

        public bool Contains(uint actionID) => Type != ComboType.Strict ? ActionPos.ContainsKey(actionID) : ComboActions.Contains(actionID);
        public bool StateUpdate(uint actionID, ActionStatus status = ActionStatus.Done, IntPtr actionManager = default(IntPtr))
        {
            // 1 -> 2 -> 3 : 1
            // 1 -> 2 : 2
            var baseActionID = Actions.BaseActionID(actionID);

            var index = -1;
            if (Type != ComboType.Strict) {
                index = ActionPos.ContainsKey(actionID) ? ActionPos[actionID] : -1;
                if (index == -1)
                    index = ActionPos.ContainsKey(baseActionID) ? ActionPos[baseActionID] : -1;
            } else {
                index = ComboActions.IndexOf(actionID);
            }
            
            if (index == -1) return false;
            
            var rgroup = GetRecastGroup(actionManager, actionID);
            var recast = GetRecastTimeRemain(actionManager, actionID);

            PluginLog.Debug($"ComboGroup: {GroupID}, Index: {CurrentIndex}, action: {baseActionID} {rgroup} {status} {recast}");
    
            int offset = 0;
            switch (Type)
            {
                case ComboType.Manual:
                    CurrentIndex = (index + 1) % ComboActions.Count;
                    break;
                case ComboType.Strict:
                    PluginLog.Debug($"[Strict]: {CurrentIndex} {ComboActions[CurrentIndex]} {actionID} status: {status}, remain: {recast}");
                    if (recast > 3)
                        break;
                    if ((status == ActionStatus.Done || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned) && Actions.Equals(ComboActions[CurrentIndex], actionID))
                        CurrentIndex = (CurrentIndex + 1) % ComboActions.Count;
                    break;
                case ComboType.Linear:
                    if ((status == ActionStatus.Done || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned))
                        CurrentIndex = (index + 1) % ComboActions.Count;
                    break;
                case ComboType.LinearWithSkip:
                    // 同组技能不能跳过
                    // 如果能力技在cd, 可以跳过能力技
                    offset = 1;
                    for (; offset<ComboActions.Count; offset++) {
                        var naction = ComboActions[(index+offset)%ComboActions.Count];
                        var nstatus = GetActionStatus(actionManager, naction);
                        var nrgroup = GetRecastGroup(actionManager, naction);
                        var nrecast = GetRecastTimeRemain(actionManager, naction);
                        PluginLog.Debug($"{index} {offset} {naction} {nrgroup} {nstatus} {nrecast}");
                        
                        // a1 b1 a2 *a3 : 不可跳过a1
                        // *a1 b1 a2 a3 : 可以跳过b1, 限制: a1(链首)必须执行完!.
                        // a1 *b1 a2 a3 : a1 a2 a3都没ready, 则index+1
                        if (nrgroup == rgroup) {
                            if (status == ActionStatus.Done) {
                                CurrentIndex = (index + offset) % ComboActions.Count;
                            }
                            break;
                        } else if (status == ActionStatus.Done && nstatus == ActionStatus.Done) {
                            CurrentIndex = (index + offset) % ComboActions.Count;
                            break;
                        }
                    }
                    
                    if (offset == ComboActions.Count)
                        CurrentIndex = (index + 1) % ComboActions.Count;
                    
                    break;
                case ComboType.Priority:
                    // if (status != ActionStatus.Done)
                        // break;
                    // offset = 0;
                    // for (; offset<ComboActions.Count; offset++) {
                    //     var naction = ComboActions[offset%ComboActions.Count];
                    //     var nstatus = GetActionStatus(actionManager, naction);
                    //     var nrgroup = GetRecastGroup(actionManager, naction);
                    //     var nrecast = GetRecastTimeRemain(actionManager, naction);
                    //     PluginLog.Debug($"{index} {offset} {naction} {nrgroup} {nstatus} {nrecast}");
                        
                    //     if (status == ActionStatus.Done) {
                    //         if (nstatus == ActionStatus.Pending) {
                    //             CurrentIndex = offset;
                    //             break;
                    //         }
                    //     }

                    //     // if (nstatus == ActionStatus.Done) {
                    //     //     CurrentIndex = offset;
                    //     //     break;
                    //     // }
                    // }
                    
                    // if (offset == ComboActions.Count)
                        // CurrentIndex = (index + 1) % ComboActions.Count;
                    break;
                default:
                    break;
            }

            return true;
        }

        private ActionStatus GetActionStatus(IntPtr actionManager, uint actionID)
        {
            uint status = 0;
            uint targetedActorID = 3758096384U;
            if (TargetManager.SoftTarget is not null) {
                targetedActorID = TargetManager.SoftTarget.ObjectId;
            } else if (TargetManager.Target is not null) {
                targetedActorID = TargetManager.Target.ObjectId;
            }
            unsafe {
                var am = (ActionManager*)actionManager;
                if (am != null) {
                    status = am->GetActionStatus(Actions.ActionType(actionID), actionID, targetedActorID);
                }
            }
            try {
                return (ActionStatus)status;
            } catch(Exception e) {
                PluginLog.Debug($"Exception: {e}");
                return ActionStatus.Invalid;
            }
        }

        private float GetRecastTimeRemain(IntPtr actionManager, uint actionID)
        {
            float recast = 0f;
            unsafe {
                var am = (ActionManager*)actionManager;
                if (am != null) {
                    var elapsed = am->GetRecastTimeElapsed(Actions.ActionType(actionID), actionID);
                    var total = am->GetRecastTime(Actions.ActionType(actionID), actionID);
                    recast = total - elapsed;
                }
            }
            return recast;
        }

        private int GetRecastGroup(IntPtr actionManager, uint actionID)
        {
            int group = 0;
            unsafe {
                var am = (ActionManager*)actionManager;
                if (am != null) {
                    group = am->GetRecastGroup((int)Actions.ActionType(actionID), actionID);
                }
            }
            return group;
        }
    }

    public unsafe class ComboManager
    {
        public Dictionary<uint, ComboGroup> ComboGroups = new Dictionary<uint, ComboGroup>();
        public ActionMap Actions = new ActionMap();

        private ActionManager* am;

        public ComboManager(List<(uint, List<uint>, string)> actions)
        {
            foreach (var (groupID, comboActions, comboType) in actions) {
                // var groupID = i.Key;
                // var comboActions = i.Value;
                var combo = new ComboGroup(groupID, comboActions, comboType);
                ComboGroups.Add(groupID, combo);
            }
            this.am = ActionManager.Instance();
        }

        public bool StateUpdate(uint actionID, ActionStatus status = ActionStatus.Done)
        {
            bool flag = false;
            foreach (var i in ComboGroups) {
                var combo = i.Value;
                var ret = combo.StateUpdate(actionID, status, (IntPtr)this.am);
                if (ret)
                    flag = ret;
            }
            return flag;
        }

        public uint Current(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => ComboGroups.ContainsKey(groupID) ? ComboGroups[groupID].Current(lastComboAction, comboTimer) : groupID;
        public bool Contains(uint actionID) => ComboGroups.Any(x => x.Key == actionID || x.Value.Contains(actionID) || x.Key == Actions.BaseActionID(actionID) || x.Value.Contains(Actions.BaseActionID(actionID)));
        public bool ContainsGroup(uint actionID) => ComboGroups.ContainsKey(actionID);
    }
}