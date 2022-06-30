using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using GamepadSelection.Attributes;
using Newtonsoft.Json;
using System;
using System.Linq;
using Dalamud.Logging;

namespace GamepadSelection
{
    public class Plugin : IDalamudPlugin
    {
        internal readonly DalamudPluginInterface pluginInterface;
        internal readonly ChatGui chat;
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
            ClientState clientState,
            GamepadState gamepad,
            PartyList partyList,
            BuddyList buddyList)
        {
            this.pluginInterface = pi;
            this.chat = chat;
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
<selectOrder>   The order for party member selection (only accepted Digipad and y/b/a/x buttons).
   Xbox |   PS
    y   |   △   |   n:North
    b   |   ○   |   e:East
    a   |   x   |   s:South
    x   |   □   |   w:West")]
        public void CommandGs(string command, string args)
        {
            if (args is null || args == "") {
                this.window.Toggle();
            } else {
                var argv = args.Trim().Split(" ").Where(a => a != "").ToList();
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
                            var action = argv[1];
                            if (argv.Count > 2) {
                                this.config.rules.TryAdd(action, argv[2]);
                            } else {
                                if (!this.config.actionsInMonitor.Contains(action)) {
                                    this.config.actionsInMonitor.Add(action);
                                }
                            }
                            this.Echo($"Add action: {action} ... [ok]");
                        } catch {
                            this.chat.PrintError($"Add action failed.");
                        }
                        break;
                    case "remove":
                        try {
                            var action = argv[1];
                            this.config.actionsInMonitor.Remove(action);
                            this.config.rules.Remove(action);
                            this.Echo($"Remove action: {action} ... [ok]");
                        } catch {
                            this.chat.PrintError($"Remove action failed.");
                        }
                        break;
                    default:
                        break;
                }

                try {
                    var content = JsonConvert.SerializeObject(this.config, Formatting.Indented);
                    this.config.UpdateContent(content);
                } catch(Exception e) {
                    PluginLog.Log($"Exception: {e}");
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
