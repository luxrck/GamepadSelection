using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Newtonsoft.Json;
using ImGuiNET;
using System;
using System.Numerics;


namespace GamepadTweaks
{
    public class PluginWindow : Window
    {
        private Configuration Config = Plugin.Config;
        private string errorMessage;
        // private ImFontPtr? font;

        public PluginWindow() : base("Gi Settings")
        {
            this.errorMessage = "";

            IsOpen = false;
            Size = new Vector2(800, 600);
            SizeCondition = ImGuiCond.FirstUseEver;
            WindowName = "Gi Settings";
            Flags = ImGuiWindowFlags.NoCollapse;
        }

        public override void Draw()
        {
            if (!IsOpen) return;

            // if (this.font.HasValue) {
            //     ImGui.PushFont(this.font.Value);
            // }

            ImGui.SetNextItemWidth(-1);
            ImGui.SetNextWindowSizeConstraints(new Vector2(800, 600), new Vector2(float.MaxValue, float.MaxValue));

            ImGui.InputTextMultiline("",
                                     ref Config.content,
                                     1000,
                                     new Vector2(ImGui.GetWindowWidth(), ImGui.GetWindowHeight() - 100),
                                     ImGuiInputTextFlags.AllowTabInput);
            
            // if (this.font.HasValue) {
            //     ImGui.PopFont();
            // }
            
            if (this.errorMessage != "") {
                if (ImGui.BeginPopupModal("Error")) {
                    ImGui.TextColored(new Vector4(255, 0, 0, 255), $"{this.errorMessage}");
                    if (ImGui.Button("Close")) {
                        this.errorMessage = "";
                    }
                    ImGui.EndPopup();
                }
            }
            
            if (ImGui.Button("Save")) {
                this.Save();
            }

            ImGui.SameLine();

            if (ImGui.Button("Close")) {
                IsOpen = false;
            }

            ImGui.SameLine();

            if (ImGui.Button("Save & Close")) {
                if (this.Save()) {
                    IsOpen = false;
                }
            }
        }

        private bool Save() {
            PluginLog.Debug($"Update config: {Config.content}");
            if (Config.Update(Config.content)) {
                Config.Save();
                return true;
            } else {
                try {
                    JsonConvert.DeserializeObject<Configuration>(Config.content);
                } catch(JsonReaderException e) {
                    this.errorMessage = e.Message;
                    ImGui.OpenPopup("Error");
                }
                return false;
            }
        }
    }
}
