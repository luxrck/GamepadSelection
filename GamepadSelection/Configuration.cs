using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;

namespace GamepadSelection
{
    [JsonObject(MemberSerialization.OptIn)]
    public class Configuration : IPluginConfiguration
    {
        int IPluginConfiguration.Version { get; set; }

        public delegate void UpdateActionsInMonitorDelegate(Dictionary<uint, string> actions);
        public UpdateActionsInMonitorDelegate UpdateActionsInMonitor;

        // public delegate void UpdateContentDelegate(string content);
        // public UpdateContentDelegate UpdateContent;

        // {
        //     "alwaysInParty": false,
        //     "actionsInMonitor": [
        //          "均衡诊断", "神祝祷"
        //     ],
        //     "selectOrder": "y b a x right down up left",
        //     "rules": {
        //         "均衡诊断": "y b a x right down up left",
        //         "17055": "y b a x up right down left",   // 出卡
        //     }
        // }

        #region Saved configuration values
        [JsonProperty]
        public bool alwaysInParty {get; set; } = false;
        [JsonProperty]
        public List<string> actionsInMonitor {get; set; } = new List<string>();
        // public List<string> actionsInMonitor {get; set; } = new List<string>() {
            // "均衡诊断", "白牛清汁", "灵橡清汁", "出卡"
        // };
        [JsonProperty]
        public string selectOrder {get; set; } = "y b a x up right down left";
        // [JsonProperty]
        // public string partyMemeberSortOrder {get; set; } = "thmr";  // always put Self in 1st place. eg: [s]thmr
        [JsonProperty]
        public Dictionary<string, string> rules {get; set; } = new Dictionary<string, string>();
        #endregion

        public Dictionary<string, uint> actions = new Dictionary<string, uint> {
            {"诊断", 24284},
            {"均衡诊断", 24284},   // 均衡诊断是24291, 但UseAction的参数ActionID却使用的是24284
            {"白牛清汁", 24303},
            {"灵橡清汁", 24296},
            {"混合", 24317},
            {"输血", 24305},

            {"再生", 137},
            {"天赐祝福", 140},
            {"神祝祷", 7432},
            {"神名", 3570},
            {"水流幕", 25861},
            {"安慰之心", 16531},

            {"先天禀赋", 3614},
            {"出卡", 17055},
            {"吉星相位", 3595},
            {"星位合图", 3612},
            {"出王冠卡", 25869},
            {"天星交错", 16556},
            {"擢升", 25873},

            {"鼓舞激励之策", 185},
            {"生命活性法", 189},
            {"深谋远虑之策", 7434},
            {"以太契约", 7423},
            {"生命回生法", 25867},

            {"Diagnosis", 24284},
            {"Eukrasian Diagnosis", 24284},
            {"Taurochole", 24303},
            {"Druochole", 24296},
            {"Krasis", 24317},
            {"Haima", 24305},

            {"Regen", 137},
            {"Benediction", 140},
            {"Divine Benison", 7432},
            {"Tetragrammaton", 3570},
            {"Aquaveil", 25861},
            {"Afflatus Solace", 16531},

            {"Essential Dignity", 3614},
            {"Play", 17055},
            {"Aspected Benefic", 3595},
            {"Synastry", 3612},
            {"Crown Play", 25869},
            {"Celestial Intersection", 16556},
            {"Exaltation", 25873},

            {"Adloquium", 185},
            {"Lustrate", 189},
            {"Excogitation", 7434},
            {"Aetherpact", 7423},
            {"Protraction", 25867},
        };

        internal DirectoryInfo root;
        internal string configFile;
        internal string fontFile;
        internal string assetFile;

        internal string content;

        public Configuration() {
            DalamudPluginInterface PluginInterface = Plugin.PluginInterface;
            
            this.root = PluginInterface.ConfigDirectory;
            var cd = this.root.ToString();
            this.configFile = cd + $"/{PluginInterface.ConfigFile.Name}";
            this.fontFile = cd + "/Font.otf";
            this.assetFile = cd + "/Actions.json";

            try {
                this.content = JsonConvert.SerializeObject(this, Formatting.Indented);
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

            return config;
        }

        public Dictionary<uint, string> GetActionsInMonitor()
        {
            var d = new Dictionary<uint, string>();
            try {
                foreach(string s in this.actionsInMonitor) {
                    if (this.actions.ContainsKey(s)) {
                        d.TryAdd(this.actions[s], this.selectOrder);
                    }
                }
                foreach(var i in this.rules) {
                    uint actionID = 0;
                    if (this.actions.ContainsKey(i.Key)) {
                        actionID = this.actions[i.Key];
                    } else {
                        try {
                            actionID = UInt32.Parse(i.Key);
                        } catch {}
                    }
                    
                    if (actionID > 0) {
                        var value = i.Value == "" || i.Value.ToLower() == "default" ? this.selectOrder : i.Value;
                        d.TryAdd(actionID, value);
                    }
                }
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
            }
            return d;
        }

        public bool Update(string content = "")
        {
            try {
                if (content is null || content == "") {
                    this.content = JsonConvert.SerializeObject(this, Formatting.Indented);
                } else {
                    var config = JsonConvert.DeserializeObject<Configuration>(content);
                    
                    if (config is null) return false;
                    
                    this.alwaysInParty = config.alwaysInParty;
                    this.actionsInMonitor = config.actionsInMonitor;
                    this.selectOrder = config.selectOrder;
                    this.rules = config.rules;

                    this.content = content;
                }
                
                var actions = this.GetActionsInMonitor();
                this.UpdateActionsInMonitor(actions);

                return true;
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                
                return false;
            }   
        }

        public string Save()
        {
            var content = "";
            try {

                File.WriteAllText(this.configFile, this.content);
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
            }
            return content;
        }
    }
}