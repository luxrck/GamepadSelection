using Dalamud.Game.ClientState.Objects;
using Dalamud.Logging;


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
        LinearBlocked = 3,
        StrictBlocked = 4,
        Ochain = 6,
        // a : macro style
        Async = 7,
    }

    // Used by Ochain
    public enum ComboActionType : int
    {
        // <action>
        Single = 0x01,
        // <action> {m,u}
        Multi = 0x02,
        // <action> *
        Skipable = 0x04,
        // <action> !
        Blocking = 0x08,
        Ability = 0x10,
        // real combo in game. shouldn't skip or block
        Group = 0x20,
        // <action> ?
        SingleSkipable = Single | Skipable,
        // <action> {a,b}?
        MultiSkipable = Multi | Skipable,
    }

    public class ComboAction
    {
        public ComboActionType Type = ComboActionType.Single;
        public int MinimumCount = 1;
        public int MaximumCount = 1;
        public int Group = 0;
        public int Count = 0;
        public DateTime LastTime = DateTime.Now;

        public Actions Actions = Plugin.Actions;

        public GameAction Action = null!;
        public uint ID => Action.ID;
        public string Name => Action.Name;
        public bool IsCasting => Action.IsCasting;
        public bool IsValid => Action.IsValid;
        // public bool ExecuteFinished => Action.Finished;

        public bool Finished => Count >= MaximumCount;
        public bool Executed => Count > 0;
        public uint CastTimeTotalMilliseconds => Action.CastTimeTotalMilliseconds;

        public void Restore() => Count = 0;
        public void Update() => Count += 1;

        public async Task<bool> Wait() => await Action.Wait();
    }

    public class ComboGroup
    {
        public uint GroupID;
        public int CurrentIndex;
        public List<ComboAction> ComboActions;
        public ComboType Type;

        public Actions Actions = Plugin.Actions;
        public DateTime LastTime = DateTime.Now;

        private TargetManager TargetManager = Plugin.TargetManager;

        private SemaphoreSlim actionLock = new SemaphoreSlim(1, 1);
        private SemaphoreSlim actionLockHighPriority = new SemaphoreSlim(1, 1);

        private bool InComboState => Plugin.GamepadActionManager.InComboState;

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
                case "lb":
                    Type = ComboType.LinearBlocked; break;
                case "sb":
                    Type = ComboType.StrictBlocked; break;
                case "o":
                    Type = ComboType.Ochain; break;
                case "a":
                    Type = ComboType.Async; break;
                default:
                    Type = ComboType.Linear; break;
            }
            ComboActions = actions;
        }

        public uint Current(uint lastComboAction = 0, float comboTimer = 0f)
        {
            return ComboActions.Count > 0 ? ComboActions[CurrentIndex%ComboActions.Count].ID : GroupID;
        }

        public bool Contains(uint actionID) => ComboActions.Any(x => Actions.Equals(x.ID, actionID));

        public void StateReset() => CurrentIndex = 0;

        public async Task<bool> StateUpdate(GameAction a, bool succeed = true, DateTime now = default(DateTime))
        {
            if (ComboActions.Count == 0) return false;

            // if (!succeed) return false;
            var actionID = a.ID;
            var cstatus = a.Status;

            if (!Plugin.Ready || !Plugin.Player) return false;
            if (cstatus == ActionStatus.Locking) return false;

            var index = ComboActions.FindIndex(CurrentIndex, x => Actions.Equals(x.ID, actionID));
            if (index == -1)
                index = ComboActions.FindIndex(0, x => Actions.Equals(x.ID, actionID));

            var originalIndex = index;

            if (index == -1) index = CurrentIndex;

            var caction = ComboActions[index % ComboActions.Count];
            var cadjust = Actions.AdjustedActionID(caction.ID);
            var crgroup = Actions.RecastGroup(cadjust);

            cstatus = originalIndex == -1 ? Actions.ActionStatus(cadjust) : cstatus;

            // only handle: Ready, Pending, NotSatisfied and NotLearned
            if (cstatus != ActionStatus.Ready && cstatus != ActionStatus.Pending && cstatus != ActionStatus.NotSatisfied && cstatus != ActionStatus.NotLearned)
                return true;

            var baseActionID = Actions.BaseActionID(actionID);

            // *a1 -> a2 -> a3 -> a1 -> a4
            // task1 : Update(a1 Ready) -> Lock -> DoSomething -> Wait(500ms) -> CurrentIndex++ -> Unlock
            // task2 :      |-> Update(a1 Pending) -> Give up if not waited -> ...
            // 0    1    2    3    4    5    6    7    8    9    10
            // a         b
            //      a
            //                a
            if (originalIndex == -1) {
                if (Type != ComboType.Ochain) {
                    return false;
                } else {
                    if (!(await this.actionLock.WaitAsync(0))) return false;
                    if (!(await this.actionLockHighPriority.WaitAsync(0))) {
                        this.actionLock.Release();
                        return false;
                    }
                }
            } else {
                await this.actionLock.WaitAsync();
                // if ((now - this.LastTime).TotalMilliseconds < animationTotal) {
                if (now < this.LastTime) {
                    // 这可以确保在小于animationDelay的时间段内发出的pending actions可以被忽略
                    if (cstatus == ActionStatus.Pending) {
                        this.actionLock.Release();
                        return false;
                    } else if (cstatus != ActionStatus.Ready) {
                        if (!(await this.actionLockHighPriority.WaitAsync(0))) {
                            this.actionLock.Release();
                            return false;
                        }
                    } else {
                        await this.actionLockHighPriority.WaitAsync();
                    }
                } else {
                    await this.actionLockHighPriority.WaitAsync();
                }
            }

            // a1 a2[notlearned] a3
            // 如果a1 a2是同一group, a1执行完之后, 查询a2的status将会是pending或locking
            // 等到a2真正可以被执行的时候(比如下一个gcd开始), 它的status才会变成ready(或notlearned等)
            if (cstatus == ActionStatus.NotLearned && caction.Type != ComboActionType.Blocking) {
                // PluginLog.Debug($"[ComboStateUpdate] action not learned. {Actions.Name(caction.ID)} at {CurrentIndex} of Group: {GroupID}. Triggerd by {actionID}");
                CurrentIndex = (index + 1) % ComboActions.Count;
                this.actionLockHighPriority.Release();
                this.actionLock.Release();
                return true;
            }

            // succeed: UseAction成功执行. 由于是non-blocking, 不能表示之后Action真的成功了.
            // Action.Succeed: 如果需要咏唱, 则代表成功咏唱, 即Action真正被成功施放.
            // 注意不是cstatus
            int animationDelay = 0;
            if (originalIndex != -1) {
                // 例如: 抽卡 -> 出卡
                // 抽卡结束, 出卡技能不一定可以立刻可用, 所以等一等.
                // 目前需要咏唱的技能不可以立即Finished.
                // PluginLog.Debug($"starting wait for action: {caction.Name} done.");
                animationDelay = a.Finished ? 150 : 50;
                var aa = DateTime.Now;
                // PluginLog.Debug($"Try wait. {caction.ID}, finished: {a.Finished}");
                if (!await a.Wait()) {
                    // PluginLog.Debug($"[ComboStateUpdate][Casting] Group: {GroupID}, action: {caction.ID} failed.");
                    this.actionLockHighPriority.Release();
                    this.actionLock.Release();
                    return false;
                }
                var bb = DateTime.Now;
                // PluginLog.Debug($"Try wait...done {caction.ID} {(bb-aa).TotalMilliseconds}");
            }

            // 因为可能需要wait, 所以放在这里
            var cremain = Actions.RecastTimeRemain(cadjust);

            // 到这里只存在三种状态: Ready, Pending, NotSatisfied
            switch (Type)
            {
                // m l s lb sb
                case ComboType.Manual:
                    index = (index + 1) % ComboActions.Count;
                    break;
                case ComboType.Strict:
                    if (!Actions.Equals(ComboActions[CurrentIndex].ID, actionID))
                        break;
                    if (cstatus == ActionStatus.Ready || cstatus == ActionStatus.NotSatisfied) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    } else if (cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.StrictBlocked:
                    index = CurrentIndex;
                    if (!Actions.Equals(ComboActions[CurrentIndex].ID, actionID))
                        break;
                    if (cstatus == ActionStatus.Ready) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.Linear:
                    if (cstatus == ActionStatus.Ready || cstatus == ActionStatus.NotSatisfied) {
                        index = (index + 1) % ComboActions.Count;
                    } else if (cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                        index = (index + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.LinearBlocked:
                    if (cstatus == ActionStatus.Ready) {
                        index = (index + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.Ochain:
                    // usually by GamepadActionManager.UpdateFramework
                    // or actions in other group
                    if (originalIndex == -1) {
                        originalIndex = index;
                        switch (caction.Type)
                        {
                            // 跳NotSatisfied需要确保action已经执行
                            case ComboActionType.Single:
                            case ComboActionType.Multi:
                            case ComboActionType.SingleSkipable:
                            case ComboActionType.MultiSkipable:
                                if (cstatus == ActionStatus.NotSatisfied && caction.Executed || cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                                    index += 1; // caction.Restore();
                                }
                                break;
                            // 全跳
                            case ComboActionType.Skipable:
                                if (cstatus == ActionStatus.NotSatisfied || cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                                    index += 1;
                                }
                                break;
                            case ComboActionType.Group:
                                if (!InComboState) {
                                    var cgroup = caction.Group;
                                    if (cgroup <= 0) break;
                                    for (; index >= 0; index--) {
                                        if (ComboActions[index].Group != cgroup) {
                                            break;
                                        }
                                    }
                                    index++;
                                }
                                break;
                        }

                        animationDelay = 0;
                    } else {
                        switch (caction.Type)
                        {
                            case ComboActionType.Skipable:
                            case ComboActionType.Single:
                            case ComboActionType.SingleSkipable:
                            case ComboActionType.Multi:
                            case ComboActionType.MultiSkipable:
                                if (cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalSeconds) {
                                    index += 1; animationDelay = 0;
                                } else if (cstatus == ActionStatus.Ready) {
                                    if (succeed) {
                                        // caction.Count += 1;
                                        caction.Update();
                                    }
                                    if (caction.Finished) { // Count >= MaximumCount
                                        index += 1; // caction.Restore();
                                    } else if (caction.Count >= caction.MinimumCount) {
                                        var naction = ComboActions[(index+1)%ComboActions.Count];
                                        var nadjust = Actions.AdjustedActionID(naction.ID);
                                        var nstatus = Actions.ActionStatus(nadjust);
                                        var nrgroup = Actions.RecastGroup(nadjust);
                                        var nremain = Actions.RecastTimeRemain(nadjust);
                                        if (nstatus == ActionStatus.Ready || nrgroup == crgroup || nstatus == ActionStatus.Pending && nremain <= Configuration.GlobalCoolingDown.TotalSeconds) {
                                            index += 1; // caction.Restore();
                                        }
                                    }
                                } else if (cstatus == ActionStatus.NotSatisfied) {
                                    if (caction.Type == ComboActionType.Single || caction.Type == ComboActionType.Multi) {
                                        // 未执行时, 必须等待.
                                        // 直到执行过之后再次出现NotSatisfied时才可被自动跳过
                                        if (caction.Executed) { // Count > 0
                                            index += 1; animationDelay = 0; // caction.Restore();
                                        }
                                        // else {
                                        //     // caction.Executed = true;
                                        //     caction.Update(); // <---- 防止卡住. 点两次
                                        // }
                                    } else if (caction.Type == ComboActionType.SingleSkipable || caction.Type == ComboActionType.MultiSkipable || caction.Type == ComboActionType.Skipable) {
                                        // index += 1; animationDelay = 0; // caction.Restore();  // 点一次
                                        caction.Update();   // 不满足点一次跳过
                                    }
                                }
                                break;
                            case ComboActionType.Blocking:  // Pending ?
                                if (cstatus == ActionStatus.Ready) {
                                    if (succeed)
                                        index += 1;
                                } else {
                                    // caction.Count += 1;
                                    caction.Update();
                                    if (caction.Count >= 16) {   // <--- 点16次
                                        index += 1; // caction.Restore();
                                    }
                                }
                                break;
                            case ComboActionType.Group:
                                if (cstatus == ActionStatus.Ready) {
                                    if (succeed)
                                        index += 1;
                                }
                                break;
                        }

                        // PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, CurrentIndex: {CurrentIndex}, origIndex: {originalIndex}, index: {index}, action: {Actions[caction.ID]}, id: {caction.ID}, exec?: {caction.Executed}, status: {cstatus}, type: {caction.Type}, count: {caction.Count}, ucount: {caction.MaximumCount}, crgroup: {crgroup}, remain: {cremain}, iscasting: {Plugin.Player!.IsCasting}, usetype: {a.UseType}");
                    }
                    break;
                case ComboType.Async:
                    // TODO
                    break;
                default:
                    break;
            }

            // 同一个Action可能会以不跳的Status被多次执行
            // *a1 a2 a3
            // a1! && Ready -> CurrentIndex++
            // wait 20ms
            // a! && Pending -> ??? 应该忽略这个
            if (originalIndex != -1 && originalIndex != index && CurrentIndex != index) {
                // if (animationDelay > 0)
                // PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, animationDelay: {animationDelay}ms. {Actions.Name(caction.ID)} -> {Actions.Name(ComboActions[index%ComboActions.Count].ID)}");

                await Task.Delay(animationDelay);

                // 反正能力技之间的插入也有时间间隔, 不如等一等, 放动画
                await Task.Delay(animationDelay);

                // 切换到下一个action之前把当前action的状态复位
                caction.Restore();
                CurrentIndex = index % ComboActions.Count;

                // CurrentIndex更新之后不代表就立刻转移到了下一个技能, 到GetIcon更新技能图标还有一段时间.
                // 继续sleep, 留给GetIcon一点时间, 到图标更新完成.
                await Task.Delay(50);

                // 应该做出假设: 此时已经成功转移到下一个技能, 并且技能图标已经更新.
                // 那么需要把这之前等待的其它Task全部清除. 只处理在这之后发出的Task.
                this.LastTime = DateTime.Now;
                // PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, animationDelay: {animationDelay}ms. {Actions.Name(caction.ID)} -> {Actions.Name(ComboActions[index%ComboActions.Count].ID)} done");
            }

            this.actionLockHighPriority.Release();
            this.actionLock.Release();

            return true;
        }
    }

    public class ComboManager
    {
        public Dictionary<uint, ComboGroup> ComboGroups = new Dictionary<uint, ComboGroup>();
        public Actions Actions = Plugin.Actions;

        public ComboManager() {}
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

        public async Task<bool> StateUpdate(GameAction a, bool succeed = true, DateTime timestamp = default(DateTime))
        {
            return (await Task.WhenAll(ComboGroups.Select(i => i.Value.StateUpdate(a, succeed, timestamp)).ToArray())).Any();
        }

        public uint Current(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => ComboGroups.ContainsKey(groupID) ? ComboGroups[groupID].Current(lastComboAction, comboTimer) : groupID;
        public bool Contains(uint actionID) => ComboGroups.Any(x => x.Value.Contains(actionID));
        public bool ContainsGroup(uint actionID) => ComboGroups.ContainsKey(actionID);
    }
}