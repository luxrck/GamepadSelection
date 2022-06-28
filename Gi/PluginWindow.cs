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
        private string errorMessage;
        private ImFontPtr? font;

        public PluginWindow(Configuration config) : base("Gi Settings")
        {
            this.config = config;
            this.content = JsonConvert.SerializeObject(config, Formatting.Indented);
            this.errorMessage = "";

            // try {
            //     var fonts = ImGui.GetIO().Fonts;
            //     this.font = fonts.AddFontFromFileTTF(this.config.fontFile, 24.0f);
            //     fonts.Build();
            //     PluginLog.Log($"Font IsLoaded?: {this.font.Value.IsLoaded()} {this.config.fontFile}");
            // } catch(Exception e) {
            //     PluginLog.Log($"Exception: {e}");
            //     this.font = null;
            // }

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
                                     ref this.content,
                                     1000,
                                     new Vector2(ImGui.GetWindowWidth(), ImGui.GetWindowHeight() - 70),
                                     ImGuiInputTextFlags.AllowTabInput);
            
            // if (this.font.HasValue) {
            //     ImGui.PopFont();
            // }
            
            if (ImGui.Button("Save")) {
                this.Save();
            }
            
            ImGui.SameLine();

            if (ImGui.Button("Close")) {
                IsOpen = false;
            }

            ImGui.SameLine();

            if (ImGui.Button("Save & Close")) {
                this.Save();
                IsOpen = false;
            }
                
            if (this.errorMessage != "") {
                if (ImGui.BeginPopupModal("Error")) {
                    ImGui.TextColored(new Vector4(255, 0, 0, 255), $"{this.errorMessage}");
                    if (ImGui.Button("Close")) {
                        this.errorMessage = "";
                    }
                    ImGui.EndPopup();
                }
            }
        }

        private void Save() {
            PluginLog.Log($"Update config: {this.content}");
            if (this.config.Update(this.content)) {
                this.config.Save();
            } else {
                try {
                    JsonConvert.DeserializeObject<Configuration>(this.content);
                } catch(JsonReaderException e) {
                    this.errorMessage = e.Message;
                    ImGui.OpenPopup("Error");
                }
            }
        }
    }
}
