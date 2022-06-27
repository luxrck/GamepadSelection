using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;

namespace Gi
{
    [JsonObject(MemberSerialization.OptIn)]
    public class Configuration : IPluginConfiguration
    {
        int IPluginConfiguration.Version { get; set; }

        public delegate void UpdateActionsInMonitorDelegate(Dictionary<uint, string> actions);
        public UpdateActionsInMonitorDelegate UpdateActionsInMonitor;

        // {
        //     "skillsInGSM": [],
        //     "selectOrder": "",
        //     "partyMemeberSortOrder": "s t t h m m r r",
        //     "rules": {
        //         "均衡诊断": "y b a x right down up left"
        //     }
        // }

        #region Saved configuration values
        [JsonProperty]
        public List<string> skillsInGSM {get; set; } = new List<string>() {
            "均衡诊断", "白牛清汁", "出卡"
        };
        [JsonProperty]
        public string selectOrder {get; set; } = "y b a x right down up left";
        [JsonProperty]
        public string partyMemeberSortOrder {get; set; } = "thmr";
        [JsonProperty]
        public Dictionary<uint, string> rules {get; set; } = new Dictionary<uint, string>();
        #endregion

        public DalamudPluginInterface pluginInterface;
        public Dictionary<string, uint> actions = new Dictionary<string, uint> {
            {"均衡诊断", 24284},
            {"白牛清汁", 24303},
            {"灵橡清汁", 24296},

            {"神祝祷", 7432},
            {"神名", 3570},
            {"水流幕", 25861},
            {"安慰之心", 16531},

            {"出卡", 17055}
        };

        public Configuration(DalamudPluginInterface pi)
        {
            this.pluginInterface = pi;
        }

        public static Configuration Load(DalamudPluginInterface pi)
        {
            try {
                var configFile = pi.ConfigFile.ToString();
                var content = File.ReadAllText(configFile);
                var config = JsonConvert.DeserializeObject<Configuration>(content);
                config.pluginInterface = pi;
                return config;
            } catch(Exception e) {
                return new Configuration(pi);
            }
        }

        public Dictionary<uint, string> GetActionsInMonitor()
        {
            var d = new Dictionary<uint, string>();
            try {
                foreach(string s in this.skillsInGSM) {
                    if (this.actions.ContainsKey(s)) {
                        d.Add(this.actions[s], this.selectOrder);
                    }
                }
                foreach(var i in this.rules) {
                    d.Add(i.Key, i.Value);
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
                this.skillsInGSM = config.skillsInGSM;
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
                // this.pluginInterface.SavePluginConfig(this);
                File.WriteAllText(this.pluginInterface.ConfigFile.ToString(), content);
            } catch(Exception e) {
                PluginLog.Log($"Exception: {e}");
            }
            return content;
        }
    }
}