using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Dalamud.Plugin;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using GamepadSelection.Attributes;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace GamepadSelection
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
        
        public static PluginCommandManager<Plugin> Commands { get; private set; }
        public static Configuration Config { get; private set; }
        
        public string Name => "Gamepad Selection (for Healers)";
        
        private PluginWindow Window { get; set; }
        private WindowSystem WindowSystem { get; set; }
        
        private GamepadActionManager GamepadActionManager;

        public Plugin(
            DalamudPluginInterface pi,
            CommandManager commands)
        {
            Config = Configuration.Load();
            
            // Load all of our commands
            Commands = new PluginCommandManager<Plugin>(this, commands);
            GamepadActionManager = new GamepadActionManager();
            
            // Initialize the UI
            Window = new PluginWindow();
            WindowSystem = new WindowSystem(Name);
            WindowSystem.AddWindow(Window);
            
            PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        }
        
        [Command("/gi")]
        [HelpMessage(@"Open setting panel.
/gi list → List actions and corresponding selection order.
/gi add <action> [<selectOrder>] → Add specific <action> in monitor.
/gi remove <action> → Remove specific monitored <action>.

<action>        Action name (in string).
<selectOrder>   The order for party member selection (only accepted Dpad and y/b/a/x buttons).
   Xbox |   PS
    y   |   △   |   n:North
    b   |   ○   |   e:East
    a   |   x   |   s:South
    x   |   □   |   w:West")]
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
                    case "list":
                        this.Echo("[Actions in Monitor]");
                        foreach(string a in Config.gtoff) {
                            this.Echo($"{a} : gtoff");
                        }
                        foreach(string a in Config.gs) {
                            this.Echo($"{a} : default");
                        }
                        foreach(var a in Config.rules) {
                            this.Echo($"{a.Key} : {a.Value}");
                        }
                        break;
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
                            this.Echo($"Add action: {action} ... [ok]");
                        } catch(Exception e) {
                            Chat.PrintError($"Add action failed.");
                            PluginLog.Error($"Exception: {e}");
                        }
                        break;
                    case "remove":
                        try {
                            var action = argv[1];
                            Config.gs.Remove(action);
                            Config.rules.Remove(action);
                            this.Echo($"Remove action: {action} ... [ok]");
                        } catch(Exception e) {
                            Chat.PrintError($"Remove action failed.");
                            PluginLog.Error($"Exception: {e}");
                        }
                        break;
                    default:
                        break;
                }

                try {
                    Config.Update();
                } catch(Exception e) {
                    PluginLog.Error($"Exception: {e}");
                }
                Chat.UpdateQueue();
            }
        }

        public void Echo(string s)
        {
            Chat.PrintChat(new XivChatEntry() {
                Message = s,
                Type = XivChatType.Debug,
            });
        }

        public void Error(string s) {
            Chat.PrintError(s);
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            Commands.Dispose();
            GamepadActionManager.Dispose();

            Config.Save();

            PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
            WindowSystem.RemoveAllWindows();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
