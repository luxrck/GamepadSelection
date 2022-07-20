using Dalamud.Configuration;
using Dalamud.Logging;
using Dalamud.Plugin;
using Newtonsoft.Json;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GamepadTweaks
{
    [JsonObject(MemberSerialization.OptIn)]
    public class Configuration : IPluginConfiguration
    {
        public static uint DefaultInvalidGameObjectID = 3758096384U;
        public class GlobalCoolingDown
        {
            public static float TotalSeconds = 2.7f;
            public static uint TotalMilliseconds = 2700;
            public static uint AnimationWindow = 700;
            public static uint SlidingWindow = 700;

            //实际上也许是最后1/4个GCD时间段
            public static uint RecastWindow = 500;
        }

        int IPluginConfiguration.Version { get; set; }

        #region Saved configuration values
        [JsonProperty]
        public bool alwaysInParty { get; set; } = false;
        [JsonProperty]
        public bool autoTargeting { get; set; } = false;
        [JsonProperty]
        public bool actionAutoDelay { get; set; } = false;
        [JsonProperty]
        public bool alwaysTargetingNearestEnemy { get; set; } = false;
        [JsonProperty]
        public List<string> gtoff { get; set; } = new List<string>();
        [JsonProperty]
        public List<string> gs { get; set; } = new List<string>();
        // public List<string> gs {get; set; } = new List<string>() {
            // "均衡诊断", "白priority", "出卡"
        // };
        [JsonProperty]
        public string priority { get; set; } = "y b a x up right down left";
        // [JsonProperty]
        // public string partyMemeberSortOrder {get; set; } = "thmr";  // always put Self in 1st place. eg: [s]thmr
        [JsonProperty]
        public List<string> combo { get; set; } = new List<string>();
        [JsonProperty]
        public Dictionary<string, string> rules { get; set; } = new Dictionary<string, string>();
        #endregion

        // public Dictionary<string, uint> actions;
        public Actions Actions = new Actions();
        public ComboManager ComboManager = null!;

        public static string Root => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? String.Empty;
        public static string FontFile => Path.Combine(Root, "sarasa-fixed-sc-regular.subset.ttf");
        public static string GlyphRangesFile => Path.Combine(Root, "chars.txt");
        public static string ActionFile => Path.Combine(Root, "Actions.json");
        public static string ConfigFile => Plugin.PluginInterface.ConfigFile.ToString();

        internal string content = String.Empty;

        private HashSet<uint> gsActions = new HashSet<uint>();
        private HashSet<uint> gtoffActions = new HashSet<uint>();
        private Dictionary<uint, string> userActions = new Dictionary<uint, string>();

        // public Configuration()
        // {
        //     try {
        //         this.Update();
        //     } catch(Exception e) {
        //         PluginLog.Error($"Exception: {e}");
        //     }
        // }

        public static Configuration Load()
        {
            Configuration? config = null;

            try {
                var content = File.ReadAllText(Plugin.PluginInterface.ConfigFile.ToString());
                config = JsonConvert.DeserializeObject<Configuration>(content) ?? new Configuration();
                config.content = content;
                config.UpdateActions();
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                config = new Configuration();
                config.Update();
            }

            return config;
        }

        public bool ActionInMonitor(uint actionID) => IsGsAction(actionID) || IsGtoffAction(actionID) || IsUserAction(actionID);
        public bool IsGsAction(uint actionID) => this.gsActions.Any(x =>Actions.Equals(x, actionID)) || IsUserAction(actionID);
        public bool IsGtoffAction(uint actionID) => this.gtoffActions.Any(x => Actions.Equals(x, actionID));
        public bool IsUserAction(uint actionID) => this.userActions.Any(x => Actions.Equals(x.Key, actionID));
        public bool IsComboAction(uint actionID) => this.ComboManager.Contains(actionID);
        public bool IsComboGroup(uint actionID) => this.ComboManager.ContainsGroup(actionID);

        public string SelectOrder(uint actionID) => IsUserAction(actionID) ? this.userActions[actionID] : this.priority;

        public uint CurrentComboAction(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => this.ComboManager.Current(groupID, lastComboAction, comboTimer);
        public void ResetComboState(uint groupID) => this.ComboManager.StateReset(groupID);
        public async Task<bool> UpdateComboState(GameAction a, bool succeed = true, DateTime timestamp = default(DateTime)) => await this.ComboManager.StateUpdate(a, succeed, timestamp);

        public bool Update(string content = "")
        {
            try {
                if (String.IsNullOrEmpty(content)) {
                    using (var fs = File.Create(ConfigFile))
                    using (var sw = new StreamWriter(fs))
                    using (var jw = new JsonTextWriter(sw) {
                        Formatting = Formatting.Indented,
                        Indentation = 1,
                        IndentChar = '\t'
                    }) {
                        (new JsonSerializer()).Serialize(jw, this);
                    }

                    this.content = File.ReadAllText(ConfigFile);
                } else {
                    var config = JsonConvert.DeserializeObject<Configuration>(content);

                    if (config is null) return false;

                    this.alwaysInParty = config.alwaysInParty;
                    this.autoTargeting = config.autoTargeting;
                    this.actionAutoDelay = config.actionAutoDelay;
                    this.alwaysTargetingNearestEnemy = config.alwaysTargetingNearestEnemy;
                    this.gs = config.gs;
                    this.gtoff = config.gtoff;
                    this.priority = config.priority;
                    this.combo = config.combo;
                    this.rules = config.rules;

                    this.content = content;
                }

                return this.UpdateActions();
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");

                return false;
            }
        }

        private bool UpdateActions()
        {
            try {
                var gs = new HashSet<uint>();
                foreach(string s in this.gs) {
                    if (Actions.Contains(s)) {
                        gs.Add(Actions[s]);
                    }
                }
                this.gsActions = gs;

                var gtoff = new HashSet<uint>();
                foreach(string s in this.gtoff) {
                    if (Actions.Contains(s)) {
                        gtoff.Add(Actions[s]);
                    }
                }
                this.gtoffActions = gtoff;

                var ua = new Dictionary<uint, string>();
                foreach(var i in this.rules) {
                    uint actionID = 0;
                    if (Actions.Contains(i.Key)) {
                        actionID = Actions[i.Key];
                    } else {
                        try {
                            actionID = UInt32.Parse(i.Key);
                        } catch {}
                    }

                    if (actionID > 0) {
                        var value = i.Value == "" || i.Value.ToLower() == "default" ? this.priority : i.Value;
                        ua.TryAdd(actionID, value);
                    }
                }
                this.userActions = ua;

                var cg = new List<(uint GroupID, List<ComboAction> ComboActions, string ComboType)>();
                foreach (string s in this.combo) {
                    var ss = s.Split(":");
                    var comboType = ss[0].Trim();
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
                        var id = Actions[action.Trim()];

                        PluginLog.Debug($"ComboAction: {id} {action.Trim()} {comboActionType} {minCount} {maxCount} iscombo: {comboID}");

                        return new ComboAction() {
                            Action = new GameAction() { ID = id },
                            Type = comboActionType,
                            MinimumCount = minCount,
                            MaximumCount = maxCount,
                            Group = comboID,
                        };
                    }).ToList();

                    var groupID = Actions[ss[2].Trim()];

                    // 如果有不合法的action, 此链作废
                    if (groupID == 0 || comboActions.Any(x => !x.IsValid)) {
                        continue;
                    }

                    PluginLog.Debug($"\tin {comboType} : {groupID} {ss[2].Trim()}");
                    cg.Add((groupID, comboActions, comboType));
                }
                this.ComboManager = new ComboManager(cg);
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                return false;
            }

            return true;
        }

        public bool Save()
        {
            try {
                File.WriteAllText(ConfigFile, this.content);
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                return false;
            }
            return true;
        }
    }
}