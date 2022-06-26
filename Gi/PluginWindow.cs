using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Newtonsoft.Json;
using ImGuiNET;
using System;
using System.Numerics;

namespace Gi
{
    public class PluginWindow : Window
    {
        private Configuration config;
        private string content;

        public PluginWindow(Configuration config) : base("Gi Settings")
        {
            this.config = config;
            this.content = JsonConvert.SerializeObject(config, Formatting.Indented);

            IsOpen = false;
            Size = new Vector2(800, 600);
            SizeCondition = ImGuiCond.FirstUseEver;
            WindowName = "Gi Settings";
            Flags = ImGuiWindowFlags.NoCollapse;
        }

        public override void Draw()
        {
            if (!IsOpen) return;

            try {
                ImGui.SetNextItemWidth(-1);
                ImGui.SetNextWindowSizeConstraints(new Vector2(800, 600), new Vector2(float.MaxValue, float.MaxValue));
                ImGui.InputTextMultiline("",
                                         ref this.content,
                                         1000,
                                         new Vector2(ImGui.GetWindowWidth(), ImGui.GetWindowHeight() - 70),
                                         ImGuiInputTextFlags.AllowTabInput);
                if (ImGui.Button("Save")) {
                    PluginLog.Log($"Update config: {this.content}");
                    if (this.config.Update(this.content))
                        this.config.Save();
                    IsOpen = false;
                }
            } catch(Exception e) {
                PluginLog.Log($"Exception: {e}");
            }
        }
    }
}
