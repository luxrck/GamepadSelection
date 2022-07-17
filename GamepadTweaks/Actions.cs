using Dalamud;
using Dalamud.Game.ClientState;
using Dalamud.Logging;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace GamepadTweaks
{
    public enum ActionUseType : uint
    {
        Normal = 0,
        AutoGenerated = 1,
        Macro = 2,
    }

    public enum ActionStatus : uint
    {
        Ready = 0,
        Unk_570 = 570,
        Unk_571 = 571,
        NotSatisfied = 572,
        NotLearned = 573,
        // Action正处于动画中, 执行其它Action就会被锁住
        // 咏唱时间内
        Locking = 580,
        Unk_581 = 581,
        // 咏唱时间段外, 复唱时间内
        Pending = 582,

        // 下面是我自定义的
        Delay = 0xffff0001,
        LocalDelay = 0xffff0002,
        Invalid = 0xffffffff,
    }

    public class ActionMap
    {
        // private Dictionary<uint, HashSet<uint>> Alias = new Dictionary<uint, HashSet<uint>>() {
        //     {17055, new HashSet<uint>() {4401, 4402, 4403, 4404, 4405, 4406} },   // Play
        //     {25869, new HashSet<uint>() {7444, 7445} }, // Crown Play
        //     {25822, new HashSet<uint>() {3582} },   // 星极超流
        //     {3579, new HashSet<uint>() {25820} },   // 毁荡
        //     {25883, new HashSet<uint>() {25819, 25818, 25817} },    // 宝石耀
        // };

        private Dictionary<uint, uint[]> Alias = new Dictionary<uint, uint[]>() {
            {17055, new uint[] {4401, 4402, 4403, 4404, 4405, 4406} },   // Play
            {25869, new uint[] {7444, 7445} }, // Crown Play
            {25822, new uint[] {3582} },   // 星极超流
            {3579,  new uint[] {25820} },   // 毁荡
            {25883, new uint[] {25819, 25818, 25817} },    // 宝石耀
            {25884, new uint[] {25816, 25815, 25814} },    // 宝石辉
            {25800, new uint[] {3581} },    //以太蓄能
        };

        private Dictionary<uint, uint> AliasMap = new Dictionary<uint, uint>();

        private List<(string Lang, string Name, uint ID)> Actions = new List<(string, string, uint)> {
            // SGE
            {("zh", "均衡诊断", 24291)},
            {("zh", "白牛清汁", 24303)},
            {("zh", "灵橡清汁", 24296)},
            {("zh", "混合", 24317)},
            {("zh", "输血", 24305)},
            {("en", "Diagnosis", 24284)},
            {("en", "Eukrasian Diagnosis", 24291)},
            {("en", "Taurochole", 24303)},
            {("en", "Druochole", 24296)},
            {("en", "Krasis", 24317)},
            {("en", "Haima", 24305)},

            // WHM
            {("zh", "再生", 137)},
            {("zh", "天赐祝福", 140)},
            {("zh", "神祝祷", 7432)},
            {("zh", "神名", 3570)},
            {("zh", "水流幕", 25861)},
            {("zh", "安慰之心", 16531)},
            {("zh", "庇护所", 3569)},
            {("zh", "礼仪之铃", 25862)},
            {("en", "Regen", 137)},
            {("en", "Benediction", 140)},
            {("en", "Divine Benison", 7432)},
            {("en", "Tetragrammaton", 3570)},
            {("en", "Aquaveil", 25861)},
            {("en", "Afflatus Solace", 16531)},
            {("en", "Asylum", 3569)},
            {("en", "Liturgy of the Bell", 25862)},

            // AST
            {("zh", "先天禀赋", 3614)},
            {("zh", "出卡", 17055)},
            {("zh", "吉星相位", 3595)},
            {("zh", "星位合图", 3612)},
            {("zh", "出王冠卡", 25869)},
            {("zh", "天星交错", 16556)},
            {("zh", "擢升", 25873)},
            {("zh", "地星", 7439)},
            {("zh", "小奥秘卡", 7443)},
            {("zh", "抽卡", 3590)},
            {("en", "Essential Dignity", 3614)},
            {("en", "Play", 17055)},
            {("en", "Aspected Benefic", 3595)},
            {("en", "Synastry", 3612)},
            {("en", "Crown Play", 25869)},
            {("en", "Celestial Intersection", 16556)},
            {("en", "Exaltation", 25873)},

            // SCH
            {("zh", "鼓舞激励之策", 185)},
            {("zh", "野战治疗阵", 188)},
            {("zh", "生命活性法", 189)},
            {("zh", "深谋远虑之策", 7434)},
            {("zh", "以太契约", 7423)},
            {("zh", "生命回生法", 25867)},
            {("en", "Adloquium", 185)},
            {("en", "Lustrate", 189)},
            {("en", "Excogitation", 7434)},
            {("en", "Aetherpact", 7423)},
            {("en", "Protraction", 25867)},

            // WAR
            {("zh", "重劈", 31)},
            {("zh", "凶残裂", 37)},
            {("zh", "超压斧", 41)},
            {("zh", "暴风斩", 42)},
            {("zh", "暴风碎", 45)},
            {("zh", "飞斧", 46)},
            {("zh", "守护", 48)},
            {("zh", "铁壁", 7531)},
            {("zh", "雪仇", 7535)},
            {("zh", "下踢", 7540)},

            // SMN
            {("zh", "龙神附体", 3581)},
            {("zh", "死星核爆", 3582)},
            {("zh", "以太蓄能", 25800)},
            {("zh", "星极脉冲", 25820)},
            {("zh", "星极超流", 25822)},
            {("zh", "宝石耀", 25883)},
            {("zh", "宝石辉", 25884)},
            {("zh", "风神召唤", 25807)},
            {("zh", "土神召唤", 25806)},
            {("zh", "火神召唤", 25805)},
            {("zh", "毁荡", 3579)},
            {("zh", "能量吸收", 16508)},
            {("zh", "能量抽取", 16510)},
            {("zh", "迸裂", 16511)},
            {("zh", "龙神迸发", 7429)},
            {("zh", "溃烂爆发", 181)},
            {("zh", "痛苦核爆", 3578)},
            {("zh", "绿宝石毁荡", 25819)},
            {("zh", "黄宝石毁荡", 25818)},
            {("zh", "红宝石毁荡", 25817)},
            {("zh", "绿宝石迸裂", 25816)},
            {("zh", "黄宝石迸裂", 25815)},
            {("zh", "红宝石迸裂", 25814)},
        };

        private Dictionary<uint, Dictionary<string, string>> ActionsMap = new Dictionary<uint, Dictionary<string, string>>();

        private ClientState ClientState = Plugin.ClientState;

        private string ClientLanguage = "zh";

        public ActionMap()
        {
            foreach (var a in Alias) {
                foreach (var b in a.Value) {
                    AliasMap[b] = a.Key;
                }
                AliasMap[a.Key] = a.Key;
            }

            foreach (var (lang, name, id) in Actions) {
                if (!ActionsMap.ContainsKey(id))
                    ActionsMap[id] = new Dictionary<string, string>();
                ActionsMap[id][lang] = name;
            }

            switch (ClientState.ClientLanguage)
            {
                case Dalamud.ClientLanguage.ChineseSimplified:
                    ClientLanguage = "zh";
                    break;
                case Dalamud.ClientLanguage.English:
                    ClientLanguage = "en";
                    break;
            }
        }

        public bool Contains(string action) => Actions.Any(x => x.Name == action);
        public bool Equals(uint a1, uint a2) => BaseActionID(a1) == BaseActionID(a2);

        public uint this[string a]
        {
            get => Contains(a) ? Actions.First(x => x.Name == a).ID : 0;
        }

        public string this[uint i]
        {
            get => ActionsMap.ContainsKey(i) ? (ActionsMap[i].ContainsKey(ClientLanguage) ? ActionsMap[i][ClientLanguage] : String.Empty) : String.Empty;
        }

        public uint BaseActionID(uint actionID) => AliasMap.ContainsKey(actionID) ? AliasMap[actionID] : actionID;
        public FFXIVClientStructs.FFXIV.Client.Game.ActionType ActionType(uint actionID) => FFXIVClientStructs.FFXIV.Client.Game.ActionType.Spell;

        public uint AdjustedActionID(uint actionID)
        {
            var adjustedID = actionID;
            unsafe {
                var am = ActionManager.Instance();
                if (am != null) {
                    // real action id
                    adjustedID = am->GetAdjustedActionId(actionID);
                }
            }
            return adjustedID;
        }

        // ActionType.Spell == 0x01;
        // ActionType.Ability == 0x04;
        public ActionStatus ActionStatus(uint actionID, ActionType actionType = 0U, uint targetedActorID = 3758096384U)
        {
            uint status = 0;
            unsafe {
                var am = ActionManager.Instance();
                if (am != null) {
                    var at = actionType > 0 ? actionType : ActionType(actionID);
                    status = am->GetActionStatus(at, actionID, targetedActorID);
                }
            }

            return (GamepadTweaks.ActionStatus)status;
        }

        public float RecastTimeRemain(uint actionID)
        {
            float recast = 0f;
            unsafe {
                var am = ActionManager.Instance();
                if (am != null) {
                    var elapsed = am->GetRecastTimeElapsed(ActionType(actionID), actionID);
                    var total = am->GetRecastTime(ActionType(actionID), actionID);
                    // var total = am->GetAdjustedCastTime(ActionType(actionID), actionID);
                    recast = total - elapsed;
                }
            }
            return recast;
        }

        public int RecastGroup(uint actionID)
        {
            int group = 0;
            unsafe {
                var am = ActionManager.Instance();
                if (am != null) {
                    group = am->GetRecastGroup((int)ActionType(actionID), actionID);
                }
            }
            return group;
        }
    }
}