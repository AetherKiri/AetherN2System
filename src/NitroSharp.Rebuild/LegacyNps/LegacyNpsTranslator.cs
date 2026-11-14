using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NitroSharp.LegacyNps;

internal sealed record LegacyNpsTranslation(string Source, IReadOnlyList<string> Diagnostics);

internal sealed record LegacyNpsTextBox(int X, int Y, int Width, int Height,
    int MarginLeft = 0, int MarginTop = 10, int MarginRight = 0, int MarginBottom = 0,
    string? FrameImage = null, int Opacity = 100);

internal sealed record LegacyNpsPresentation(IReadOnlyDictionary<int, LegacyNpsTextBox> TextBoxes,
    IReadOnlyDictionary<string, int> Alignments, IReadOnlyDictionary<string, string>? TransitionMasks = null);

// A bounded dispatcher keeps jumps inside the NSS bytecode's signed 16-bit range.
// Unsupported operations remain executable failures, rather than compilation-time
// rejection of later branches or silently successful placeholders.
internal static class LegacyNpsTranslator
{
    public static LegacyNpsTranslation Translate(string source, string sourceName,
        Func<string, string>? resolveModule = null, Func<string, string, string>? resolveAsset = null,
        int width = 800, int height = 600, LegacyNpsPresentation? presentation = null)
        => new Translation(source, sourceName, resolveModule, resolveAsset, width, height, presentation).Run();

    private sealed class Translation
    {
        private readonly List<LegacyNpsToken> _tokens;
        private readonly Dictionary<string, int> _markers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _diagnostics = [];
        private readonly string _name;
        private readonly string _prefix;
        private readonly Func<string, string> _module;
        private readonly Func<string, string, string> _asset;
        private readonly int _width, _height;
        private readonly IReadOnlyDictionary<int, LegacyNpsTextBox> _boxes;
        private readonly IReadOnlyDictionary<string, int> _alignments;
        private readonly IReadOnlyDictionary<string, string> _transitionMasks;
        private readonly Dictionary<string, int> _actors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _layers = new(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _output = new();
        private string Pc => "$" + _prefix + "pc";
        private string Dispatch => "$" + _prefix + "dispatch";
        private string Stage => "$" + _prefix + "stage";
        private string Front => "$" + _prefix + "front";
        private string Temp => "$" + _prefix + "temp";
        private string Box => "$" + _prefix + "box";
        private string Background => "$" + _prefix + "background";
        private string BackgroundKind => "$" + _prefix + "background_kind";
        private string ScenePriority => "$" + _prefix + "scene_priority";
        private string RevealDuration => "$" + _prefix + "reveal_duration";

        public Translation(string source, string name, Func<string, string>? module,
            Func<string, string, string>? asset, int width, int height, LegacyNpsPresentation? presentation)
        {
            _name = name; _width = width; _height = height;
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            _prefix = "legacy_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12] + "_";
            _module = module ?? (path => Path.ChangeExtension(path.Replace('\\', '/'), ".nss"));
            _asset = asset ?? ((kind, path) => kind + "/" + path.Replace('\\', '/')
                + (Path.HasExtension(path) ? "" : kind is "sound" or "voice" ? ".ogg" : ".png"));
            _boxes = presentation?.TextBoxes ?? new Dictionary<int, LegacyNpsTextBox>
                { [9] = new(20, height - 180, width - 40, 170) };
            _alignments = presentation?.Alignments ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                { ["center"] = width / 2, ["left"] = width * 3 / 10, ["right"] = width * 7 / 10 };
            _transitionMasks = presentation?.TransitionMasks ?? new Dictionary<string, string>();
            _tokens = LegacyNpsReader.Read(source).Where(token => token.Kind != LegacyNpsTokenKind.Comment
                && (token.Kind != LegacyNpsTokenKind.Text || !string.IsNullOrWhiteSpace(token.Value))).ToList();
            for (int i = 0; i < _tokens.Count; i++)
            {
                if (_tokens[i].Value == "MARKER" && _tokens[i].Attributes.TryGetValue("NAME", out string? marker)
                    && !_markers.TryAdd(marker, i)) throw new FormatException($"Duplicate NPS marker '{marker}' in {name}.");
                if (_tokens[i].Value == "BUSTUP" && _tokens[i].Attributes.TryGetValue("NAME", out string? actor))
                    _actors.TryAdd(actor, _actors.Count);
                if (_tokens[i].Value == "LAYER")
                    _layers.TryAdd(_tokens[i].Attributes.GetValueOrDefault("NUM", "0"), _layers.Count);
            }
        }

        public LegacyNpsTranslation Run()
        {
            Line("chapter main { call_scene @->legacy_entry; }");
            Line("scene legacy_entry {");
            Line($"{Pc} = 0; {Front} = \"legacy_front\"; {Stage} = \"legacy_stage\";");
            Line("CreateName(\"legacy_front\"); CreateName(\"legacy_stage\");");
            Line($"{Box} = 9; {_prefix}reset_scene();");
            Line($"while ({Pc} < {_tokens.Count}) {{ {Dispatch} = {Pc};");
            for (int i = 0; i < _tokens.Count; i += 32)
                Line($"if ({Dispatch} >= {i} && {Dispatch} < {i + 32}) {{ {_prefix}step{i / 32}(); }}");
            Line("} }");
            for (int group = 0; group * 32 < _tokens.Count; group++)
            {
                Line($"function {_prefix}step{group}() {{");
                for (int i = group * 32; i < Math.Min((group + 1) * 32, _tokens.Count); i++)
                {
                    Line($"if ({Dispatch} == {i}) {{ {Pc} = {i + 1};");
                    int outputStart = _output.Length;
                    int diagnosticStart = _diagnostics.Count;
                    try { Emit(_tokens[i], i, group); }
                    catch (FormatException error)
                    {
                        _output.Length = outputStart;
                        _diagnostics.RemoveRange(diagnosticStart, _diagnostics.Count - diagnosticStart);
                        Unsupported(_tokens[i], error.Message);
                    }
                    Line("}");
                }
                Line("}");
            }
            EmitSceneFunctions();
            return new LegacyNpsTranslation(_output.ToString(), _diagnostics);
        }

        private void Emit(LegacyNpsToken token, int index, int group)
        {
            if (token.Kind == LegacyNpsTokenKind.Text)
            {
                string block = "page" + index;
                // PRE text cannot contain its own closing delimiter without changing syntax.
                if (token.Value.Contains("</PRE>", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Nested PRE delimiter");
                // NSS interprets braces inside PRE as executable code. Legacy
                // substitutions need their own translator before entering PRE.
                if (token.Value.IndexOfAny(['{', '}']) >= 0) throw new FormatException("Legacy text substitution");
                Line($"<PRE legacy_box>\n[{block}]\n{token.Value.Trim()}\n</PRE>");
                foreach (var (type, box) in _boxes)
                {
                    Line($"if ({Box} == {type}) {{");
                    EmitBox(box);
                    Line($"LoadText({_prefix}step{group}, \"legacy_box\", \"@{block}\", {box.Width - box.MarginLeft - box.MarginRight}, {box.Height - box.MarginTop - box.MarginBottom}, 0, 26);");
                    Line("}");
                }
                Line($"WaitText(\"legacy_box/{block}\", 0); Delete(\"legacy_box/{block}\");");
                return;
            }
            string A(string key, string fallback = "") => token.Attributes.GetValueOrDefault(key, fallback);
            switch (token.Value)
            {
                case "INCLUDE":
                    if (!A("SRC").Equals("include.h", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Nonstandard include");
                    _diagnostics.Add($"{_name}:{token.Line}: standard include.h has no external body; intrinsic commands are translated directly.");
                    break;
                case "MARKER": break;
                case "WAIT": Line($"Wait({Time(A("TIME", "0"))});"); break;
                case "K":
                    if (index == 0 || _tokens[index - 1].Kind != LegacyNpsTokenKind.Text) Line("WaitKey();");
                    break;
                case "CLEAR": Line($"{_prefix}reset_scene();"); break;
                case "HIDEMESSAGE": Line("Delete(\"legacy_box/*\"); Delete(\"legacy_box_frame\");"); break;
                case "BACKGROUND":
                    string background = A("SRC");
                    if (background.StartsWith('#'))
                    {
                        if (!Regex.IsMatch(background, "^#[0-9a-fA-F]{6}$")) throw new FormatException("Invalid background color");
                        Line($"{BackgroundKind} = 1; {Background} = {Q(background)};");
                    }
                    else Line($"{BackgroundKind} = 2; {Background} = {Q(_asset("cg", background))};");
                    break;
                case "LAYER":
                    string layer = Layer(A("NUM", "0"));
                    if (A("SRC").Length == 0) Line($"{layer}_visible = false;");
                    else
                    {
                        Line($"{layer}_image = {Q(_asset("cg", A("SRC")))}; {layer}_x = {Number(A("X", "0"))}; {layer}_y = {Number(A("Y", "0"))};");
                        Line($"{layer}_alpha = {Number(A("TRANSPARENT", "100"))} * 10; {layer}_visible = true;");
                    }
                    break;
                case "BUSTUP": EmitBustup(token); break;
                case "ANIM": case "BGANIM": EmitAnimation(token, index); break;
                case "WIPE":
                    string effect = A("EFFECT", "NORMAL").ToUpperInvariant();
                    string? mask = null;
                    if (effect is not ("NORMAL" or "FADE") && !_transitionMasks.TryGetValue(effect, out mask)) throw new FormatException("WIPE " + effect);
                    Line($"{ScenePriority} = 1000; {_prefix}render_scene();");
                    if (mask is null) EmitReveal(effect == "NORMAL" ? "0" : Time(A("TIME", "0")));
                    else
                    {
                        EmitReveal("0");
                        Line($"DrawTransition({Stage} + \"/*\", {Time(A("TIME", "0"))}, 0, 1000, {(effect == "BLIND" ? 0 : 100)}, null, {Q(mask)}, true);");
                    }
                    // Normalize the committed scene back to the lower priority
                    // band, so each pending scene can cover every old layer.
                    Line($"Delete({Front}); CreateName({Front}); {Temp} = {Stage}; {Stage} = {Front}; {ScenePriority} = 0; {_prefix}render_scene();");
                    EmitReveal("0");
                    Line($"{Stage} = {Temp}; Delete({Stage} + \"/*\");");
                    break;
                case "BGM": case "SE": case "VOICE": EmitAudio(token); break;
                case "A":
                    string condition = Condition(A("OPERATOR"));
                    Line($"if ({condition}) {{"); Jump(A("HREF"), token); Line("}");
                    break;
                case "CHOICE": EmitChoice(token, index); break;
                case "SELECT":
                    Line("select {");
                    for (int i = 0; i < _tokens.Count; i++)
                    {
                        LegacyNpsToken choice = _tokens[i];
                        if (choice.Kind != LegacyNpsTokenKind.Command || choice.Value != "CHOICE") continue;
                        Line($"case legacy_choice_{i} {{ Delete(\"legacy_choice_*\");");
                        if (choice.Attributes.TryGetValue("PUSHSE", out string? sound) && sound.Length > 0)
                            PlayUiSound(sound, choice);
                        Jump(choice.Attributes.GetValueOrDefault("HREF", ""), choice); Line("}");
                    }
                    Line("}"); break;
                case "SYSTEM":
                    if (A("INIT").Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string name in LocalNames()) Line($"VariableValue($, {Q(LocalKey(name))}, 0);");
                    }
                    else if (A("MENU").Length > 0) Line($"VariableValue($, {Q(LocalKey("menu_mask"))}, {Q(A("MENU"))});");
                    else if (A("CMD").Equals("savemode", StringComparison.OrdinalIgnoreCase))
                        Line($"VariableValue($, {Q(LocalKey("save_enabled"))}, {(A("MODE").Equals("on", StringComparison.OrdinalIgnoreCase) ? "true" : "false")});");
                    else if (A("CMD").Equals("timerclear", StringComparison.OrdinalIgnoreCase))
                        Line($"VariableValue($, {Q(LocalKey("timer"))}, 0);");
                    else throw new FormatException("SYSTEM " + A("CMD"));
                    break;
                case "BOX":
                    if (!int.TryParse(A("TYPE"), out int boxType) || !_boxes.ContainsKey(boxType)) throw new FormatException("BOX " + A("TYPE"));
                    Line($"{Box} = {boxType}; Delete(\"legacy_box/*\"); Delete(\"legacy_box_frame\");"); break;
                case "END":
                    if (!A("OPTION").Equals("CLOSE", StringComparison.OrdinalIgnoreCase)) throw new FormatException("END " + A("OPTION"));
                    Line("Exit(); return;"); break;
                default: Unsupported(token, token.Value); break;
            }
        }

        private string Actor(string name) => "$" + _prefix + "actor" + _actors[name];
        private string Layer(string number) => "$" + _prefix + "layer" + _layers[number];

        private void EmitAnimation(LegacyNpsToken token, int index)
        {
            string A(string key, string fallback = "") => token.Attributes.GetValueOrDefault(key, fallback);
            static int Integer(string value, int minimum, int maximum)
                => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    && number >= minimum && number <= maximum ? number : throw new FormatException("Invalid animation integer " + value);
            int number = Integer(A("NUM", "19"), 0, 19);
            int start = Integer(A("START", "0"), 0, 9999);
            int end = Math.Max(start, Integer(A("END", "0"), 0, 9999));
            // Array's built-in argument count is encoded in one byte; reserve
            // one argument for the destination variable.
            if (end - start >= 254) throw new FormatException("Animation has too many frames");
            int still = Integer(A("STILL", "0"), -1, 9999);
            int ticks = checked(Integer(A("WAIT", "0"), 0, 1000000) + 1);
            string prefix = A("SRC");
            if (prefix.Length == 0) throw new FormatException("ANIM requires SRC");
            string x = Number(A("X", "0")), y = Number(A("Y", "0"));
            var frames = new List<string>();
            for (int frame = start; frame <= end; frame++)
            {
                try { frames.Add(_asset("cg", prefix + frame.ToString("D3", CultureInfo.InvariantCulture))); }
                catch (FormatException) when (frame > start)
                {
                    // Original atlas builder 0x10026540 ignores a failed image
                    // load and copies its previous image into this integer cell.
                    frames.Add("");
                }
            }
            string finalFrame = still > 0 ? _asset("cg", prefix + still.ToString("D3", CultureInfo.InvariantCulture)) : "";
            string? sound = A("SE").Length > 0 ? _asset("sound", A("SE")) : null;
            string array = "$" + _prefix + "frames";
            string path = Front + " + " + Q("/anim" + number);
            Line($"Array({array}, {string.Join(", ", frames.Select(Q))});");
            // ANIM's WAIT is skipped 33ms ticks, while the command itself blocks.
            // Start without waiting so SE begins in the same script tick, after
            // frame loading; the visual wait never waits for the sound.
            Line($"CreateFrameAnimation({path}, {200 + number}, {x}, {y}, {array}, {ticks}, {Q(finalFrame)}, {(still == 0 ? "true" : "false")}, {(A("LOOP", "FALSE").Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "true" : "false")}, false);");
            if (sound is not null) Play("legacy_anim_se_" + _prefix + index, "SE", sound, false);
            if (token.Value == "ANIM") Line($"WaitFrameAnimation({path});");
        }

        private void EmitBustup(LegacyNpsToken token)
        {
            string A(string key, string fallback = "") => token.Attributes.GetValueOrDefault(key, fallback);
            string name = A("NAME");
            if (name.Length == 0 || !_actors.ContainsKey(name)) throw new FormatException("BUSTUP requires NAME");
            string mode = A("MODE").ToUpperInvariant();
            if (mode is not ("" or "ON" or "OFF")) throw new FormatException("BUSTUP mode " + mode);
            string actor = Actor(name);
            // Resolve every supplied field before emitting changes, so an invalid
            // face or alignment cannot leave a half-updated actor descriptor.
            string? image = A("FACE").Length > 0 ? _asset("cg", name + "_" + A("FACE")) : null;
            int? x = null;
            if (A("ALIGN").Length > 0)
            {
                if (!_alignments.TryGetValue(A("ALIGN"), out int align)) throw new FormatException("BUSTUP alignment " + A("ALIGN"));
                x = align - _width / 2;
            }
            if (image is not null) Line($"{actor}_image = {Q(image)};");
            if (x is { } position) Line($"{actor}_x = {position};");
            if (mode == "OFF") Line($"{actor}_visible = false;");
            else if (mode == "ON")
            {
                if (image is null) Line($"if ({actor}_image == \"\") {{ LegacyUnsupported({Q(_name)}, {token.Line}, \"BUSTUP has no face\"); return; }}");
                Line($"{actor}_visible = true;");
            }
        }

        private void EmitSceneFunctions()
        {
            Line($"function {_prefix}reset_scene() {{ {BackgroundKind} = 0; {Background} = \"\";");
            foreach (string name in _actors.Keys)
            {
                string actor = Actor(name);
                Line($"{actor}_visible = false; {actor}_image = \"\"; {actor}_x = 0;");
            }
            foreach (string number in _layers.Keys)
            {
                string layer = Layer(number);
                Line($"{layer}_visible = false; {layer}_image = \"\"; {layer}_x = 0; {layer}_y = 0; {layer}_alpha = 1000;");
            }
            Line("}");
            // Descriptors survive a commit. Rebuilding the pending scene from
            // them preserves the background and other actors on incremental edits.
            Line($"function {_prefix}render_scene() {{ Delete({Stage} + \"/*\");");
            Line($"CreateColor({Stage} + \"/blank\", {ScenePriority}, 0, 0, {_width}, {_height}, #000000); Fade({Stage} + \"/blank\", 0, 0, null, false);");
            Line($"if ({BackgroundKind} == 1) {{ CreateColor({Stage} + \"/background\", {ScenePriority} + 10, 0, 0, {_width}, {_height}, {Background}); }}");
            Line($"if ({BackgroundKind} == 2) {{ CreateTexture({Stage} + \"/background\", {ScenePriority} + 10, Center, Middle, {Background}); }}");
            Line($"Fade({Stage} + \"/background\", 0, 0, null, false);");
            foreach (var (name, index) in _actors)
            {
                string actor = Actor(name), path = Q("/actor" + index);
                Line($"if ({actor}_visible) {{ CreateTexture({Stage} + {path}, {ScenePriority} + {100 + index}, {actor}_x, 0, {actor}_image); Fade({Stage} + {path}, 0, 0, null, false); }}");
            }
            foreach (var (number, index) in _layers)
            {
                string layer = Layer(number), path = Q("/layer" + index);
                Line($"if ({layer}_visible) {{ CreateTexture({Stage} + {path}, {ScenePriority} + 20, {layer}_x, {layer}_y, {layer}_image); Fade({Stage} + {path}, 0, 0, null, false); }}");
            }
            Line("}");
            Line($"function {_prefix}reveal_scene() {{");
            EmitRevealBody();
            Line("}");
        }

        private void EmitReveal(string duration)
        {
            Line($"{RevealDuration} = {duration}; {_prefix}reveal_scene();");
        }

        private void EmitRevealBody()
        {
            Line($"Fade({Stage} + \"/blank\", {RevealDuration}, 1000, null, false); Fade({Stage} + \"/background\", {RevealDuration}, 1000, null, false);");
            foreach (var (name, index) in _actors)
                Line($"Fade({Stage} + {Q("/actor" + index)}, {RevealDuration}, 1000, null, false);");
            foreach (var (number, index) in _layers)
                Line($"Fade({Stage} + {Q("/layer" + index)}, {RevealDuration}, {Layer(number)}_alpha, null, false);");
            Line($"if ({RevealDuration} > 0) {{ Wait({RevealDuration}); }}");
        }

        private void EmitBox(LegacyNpsTextBox box)
        {
            if (box.Width <= box.MarginLeft + box.MarginRight || box.Height <= box.MarginTop + box.MarginBottom)
                throw new FormatException("Invalid BOX text area");
            Line("Delete(\"legacy_box\"); Delete(\"legacy_box_frame\");");
            if (box.FrameImage is { } frame)
            {
                Line($"CreateTexture(\"legacy_box_frame\", 9999, {box.X}, {box.Y}, {Q(frame)});");
                Line($"Fade(\"legacy_box_frame\", 0, {box.Opacity * 10}, null, false);");
            }
            // DialoguePage contributes its inherited ten-pixel top inset.
            // Position the container so the configured inner area is exact.
            Line($"CreateWindow(\"legacy_box\", 10000, {box.X + box.MarginLeft}, {box.Y + box.MarginTop - 10}, {box.Width - box.MarginLeft - box.MarginRight}, {box.Height - box.MarginTop - box.MarginBottom + 10}, false);");
        }

        private void EmitChoice(LegacyNpsToken token, int index)
        {
            string A(string key, string fallback = "") => token.Attributes.GetValueOrDefault(key, fallback);
            string[] files = A("FILE").Split(';');
            if (files.Length is < 1 or > 3 || files.Any(string.IsNullOrEmpty)) throw new FormatException("CHOICE image list");
            string name = "legacy_choice_" + index;
            Line($"if ({Condition(A("OPERATOR"))}) {{ CreateChoice({Q(name)});");
            string[] states = ["MouseUsual", "MouseOver", "MouseClick"];
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i].Replace('\\', '/');
                if (!path.StartsWith("cg/", StringComparison.OrdinalIgnoreCase)) path = _asset("cg", path);
                Line($"CreateTexture({Q(name + "/" + states[i] + "/image")}, 1000, {Number(A("X", "0"))}, {Number(A("Y", "0"))}, {Q(path)});");
            }
            Line("}");
            if (A("ONSE").Length > 0) _diagnostics.Add($"{_name}:{token.Line}: CHOICE hover sound is not yet bound to a state-enter thread.");
        }

        private void PlayUiSound(string sound, LegacyNpsToken token)
        {
            try { Play("legacy_click", "SE", _asset("sound", sound), false); }
            catch (FormatException error)
            {
                _diagnostics.Add($"{_name}:{token.Line}: unavailable optional CHOICE UI sound '{sound}': {error.Message}; selection remains active.");
            }
        }

        private void EmitAudio(LegacyNpsToken token)
        {
            string A(string key, string fallback = "") => token.Attributes.GetValueOrDefault(key, fallback);
            string name = token.Value == "BGM" ? "legacy_bgm" : token.Value == "VOICE" ? "legacy_voice" : "legacy_se_" + A("ID", "1");
            string mode = A("MODE", "NORMAL").ToUpperInvariant();
            if (mode is not ("NORMAL" or "FADEIN" or "FADEOUT")) throw new FormatException("Audio mode " + mode);
            if (A("SRC").Length == 0)
            {
                if (mode == "FADEOUT") Line($"SetVolume({Q(name)}, {Time(A("TIME", "0"))}, 0); Wait({Time(A("TIME", "0"))}); Request({Q(name)}, Stop);");
                else Line($"Request({Q(name)}, Stop);");
                return;
            }
            bool loop = A("LOOP", token.Value == "BGM" ? "on" : "off").Equals("on", StringComparison.OrdinalIgnoreCase);
            Play(name, token.Value == "VOICE" ? "Voice" : token.Value == "BGM" ? "BGM" : "SE",
                _asset(token.Value == "VOICE" ? "voice" : "sound", A("SRC")), loop);
            if (mode == "FADEIN")
            {
                Line($"SetVolume({Q(name)}, 0, 0); SetVolume({Q(name)}, {Time(A("TIME", "0"))}, 1000);");
            }
        }

        private void Play(string name, string kind, string path, bool loop)
        {
            Line($"CreateSound({Q(name)}, {kind}, {Q(path)}); SetLoop({Q(name)}, {(loop ? "true" : "false")}); Request({Q(name)}, Play);");
        }

        private void Jump(string target, LegacyNpsToken token)
        {
            if (target.StartsWith('#'))
            {
                if (!_markers.TryGetValue(target, out int destination)) { Unsupported(token, "Missing marker " + target); return; }
                Line($"{Pc} = {destination};");
            }
            else if (target.Length > 0)
            {
                Line($"{Pc} = {_tokens.Count}; call_scene {Q(_module(target) + "->legacy_entry")}; return;");
            }
            else Unsupported(token, "Empty jump target");
        }

        private string Condition(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition)) return "true";
            Match match = Regex.Match(condition, @"^\s*([^=<>!&|]+?)\s*(==|!=|>=|<=|=|>|<)\s*(true|false|-?\d+(?:\.\d+)?)\s*$", RegexOptions.IgnoreCase);
            if (!match.Success) throw new FormatException("Unsupported condition " + condition);
            string op = match.Groups[2].Value == "=" ? "==" : match.Groups[2].Value;
            return $"VariableValue($, {Q(LocalKey(match.Groups[1].Value.Trim()))}) {op} {match.Groups[3].Value.ToLowerInvariant()}";
        }

        private IEnumerable<string> LocalNames()
        {
            foreach (LegacyNpsToken token in _tokens)
                if (token.Attributes.TryGetValue("OPERATOR", out string? expression))
                {
                    Match match = Regex.Match(expression, @"^\s*([^=<>!&|]+?)\s*[=<>!]");
                    if (match.Success) yield return match.Groups[1].Value.Trim();
                }
        }

        private string LocalKey(string name) => _prefix + name;
        private static string Number(string value)
            => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number)
                ? number.ToString(CultureInfo.InvariantCulture) : throw new FormatException("Invalid number " + value);
        private static string Time(string value) => Number(value) + " * 100";
        private static string Q(string value)
        {
            if (value.IndexOfAny(['"', '\r', '\n']) >= 0) throw new FormatException("Value cannot be represented as an NSS string");
            return "\"" + value.Replace('\\', '/') + "\"";
        }
        private void Unsupported(LegacyNpsToken token, string command)
        {
            Line($"LegacyUnsupported({Q(_name)}, {token.Line}, {Q(command.Replace('"', '\''))}); return;");
            _diagnostics.Add($"{_name}:{token.Line}: execution stops at {command}.");
        }
        private void Line(string line) => _output.AppendLine(line);
    }
}
