using Dalamud.Logging;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace GamepadTweaks
{
    public class ActionMap
    {
        public Dictionary<uint, HashSet<uint>> Actions = new Dictionary<uint, HashSet<uint>>() {
            {17055, new HashSet<uint>() {4401, 4402, 4403, 4404, 4405, 4406} },   // Play
            {25869, new HashSet<uint>() {7444, 7445} }, // Crown Play
        };

        internal Dictionary<uint, uint> actionMap = new Dictionary<uint, uint>();

        public ActionMap()
        {
            foreach (var a in Actions) {
                foreach (var b in a.Value) {
                    actionMap.TryAdd(b, a.Key);
                }
                actionMap.TryAdd(a.Key, a.Key);
            }
        }

        public uint GetBaseActionID(uint actionID) => actionMap.ContainsKey(actionID) ? actionMap[actionID] : actionID;
    }
}