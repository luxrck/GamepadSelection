using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Dalamud.Plugin;
using XivCommon;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Diagnostics;

using GamepadTweaks.Attributes;

namespace GamepadTweaks
{
    public class Plugin : IDalamudPlugin
    {
        [PluginService] public static DalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] public static ChatGui Chat { get; private set; } = null!;
        [PluginService] public static ClientState ClientState { get; private set; } = null!;
        [PluginService] public static GameGui GameGui { get; private set; } = null!;
        [PluginService] public static ObjectTable Objects { get; private set; } = null!;
        [PluginService] public static SigScanner SigScanner { get; private set; } = null!;
        [PluginService] public static PartyList PartyList { get; private set; } = null!;
        [PluginService] public static GamepadState GamepadState { get; private set; } = null!;
        [PluginService] public static TargetManager TargetManager { get; private set; } = null!;
        [PluginService] public static Framework Framework { get; private set; } = null!;

        public static XivCommonBase XivCommon = new XivCommonBase();

        public static string ClientLanguage => ClientState is null ? "zh" : ClientState.ClientLanguage switch {
            // Dalamud.ClientLanguage.ChineseSimplified => "zh",
            Dalamud.ClientLanguage.English => "en",
            Dalamud.ClientLanguage.Japanese => "jp",
            Dalamud.ClientLanguage.French => "fr",
            Dalamud.ClientLanguage.German => "de",
            _ => "zh",
        };

        public string Name => "Gamepad Tweaks (for Healers)";

        // aaah
        public static uint PlayerSpellSpeed {
            get {
                if (!Plugin.Ready) return 0;
                unsafe {
                    return *(uint*)(SigScanner.Module.BaseAddress + 0x1e9fe4c);
                }
            }
        }

        public static uint PlayerSkillSpeed {
            get {
                if (!Plugin.Ready) return 0;
                unsafe {
                    return *(uint*)(SigScanner.Module.BaseAddress + 0x1e9fe48);
                }
            }
        }

        public static GamepadActionManager GamepadActionManager{ get; private set; } = null!;
        public static Configuration Config { get; private set; } = null!;
        public static Actions Actions { get; private set; } = null!;

        public PluginWindow Window { get; set; }
        public WindowSystem WindowSystem { get; set; }
        public PluginCommandManager<Plugin> Commands;

        public Plugin(
            DalamudPluginInterface pi,
            CommandManager commands)
        {
            Actions = GamepadTweaks.Actions.Instance();
            Config = Configuration.Load();

            GamepadActionManager = new GamepadActionManager();

            // Load all of our commands
            Commands = new PluginCommandManager<Plugin>(this, commands);


            // Initialize the UI
            Window = new PluginWindow();
            WindowSystem = new WindowSystem(Name);
            WindowSystem.AddWindow(Window);

            PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        }

        [Command("/gt")]
        [HelpMessage(@"Open config window.
/gt config → Open config file with external file editor.
/gt on/off → Enable/Disable this plugin.
/gt info → Show gt info.
/gt add <action> [<selectOrder>] → Add specific <action> in monitor.
/gt remove <action> → Remove specific monitored <action>.
/gt reset [<action>] → Reset combo index for given group (reset all if <action> not given).
/gt id <action> → Show Action ID.

<action>        Action name (in string).
<selectOrder>   The order for party member selection (only accepted Dpad and y/b/a/x buttons (Xbox)).")]
        public void CommandGi(string command, string args)
        {
            if (args is null || args == "") {
                Window.Toggle();
            } else {
                // add "Celestial Intersection" 'y b a x'
                // add Haima default
                var argv = args.Trim().Split(" ", 2).Where(a => a != "").ToList();
                switch(argv[0])
                {
                    case "test":
                        return;
                    case "config":
                        var editor = new Process() {
                            StartInfo = new ProcessStartInfo(Configuration.ConfigFile) {
                                UseShellExecute = true,
                            },
                        };
                        PluginLog.Debug($"Open: {Configuration.ConfigFile}");
                        editor.Start();
                        return;
                    case "on":
                        Echo("[GamepadTweaks] Enabled.");
                        GamepadActionManager.Enable();
                        Chat.UpdateQueue();
                        return;
                    case "off":
                        Echo("[GamepadTweaks] Disabled.");
                        GamepadActionManager.Disable();
                        Chat.UpdateQueue();
                        return;
                    case "info":
                        string bs(bool x) => x ? "●" : "○";
                        // string pr<T>(T x, int s = 6) => $"{x}".PadRight(s);
                        // string pl<T>(T x, int s = 6) => $"{x}".PadLeft(s);
                        Echo("====== [S GamepadTweaks] ======");
                        Echo($"小队成员: {bs(Config.alwaysInParty || PartyList.Length > 0)}");
                        Echo($"自动锁定: {bs(Config.autoTargeting)}");
                        foreach(string a in Config.gtoff) {
                            Echo($"[G] {a}");
                        }
                        foreach(string a in Config.gs) {
                            Echo($"[D] {a}");
                        }
                        foreach(var a in Config.rules) {
                            Echo(@$"[U] {a.Key} =>
        {a.Value}");
                        }
                        Echo("====== [E GamepadTweaks] ======");
                        Chat.UpdateQueue();
                        return;
                    case "add":
                        try {
                            var actionkv = argv[1].Trim();
                            var pattern = new Regex(@"[\""\']?\s*(?<action>[\w\s]+\w)\s*[\""\']?(\s+[\""\']?\s*(?<order>[\w\s]+\w)\s*[\""\']?)?",
                                                    RegexOptions.Compiled);

                            var match = pattern.Match(actionkv);

                            var action = match.Groups.ContainsKey("action") ? match.Groups["action"].ToString() : "";
                            var order = match.Groups.ContainsKey("order") ? match.Groups["order"].ToString() : "";

                            if (order != "") {
                                Config.rules.TryAdd(action, order);
                            } else {
                                if (!Config.gs.Contains(action)) {
                                    Config.gs.Add(action);
                                }
                            }
                            Echo($"[GamepadTweaks] Add action: {action} ... [ok]");
                        } catch(Exception e) {
                            Chat.PrintError($"[GamepadTweaks] Add action failed.");
                            PluginLog.Error($"Exception: {e}");
                        }
                        Chat.UpdateQueue();
                        break;
                    case "remove":
                        try {
                            var action = argv[1];
                            Config.gs.Remove(action);
                            Config.rules.Remove(action);
                            Echo($"[GamepadTweaks] Remove action: {action} ... [ok]");
                        } catch(Exception e) {
                            Chat.PrintError($"[GamepadTweaks] Remove action failed.");
                            PluginLog.Error($"Exception: {e}");
                        }
                        Chat.UpdateQueue();
                        break;
                    case "reset":
                        uint groupID = 0;
                        if (argv.Count > 1) {
                            groupID = Actions.ID(argv[1].Trim());
                        }
                        Config.ResetComboState(groupID);
                        return;
                    case "id":
                        Task.Run(async () => {
                            try {
                                var action = argv[1];
                                Send($"/ac {action} <t>");
                                await Task.Delay(1000);
                                var a = GamepadActionManager.LastActionID;
                                PluginLog.Debug($"Action: {action}, ID?: {a}");
                                Echo($"[GamepadTweaks] Action: {action}, ID: {a}");
                            } catch(Exception e) {
                                Chat.PrintError($"[GamepadTweaks] Retrive action id failed.");
                                PluginLog.Error($"Exception: {e}");
                            }
                            Chat.UpdateQueue();
                        });
                        return;
                    default:
                        return;
                }

                try {
                    Config.Update();
                } catch(Exception e) {
                    PluginLog.Error($"Exception: {e}");
                }
            }
        }

        public static bool Ready => ClientState.LocalPlayer is not null;
        public static PlayerCharacter? Player => ClientState.LocalPlayer;

        public static void Echo(string s)
        {
            Chat.PrintChat(new XivChatEntry() {
                Message = s,
                Type = XivChatType.Debug,
            });
        }

        public static void Send(string s)
        {
            PluginLog.Debug($"[Send] {s}");
            XivCommon.Functions.Chat.SendMessage(s);
        }

        public void Error(string s) {
            Chat.PrintError(s);
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            Commands.Dispose();

            // Framework.Update -= GamepadActionManager.UpdateFramework;
            GamepadActionManager.Dispose();

            Config.Dispose();

            PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
            WindowSystem.RemoveAllWindows();

            Window.Dispose();
            PluginLog.Debug("Exiting GamepadTweaks.");
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
