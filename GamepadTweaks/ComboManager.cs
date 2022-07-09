using Dalamud.Logging;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace GamepadTweaks
{
    public class ComboGroup
    {
        public uint GroupID;
        public int CurrentIndex;
        public List<uint> Actions;

        public ActionMap ActionMap = new ActionMap();

        private Dictionary<uint, int> actionPos = new Dictionary<uint, int>();

        public ComboGroup(uint id, List<uint> actions)
        {
            GroupID = id;
            CurrentIndex = 0;
            Actions = actions;
            for (var i=0; i<actions.Count; i++) {
                actionPos.Add(actions[i], i);
            }
        }

        public uint Current(uint lastComboAction = 0, float comboTimer = 0f)
        {
            if (lastComboAction == 0 || comboTimer <= 0) {
                return Actions[CurrentIndex];
            }
            
            int index = actionPos.ContainsKey(lastComboAction) ? actionPos[lastComboAction] : -1;
            var actionID = index < 0 ? Actions[CurrentIndex] : Actions[(index+1)%Actions.Count];

            return actionID;
        }

        public bool Contains(uint actionID) => actionPos.ContainsKey(actionID);
        public bool StateUpdate(uint actionID)
        {
            // 1 -> 2 -> 3 : 1
            // 1 -> 2 : 2
            var baseActionID = ActionMap.GetBaseActionID(actionID);
            PluginLog.Debug($"ComboGroup: {GroupID}, Index: {CurrentIndex}, IndexValue: {Actions[CurrentIndex]} {actionID} {baseActionID}");
            int index = actionPos.ContainsKey(actionID) ? actionPos[actionID] : -1;
            if (index == -1)
                index = actionPos.ContainsKey(baseActionID) ? actionPos[baseActionID] : -1;
            
            if (index == -1) return false;
            
            CurrentIndex = (index + 1) % Actions.Count;
            return true;
        }
    }
    public class ComboManager
    {
        public Dictionary<uint, ComboGroup> ComboGroups = new Dictionary<uint, ComboGroup>();
        public ActionMap ActionMap = new ActionMap();

        public ComboManager(Dictionary<uint, List<uint>> actions)
        {
            foreach (var i in actions) {
                var groupID = i.Key;
                var comboActions = i.Value;
                var combo = new ComboGroup(groupID, comboActions);
                ComboGroups.Add(groupID, combo);
            }
        }

        public bool StateUpdate(uint actionID)
        {
            bool flag = false;
            foreach (var i in ComboGroups) {
                var combo = i.Value;
                var ret = combo.StateUpdate(actionID);
                if (ret)
                    flag = ret;
            }
            return flag;
        }

        public uint Current(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => ComboGroups.ContainsKey(groupID) ? ComboGroups[groupID].Current(lastComboAction, comboTimer) : groupID;
        public bool Contains(uint actionID) => ComboGroups.Any(x => x.Key == actionID || x.Value.Contains(actionID) || x.Key == ActionMap.GetBaseActionID(actionID) || x.Value.Contains(ActionMap.GetBaseActionID(actionID)));
        public bool ContainsGroup(uint actionID) => ComboGroups.ContainsKey(actionID);
    }
}