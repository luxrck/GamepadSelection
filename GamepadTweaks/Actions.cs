using Dalamud.Logging;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace GamepadTweaks
{
    public enum ActionStatus : uint
    {
        Done = 0,
        Unk_570 = 570,
        Unk_571 = 571,
        NotSatisfied = 572,
        NotLearned = 573,
        Locking = 580,
        Unk_581 = 581,
        // Pending dosen't mean definetly could be execute.
        // try add to Queue.
        Pending = 582,
        Invalid = 0xffffffff,
    }

    public class ActionMap
    {
        private Dictionary<uint, HashSet<uint>> Alias = new Dictionary<uint, HashSet<uint>>() {
            {17055, new HashSet<uint>() {4401, 4402, 4403, 4404, 4405, 4406} },   // Play
            {25869, new HashSet<uint>() {7444, 7445} }, // Crown Play
            {25822, new HashSet<uint>() {3582} },   // 星极超流
            {3579, new HashSet<uint>() {25820} },   // 毁荡
            {25883, new HashSet<uint>() {25819, 25818, 25817} },    // 宝石耀
        };
        
        private Dictionary<uint, uint> AliasMap = new Dictionary<uint, uint>();

        private Dictionary<string, uint> Actions = new Dictionary<string, uint> {
            // SGE
            {"均衡诊断", 24291},
            {"白牛清汁", 24303},
            {"灵橡清汁", 24296},
            {"混合", 24317},
            {"输血", 24305},
            {"Diagnosis", 24284},
            {"Eukrasian Diagnosis", 24291},
            {"Taurochole", 24303},
            {"Druochole", 24296},
            {"Krasis", 24317},
            {"Haima", 24305},

            // WHM
            {"再生", 137},
            {"天赐祝福", 140},
            {"神祝祷", 7432},
            {"神名", 3570},
            {"水流幕", 25861},
            {"安慰之心", 16531},
            {"庇护所", 3569},
            {"礼仪之铃", 25862},
            {"Regen", 137},
            {"Benediction", 140},
            {"Divine Benison", 7432},
            {"Tetragrammaton", 3570},
            {"Aquaveil", 25861},
            {"Afflatus Solace", 16531},
            {"Asylum", 3569},
            {"Liturgy of the Bell", 25862},

            // AST
            {"先天禀赋", 3614},
            {"出卡", 17055},
            {"吉星相位", 3595},
            {"星位合图", 3612},
            {"出王冠卡", 25869},
            {"天星交错", 16556},
            {"擢升", 25873},
            {"地星", 7439},
            {"小奥秘卡", 7443},
            {"抽卡", 3590},
            {"Essential Dignity", 3614},
            {"Play", 17055},
            {"Aspected Benefic", 3595},
            {"Synastry", 3612},
            {"Crown Play", 25869},
            {"Celestial Intersection", 16556},
            {"Exaltation", 25873},

            // SCH
            {"鼓舞激励之策", 185},
            {"生命活性法", 189},
            {"深谋远虑之策", 7434},
            {"以太契约", 7423},
            {"生命回生法", 25867},
            {"Adloquium", 185},
            {"Lustrate", 189},
            {"Excogitation", 7434},
            {"Aetherpact", 7423},
            {"Protraction", 25867},

            // WAR
            {"重劈", 31},
            {"凶残裂", 37},
            {"暴风斩", 42},
            {"暴风碎", 45},
            {"铁壁", 7531},
            {"雪仇", 7535},
            {"下踢", 7540},

            // SMN
            {"龙神附体", 3581},
            {"死星核爆", 3582},
            {"星极脉冲", 25820},
            {"星极超流", 25822},
            {"宝石耀", 25883},
            {"风神召唤", 25807},
            {"土神召唤", 25806},
            {"火神召唤", 25805},
            {"毁荡", 3579},
            {"能量吸收", 16508},
            {"龙神迸发", 7429},
            {"溃烂爆发", 181},
            {"绿宝石毁荡", 25819},
            {"黄宝石毁荡", 25818},
            {"红宝石毁荡", 25817},
        };

        public ActionMap()
        {
            foreach (var a in Alias) {
                foreach (var b in a.Value) {
                    AliasMap.TryAdd(b, a.Key);
                }
                AliasMap.TryAdd(a.Key, a.Key);
            }
        }

        public bool Contains(string action) => Actions.ContainsKey(action);
        public bool Equals(uint a1, uint a2) => BaseActionID(a1) == BaseActionID(a2);

        public uint this[string a]
        {
            get => Contains(a) ? Actions[a] : 0;
            set => Actions[a] = value;
        }

        public uint BaseActionID(uint actionID) => AliasMap.ContainsKey(actionID) ? AliasMap[actionID] : actionID;
        public FFXIVClientStructs.FFXIV.Client.Game.ActionType ActionType(uint actionID) => FFXIVClientStructs.FFXIV.Client.Game.ActionType.Spell;
    }
}