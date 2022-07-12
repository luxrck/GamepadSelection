using Dalamud.Game.ClientState.Objects;
using Dalamud.Logging;

using FFXIVClientStructs.FFXIV.Client.Game;


namespace GamepadTweaks
{
    public enum ComboType : int
    {
        // m : 触发即跳转到下一个, 不进行任何确认
        Manual = 0,
        // l : 如果Action位于combo-chain中, 触发Action则会将该combo-chain的当前状态指向该action
        // a1 *a2 a3 a4 a5 : Pre : Trigger a4
        // a1 a2 a3 *a4 a5 : Now
        Linear = 1,
        // s : 和l类似, 不过严格按照combo-chain的顺序从头到尾.
        // a1 *a2 a3 a4 a5 : Pre : Trigger a3
        // a1 *a2 a3 a4 a5 : Now : Trigger a2
        // a1 a2 *a3 a4 a5 : Nxt
        Strict = 2,
        // ls : 和l类似, 不过可以自动跳过处于cd中的能力技
        // a1 *a2 b1 a4 a5 : Pre : Trigger a2
        // a1 a2 *b1 a4 a5 : Now : if b1 is avaliable
        // a1 a2 b1 *a4 a5 : Now : if b1 in CD
        LinearWithSkip = 3,
        StrictWithSkip = 4,
        LinearBlocked = 5,
        StrictBlocked = 6,
        // p : like macro
        Priority = 7,
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
                case "m":
                    Type = ComboType.Manual; break;
                case "l":
                    Type = ComboType.Linear; break;
                case "s":
                    Type = ComboType.Strict; break;
                case "ls":
                    Type = ComboType.LinearWithSkip; break;
                case "ss":
                    Type = ComboType.StrictWithSkip; break;
                case "lb":
                    Type = ComboType.LinearBlocked; break;
                case "sb":
                    Type = ComboType.StrictBlocked; break;
                case "p":
                    Type = ComboType.Priority; break;
                default:
                    Type = ComboType.Linear; break;
            }
            ComboActions = actions;

            if (Type != ComboType.Strict)
                for (var i=0; i<actions.Count; i++) {
                    ActionPos.Add(actions[i], i);
                }
        }

        public uint Current(uint lastComboAction = 0, float comboTimer = 0f)
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

        public bool Contains(uint actionID) => ComboActions.Contains(actionID);
        public async Task<bool> StateUpdate(uint actionID, ActionStatus status = ActionStatus.Ready)
        {
            // 1 -> 2 -> 3 : 1
            // 1 -> 2 : 2
            var baseActionID = Actions.BaseActionID(actionID);

            // var index = -1;
            // if (Type != ComboType.Strict) {
            //     index = ActionPos.ContainsKey(actionID) ? ActionPos[actionID] : -1;
            //     if (index == -1)
            //         index = ActionPos.ContainsKey(baseActionID) ? ActionPos[baseActionID] : -1;
            // } else {
            //     index = ComboActions.IndexOf(actionID);
            // }

            var index = ComboActions.FindIndex(CurrentIndex, x => Actions.Equals(x, actionID));
            if (index == -1)
                index = ComboActions.FindIndex(0, x => Actions.Equals(x, actionID));

            if (index == -1) return false;

            var rgroup = Actions.RecastGroup(actionID);
            var recast = Actions.RecastTimeRemain(actionID);

            PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, CurrentIndex: {CurrentIndex}, index: {index}, baseAction: {baseActionID} action: {actionID}, rgroup: {rgroup} status: {status} remain: {recast}");

            int offset = 0;
            switch (Type)
            {
                case ComboType.Manual:
                    index = (index + 1) % ComboActions.Count;
                    break;
                case ComboType.Strict:
                case ComboType.StrictBlocked:
                    index = CurrentIndex;
                    PluginLog.Debug($"[{Type}] Index: {CurrentIndex}, ComboCurrent: {ComboActions[CurrentIndex]}");
                    if (!Actions.Equals(ComboActions[CurrentIndex], actionID))
                        break;
                    if ((status == ActionStatus.Ready || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned)) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    } else if (Type == ComboType.StrictBlocked && recast > 2.6) {    // gcd == ~2.5, 设3为了防止到能力技(cd一般>=3)时卡住.
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.Linear:
                case ComboType.LinearBlocked:
                    PluginLog.Debug($"[{Type}] Index: {CurrentIndex}, ComboCurrent: {ComboActions[CurrentIndex]}");
                    if ((status == ActionStatus.Ready || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned)) {
                        index = (index + 1) % ComboActions.Count;
                    } else if (Type == ComboType.Linear && recast > 2.6) {
                        index = (index + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.LinearWithSkip:
                    // 同组技能不能跳过
                    // 如果能力技在cd, 可以跳过能力技
                    offset = 1;
                    for (; offset<ComboActions.Count; offset++) {
                        var naction = ComboActions[(index+offset)%ComboActions.Count];
                        var nstatus = Actions.ActionStatus(naction);
                        var nrgroup = Actions.RecastGroup(naction);
                        var nrecast = Actions.RecastTimeRemain(naction);
                        PluginLog.Debug($"{index} {offset} {naction} {nrgroup} {nstatus} {nrecast}");

                        // a1 b1 a2 *a3 : 不可跳过a1
                        // *a1 b1 a2 a3 : 可以跳过b1, 限制: a1(链首)必须执行完!.
                        // a1 *b1 a2 a3 : a1 a2 a3都没ready, 则index+1
                        if ((status == ActionStatus.Ready || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned)) {
                            if (nrgroup == rgroup) {
                                index = (index + offset) % ComboActions.Count;
                                break;
                            } else {
                                if (nstatus == ActionStatus.Ready || nstatus == ActionStatus.Pending && nrecast <= 2.6) {
                                    index = (index + offset) % ComboActions.Count;
                                    break;
                                }
                            }
                        } else if (recast > 2.6) {
                            index = (index + 1) % ComboActions.Count;
                            break;
                        }
                    }

                    if (offset == ComboActions.Count)
                        index = (index + 1) % ComboActions.Count;

                    break;
                case ComboType.StrictWithSkip:
                    PluginLog.Debug($"[StrictWithSkip] Index: {CurrentIndex}, ComboCurrent: {ComboActions[CurrentIndex]}");
                    if (!Actions.Equals(ComboActions[CurrentIndex], actionID))
                        break;
                    index = CurrentIndex;
                    offset = 1;
                    for (; offset<ComboActions.Count; offset++) {
                        var naction = ComboActions[(index+offset)%ComboActions.Count];
                        var nstatus = Actions.ActionStatus(naction);
                        var nrgroup = Actions.RecastGroup(naction);
                        var nrecast = Actions.RecastTimeRemain(naction);
                        PluginLog.Debug($"{index} {offset} {naction} {nrgroup} {nstatus} {nrecast}");

                        // a1 b1 a2 *a3 : 不可跳过a1
                        // *a1 b1 a2 a3 : 可以跳过b1, 限制: a1(链首)必须执行完!.
                        // a1 *b1 a2 a3 : a1 a2 a3都没ready, 则index+1
                        if ((status == ActionStatus.Ready || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned)) {
                            if (nrgroup == rgroup) {
                                index = (index + offset) % ComboActions.Count;
                                break;
                            } else {
                                if (nstatus == ActionStatus.Ready || nstatus == ActionStatus.Pending && nrecast <= 2.6) {
                                    index = (index + offset) % ComboActions.Count;
                                    break;
                                }
                            }
                        } else if (recast > 2.6) {
                            index = (index + 1) % ComboActions.Count;
                            break;
                        }
                    }

                    if (offset == ComboActions.Count)
                        index = (index + 1) % ComboActions.Count;

                    break;
                case ComboType.Priority:
                    // TODO
                    // may be just using macro is the best choice.
                    break;
                default:
                    break;
            }

            if (CurrentIndex != index) {
                // 反正能力技之间的插入也有时间间隔, 不如等一等, 放动画
                await Task.Delay(500);
                PluginLog.Debug($"{index} {CurrentIndex}");
                CurrentIndex = index;
            }

            return true;
        }
    }

    public class ComboManager
    {
        public Dictionary<uint, ComboGroup> ComboGroups = new Dictionary<uint, ComboGroup>();
        public ActionMap Actions = new ActionMap();

        public ComboManager(List<(uint, List<uint>, string)> actions)
        {
            foreach (var (groupID, comboActions, comboType) in actions) {
                // var groupID = i.Key;
                // var comboActions = i.Value;
                var combo = new ComboGroup(groupID, comboActions, comboType);
                ComboGroups.Add(groupID, combo);
            }
        }

        public async Task<bool> StateUpdate(uint actionID, ActionStatus status = ActionStatus.Ready)
        {
            bool flag = false;
            foreach (var i in ComboGroups) {
                var combo = i.Value;
                var ret = await combo.StateUpdate(actionID, status);
                if (ret)
                    flag = ret;
            }
            return flag;
        }

        public uint Current(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => ComboGroups.ContainsKey(groupID) ? ComboGroups[groupID].Current(lastComboAction, comboTimer) : groupID;
        public bool Contains(uint actionID) => ComboGroups.Any(x => x.Value.Contains(actionID) || x.Value.Contains(Actions.BaseActionID(actionID)));
        public bool ContainsGroup(uint actionID) => ComboGroups.ContainsKey(actionID);
    }
}