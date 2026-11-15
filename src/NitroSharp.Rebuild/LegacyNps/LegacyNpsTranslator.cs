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
    string? FrameImage = null, int Opacity = 100, bool ColumnarWriting = false);

internal sealed record LegacyNpsChoiceBox(int X, int Y, int Width, int Height,
    int MarginTop = 0, int FrameX = 0, int FrameY = 0, string? Normal = null,
    string? Over = null, string? Down = null, int Opacity = 50);

internal sealed record LegacyNpsPresentation(IReadOnlyDictionary<int, LegacyNpsTextBox> TextBoxes,
    IReadOnlyDictionary<string, int> Alignments, IReadOnlyDictionary<string, string>? TransitionMasks = null,
    IReadOnlyDictionary<int, LegacyNpsChoiceBox>? ChoiceBoxes = null,
    int BgmVolume = 100, int SeVolume = 100, int VoiceVolume = 100);

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
        private readonly IReadOnlyDictionary<int, LegacyNpsChoiceBox> _choiceBoxes;
        private readonly Dictionary<string, int> _actors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _layers = new(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _output = new();
        private int _workTemporary;
        private bool _usesSettings;
        private readonly LegacyNpsPresentation? _presentation;
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
            _name = name; _width = width; _height = height; _presentation = presentation;
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            _prefix = "legacy_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12] + "_";
            _module = module ?? (path => Path.ChangeExtension(path.Replace('\\', '/'), ".nss"));
            _asset = asset ?? ((kind, path) => (kind == "glass" ? "cg" : kind) + "/" + path.Replace('\\', '/')
                + (Path.HasExtension(path) ? "" : kind is "sound" or "voice" ? ".ogg" : kind == "movie" ? ".mpg" : ".png"));
            _boxes = presentation?.TextBoxes ?? new Dictionary<int, LegacyNpsTextBox>
                { [9] = new(20, height - 180, width - 40, 170) };
            _alignments = presentation?.Alignments ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                { ["center"] = width / 2, ["left"] = width * 3 / 10, ["right"] = width * 7 / 10 };
            _transitionMasks = presentation?.TransitionMasks ?? new Dictionary<string, string>();
            _choiceBoxes = presentation?.ChoiceBoxes ?? new Dictionary<int, LegacyNpsChoiceBox>();
            _tokens = LegacyNpsReader.Read(source).Where(token => token.Kind != LegacyNpsTokenKind.Comment
                && token.Value != "/A" && (token.Kind != LegacyNpsTokenKind.Text || !string.IsNullOrWhiteSpace(token.Value))).ToList();
            for (int i = 0; i < _tokens.Count; i++)
            {
                if (_tokens[i].Value == "MARKER" && _tokens[i].Attributes.TryGetValue("NAME", out string? marker)
                    && !_markers.TryAdd(marker, i)) throw new FormatException($"Duplicate NPS marker '{marker}' in {name}.");
                if (_tokens[i].Value == "BUSTUP" && _tokens[i].Attributes.TryGetValue("NAME", out string? actor))
                    _actors.TryAdd(actor, _actors.Count);
                if (_tokens[i].Value is "LAYER" or "GLASS" or "SCROLL" or "ANIM" or "BGANIM")
                {
                    string number = _tokens[i].Attributes.GetValueOrDefault("NUM", _tokens[i].Value is "ANIM" or "BGANIM" ? "19" : "1");
                    if (string.IsNullOrWhiteSpace(number)) number = "1";
                    if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) && slot is >= 0 and < 20)
                        _layers.TryAdd(slot.ToString(CultureInfo.InvariantCulture), _layers.Count);
                }
            }
        }

        public LegacyNpsTranslation Run()
        {
            Line("chapter main { call_scene @->legacy_entry; }");
            Line("scene legacy_entry {");
            Line(LegacyNpsSettings.Initialize(_presentation));
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
            EmitDelayedScrollFunctions();
            if (_usesSettings) Line(LegacyNpsSettings.Generate(_prefix + "config", _width, _height));
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
                    int innerWidth = box.Width - box.MarginLeft - box.MarginRight;
                    int innerHeight = box.Height - box.MarginTop - box.MarginBottom;
                    Line($"LoadText({_prefix}step{group}, \"legacy_box\", \"@{block}\", {(box.ColumnarWriting ? innerHeight : innerWidth)}, {(box.ColumnarWriting ? innerWidth : innerHeight)}, 0, 26);");
                    if (box.ColumnarWriting)
                        Line($"SetVertex(\"legacy_box/{block}\", 0, 0); Rotate(\"legacy_box/{block}\", 0, 0, 0, 90, null, false);");
                    Line("}");
                }
                Line($"Request(\"legacy_box/{block}\", Enter); WaitText(\"legacy_box/{block}\", 0); Delete(\"legacy_box/{block}\");");
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
                case "LAYER": case "GLASS": EmitGlass(token); break;
                case "BUSTUP": EmitBustup(token); break;
                case "ANIM": case "BGANIM": EmitAnimation(token, index); break;
                case "SCROLL": EmitScroll(token); break;
                case "QUAKE":
                    if (!int.TryParse(A("COUNT", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cycles)
                        || cycles is < 0 or > 1024) throw new FormatException("QUAKE COUNT must be between 0 and 1024");
                    string quakeX = Number(string.IsNullOrWhiteSpace(A("X")) ? "0" : A("X"));
                    string quakeY = Number(string.IsNullOrWhiteSpace(A("Y")) ? "0" : A("Y"));
                    string quakeTime = string.IsNullOrWhiteSpace(A("TIME")) ? "50" : Number(A("TIME"));
                    if (float.Parse(quakeTime, CultureInfo.InvariantCulture) < 0) throw new FormatException("QUAKE TIME must not be negative");
                    string repeat = "$" + _prefix + "quake_repeat";
                    Line($"{repeat} = 0; while ({repeat} < {cycles}) {{");
                    foreach (int sign in new[] { 1, -1, -1, 1 })
                    {
                        string dx = sign == 1 ? quakeX : Number((-float.Parse(quakeX, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
                        string dy = sign == 1 ? quakeY : Number((-float.Parse(quakeY, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
                        Line($"Move({Front} + \"/*\", 0, @{dx}, @{dy}, null, false); Wait({quakeTime});");
                    }
                    Line($"{repeat} = {repeat} + 1; }}");
                    break;
                case "WIPE":
                    string effect = A("EFFECT", "NORMAL").ToUpperInvariant();
                    string duration = Time(A("TIME", "16"));
                    if (Number(A("TIME", "16")) == "0") effect = "NORMAL";
                    if (effect == "DELAYFADE" && float.Parse(duration.Split(' ')[0], CultureInfo.InvariantCulture) < 1.28f)
                        throw new FormatException("DELAYFADE TIME must allow a nonzero native interval");
                    bool bridge = effect is "WHITEOUT" or "BLACKOUT";
                    bool missingMask = false;
                    string? mask = null;
                    if (effect is "SPIRAL" or "RASTER" or "OVER" or "UNDER" or "NOIZE" or "SLASH" or "SLASH2" or "TINY" or "TRANSFORM")
                        throw new FormatException("WIPE " + effect);
                    if (effect is not ("NORMAL" or "FADE" or "WHITEOUT" or "BLACKOUT" or "DELAYFADE"))
                    {
                        if (!_transitionMasks.TryGetValue(effect, out mask))
                            mask = _asset("wipe", A("EFFECT"));
                        missingMask = mask.Length == 0;
                    }
                    Line($"{_prefix}finish_animations();");
                    if (missingMask)
                    {
                        // Failed original mask loads leave an empty bitmap:
                        // every timed blend is skipped, then final redraw commits.
                        _diagnostics.Add($"{_name}:{token.Line}: missing authored WIPE mask '{A("EFFECT")}'; keeping the previous scene for the original timed wait before committing.");
                        Line($"Wait(({duration}) - ({duration}) % 128);");
                    }
                    if (bridge)
                    {
                        // Original WHITEOUT/BLACKOUT each run two complete TIME
                        // phases: current scene -> solid color -> pending scene.
                        Line($"CreateColor(\"legacy_wipe_bridge\", 900, 0, 0, {_width}, {_height}, {(effect == "WHITEOUT" ? "#FFFFFF" : "#000000")});");
                        Line($"DrawLegacyTransition(\"legacy_wipe_bridge\", {duration}, \"\", false, true);");
                    }
                    Line($"{ScenePriority} = 1000; {_prefix}render_scene();");
                    EmitReveal("0");
                    if (effect == "BLIND")
                        Line($"DrawTransition({Stage} + \"/*\", {duration}, 0, 1000, 0, null, {Q(mask!)}, true);");
                    else if (effect == "DELAYFADE")
                    {
                        Line($"DrawLegacyTransition({Stage} + \"/*\", {duration}, \"\", false, false, true);");
                        Line($"${_prefix}scroll_tick = ({duration}) - ({duration}) % 128; ${_prefix}scroll_tick = ${_prefix}scroll_tick / 128;");
                        Line($"{_prefix}run_delayed_scrolls(); WaitAction(\"__legacy_transition_*\", null);");
                    }
                    else if (effect != "NORMAL" && !missingMask)
                    {
                        Line($"DrawLegacyTransition({Stage} + \"/*\", {duration}, {Q(mask ?? "")}, {(mask is null ? "false" : "true")}, true);");
                    }
                    Line($"{_prefix}finish_scrolls();");
                    // Normalize the committed scene back to the lower priority
                    // band, so each pending scene can cover every old layer.
                    Line($"Delete({Front}); CreateName({Front}); {Temp} = {Stage}; {Stage} = {Front}; {ScenePriority} = 0; {_prefix}render_scene();");
                    EmitReveal("0");
                    if (bridge) Line("Delete(\"legacy_wipe_bridge\");");
                    Line($"{Stage} = {Temp}; Delete({Stage} + \"/*\");");
                    break;
                case "BGM": case "SE": case "VOICE": EmitAudio(token); break;
                case "MOVIE":
                    if (A("OPT").Length > 0) throw new FormatException("MOVIE OPT " + A("OPT"));
                    if (A("SRC").Length == 0) throw new FormatException("MOVIE requires SRC");
                    string movieX = Number(string.IsNullOrWhiteSpace(A("SENTERX")) ? "0" : A("SENTERX"));
                    string movieY = Number(string.IsNullOrWhiteSpace(A("SENTERY")) ? "0" : A("SENTERY"));
                    string movie = _asset("movie", A("SRC"));
                    Line($"CreateMovie(\"legacy_movie\", 11000, Center, Middle, false, false, {Q(movie)});");
                    if (movieX != "0" || movieY != "0") Line($"Move(\"legacy_movie\", 0, @{movieX}, @{movieY}, null, false);");
                    Line($"WaitPlay(\"legacy_movie\", {(A("SKIP").Equals("on", StringComparison.OrdinalIgnoreCase) ? "true" : "false")}); Delete(\"legacy_movie\");");
                    break;
                case "A":
                    string condition = Condition(A("OPERATOR"));
                    Line($"if ({condition}) {{"); Jump(A("HREF"), token); Line("}");
                    break;
                case "CALC": EmitCalculation(token); break;
                case "CHOICE":
                    if (token.Attributes.ContainsKey("TEXT") && !token.Attributes.ContainsKey("FILE")) EmitTextChoices(index);
                    else EmitChoice(token, index);
                    break;
                case "SELECT":
                    if (index > 0 && _tokens[index - 1].Value == "CHOICE" && _tokens[index - 1].Attributes.ContainsKey("TEXT")
                        && !_tokens[index - 1].Attributes.ContainsKey("FILE")) break;
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
                        Line("$legacy_work_epoch = VariableValue($, \"legacy_work_epoch\") + 1;");
                    }
                    else if (A("MENU").Length > 0)
                    {
                        Line($"VariableValue($, {Q(LocalKey("menu_mask"))}, {Q(A("MENU"))});");
                        _diagnostics.Add($"{_name}:{token.Line}: optional SYSTEM MENU capability is unsupported: authored mask is retained, but host menu controls are not connected.");
                    }
                    else if (A("CMD").Equals("savemode", StringComparison.OrdinalIgnoreCase))
                    {
                        Line($"VariableValue($, {Q(LocalKey("suppress_choice_autosave"))}, {(A("MODE").ToUpperInvariant() is "ON" or "TRUE" ? "true" : "false")});");
                        _diagnostics.Add($"{_name}:{token.Line}: optional SYSTEM savemode capability is unsupported: automatic choice-save suppression is retained, but host storage is not connected.");
                    }
                    else if (A("CMD").Equals("timerclear", StringComparison.OrdinalIgnoreCase))
                    {
                        Line($"VariableValue($, {Q(LocalKey("timer"))}, 0);");
                        _diagnostics.Add($"{_name}:{token.Line}: optional SYSTEM timerclear capability is unsupported: the authored reset is retained, but host play-time tracking is not connected.");
                    }
                    else if (A("CMD").Equals("config", StringComparison.OrdinalIgnoreCase))
                    {
                        _usesSettings = true;
                        Line($"{_prefix}config();");
                        foreach (var (type, box) in _boxes)
                            if (box.ColumnarWriting) Line($"if ({Box} == {type}) {{ SetTextOrientation(true); }}");
                    }
                    else if (A("CMD").Equals("ending", StringComparison.OrdinalIgnoreCase)) EmitEnding(token);
                    else if (A("CMD").Equals("sceneChange", StringComparison.OrdinalIgnoreCase))
                    {
                        // Original SkipStop scene boundary cancels skip and
                        // finalizes active asynchronous visual sequences.
                        Line($"$SYSTEM_skip = false; {_prefix}finish_animations();");
                    }
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

        private void EmitScroll(LegacyNpsToken token)
        {
            string A(string key, string fallback = "") => token.Attributes.TryGetValue(key, out string? value)
                && !string.IsNullOrWhiteSpace(value) ? value : fallback;
            if (token.Attributes.Keys.Any(key => Regex.IsMatch(key, @"^(NUM|TIME|X|Y|COUNT|SEQ)\d+$"))
                || A("SEQ").Length > 0
                || A("ASYNC", "FALSE").Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("SCROLL requires a single target without ASYNC or SEQ");
            if (!int.TryParse(A("NUM", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) || slot is < 0 or >= 20)
                throw new FormatException("SCROLL NUM must be between 0 and 19");
            if (!int.TryParse(A("COUNT", "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count is < 0 or > 4096)
                throw new FormatException("SCROLL COUNT must be between 0 and 4096");
            int interval = 100;
            if (A("TIME").Length > 0)
            {
                if (!int.TryParse(A("TIME"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int time) || time is < 0 or > 1000000)
                    throw new FormatException("SCROLL TIME must be a nonnegative bounded integer");
                interval = count > 0 ? time * 100 / count : 0;
            }
            string layer = Layer(slot.ToString(CultureInfo.InvariantCulture));
            string path = Front + " + " + Q("/layer" + _layers[slot.ToString(CultureInfo.InvariantCulture)]);
            string x = Target(A("X"), layer + "_x"), y = Target(A("Y"), layer + "_y");
            if (A("DELAY", "FALSE").Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                // The native queue stores the target without changing the
                // displayed scene or the current descriptor until WIPE.
                Line($"if ({layer}_visible) {{ {layer}_scroll_x = {x}; {layer}_scroll_y = {y}; {layer}_scroll_count = {count}; {layer}_queued = true; }}");
                return;
            }
            string temp = "$" + _prefix + "scroll_";
            Line($"if ({layer}_visible) {{ {temp}tx = {x}; {temp}ty = {y};");
            bool silent = A("SILENT", "FALSE").Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            if (!silent && count > 0)
            {
                Line($"{temp}fx = {layer}_x * 256; {temp}fy = {layer}_y * 256;");
                Line($"{temp}dx = ({temp}tx * 256 - {temp}fx) / {count}; {temp}dx = {temp}dx - {temp}dx % 1;");
                Line($"{temp}dy = ({temp}ty * 256 - {temp}fy) / {count}; {temp}dy = {temp}dy - {temp}dy % 1;");
                Line($"{temp}i = 0; while ({temp}i < {count}) {{");
                // Native fixed-point >>8 floors negative coordinates, whereas
                // division truncates the per-step delta toward zero.
                foreach (string axis in new[] { "x", "y" })
                {
                    Line($"{temp}{axis} = ({temp}f{axis} - {temp}f{axis} % 256) / 256;");
                    Line($"if ({temp}f{axis} < 0 && {temp}f{axis} % 256 != 0) {{ {temp}{axis} = {temp}{axis} - 1; }}");
                }
                Line($"Move({path}, 0, {temp}x, {temp}y, null, false); WaitKey({interval});");
                Line($"if ({AdvanceInput}) {{ {temp}i = {count}; }}");
                Line($"{temp}fx = {temp}fx + {temp}dx; {temp}fy = {temp}fy + {temp}dy; {temp}i = {temp}i + 1; }}");
            }
            Line($"{layer}_x = {temp}tx; {layer}_y = {temp}ty;");
            if (!silent) Line($"Move({path}, 0, {layer}_x, {layer}_y, null, false);");
            Line("}");

            static string Target(string value, string current)
            {
                if (value.Length == 0) return current;
                bool relative = value.StartsWith('@');
                if (!int.TryParse(relative ? value[1..] : value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    || number is < -32767 or > 32767) throw new FormatException("SCROLL coordinate must be a bounded integer or @relative integer");
                return relative ? "(" + current + " + " + number.ToString(CultureInfo.InvariantCulture) + ")" : number.ToString(CultureInfo.InvariantCulture);
            }
        }

        private const string AdvanceInput = "$SYSTEM_l_button_down || $SYSTEM_r_button_down || $SYSTEM_keydown_enter || $SYSTEM_XBOX360_button_a_down";

        private void EmitDelayedScrollFunctions()
        {
            string timer = "$" + _prefix + "scroll_elapsed", tick = "$" + _prefix + "scroll_tick";
            string active = "$" + _prefix + "scroll_active";
            Line($"function {_prefix}finish_scrolls() {{");
            foreach (string number in _layers.Keys)
            {
                string layer = Layer(number);
                Line($"if ({layer}_queued) {{ {layer}_x = {layer}_scroll_x; {layer}_y = {layer}_scroll_y; {layer}_queued = false; }}");
            }
            Line("}");
            Line($"function {_prefix}run_delayed_scrolls() {{ {timer} = 0; {active} = true;");
            foreach (string number in _layers.Keys)
            {
                string layer = Layer(number);
                Line($"if ({layer}_queued) {{ {layer}_scroll_fx = {layer}_x * 256; {layer}_scroll_fy = {layer}_y * 256;");
                Line($"if ({layer}_scroll_count > 0) {{ {layer}_scroll_dx = ({layer}_scroll_x * 256 - {layer}_scroll_fx) / {layer}_scroll_count; {layer}_scroll_dx = {layer}_scroll_dx - {layer}_scroll_dx % 1;");
                Line($"{layer}_scroll_dy = ({layer}_scroll_y * 256 - {layer}_scroll_fy) / {layer}_scroll_count; {layer}_scroll_dy = {layer}_scroll_dy - {layer}_scroll_dy % 1; }} }}");
            }
            Line($"{_prefix}step_delayed_scrolls();");
            // DELAYFADE advances motion on the original >100ms accumulator,
            // independently of the 128 fade intervals. Motion can outlast fade.
            Line($"while ({active}) {{ WaitKey({tick}); {timer} = {timer} + {tick};");
            Line($"if ({AdvanceInput}) {{ Delete(\"__legacy_transition_*\"); {active} = false; }} else {{");
            Line($"if ({timer} > 100) {{ {timer} = {timer} - 100; {_prefix}step_delayed_scrolls(); }} }} }} }}");
            Line($"function {_prefix}step_delayed_scrolls() {{ {active} = false;");
            foreach (var (number, index) in _layers)
            {
                string layer = Layer(number), path = Stage + " + " + Q("/layer" + index);
                Line($"if ({layer}_queued) {{ if ({layer}_scroll_count > 0) {{ {active} = true;");
                foreach (string axis in new[] { "x", "y" })
                {
                    Line($"{layer}_scroll_{axis}pos = ({layer}_scroll_f{axis} - {layer}_scroll_f{axis} % 256) / 256;");
                    Line($"if ({layer}_scroll_f{axis} < 0 && {layer}_scroll_f{axis} % 256 != 0) {{ {layer}_scroll_{axis}pos = {layer}_scroll_{axis}pos - 1; }}");
                }
                Line($"Move({path}, 0, {layer}_scroll_xpos, {layer}_scroll_ypos, null, false);");
                Line($"{layer}_scroll_fx = {layer}_scroll_fx + {layer}_scroll_dx; {layer}_scroll_fy = {layer}_scroll_fy + {layer}_scroll_dy; {layer}_scroll_count = {layer}_scroll_count - 1; }}");
                Line($"else {{ Move({path}, 0, {layer}_scroll_x, {layer}_scroll_y, null, false); }} }}");
            }
            Line("}");
        }

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
            string layer = Layer(number.ToString(CultureInfo.InvariantCulture));
            string path = Front + " + " + Q("/layer" + _layers[number.ToString(CultureInfo.InvariantCulture)]);
            Line($"Array({array}, {string.Join(", ", frames.Select(Q))});");
            // ANIM's WAIT is skipped 33ms ticks, while the command itself blocks.
            // Start without waiting so SE begins in the same script tick, after
            // frame loading; the visual wait never waits for the sound.
            Line($"FinishFrameAnimation({path}); {layer}_animated = true; {layer}_remove = {(still == 0 ? "true" : "false")}; {layer}_visible = true; {layer}_kind = 2; {layer}_x = {x}; {layer}_y = {y}; {layer}_zoom = 1000; {layer}_alpha = 1000;");
            Line($"CreateFrameAnimation({path}, {(number == 0 ? 20 : 200 + number)}, {x}, {y}, {array}, {ticks}, {Q(finalFrame)}, {(still == 0 ? "true" : "false")}, {(A("LOOP", "FALSE").Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "true" : "false")}, false);");
            if (sound is not null) Play("legacy_anim_se_" + _prefix + index, "SE", sound, false);
            if (token.Value == "ANIM")
            {
                Line($"WaitFrameAnimation({path});");
                EmitFinishAnimation(number.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void EmitFinishAnimation(string number)
        {
            string layer = Layer(number), path = Front + " + " + Q("/layer" + _layers[number]);
            string source = "$" + _prefix + "finished_source";
            Line($"if ({layer}_animated) {{ {source} = FinishFrameAnimation({path}); {layer}_animated = false;");
            Line($"if ({source} != null) {{ {layer}_image = {source}; {layer}_visible = {source} != \"\"; {layer}_kind = 2; }} else {{ if ({layer}_remove) {{ {layer}_visible = false; {layer}_image = \"\"; }} }} }}");
        }

        private void EmitGlass(LegacyNpsToken token)
        {
            string A(string key, string fallback = "") => token.Attributes.TryGetValue(key, out string? value)
                && !string.IsNullOrWhiteSpace(value) ? value : fallback;
            if (!int.TryParse(A("NUM", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)
                || slot is < 0 or >= 20) throw new FormatException("GLASS NUM must be between 0 and 19");
            string layer = Layer(slot.ToString(CultureInfo.InvariantCulture));
            string mode = A("MODE", "ON").ToUpperInvariant();
            if (mode is not ("ON" or "TRUE" or "OFF" or "FALSE")) throw new FormatException("GLASS MODE " + mode);
            if (mode is "OFF" or "FALSE" || A("SRC").Length == 0)
            {
                Line($"FinishFrameAnimation({Front} + {Q("/layer" + _layers[slot.ToString(CultureInfo.InvariantCulture)])}); {layer}_animated = false; {layer}_visible = false; {layer}_image = \"\";");
                return;
            }
            if (Number(A("ZOOMTYPE", "0")) != "0" || Number(A("SHADE", "0")) != "0"
                || A("REVERSE", "OFF").ToUpperInvariant() is not ("OFF" or "FALSE")
                || A("FULCRUMW", "LEFT").ToUpperInvariant() != "LEFT" || A("FULCRUMH", "TOP").ToUpperInvariant() != "TOP")
                throw new FormatException("GLASS requires default zoom type, shade, reverse, and anchors");
            string x = Number(A("X", "0")), y = Number(A("Y", "0"));
            string zoom = Number(A("ZOOM", "100")), opacity = Number(A("TRANSPARENT", "100"));
            if (float.Parse(zoom, CultureInfo.InvariantCulture) is <= 0 or > 100)
                throw new FormatException("GLASS ZOOM above 100 requires original source-window cropping");
            if (float.Parse(opacity, CultureInfo.InvariantCulture) is < 0 or > 100)
                throw new FormatException("GLASS TRANSPARENT must be between 0 and 100");
            string source = A("SRC");
            bool color = source.StartsWith('#');
            if (color && !Regex.IsMatch(source, "^#[0-9a-fA-F]{6}$")) throw new FormatException("Invalid GLASS color");
            if (!color)
            {
                source = _asset("glass", source);
                if (source.Length == 0)
                {
                    _diagnostics.Add($"{_name}:{token.Line}: missing authored GLASS image '{A("SRC")}'; numbered layer cleared.");
                    Line($"FinishFrameAnimation({Front} + {Q("/layer" + _layers[slot.ToString(CultureInfo.InvariantCulture)])}); {layer}_animated = false; {layer}_visible = false; {layer}_image = \"\";");
                    return;
                }
            }
            Line($"FinishFrameAnimation({Front} + {Q("/layer" + _layers[slot.ToString(CultureInfo.InvariantCulture)])}); {layer}_animated = false;");
            Line($"{layer}_image = {Q(source)}; {layer}_kind = {(color ? 1 : 2)}; {layer}_x = {x}; {layer}_y = {y};");
            Line($"{layer}_zoom = {zoom} * 10; {layer}_alpha = {opacity} * 10; {layer}_visible = true;");
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
                Line($"FinishFrameAnimation({Front} + {Q("/layer" + _layers[number])}); {layer}_animated = false; {layer}_queued = false; {layer}_visible = false; {layer}_image = \"\"; {layer}_kind = 2; {layer}_x = 0; {layer}_y = 0; {layer}_alpha = 1000; {layer}_zoom = 1000;");
            }
            Line("}");
            Line($"function {_prefix}finish_animations() {{");
            foreach (string number in _layers.Keys) EmitFinishAnimation(number);
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
                int priority = number == "0" ? 20 : 200 + int.Parse(number, CultureInfo.InvariantCulture);
                Line($"if ({layer}_visible) {{");
                Line($"if ({layer}_kind == 1) {{ CreateColor({Stage} + {path}, {ScenePriority} + {priority}, {layer}_x, {layer}_y, {_width}, {_height}, {layer}_image); }}");
                Line($"if ({layer}_kind == 2) {{ CreateTexture({Stage} + {path}, {ScenePriority} + {priority}, {layer}_x, {layer}_y, {layer}_image); }}");
                Line($"SetVertex({Stage} + {path}, 0, 0); Zoom({Stage} + {path}, 0, {layer}_zoom, {layer}_zoom, null, false); Fade({Stage} + {path}, 0, 0, null, false); }}");
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
            Line($"SetTextOrientation({(box.ColumnarWriting ? "true" : "false")});");
            if (box.FrameImage is { } frame)
            {
                Line($"CreateTexture(\"legacy_box_frame\", 9999, {box.X}, {box.Y}, {Q(frame)});");
                Line($"Fade(\"legacy_box_frame\", 0, {box.Opacity * 10}, null, false);");
            }
            // DialoguePage contributes its inherited ten-pixel top inset.
            // Position the container so the configured inner area is exact.
            if (box.ColumnarWriting)
                Line($"CreateWindow(\"legacy_box\", 10000, {box.X + box.Width - box.MarginRight + 10}, {box.Y + box.MarginTop}, {box.Height - box.MarginTop - box.MarginBottom}, {box.Width - box.MarginLeft - box.MarginRight + 10}, false);");
            else
                Line($"CreateWindow(\"legacy_box\", 10000, {box.X + box.MarginLeft}, {box.Y + box.MarginTop - 10}, {box.Width - box.MarginLeft - box.MarginRight}, {box.Height - box.MarginTop - box.MarginBottom + 10}, false);");
        }

        private void EmitTextChoices(int index)
        {
            if (index > 0 && _tokens[index - 1].Value == "CHOICE"
                && _tokens[index - 1].Attributes.ContainsKey("TEXT") && !_tokens[index - 1].Attributes.ContainsKey("FILE")) return;
            int end = index;
            while (end < _tokens.Count && _tokens[end].Value == "CHOICE"
                && _tokens[end].Attributes.ContainsKey("TEXT") && !_tokens[end].Attributes.ContainsKey("FILE")) end++;
            string slot = "$" + _prefix + "choice_slot";
            Line($"{slot} = 0; SetTextOrientation(false); $SYSTEM_skip = false; Request(\"legacy_voice\", Stop);");
            var selectable = new List<int>();
            for (int i = index; i < end; i++)
            {
                LegacyNpsToken choice = _tokens[i];
                string text = choice.Attributes.GetValueOrDefault("TEXT", "");
                if (text.Length == 0)
                {
                    if (choice.Attributes.GetValueOrDefault("COUNTABLE", "").ToUpperInvariant() is "TRUE" or "ON") Line($"{slot} = {slot} + 1;");
                    continue;
                }
                if (_choiceBoxes.Count == 0) throw new FormatException("Text CHOICE requires original choice.ini presentation");
                if (choice.Attributes.GetValueOrDefault("HREF", "").Length == 0) throw new FormatException("Text CHOICE requires HREF");
                if (choice.Attributes.ContainsKey("X") || choice.Attributes.ContainsKey("Y")) throw new FormatException("Text CHOICE explicit position requires native button layout");
                Line($"if ({Condition(choice.Attributes.GetValueOrDefault("OPERATOR", ""))}) {{");
                string name = "legacy_choice_" + i;
                Line($"CreateChoice({Q(name)}, true);");
                foreach (var (number, box) in _choiceBoxes)
                {
                    Line($"if ({slot} == {number}) {{");
                    string[] states = ["MouseUsual", "MouseOver", "MouseClick"];
                    string?[] frames = [box.Normal, box.Over ?? box.Normal, box.Down ?? box.Over ?? box.Normal];
                    for (int state = 0; state < states.Length; state++)
                    {
                        string root = name + "/" + states[state];
                        // A transparent full box remains the first direct visual,
                        // giving the native configured rectangle its hit target.
                        Line($"CreateColor({Q(root + "/hit")}, 11000, {box.X}, {box.Y}, {box.Width}, {box.Height}, #000000); Fade({Q(root + "/hit")}, 0, 0, null, false);");
                        if (frames[state] is { } frame)
                            Line($"CreateTexture({Q(root + "/frame")}, 11001, {box.X + box.FrameX}, {box.Y + box.FrameY}, {Q(frame)}); Fade({Q(root + "/frame")}, 0, {box.Opacity * 10}, null, false);");
                        string markup = "<FONT size='24'" + (state > 0 ? " color='#FFFF00'" : "") + ">" + text + "</FONT>";
                        Line($"CreateText({Q(root + "/text")}, 11002, Center, {box.Y + box.MarginTop - 15}, {box.Width}, {box.Height}, {Q(markup)});");
                        // CreateText has 34px of trailing NSS button padding.
                        // Compensate it while centering the visible glyph run.
                        Line($"Move({Q(root + "/text")}, 0, @{box.X + box.Width / 2 - _width / 2 + 17}, @0, null, false);");
                    }
                    Line("}");
                }
                if (choice.Attributes.GetValueOrDefault("DISABLE", "").ToUpperInvariant() is "TRUE" or "ON") Line($"Request({Q(name)}, Disable);");
                Line($"{slot} = {slot} + 1; }}");
                selectable.Add(i);
            }
            if (selectable.Count == 0) { Line($"{Pc} = {end};"); return; }
            Line("select {");
            foreach (int i in selectable)
            {
                LegacyNpsToken choice = _tokens[i];
                Line($"case legacy_choice_{i} {{ Delete(\"legacy_choice_*\");");
                if (choice.Attributes.GetValueOrDefault("PUSHSE", "").Length > 0) PlayUiSound(choice.Attributes["PUSHSE"], choice);
                Jump(choice.Attributes["HREF"], choice);
                Line("}");
            }
            Line("}");
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

        private void EmitEnding(LegacyNpsToken token)
        {
            string A(string key) => token.Attributes.GetValueOrDefault(key, "");
            if (!A("TURN").Equals("TRUE", StringComparison.OrdinalIgnoreCase) || A("ROLL").Length == 0
                || A("FILE").Length > 0 || A("END").Length > 0 || A("LOGO").Length > 0)
                throw new FormatException("SYSTEM ending requires a single horizontal ROLL with TURN=TRUE and no FILE, END, or LOGO");
            string image = _asset("cg", A("ROLL"));
            string skip = A("SKIP").Length == 0 ? "false" : WorkOperand(A("SKIP"));
            string width = "$" + _prefix + "credits_width", x = "$" + _prefix + "credits_x";
            string interval = "$" + _prefix + "credits_interval", opacity = "$" + _prefix + "credits_alpha";
            string skipped = "$" + _prefix + "credits_skipped", canSkip = "$" + _prefix + "credits_can_skip";
            Line($"$SYSTEM_skip = false; {_prefix}finish_animations(); Delete(\"legacy_box\"); Delete(\"legacy_box_frame\"); Request(\"legacy_voice\", Stop);");
            Line($"CreateTexture(\"legacy_credits\", 10500, 0, 0, {Q(image)}); {width} = ImageHorizon(\"legacy_credits\");");
            Line($"if ({width} <= 0) {{ LegacyUnsupported({Q(_name)}, {token.Line}, \"SYSTEM ending ROLL has no image width\"); return; }}");
            // Native roll interval is integer BGM duration / travel distance;
            // a zero result falls back to20ms, independently of playback waits.
            Line($"{interval} = DurationTime(\"legacy_bgm\") / {width}; {interval} = {interval} - {interval} % 1; if ({interval} == 0) {{ {interval} = 20; }}");
            Line($"{x} = 0 - {width}; {opacity} = 100; {skipped} = false; {canSkip} = {skip};");
            Line($"while ({x} < 0 && {opacity} > 0) {{ Move(\"legacy_credits\", 0, {x}, 0, null, false); {x} = {x} + 1;");
            Line($"if ({skipped}) {{ {opacity} = {opacity} - 4; Fade(\"legacy_credits\", 0, {opacity} * 10, null, false); }}");
            Line($"WaitKey({interval}); if ({canSkip} && !{skipped} && ({AdvanceInput})) {{ {skipped} = true; SetVolume(\"legacy_bgm\", 3000, 0); }} }}");
            // The completed native roll remains for five seconds. Its final
            // untimed alpha4 loop redraws immediately before slot removal.
            Line($"if ({opacity} > 0) {{ WaitKey(5000); }} Fade(\"legacy_credits\", 0, 0, null, false); Delete(\"legacy_credits\"); SetVolume(\"legacy_bgm\", 3000, 0);");
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
            string path = _asset(token.Value == "VOICE" ? "voice" : "sound", A("SRC"));
            if (token.Value == "VOICE")
            {
                Line($"Request({Q(name)}, Stop);");
                if (path.Length == 0)
                {
                    _diagnostics.Add($"{_name}:{token.Line}: missing authored VOICE '{A("SRC")}'; voice channel stopped and dialogue continues silently.");
                    return;
                }
            }
            Play(name, token.Value == "VOICE" ? "Voice" : token.Value == "BGM" ? "BGM" : "SE",
                path, loop);
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
            Match match = Regex.Match(condition, @"^\s*([^=<>!&|]+?)\s*(==|!=|>=|<=|=|!|>|<)\s*([^=<>!&|]+?)\s*$", RegexOptions.IgnoreCase);
            if (!match.Success) throw new FormatException("Unsupported condition " + condition);
            string op = match.Groups[2].Value switch { "=" => "==", "!" => "!=", var value => value };
            return ReadWork(match.Groups[1].Value.Trim()) + " " + op + " " + WorkOperand(match.Groups[3].Value.Trim());
        }

        private string ReadWork(string name)
        {
            string key = WorkName(name), value = "$" + _prefix + "work" + _workTemporary++;
            Line($"{value} = VariableValue($, {LocalWorkKey(key)});");
            Line($"if (VariableValue(#, {Q("legacy_work_has_global_" + key)})) {{ {value} = VariableValue(#, {Q("legacy_work_global_" + key)}); }}");
            return value;
        }

        private void EmitCalculation(LegacyNpsToken token)
        {
            string name = token.Attributes.GetValueOrDefault("WORKNAME", token.Attributes.GetValueOrDefault("WORK", ""));
            string key = WorkName(name);
            string mode = token.Attributes.GetValueOrDefault("MODE", "").ToUpperInvariant();
            if (mode is not ("" or "GLOBAL" or "NOREF")) throw new FormatException("CALC MODE " + mode);
            string expression = token.Attributes.GetValueOrDefault("OPERATOR", "").Trim();
            if (expression.Length == 0) throw new FormatException("CALC requires OPERATOR");
            string result;
            if (mode == "NOREF") result = Q(expression);
            else if (int.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                && !expression.StartsWith('+') && !expression.StartsWith('-')) result = number.ToString(CultureInfo.InvariantCulture);
            else if (expression.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) result = "1";
            else if (expression.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) result = "0";
            else
            {
                string normalized = expression.Replace('＋', '+').Replace('－', '-').Replace('×', '*').Replace('／', '/').Replace('％', '%');
                string[] pieces = Regex.Split(normalized, @"([+*/%\-])");
                if (pieces.Length == 1)
                    result = normalized.StartsWith("random(", StringComparison.OrdinalIgnoreCase) ? WorkOperand(normalized)
                        : normalized.StartsWith('#') ? ReadWork(normalized[1..]) : Q(normalized);
                else
                {
                    result = pieces[0].Trim().Length == 0 ? ReadWork(name) : WorkOperand(pieces[0].Trim());
                    for (int i = 1; i < pieces.Length; i += 2)
                    {
                        if (i + 1 >= pieces.Length || pieces[i + 1].Trim().Length == 0) throw new FormatException("CALC expression " + expression);
                        string right = WorkOperand(pieces[i + 1].Trim()), operation = pieces[i];
                        string temp = "$" + _prefix + "work" + _workTemporary++;
                        Line($"{temp} = {result} {operation} {right};");
                        if (operation == "/") Line($"{temp} = {temp} - {temp} % 1;");
                        result = temp;
                    }
                }
            }
            if (mode == "GLOBAL")
                Line($"VariableValue(#, {Q("legacy_work_has_global_" + key)}, true); VariableValue(#, {Q("legacy_work_global_" + key)}, {result});");
            else
            {
                Line($"if (VariableValue(#, {Q("legacy_work_has_global_" + key)})) {{ VariableValue(#, {Q("legacy_work_global_" + key)}, {result}); }}");
                Line($"else {{ VariableValue($, {LocalWorkKey(key)}, {result}); }}");
            }
        }

        private string WorkOperand(string operand)
        {
            if (int.TryParse(operand, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return number.ToString(CultureInfo.InvariantCulture);
            if (operand.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return "1";
            if (operand.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return "0";
            Match random = Regex.Match(operand, @"^random\(\s*(\d+)\s*\)$", RegexOptions.IgnoreCase);
            if (random.Success && int.TryParse(random.Groups[1].Value, out int maximum) && maximum > 0) return "Random(" + maximum + ")";
            return ReadWork(operand.TrimStart('#'));
        }

        private static string WorkName(string name)
        {
            name = name.Trim().TrimStart('#');
            if (name.Length == 0 || name.IndexOfAny(['"', '\'', '[', ']', '$', '#', '\r', '\n']) >= 0)
                throw new FormatException("Invalid CALC work name " + name);
            // The original name lookup uses lstrcmpiA and lives on the engine,
            // so chapters share both local and global work tables.
            return name.ToUpperInvariant();
        }

        private static string LocalWorkKey(string name)
            => Q("legacy_work_local_") + " + VariableValue($, \"legacy_work_epoch\") + " + Q("_" + name);
        private static string LocalKey(string name) => "legacy_system_" + name;
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
