using Dalamud.Game.ClientState.Objects;
using Dalamud.Logging;
using System.Text.RegularExpressions;


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

        public uint ID = 0;
        public bool IsValid => ID > 0;

        public bool Finished => Count >= MaximumCount;
        public bool Executed => Count > 0;

        public void Restore() => Count = 0;
        public void Update() => Count += 1;
    }

    public class ComboGroup
    {
        public uint GroupID;
        public int CurrentIndex;
        public List<ComboAction> ComboActions;
        public ComboType Type;

        public DateTime LastTime = DateTime.Now;
        public uint LastActionID = 0;
        public int LastIndex = 0;

        public static Actions Actions = Plugin.Actions;
        
        private SemaphoreSlim actionLock = new SemaphoreSlim(1, 1);
        private SemaphoreSlim actionLockHighPriority = new SemaphoreSlim(1, 1);

        private bool InComboState => Plugin.GamepadActionManager.InComboState;
        private uint LastComboAction => Plugin.GamepadActionManager.LastComboAction;

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

        public static ComboGroup? FromString(string combo)
        {
            if (String.IsNullOrEmpty(combo)) return null;

            var ss = combo.Split(":");
            var sh = ss[0].Trim().Split(" ", 2);
            var comboType = sh[0].Trim();
            var comboGroup = sh[1].Trim();
            var groupID = Actions.ID(comboGroup);

            // PluginLog.Debug($"\tin {comboType} : {groupID} {comboGroup}");

            var comboActions = ss[1].Split("->").Where(a => !String.IsNullOrEmpty(a) && !String.IsNullOrWhiteSpace(a)).Select(a => {
                var pattern = new Regex(@"(?<action>[\w\s]+\w)(?<type>[\d\,\{\}\*\!\?#]+)?", RegexOptions.Compiled);
                var match = pattern.Match(a.Trim());
                var action = match.Groups.ContainsKey("action") ? match.Groups["action"].ToString().Trim() : "";
                var type = match.Groups.ContainsKey("type") ? match.Groups["type"].ToString().Trim() : "";

                var comboActionType = ComboActionType.Single;
                int minCount = 1;
                int maxCount = 1;
                var comboID = 0;
                switch (type)
                {
                    case "":
                        comboActionType = ComboActionType.Single; break;
                    case "?":
                        comboActionType = ComboActionType.SingleSkipable; break;
                    case "*":
                        comboActionType = ComboActionType.Skipable; break;
                    case "!":
                        comboActionType = ComboActionType.Blocking; break;
                    default:
                        if (type.StartsWith("{")) {
                            comboActionType = ComboActionType.Multi;
                            var tpattern = new Regex(@"(?<mc>\d+)\s*(,\s*(?<uc>\d+))?\s*}(?<sk>[?])?", RegexOptions.Compiled);
                            var tmatch = tpattern.Match(type);
                            minCount = Int32.Parse(tmatch.Groups["mc"].ToString());
                            if (tmatch.Groups.ContainsKey("uc")) {
                                var ucs = tmatch.Groups["uc"].ToString().Trim();
                                maxCount = !String.IsNullOrEmpty(ucs) ? Int32.Parse(ucs) : minCount;
                            } else {
                                maxCount = minCount;
                            }
                            if (tmatch.Groups.ContainsKey("sk")) {
                                var sks = tmatch.Groups["sk"].ToString().Trim();
                                if (sks == "?")
                                    comboActionType = ComboActionType.MultiSkipable;
                            }
                        } else if (type.StartsWith("#")) {
                            comboActionType = ComboActionType.Group;
                            var gs = type.Substring(1);
                            if (String.IsNullOrEmpty(gs)) {
                                comboID = 1;
                            } else {
                                try {
                                    comboID = Int32.Parse(type.Substring(1));
                                } catch(Exception e) {
                                    PluginLog.Debug($"Exception: {e}");
                                    comboID = 1;
                                }
                            }
                        }
                        break;
                }
                var id = Actions.ID(action.Trim());

                // PluginLog.Debug($"ComboAction: {id} {action.Trim()} {comboActionType} {minCount} {maxCount} subgroup: {comboID}");

                return new ComboAction() {
                    ID = id,
                    Type = comboActionType,
                    MinimumCount = minCount,
                    MaximumCount = maxCount,
                    Group = comboID,
                };
            }).ToList();

            // 如果有不合法的action, 此链作废
            if (groupID == 0 || comboActions.Any(x => !x.IsValid)) {
                return null;
            }

            return new ComboGroup(groupID, comboActions, comboType);
        }

        public uint Current(uint lastComboAction = 0, float comboTimer = 0f)
        {
            return ComboActions.Count > 0 ? ComboActions[CurrentIndex%ComboActions.Count].ID : GroupID;
        }

        public bool Contains(uint actionID) => ComboActions.Any(x => Actions.Equals(x.ID, actionID));

        public void StateReset() => CurrentIndex = 0;

        public async Task<bool> StateUpdate(GameAction a, bool succeed = true)
        {
            if (ComboActions.Count == 0) return false;

            if (!Plugin.Ready || !Plugin.Player) return false;
            if (a.Status == ActionStatus.Locking) return false;

            var index = ComboActions.FindIndex(CurrentIndex, x => Actions.Equals(x.ID, a.ID));
            if (index == -1)
                index = ComboActions.FindIndex(0, x => Actions.Equals(x.ID, a.ID));

            var originalIndex = index;

            if (originalIndex == -1) index = CurrentIndex;

            var caction = ComboActions[index % ComboActions.Count];
            var cadjust = originalIndex == -1 ? Actions.AdjustedActionID(caction.ID) : a.ID;
            var cstatus = originalIndex == -1 ? Actions.ActionStatus(cadjust) : a.Status;
            var crgroup = Actions.RecastGroup(cadjust);

            // only handle: Ready, Pending, NotSatisfied and NotLearned
            if (cstatus != ActionStatus.Ready && cstatus != ActionStatus.Pending && cstatus != ActionStatus.NotSatisfied && cstatus != ActionStatus.NotLearned)
                return true;

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
                    if (!(await this.actionLock.WaitAsync(0))) {
                        return false;
                    }
                    if (!(await this.actionLockHighPriority.WaitAsync(0))) {
                        this.actionLock.Release();
                        return false;
                    }
                }
            } else {
                await this.actionLock.WaitAsync();
                // if ((now - this.LastTime).TotalMilliseconds < animationTotal) {
                // PluginLog.Debug($"id: {a.ID}, {a.LastTime.Second}:{a.LastTime.Millisecond} {this.LastTime.Second}:{this.LastTime.Millisecond}");
                if (a.LastTime < this.LastTime) {
                    // 这可以确保在小于animationDelay的时间段内发出的pending actions可以被忽略
                    // if (cstatus == ActionStatus.Pending) {
                    //     this.actionLock.Release();
                    //     return false;
                    // } else if (cstatus != ActionStatus.Ready) {
                    //     if (!(await this.actionLockHighPriority.WaitAsync(0))) {
                    //         this.actionLock.Release();
                    //         return false;
                    //     }
                    // } else {
                    //     await this.actionLockHighPriority.WaitAsync();
                    // }
                    if (cstatus == ActionStatus.Ready && a.ID != LastActionID) {
                        await this.actionLockHighPriority.WaitAsync();
                    } else {
                        this.actionLock.Release();
                        return false;
                    }
                } else {
                    // 由于系统的技能插入特性, 如果处于连击状态.
                    // 在下一个gcd开始的时候, 系统会帮我们自动插入该技能.
                    // 但是: 如果技能的咏唱时间大于gcd. 那么当咏唱结束的那个gcd开始的时候,
                    // 我们出了自己手动触发了一次UseAction[UseType==0]之外,
                    // 系统还是会帮我们自动再触发一次UseAction[UseType==1].
                    // 系统插入的这个貌似会自动等待可以插入的时机.
                    // 为了减少手动插入的损耗, StateUpdate的等待时间一定会小于Animation的时间.
                    // 也就是说, 系统插入的这个UseAction, 一定会出现在Animation结束之后,
                    // 即一定会出现在我们的StateUpdate更新完成之后.
                    if (a.IsValid && a.ID == this.LastActionID) {
                        if (a.Status != ActionStatus.Ready) {
                            this.actionLock.Release();
                            return false;
                        }
                        index = this.LastIndex;
                        caction = ComboActions[index%ComboActions.Count];
                    }
                    await this.actionLockHighPriority.WaitAsync();
                }
            }

            // a1 a2[notlearned] a3
            // 如果a1 a2是同一group, a1执行完之后, 查询a2的status将会是pending或locking
            // 等到a2真正可以被执行的时候(比如下一个gcd开始), 它的status才会变成ready(或notlearned等)
            // if (cstatus == ActionStatus.NotLearned && caction.Type != ComboActionType.Blocking) {
            if (cstatus == ActionStatus.NotLearned) {
                // PluginLog.Debug($"[ComboStateUpdate] action not learned. {Actions.Name(caction.ID)} at {CurrentIndex} of Group: {GroupID}. Triggerd by {a.Name}");
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
                animationDelay = a.Finished ? 100 : 0;
                var aa = DateTime.Now;
                PluginLog.Debug($"Try wait. {a.ID}, finished: {a.Finished}, status: {a.Status}. on Group: {GroupID}");
                if (!await a.Wait()) {
                    PluginLog.Debug($"[ComboStateUpdate][Casting] Group: {GroupID}, action: {caction.ID} failed.");
                    this.actionLockHighPriority.Release();
                    this.actionLock.Release();
                    return false;
                }
                var bb = DateTime.Now;
                PluginLog.Debug($"Try wait...done {a.ID} {(bb-aa).TotalMilliseconds}");
            }

            // 因为可能需要wait, 所以放在这里
            var cremain = Actions.RecastTime(cadjust);

            // 到这里只存在三种状态: Ready, Pending, NotSatisfied
            switch (Type)
            {
                // m l s lb sb
                case ComboType.Manual:
                    index = (index + 1) % ComboActions.Count;
                    break;
                case ComboType.Strict:
                    if (!Actions.Equals(ComboActions[CurrentIndex].ID, a.ID))
                        break;
                    if (cstatus == ActionStatus.Ready || cstatus == ActionStatus.NotSatisfied) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    } else if (cremain > Configuration.GlobalCoolingDown.TotalMilliseconds) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.StrictBlocked:
                    index = CurrentIndex;
                    if (!Actions.Equals(ComboActions[CurrentIndex].ID, a.ID))
                        break;
                    if (cstatus == ActionStatus.Ready) {
                        index = (CurrentIndex + 1) % ComboActions.Count;
                    }
                    break;
                case ComboType.Linear:
                    if (cstatus == ActionStatus.Ready || cstatus == ActionStatus.NotSatisfied) {
                        index = (index + 1) % ComboActions.Count;
                    } else if (cremain > Configuration.GlobalCoolingDown.TotalMilliseconds) {
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
                        cstatus = Actions.ActionStatus(cadjust);
                        // originalIndex = index;
                        // PluginLog.Debug($"{Actions.Name(GroupID)} : {index} {Actions.Name(caction.ID)}:{caction.ID} {caction.Group} incombostate:{InComboState}");
                        switch (caction.Type)
                        {
                            // 跳NotSatisfied需要确保action已经执行
                            // case ComboActionType.Single:
                            // case ComboActionType.Multi:
                            case ComboActionType.SingleSkipable:
                            case ComboActionType.MultiSkipable:
                                if (cstatus == ActionStatus.NotSatisfied && caction.Executed || cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalMilliseconds) {
                                    index += 1; // caction.Restore();
                                }
                                break;
                            // 全跳
                            case ComboActionType.Skipable:
                                if (cstatus == ActionStatus.NotSatisfied || cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalMilliseconds) {
                                    index += 1;
                                }
                                break;
                            case ComboActionType.Group:
                                if (!InComboState || !Contains(LastComboAction)) {
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
                                if (caction.Finished) { // Count >= MaximumCount
                                    index += 1; break;
                                }
                                if (cstatus == ActionStatus.Pending && cremain > Configuration.GlobalCoolingDown.TotalMilliseconds) {
                                    index += 1; animationDelay = 0;
                                } else if (cstatus == ActionStatus.Ready) {
                                    if (succeed) {
                                        // caction.Count += 1;
                                        caction.Update();
                                    }
                                    if (caction.Finished) { // Count >= MaximumCount
                                        index += 1; break;
                                    } else if (caction.Count >= caction.MinimumCount) {
                                        var naction = ComboActions[(index+1)%ComboActions.Count];
                                        var nadjust = Actions.AdjustedActionID(naction.ID);
                                        var nstatus = Actions.ActionStatus(nadjust);
                                        var nrgroup = Actions.RecastGroup(nadjust);
                                        var nremain = Actions.RecastTime(nadjust);
                                        if (nstatus == ActionStatus.Ready || nrgroup == crgroup || nstatus == ActionStatus.Pending && nremain <= Configuration.GlobalCoolingDown.TotalMilliseconds) {
                                            index += 1; // caction.Restore();
                                        }
                                    }
                                } else if (cstatus == ActionStatus.NotSatisfied) {
                                    if (caction.Type == ComboActionType.Single || caction.Type == ComboActionType.Multi) {
                                        // 未执行时, 必须等待.
                                        // 直到执行过之后再次出现NotSatisfied时才可被自动跳过
                                        if (caction.Executed) { // Count > 0
                                            index += 1; animationDelay = 0; // caction.Restore();
                                        } else {
                                            // caction.Executed = true;
                                            caction.Update(); // <---- 防止卡住. 点两次
                                        }
                                    } else if (caction.Type == ComboActionType.SingleSkipable || caction.Type == ComboActionType.MultiSkipable || caction.Type == ComboActionType.Skipable) {
                                        caction.Update();   // 不满足点一次跳过
                                        index += 1; animationDelay = 0; // caction.Restore();  // 点一次
                                    }
                                }
                                break;
                            case ComboActionType.Blocking:  // Pending ?
                                if (cstatus == ActionStatus.Ready) {
                                    if (succeed) {
                                        caction.Update();
                                        index += 1;
                                    }
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
                                    if (succeed) {
                                        caction.Update();
                                        index += 1;
                                    }
                                }
                                break;
                        }

                        PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, CurrentIndex: {CurrentIndex}, origIndex: {originalIndex}, index: {index}, action: {a.Name}, id: {a.ID}, caction.ID: {caction.ID}, exec?: {caction.Executed}, status: {cstatus}, succeed:{succeed}, type: {caction.Type}, count: {caction.Count}, ucount: {caction.MaximumCount}, crgroup: {crgroup}, remain: {cremain}, iscasting: {Plugin.Player!.IsCasting}, usetype: {a.UseType}");
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
            if (originalIndex != -1) {
                if (originalIndex != index) {
                // if (originalIndex != index && CurrentIndex != index) {
                    // if (animationDelay > 0)
                    // PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, animationDelay: {animationDelay}ms. {Actions.Name(caction.ID)} -> {Actions.Name(ComboActions[index%ComboActions.Count].ID)}");

                    // await Task.Delay(animationDelay);

                    // 反正能力技之间的插入也有时间间隔, 不如等一等, 放动画
                    await Task.Delay(animationDelay);

                    // 切换到下一个action之前把当前action的状态复位
                    foreach (var ac in ComboActions) {
                        ac.Restore();
                    }

                    var lastIndex = CurrentIndex;
                    CurrentIndex = index % ComboActions.Count;

                    // CurrentIndex更新之后不代表就立刻转移到了下一个技能, 到GetIcon更新技能图标还有一段时间.
                    // 继续sleep, 留给GetIcon一点时间, 到图标更新完成.
                    await Task.Delay(50);

                    // 应该做出假设: 此时已经成功转移到下一个技能, 并且技能图标已经更新.
                    // 那么需要把这之前等待的其它Task全部清除. 只处理在这之后发出的Task.
                    this.LastTime = DateTime.Now;
                    this.LastActionID = a.ID;   // <---
                    this.LastIndex = lastIndex;
                    PluginLog.Debug($"lasttime update to: {this.LastTime.Second}:{this.LastTime.Millisecond}");
                    PluginLog.Debug($"[ComboStateUpdate] Group: {GroupID}, animationDelay: {animationDelay}ms. {Actions.Name(caction.ID)} -> {Actions.Name(ComboActions[index%ComboActions.Count].ID)} done");
                } else {
                    PluginLog.Debug($"lasttime update to: {this.LastTime.Second}:{this.LastTime.Millisecond}");
                    this.LastTime = DateTime.Now;
                }
            } else {
                if (CurrentIndex != index)
                    PluginLog.Debug($"lastindex auto update to: {caction.ID} cadj?:{cadjust}, {cstatus} cs?:{Actions.ActionStatus(cadjust)} {CurrentIndex} {index}. l:{this.LastTime.Second}:{this.LastTime.Millisecond} n:{DateTime.Now.Second}:{DateTime.Now.Millisecond}");
                CurrentIndex = index;
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

        public static ComboManager FromString(string combo)
        {
            var cm = new ComboManager();

            var nodeStr = String.Empty;
            var lines = combo.Split("\n").Select(x => x.Trim()).Where(x => !String.IsNullOrEmpty(x)).ToList();
            foreach (var line in lines) {
                if (line.StartsWith("#") || line.StartsWith("//")) {
                    continue;
                }

                if (line.Contains(":")) {
                    var cg = ComboGroup.FromString(nodeStr);
                    if (cg is not null) {
                        cm.ComboGroups.Add(cg.GroupID, cg);
                    }
                    nodeStr = line;
                } else {
                    nodeStr += line;
                }
            }

            if (!String.IsNullOrEmpty(nodeStr)) {
                var cg = ComboGroup.FromString(nodeStr);
                if (cg is not null) {
                    cm.ComboGroups.Add(cg.GroupID, cg);
                }
            }

            return cm;
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

        public async Task<bool> StateUpdate(GameAction a, bool succeed = true)
        {
            return (await Task.WhenAll(ComboGroups.Select(i => i.Value.StateUpdate(a, succeed)).ToArray())).Any();
        }

        public uint Current(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => ComboGroups.ContainsKey(groupID) ? ComboGroups[groupID].Current(lastComboAction, comboTimer) : groupID;
        public bool Contains(uint actionID) => ComboGroups.Any(x => x.Value.Contains(actionID));
        public bool ContainsGroup(uint actionID) => ComboGroups.ContainsKey(actionID);
    }
}