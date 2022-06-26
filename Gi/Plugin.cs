using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Gi.Attributes;
using System;

namespace Gi
{
    public class Plugin : IDalamudPlugin
    {
        private readonly DalamudPluginInterface pluginInterface;
        private readonly ChatGui chat;
        private readonly ClientState clientState;

        private readonly PluginCommandManager<Plugin> commandManager;
        private readonly Configuration config;
        private readonly PluginWindow window;
        private readonly WindowSystem windowSystem;

        private GamepadSelection GamepadSelection;
        private PartyList partyList;
        private BuddyList buddyList;
        private GamepadState gamepad;

        public string Name => "Gamepad Selection Mode (for AST)";

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
            this.config = new Configuration(pi);
            
            // Initialize the UI
            this.window = new PluginWindow(config);
            this.windowSystem = new WindowSystem(this.Name);
            this.windowSystem.AddWindow(this.window);
            
            this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;

            // Load all of our commands
            this.commandManager = new PluginCommandManager<Plugin>(this, commands);
            
            this.GamepadSelection = new GamepadSelection(this.clientState, this.gamepad, this.partyList, this.buddyList, this.config);

        }
        
        [Command("/gi")]
        [HelpMessage("Open setting panel of Gi.")]
        public void CommandGi(string command, string args)
        {
            this.window.Toggle();
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
