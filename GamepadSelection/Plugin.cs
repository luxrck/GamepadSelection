using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Dalamud.Plugin;
using GamepadSelection.Attributes;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace GamepadSelection
{
    public class Plugin : IDalamudPlugin
    {
        internal readonly DalamudPluginInterface pluginInterface;
        internal readonly ChatGui chat;
        internal readonly GameGui game;
        internal readonly ClientState clientState;

        internal readonly PluginCommandManager<Plugin> commandManager;
        internal readonly Configuration config;
        internal readonly PluginWindow window;
        internal readonly WindowSystem windowSystem;

        internal GamepadSelection GamepadSelection;
        internal PartyList partyList;
        internal BuddyList buddyList;
        internal GamepadState gamepad;

        public string Name => "Gamepad Selection (for Healers)";

        public Plugin(
            DalamudPluginInterface pi,
            CommandManager commands,
            ChatGui chat,
            GameGui game,
            ClientState clientState,
            GamepadState gamepad,
            PartyList partyList,
            BuddyList buddyList)
        {
            this.pluginInterface = pi;
            this.chat = chat;
            this.game = game;
            this.clientState = clientState;
            this.partyList = partyList;
            this.buddyList = buddyList;
            this.gamepad = gamepad;
            // this.player = clientState.LocalPlayer;

            // Get or create a configuration object
            this.config = Configuration.Load(pi);
            
            // Initialize the UI
            this.window = new PluginWindow(config);
            this.windowSystem = new WindowSystem(this.Name);
            this.windowSystem.AddWindow(this.window);
            
            this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;

            // Load all of our commands
            this.commandManager = new PluginCommandManager<Plugin>(this, commands);
            

            // this.GamepadSelection = new GamepadSelection(this.clientState, this.gamepad, this.partyList, this.buddyList, this.config);
            this.GamepadSelection = new GamepadSelection(this);
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
            unsafe {
                var me = this.clientState.LocalPlayer;
                this.Echo("{" + $"\"{me.TargetObject.Name.ToString()}\", {me.TargetObject.ObjectId}" + "}");
            }
            return;
            if (args is null || args == "") {
                this.window.Toggle();
            } else {
                // add "Celestial Intersection" 'y b a x'
                // add Haima default
                var argv = args.Trim().Split(" ", 2).Where(a => a != "").ToList();
                switch(argv[0])
                {
                    case "list":
                        this.Echo("[Actions in Monitor]");
                        foreach(string a in this.config.actionsInMonitor) {
                            this.Echo($"{a} : default");
                        }
                        foreach(var a in this.config.rules) {
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
                                this.config.rules.TryAdd(action, order);
                            } else {
                                if (!this.config.actionsInMonitor.Contains(action)) {
                                    this.config.actionsInMonitor.Add(action);
                                }
                            }
                            this.Echo($"Add action: {action} ... [ok]");
                        } catch(Exception e) {
                            this.chat.PrintError($"Add action failed.");
                            PluginLog.Error($"Exception: {e}");
                        }
                        break;
                    case "remove":
                        try {
                            var action = argv[1];
                            this.config.actionsInMonitor.Remove(action);
                            this.config.rules.Remove(action);
                            this.Echo($"Remove action: {action} ... [ok]");
                        } catch(Exception e) {
                            this.chat.PrintError($"Remove action failed.");
                            PluginLog.Error($"Exception: {e}");
                        }
                        break;
                    default:
                        break;
                }

                try {
                    this.config.Update();
                } catch(Exception e) {
                    PluginLog.Error($"Exception: {e}");
                }
                this.chat.UpdateQueue();
            }
        }

        public void Echo(string s)
        {
            this.chat.PrintChat(new XivChatEntry() {
                Message = s,
                Type = XivChatType.Debug,
            });
        }

        public void Error(string s) {
            this.chat.PrintError(s);
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            this.commandManager.Dispose();
            this.GamepadSelection.Dispose();

            this.config.Save();

            this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
            this.windowSystem.RemoveAllWindows();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
