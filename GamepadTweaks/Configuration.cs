using Dalamud.Configuration;
using Dalamud.Logging;
using Dalamud.Plugin;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GamepadTweaks
{
    public class Configuration : IPluginConfiguration
    {
        public static uint DefaultInvalidGameObjectID = 3758096384U;
        public class GlobalCoolingDown
        {
            public static float TotalSeconds = 2.5f;
            public static int TotalMilliseconds = 2500;
            public static int AnimationWindow = 700;
            public static int SlidingWindow = 500;

            //实际上也许是最后1/4个GCD时间段
            public static int RecastWindow = 500;
        }

        int IPluginConfiguration.Version { get; set; }

        #region Saved configuration values
        [YamlMember]
        public bool alwaysInParty { get; set; } = false;
        // [YamlMember]
        // public bool autoTargeting { get; set; } = false;
        // [YamlMember]
        // public bool alwaysTargetingNearestEnemy { get; set; } = false;
        public string targeting { get; set; } = "auto";         // none, auto, nearest, least-enmity
        [YamlMember]
        public string actionSchedule { get; set; } = "none";    // none, preemptive, non-preemptive
        [YamlMember]
        public int actionRetry { get; set; } = 1;
        [YamlMember]
        public string priority { get; set; } = "y b a x up right down left";
        public List<string> gtoff { get; set; } = new List<string>();
        [YamlMember]
        public List<string> gs { get; set; } = new List<string>();
        // public List<string> gs {get; set; } = new List<string>() {
            // "均衡诊断", "白priority", "出卡"
        // };
        // [JsonProperty]
        // public string partyMemeberSortOrder {get; set; } = "thmr";  // always put Self in 1st place. eg: [s]thmr
        [YamlMember]
        public string combo { get; set; } = String.Empty;
        [YamlMember]
        public Dictionary<string, string> gsRules { get; set; } = new Dictionary<string, string>();
        #endregion

        // public Dictionary<string, uint> actions;
        private Actions Actions = Plugin.Actions;
        private ComboManager ComboManager = new GamepadTweaks.ComboManager();

        public static string Root => Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? String.Empty;
        public static string FontFile => Path.Combine(Root, "sarasa-fixed-sc-regular.subset.ttf");
        public static string GlyphRangesFile => Path.Combine(Root, "chars.txt");
        public static string ActionFile => Path.Combine(Root, "Actions.json");
        public static string AliasFile => Path.Combine(Root, "alias.txt");
        public static string ConfigName => Path.GetFileNameWithoutExtension(Plugin.PluginInterface.ConfigFile.Name) + ".yaml";
        public static string ConfigFile => Path.Combine(Plugin.PluginInterface.ConfigFile.DirectoryName ?? String.Empty, ConfigName);

        internal string content = String.Empty;

        private HashSet<uint> gsActions = new HashSet<uint>();
        private HashSet<uint> gtoffActions = new HashSet<uint>();
        private Dictionary<uint, string> userActions = new Dictionary<uint, string>();

        private FileSystemWatcher watcher = null!;

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
            Configuration config = new Configuration();

            try {
                config.UpdateFromConfigFile();
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                config.Update();
            }

            config.watcher = new FileSystemWatcher(Path.GetDirectoryName(ConfigFile) ?? String.Empty) {
                Filter = ConfigName,
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };

            config.watcher.Changed += config.OnConfigFileChanged;

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
        public async Task<bool> UpdateComboState(GameAction a, bool succeed = true) => await this.ComboManager.StateUpdate(a, succeed);

        public bool UpdateFromConfigFile()
        {
            try {
                var  content = String.Empty;
                using (var fs = new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var stream = new StreamReader(fs))
                {
                    content = stream.ReadToEnd();
                }

                if (String.IsNullOrEmpty(content) || this.content == content) return false;

                var deserializer = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();

                var config = deserializer.Deserialize<Configuration>(content);

                if (config is null) return false;

                this.alwaysInParty = config.alwaysInParty;
                // this.autoTargeting = config.autoTargeting;
                // this.alwaysTargetingNearestEnemy = config.alwaysTargetingNearestEnemy;
                this.targeting = config.targeting;
                this.actionSchedule = config.actionSchedule;
                this.actionRetry = config.actionRetry;
                this.priority = config.priority;
                this.gs = config.gs;
                this.gtoff = config.gtoff;
                this.combo = config.combo;
                this.gsRules = config.gsRules;

                this.content = content;

                return this.UpdateActions();
            } catch {
                return false;
            }
        }

        public bool Update(string content = "")
        {
            try {
                if (String.IsNullOrEmpty(content)) {
                    var serializer = new SerializerBuilder()
                        .IgnoreFields()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .Build();
                    this.content = serializer.Serialize(this);
                } else {
                    var deserializer = new DeserializerBuilder()
                        .IgnoreUnmatchedProperties()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .Build();

                    var config = deserializer.Deserialize<Configuration>(content);
                    // var config = JsonConvert.DeserializeObject<Configuration>(content);

                    if (config is null) return false;

                    this.alwaysInParty = config.alwaysInParty;
                    // this.autoTargeting = config.autoTargeting;
                    // this.alwaysTargetingNearestEnemy = config.alwaysTargetingNearestEnemy;
                    this.targeting = config.targeting;
                    this.actionSchedule = config.actionSchedule;
                    this.actionRetry = config.actionRetry;
                    this.priority = config.priority;
                    this.gs = config.gs;
                    this.gtoff = config.gtoff;
                    this.combo = config.combo;
                    this.gsRules = config.gsRules;

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
                        gs.Add(Actions.ID(s));
                    }
                }
                this.gsActions = gs;

                var gtoff = new HashSet<uint>();
                foreach(string s in this.gtoff) {
                    if (Actions.Contains(s)) {
                        gtoff.Add(Actions.ID(s));
                    }
                }
                this.gtoffActions = gtoff;

                var ua = new Dictionary<uint, string>();
                foreach(var i in this.gsRules) {
                    uint actionID = 0;
                    if (Actions.Contains(i.Key)) {
                        actionID = Actions.ID(i.Key);
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

                this.ComboManager = ComboManager.FromString(this.combo);
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}");
                return false;
            }

            return true;
        }

        public void OnConfigFileChanged(object s, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed) {
                var suc = this.UpdateFromConfigFile();
                PluginLog.Debug($"Config updateed? {suc}");
            }
        }

        public void Dispose()
        {
            this.watcher.Changed -= OnConfigFileChanged;
            Save();
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