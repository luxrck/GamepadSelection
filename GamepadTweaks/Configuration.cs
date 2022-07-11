using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace GamepadTweaks
{
    [JsonObject(MemberSerialization.OptIn)]
    public class Configuration : IPluginConfiguration
    {
        int IPluginConfiguration.Version { get; set; }

        // public delegate void UpdateContentDelegate(string content);
        // public UpdateContentDelegate UpdateContent;

        // {
        //     "alwaysInParty": false,
        //     "gs": [
        //          "均衡诊断"
        //     ],
        //     "priority": "y b a x right down up left",
        //     "rules": {
        //         "均衡诊断": "y b a x right down up left",
        //         "17055": "y b a x up right down left",   // 出卡
        //     }
        // }

        #region Saved configuration values
        [JsonProperty]
        public bool alwaysInParty { get; set; } = false;
        [JsonProperty]
        public bool autoTargeting { get; set; } = false;
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
        public ActionMap Actions = new ActionMap();
        public ComboManager ComboManager;

        internal DirectoryInfo root;
        internal string configFile;
        internal string fontFile;
        internal string assetFile;

        internal string content;

        private HashSet<uint> gsActions = new HashSet<uint>();
        private HashSet<uint> gtoffActions = new HashSet<uint>();
        private Dictionary<uint, string> userActions = new Dictionary<uint, string>();

        public Configuration()
        {
            DalamudPluginInterface PluginInterface = Plugin.PluginInterface;
            
            this.root = PluginInterface.ConfigDirectory;
            var cd = this.root.ToString();
            this.configFile = cd + $"/{PluginInterface.ConfigFile.Name}";
            this.fontFile = cd + "/Font.otf";
            this.assetFile = cd + "/Actions.json";

            try {
                this.Update();
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
            }
        }
        
        public static Configuration Load()
        {
            Configuration config = null;
            
            try {
                var configFile = Plugin.PluginInterface.ConfigDirectory.ToString() + $"/{Plugin.PluginInterface.ConfigFile.Name}";
                var content = File.ReadAllText(configFile);
                config = JsonConvert.DeserializeObject<Configuration>(content);
                if (config is null)
                    config = new Configuration();
                config.content = content;
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                config = new Configuration();
            }

            config.UpdateActions();

            return config;
        }

        public bool ActionInMonitor(uint actionID) => IsGsAction(actionID) || IsGtoffAction(actionID) || IsUserAction(actionID);
        public bool IsGsAction(uint actionID) => this.gsActions.Contains(actionID) || IsUserAction(actionID);
        public bool IsGtoffAction(uint actionID) => this.gtoffActions.Contains(actionID);
        public bool IsUserAction(uint actionID) => this.userActions.ContainsKey(actionID);
        public bool IsComboAction(uint actionID) => this.ComboManager.Contains(actionID);
        public bool IsComboGroup(uint actionID) => this.ComboManager.ContainsGroup(actionID);

        public string SelectOrder(uint actionID) => IsUserAction(actionID) ? this.userActions[actionID] : this.priority;

        public uint CurrentComboAction(uint groupID, uint lastComboAction = 0, float comboTimer = 0f) => this.ComboManager.Current(groupID, lastComboAction, comboTimer);
        public bool UpdateComboState(uint actionID, ActionStatus status = ActionStatus.Done) => this.ComboManager.StateUpdate(actionID, status);

        public bool Update(string content = "")
        {
            try {
                if (content is null || content == "") {
                    using (var fs = File.Create(this.configFile))
                    using (var sw = new StreamWriter(fs))
                    using (var jw = new JsonTextWriter(sw) { 
                        Formatting = Formatting.Indented, 
                        Indentation = 1, 
                        IndentChar = '\t'
                    }) {
                        (new JsonSerializer()).Serialize(jw, this);
                    }

                    this.content = File.ReadAllText(this.configFile);
                } else {
                    var config = JsonConvert.DeserializeObject<Configuration>(content);
                    
                    if (config is null) return false;
                    
                    this.alwaysInParty = config.alwaysInParty;
                    this.autoTargeting = config.autoTargeting;
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

                var cg = new List<(uint GroupID, List<uint> ComboActions, string ComboType)>();
                foreach (string s in this.combo) {
                    var ss = s.Split(":");
                    var comboType = ss[0].Trim();
                    var comboActions = ss[1].Split("->").Where(a => a != "").Select(a => Actions[a.Trim()]).ToList();
                    var groupID = Actions[ss[2].Trim()];
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
                File.WriteAllText(this.configFile, this.content);
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                return false;
            }
            return true;
        }
    }
}