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
        Oscript = 8,
    }

    public enum ComboActionType : int
    {
        // none
        Single = 0,
        // +
        Multiple = 1,
        // ?
        Skipable = 2,
        // *
        Key = 3,
        // !
        Blocking = 4,
        Ability = 5,
    }

    public class ComboAction
    {
        public uint ID = 0;
        public ComboActionType Type = ComboActionType.Single;
        public int MinimumCount = 1;
        public int MaximumCount = 1;
        public int Count = 0;
        public bool Executed = false;
        public void Restore() { Count = 0; Executed = false; }
    }

    public class ComboGroup
    {
        public uint GroupID;
        public int CurrentIndex;
        public List<ComboAction> ComboActions;
        public ComboType Type;

        public ActionMap Actions = new ActionMap();

        private TargetManager TargetManager = Plugin.TargetManager;

        private SemaphoreSlim actionLock = new SemaphoreSlim(1, 1);

        public ComboGroup(uint id, List<ComboAction> actions, string ctype = "l")
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
                case "o":
                    Type = ComboType.Oscript; break;
                case "p":
                    Type = ComboType.Priority; break;
                default:
                    Type = ComboType.Linear; break;
            }
            ComboActions = actions;
        }

        public uint Current(uint lastComboAction = 0, float comboTimer = 0f)
        {
            return ComboActions[CurrentIndex].ID;
            // PluginLog.Debug($"{lastComboAction} {comboTimer}");
            // if (lastComboAction == 0 || comboTimer <= 0) {
            //     return ComboActions[CurrentIndex];
            // }

            // int index = ActionPos.ContainsKey(lastComboAction) ? ActionPos[lastComboAction] : -1;
            // var actionID = index < 0 ? ComboActions[CurrentIndex] : ComboActions[(index+1)%ComboActions.Count];

            // return actionID;
        }

        public bool Contains(uint actionID) => ComboActions.Any(x => x.ID == actionID);

        public void StateReset() => CurrentIndex = 0;

        public async Task<bool> StateUpdate(uint actionID, ActionStatus status = ActionStatus.Ready)
        {
            if (status != ActionStatus.Ready && status != ActionStatus.Pending && status != ActionStatus.NotSatisfied && status != ActionStatus.NotLearned)
                return true;

            await this.actionLock.WaitAsync();

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

            var index = ComboActions.FindIndex(CurrentIndex, x => Actions.Equals(x.ID, actionID));
            if (index == -1)
                index = ComboActions.FindIndex(0, x => Actions.Equals(x.ID, actionID));

            // *a1 -> a2 -> a3 -> a1 -> a4
            // task1 : Update(a1) -> Lock -> DoSomething -> Wait(500ms) -> CurrentIndex++ -> Unlock
            // task2 :      |-> Update(a1) -> Wait Lock -> Lock -> DoSomething -> ...
            //
            if (index == -1 && Type != ComboType.Oscript) {
                this.actionLock.Release();
                return false;
            }

            var rgroup = Actions.RecastGroup(actionID);
            var recast = Actions.RecastTimeRemain(actionID);

            // PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, CurrentIndex: {CurrentIndex}, index: {index}, baseAction: {baseActionID} action: {actionID}, rgroup: {rgroup} status: {status} remain: {recast}");

            int animationDelay = 350;
            var originalIndex = index;
            switch (Type)
            {
                case ComboType.Manual:
                    index = (index + 1) % ComboActions.Count;
                    break;
                case ComboType.Strict:
                    if (!Actions.Equals(ComboActions[CurrentIndex].ID, actionID))
                        break;
                    if ((status == ActionStatus.Ready || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned)) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    } else if (recast > Configuration.GlobalCoolingDown.TotalSeconds) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.StrictBlocked:
                    index = CurrentIndex;
                    // PluginLog.Debug($"[{Type}] Index: {CurrentIndex}, ComboCurrent: {ComboActions[CurrentIndex]}");
                    if (!Actions.Equals(ComboActions[CurrentIndex].ID, actionID))
                        break;
                    if ((status == ActionStatus.Ready || status == ActionStatus.NotLearned)) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.Linear:
                    // PluginLog.Debug($"[{Type}] Index: {CurrentIndex}, ComboCurrent: {ComboActions[CurrentIndex]}");
                    if ((status == ActionStatus.Ready || status == ActionStatus.NotSatisfied || status == ActionStatus.NotLearned)) {
                        index = (index + 1) % ComboActions.Count;
                    } else if (Type == ComboType.Linear && recast > Configuration.GlobalCoolingDown.TotalSeconds) {
                        index = (index + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.LinearBlocked:
                    // PluginLog.Debug($"[{Type}] Index: {CurrentIndex}, ComboCurrent: {ComboActions[CurrentIndex]}");
                    if ((status == ActionStatus.Ready || status == ActionStatus.NotLearned)) {
                        index = (index + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.Oscript:
                    // 毁荡* -> 龙神附体 -> 星极超流 -> 火神召唤 -> [宝石耀, 宝石辉] -> 风神召唤 -> [宝石耀, 宝石辉] -> 土神召唤 -> [宝石耀, 宝石辉] -> 龙神迸发
                    // o : 毁荡* -> 龙神附体! -> 星极超流 -> 火神召唤 -> 宝石耀{2} -> 风神召唤 -> 宝石耀{4} -> 土神召唤 -> 宝石耀{4} -> 龙神迸发 : 宝石耀
                    // 迸裂* -> 龙神附体 -> 星极超流 -> 火神召唤 -> 宝石辉+ -> 风神召唤 -> 宝石辉+ -> 土神召唤 -> 宝石辉+ -> 龙神迸发
                    // 重劈 -> 下踢? -> 凶残裂 -> 暴风斩
                    // 抽卡! -> 出卡

                    // *: Key Action
                    // +: 1 or more times
                    // ?: Skip if not avaliable
                    // !: Wait for this action
                    var caction = ComboActions[(index == -1 ? CurrentIndex : index) % ComboActions.Count];
                    var cadjust = Actions.AdjustedActionID(caction.ID);
                    var cstatus = index == -1 ? Actions.ActionStatus(cadjust) : status;
                    var crgroup = Actions.RecastGroup(cadjust);
                    var cremain = Actions.RecastTimeRemain(cadjust);

                    if (cstatus == ActionStatus.NotLearned) {
                        CurrentIndex = (CurrentIndex + 1) % ComboActions.Count;
                        this.actionLock.Release();
                        return true;
                    }

                    // 刚才调用的是其它组的Action
                    if (index == -1) {
                        index = CurrentIndex;

                        switch (caction.Type)
                        {
                            case ComboActionType.Key:
                            case ComboActionType.Single:
                                if (cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                                    index += 1;
                                }
                                break;
                            case ComboActionType.Multiple:
                                if (cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds || caction.Executed && cstatus == ActionStatus.NotSatisfied) {
                                    index += 1; caction.Restore();
                                }
                                break;
                            case ComboActionType.Skipable:
                                if (cstatus == ActionStatus.NotSatisfied || cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                                    index += 1;
                                }
                                break;
                        }

                        animationDelay = 0;
                    } else {
                        if (cstatus != ActionStatus.Ready && cstatus != ActionStatus.Pending && cstatus != ActionStatus.NotSatisfied)
                            break;

                        switch (caction.Type)
                        {
                            case ComboActionType.Key:
                            case ComboActionType.Single:
                            case ComboActionType.Multiple:
                                if (cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                                    index += 1;
                                } else if (cstatus == ActionStatus.Ready) {
                                    caction.Count += 1; caction.Executed = true;
                                    if (caction.Count >= caction.MaximumCount) {
                                        index += 1; caction.Restore();
                                    } else if (caction.Count >= caction.MinimumCount) {
                                        var naction = ComboActions[(index+1)%ComboActions.Count];
                                        var nadjust = Actions.AdjustedActionID(naction.ID);
                                        var nstatus = Actions.ActionStatus(nadjust);
                                        var nrgroup = Actions.RecastGroup(nadjust);
                                        var nremain = Actions.RecastTimeRemain(nadjust);
                                        if (nstatus == ActionStatus.Ready || nrgroup == crgroup || nstatus == ActionStatus.Pending && nremain <= Configuration.GlobalCoolingDown.TotalSeconds) {
                                            index += 1; caction.Restore();
                                        }
                                    }
                                } else if (caction.Executed && cstatus == ActionStatus.NotSatisfied) {
                                    index += 1; caction.Restore(); animationDelay = 0;
                                }
                                break;
                            case ComboActionType.Skipable:
                                if (cstatus == ActionStatus.Ready || cstatus == ActionStatus.NotSatisfied || cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                                    index += 1;
                                }
                                break;
                            case ComboActionType.Blocking:  // Pending ?
                                if (cstatus == ActionStatus.Ready) {
                                    index += 1;
                                }
                                break;
                        }
                        PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, CurrentIndex: {CurrentIndex}, origIndex: {originalIndex}, index: {index}, action: {Actions[caction.ID]} {caction.Count}, status: {cstatus}, type: {caction.Type} , crgroup: {crgroup}, remain: {cremain}");
                        // animationDelay = 500;
                    }
                    // caction.LastTime = DateTime.Now;
                    break;
                case ComboType.Priority:
                    // TODO
                    // may be just using macro is the best choice.
                    break;
                default:
                    break;
            }

            if (originalIndex != index) {
                // 反正能力技之间的插入也有时间间隔, 不如等一等, 放动画
                await Task.Delay(animationDelay);
                CurrentIndex = index % ComboActions.Count;
            }

            this.actionLock.Release();

            return true;
        }
    }

    public class ComboManager
    {
        public Dictionary<uint, ComboGroup> ComboGroups = new Dictionary<uint, ComboGroup>();
        public ActionMap Actions = new ActionMap();

        public ComboManager(List<(uint, List<ComboAction>, string)> actions)
        {
            foreach (var (groupID, comboActions, comboType) in actions) {
                // var groupID = i.Key;
                // var comboActions = i.Value;
                var combo = new ComboGroup(groupID, comboActions, comboType);
                ComboGroups.Add(groupID, combo);
            }
        }

        public void StateReset(uint groupID = 0)
        {
            if (groupID == 0) {
                foreach (var i in ComboGroups) {
                    i.Value.StateReset();
                }
            } else if (ComboGroups.ContainsKey(groupID)) {
                ComboGroups[groupID].StateReset();
            }
        }

        public async Task<bool> StateUpdate(uint actionID, ActionStatus status = ActionStatus.Ready)
        {
            return (await Task.WhenAll(ComboGroups.Select(i => i.Value.StateUpdate(actionID, status)).ToArray())).Any();
            // foreach (var i in ComboGroups) {
            //     var combo = i.Value;
            //     var ret = await combo.StateUpdate(actionID, status);
            //     if (ret)
            //         flag = ret;
            // }
        }

        public uint Current(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => ComboGroups.ContainsKey(groupID) ? ComboGroups[groupID].Current(lastComboAction, comboTimer) : groupID;
        public bool Contains(uint actionID) => ComboGroups.Any(x => x.Value.Contains(actionID) || x.Value.Contains(Actions.BaseActionID(actionID)));
        public bool ContainsGroup(uint actionID) => ComboGroups.ContainsKey(actionID);
    }
}