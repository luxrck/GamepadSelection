using Dalamud.Interface;
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
        private UiBuilder UiBuilder = Plugin.PluginInterface.UiBuilder;

        private ImFontPtr Font;
        private bool FontLoaded = false;
        private string errorMessage = String.Empty;

        public PluginWindow() : base("GamepadTweaks")
        {
            IsOpen = false;
            Size = new Vector2(600, 400);
            SizeCondition = ImGuiCond.FirstUseEver;
            Flags = ImGuiWindowFlags.NoCollapse;

            if (!FontLoaded)
                UiBuilder.BuildFonts += BuildFont;
        }

        private void BuildFont()
        {
            if (!Plugin.Ready) return;

            // Do not crash!
            if (!File.Exists(Configuration.FontFile)) {
                PluginLog.Error($"Font file not found!: {Configuration.FontFile}");
                return;
            }

            try {
                Font = ImGui.GetIO().Fonts.AddFontFromFileTTF(Configuration.FontFile, 24.0f, null, ImGui.GetIO().Fonts.GetGlyphRangesChineseFull());
                // Font = ImGui.GetIO().Fonts.AddFontFromFileTTF(Config.FontFile, 24.0f);
                PluginLog.Debug($"Load Monospace Font: {Configuration.FontFile}");
            } catch(Exception e) {
                PluginLog.Error($"Exception: {e}. Errors in loading: {Configuration.FontFile}");
            }
            FontLoaded = true;
        }

        public override void Draw()
        {
            if (!IsOpen) return;
            if (!FontLoaded) {
                UiBuilder.RebuildFonts();
                return;
            }

            if (FontLoaded) ImGui.PushFont(Font);

            // ImGui.SetNextWindowSizeConstraints(Size ?? new Vector2(600, -1.0f), new Vector2(float.MaxValue, -1.0f));
            ImGui.InputTextMultiline("",
                                     ref Plugin.Config.content,
                                     10000,
                                     new Vector2(ImGui.GetWindowWidth(), ImGui.GetWindowHeight() - ImGui.GetFrameHeight() - ImGui.GetFontSize() * 2.0f),
                                     ImGuiInputTextFlags.AllowTabInput);

            if (FontLoaded) ImGui.PopFont();

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

        private bool Save()
        {
            PluginLog.Debug($"Update config: {Plugin.Config.content}");
            if (Plugin.Config.Update(Plugin.Config.content)) {
                Plugin.Config.Save();
                return true;
            } else {
                try {
                    JsonConvert.DeserializeObject<Configuration>(Plugin.Config.content);
                } catch(JsonReaderException e) {
                    this.errorMessage = e.Message;
                    ImGui.OpenPopup("Error");
                }
                return false;
            }
        }

        public void Dispose()
        {
            UiBuilder.BuildFonts -= BuildFont;
        }
    }
}
