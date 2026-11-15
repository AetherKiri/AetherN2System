using System;
using System.Collections.Generic;
using System.Text;

namespace NitroSharp.LegacyNps;

// The original SYSTEM config opens a modal Win32 dialog. Keep its script
// suspension semantics while using the portable engine's ordinary UI controls.
internal static class LegacyNpsSettings
{
    public static string Initialize(LegacyNpsPresentation? presentation)
        => "if (!$legacy_config_initialized) { $legacy_config_initialized = true; "
            + $"#SYSTEM_sound_bgm = true; #SYSTEM_sound_volume_bgm = {Math.Clamp(presentation?.BgmVolume ?? 100, 0, 100) * 10}; "
            + $"#SYSTEM_sound_se = true; #SYSTEM_sound_volume_se = {Math.Clamp(presentation?.SeVolume ?? 100, 0, 100) * 10}; "
            + $"#SYSTEM_sound_voice = true; #SYSTEM_sound_volume_voice = {Math.Clamp(presentation?.VoiceVolume ?? 100, 0, 100) * 10}; "
            + "$SYSTEM_text_interval = 48; #SYSTEM_text_speed = 200; #SYSTEM_auto_wait_per_character = 100; }";

    public static string Generate(string function, int width, int height)
    {
        var source = new StringBuilder();
        void Line(string line) => source.AppendLine(line);
        int X(int value) => value * width / 800;
        int Y(int value) => value * height / 600;
        string root = "legacy_config";
        string open = "$" + function + "_open";
        var buttons = new List<string>();
        void Text(string name, int x, int y, int w, string expression)
            => Line($"CreateText(\"{name}\", 20003, {X(x)}, {Y(y)}, {X(w)}, {Y(52)}, {expression});");
        void Button(string name, string label, int x, int y, int w)
        {
            buttons.Add(name);
            Line($"CreateChoice(\"{name}\", true);");
            foreach (string state in new[] { "MouseUsual", "MouseOver", "MouseClick" })
            {
                string path = name + "/" + state;
                string color = state == "MouseUsual" ? "#303C50" : "#536C8F";
                Line($"CreateColor(\"{path}/hit\", 20002, {X(x)}, {Y(y)}, {X(w)}, {Y(42)}, {color});");
                Text(path + "/label", x + 12, y - 7, w, "\"<FONT size='22'>" + label + "</FONT>\"");
            }
        }
        Line($"function {function}() {{ {open} = true; $SYSTEM_skip = false; SetTextOrientation(false);");
        Line($"CreateName(\"{root}\"); CreateColor(\"{root}/shade\", 20000, 0, 0, {width}, {height}, #000000); Fade(\"{root}/shade\", 0, 650, null, false);");
        Line($"CreateColor(\"{root}/panel\", 20001, {X(65)}, {Y(45)}, {X(670)}, {Y(510)}, #152030);");
        Text(root + "/title", 95, 55, 560, "\"<FONT size='30'>Settings</FONT>\"");
        string[] channels = ["bgm", "se", "voice"];
        string[] labels = ["Music", "Sound effects", "Voice"];
        for (int i = 0; i < channels.Length; i++)
        {
            int y = 125 + i * 65;
            Text(root + "/label_" + channels[i], 95, y - 7, 210, "\"<FONT size='22'>" + labels[i] + "</FONT>\"");
            Button(root + "_" + channels[i] + "_minus", "-", 310, y, 55);
            Button(root + "_" + channels[i] + "_plus", "+", 490, y, 55);
            Button(root + "_" + channels[i] + "_mute", "Mute", 570, y, 120);
        }
        Text(root + "/label_text", 95, 313, 210, "\"<FONT size='22'>Text speed</FONT>\"");
        Button(root + "_text_minus", "Slower", 310, 320, 110);
        Button(root + "_text_plus", "Faster", 570, 320, 120);
        Text(root + "/label_auto", 95, 378, 210, "\"<FONT size='22'>Auto wait</FONT>\"");
        Button(root + "_auto_minus", "Shorter", 310, 385, 110);
        Button(root + "_auto_plus", "Longer", 570, 385, 120);
        Button(root + "_close", "Close", 570, 475, 120);
        Text(root + "/hint", 95, 473, 450, "\"<FONT size='18'>Esc / right click to return</FONT>\"");
        for (int i = 0; i < buttons.Count; i++)
        {
            Line($"SetNextFocus(\"{buttons[i]}\", \"{buttons[(i + 1) % buttons.Count]}\", Right);");
            Line($"SetNextFocus(\"{buttons[i]}\", \"{buttons[(i + buttons.Count - 1) % buttons.Count]}\", Left);");
            Line($"SetNextFocus(\"{buttons[i]}\", \"{buttons[(i + 1) % buttons.Count]}\", Down);");
            Line($"SetNextFocus(\"{buttons[i]}\", \"{buttons[(i + buttons.Count - 1) % buttons.Count]}\", Up);");
        }
        Line($"while ({open}) {{ Delete(\"{root}/value_*\");");
        for (int i = 0; i < channels.Length; i++)
        {
            string channel = channels[i];
            Text(root + "/value_" + channel, 385, 118 + i * 65, 110,
                $"\"<FONT size='22'>\" + #SYSTEM_sound_volume_{channel} / 10 + \"%</FONT>\"");
            Line($"if (!#SYSTEM_sound_{channel}) {{");
            Text(root + "/value_muted_" + channel, 570, 150 + i * 65, 140, "\"<FONT size='16' color='#FFFF80'>Muted</FONT>\"");
            Line("}");
        }
        Text(root + "/value_text", 440, 313, 110, "\"<FONT size='22'>\" + $SYSTEM_text_interval + \" ms</FONT>\"");
        Text(root + "/value_auto", 440, 378, 110, "\"<FONT size='22'>\" + #SYSTEM_auto_wait_per_character + \" ms</FONT>\"");
        Line("select {");
        foreach (string channel in channels)
        {
            Line($"case {root}_{channel}_minus {{ #SYSTEM_sound_volume_{channel} = #SYSTEM_sound_volume_{channel} - 100; if (#SYSTEM_sound_volume_{channel} < 0) {{ #SYSTEM_sound_volume_{channel} = 0; }} }}");
            Line($"case {root}_{channel}_plus {{ #SYSTEM_sound_volume_{channel} = #SYSTEM_sound_volume_{channel} + 100; if (#SYSTEM_sound_volume_{channel} > 1000) {{ #SYSTEM_sound_volume_{channel} = 1000; }} }}");
            Line($"case {root}_{channel}_mute {{ #SYSTEM_sound_{channel} = !#SYSTEM_sound_{channel}; }}");
        }
        Line($"case {root}_text_minus {{ $SYSTEM_text_interval = $SYSTEM_text_interval + 8; if ($SYSTEM_text_interval > 96) {{ $SYSTEM_text_interval = 96; }} }}");
        Line($"case {root}_text_plus {{ $SYSTEM_text_interval = $SYSTEM_text_interval - 8; if ($SYSTEM_text_interval < 0) {{ $SYSTEM_text_interval = 0; }} }}");
        Line($"case {root}_auto_minus {{ #SYSTEM_auto_wait_per_character = #SYSTEM_auto_wait_per_character - 25; if (#SYSTEM_auto_wait_per_character < 25) {{ #SYSTEM_auto_wait_per_character = 25; }} }}");
        Line($"case {root}_auto_plus {{ #SYSTEM_auto_wait_per_character = #SYSTEM_auto_wait_per_character + 25; if (#SYSTEM_auto_wait_per_character > 500) {{ #SYSTEM_auto_wait_per_character = 500; }} }}");
        Line($"case {root}_close {{ {open} = false; }}");
        Line($"if ($SYSTEM_keydown_esc || $SYSTEM_r_button_down) {{ {open} = false; break; }}");
        Line($"}} }} Delete(\"{root}\"); Delete(\"{root}_*\"); }}");
        return source.ToString();
    }
}
