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

        public delegate void UpdateContentDelegate(string content);
        public UpdateContentDelegate UpdateContent;

        // {
        //     "debug": false,
        //     "actionsInMonitor": [
        //          "均衡诊断", "神祝祷"
        //     ],
        //     "selectOrder": "y b a x right down up left",
        //     "partyMemeberSortOrder": "thmr",
        //     "rules": {
        //         "均衡诊断": "y b a x right down up left",
        //         "17055": "y b a x up right down left",   // 出卡
        //     }
        // }

        #region Saved configuration values
        [JsonProperty]
        public bool debug {get; set; } = false;
        [JsonProperty]
        public List<string> actionsInMonitor {get; set; } = new List<string>();
        // public List<string> actionsInMonitor {get; set; } = new List<string>() {
            // "均衡诊断", "白牛清汁", "灵橡清汁", "出卡"
        // };
        [JsonProperty]
        public string selectOrder {get; set; } = "y b a x right down up left";
        [JsonProperty]
        public string partyMemeberSortOrder {get; set; } = "thmr";  // always put Self in 1st place. eg: [s]thmr
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

            {"神祝祷", 7432},
            {"神名", 3570},
            {"水流幕", 25861},
            {"安慰之心", 16531},

            {"先天禀赋", 3614},
            {"出卡", 17055},
            {"吉星相位", 3595},
            {"星位合图", 8918},
            {"出王冠卡", 25869},
            {"天星交错", 16556},
            {"擢升", 25873},

            {"鼓舞激励之策", 185},
            {"生命活性法", 189},
            {"深谋远虑之策", 7434},
            {"以太契约", 7423},
            {"生命回生法", 25867}
        };

        private DalamudPluginInterface pluginInterface;
        private DirectoryInfo root;
        internal string configFile;
        internal string fontFile;
        internal string assetFile;

        private static void Initialize(Configuration config, DalamudPluginInterface pi) {
            config.pluginInterface = pi;
            config.root = pi.ConfigDirectory;
            var cd = config.root.ToString();
            config.configFile = cd + $"/{pi.ConfigFile.Name}";
            config.fontFile = cd + "/Font.otf";
            config.assetFile = cd + "/Actions.json";
        }
        
        public static Configuration Load(DalamudPluginInterface pi)
        {
            Configuration config = null;
            
            try {
                var configFile = pi.ConfigDirectory.ToString() + $"/{pi.ConfigFile.Name}";
                var content = File.ReadAllText(configFile);
                config = JsonConvert.DeserializeObject<Configuration>(content);
                if (config is null)
                    config = new Configuration();
            } catch(Exception e) {
                PluginLog.Log($"Exception: {e}");
                config = new Configuration();
            }

            Configuration.Initialize(config, pi);
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
                PluginLog.Log($"Exception: {e}");
            }
            return d;
        }

        public bool Update(string content)
        {
            if (content is null || content == "") return true;

            try {
                var config = JsonConvert.DeserializeObject<Configuration>(content);
                
                if (config is null) return false;
                
                this.debug = config.debug;
                this.actionsInMonitor = config.actionsInMonitor;
                this.selectOrder = config.selectOrder;
                this.partyMemeberSortOrder = config.partyMemeberSortOrder;
                this.rules = config.rules;
                
                var actions = this.GetActionsInMonitor();
                this.UpdateActionsInMonitor(actions);

                return true;
            } catch(Exception e) {
                PluginLog.Log($"Exception: {e}");
                
                return false;
            }   
        }

        public string Save()
        {
            var content = "";
            try {

                content = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(this.configFile, content);
            } catch(Exception e) {
                PluginLog.Log($"Exception: {e}");
            }
            return content;
        }
    }
}