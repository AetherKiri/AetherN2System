using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using NitroSharp.Graphics;
using NitroSharp.Input;
using NitroSharp.Text;
using Veldrid;

namespace NitroSharp.Saving;

internal sealed class NativeSaveMenu
{
    private readonly List<Row> _rows = [];
    private readonly bool _hasHome;
    private bool? _saving;
    private int _selected;
    private uint? _confirmOverwrite;
    private bool _waitingForOperation;
    private string? _message;
    private BlankEntity? _content;
    private string? _displayedError;

    private readonly record struct Row(uint? Slot, string Label, bool Enabled, float X, float Y, float Width, float Height)
    {
        public bool Contains(Vector2 point) => point.X >= X && point.Y >= Y && point.X < X + Width && point.Y < Y + Height;
    }

    public NativeSaveMenu(GameContext ctx, bool? saving)
    {
        _saving = saving;
        _hasHome = saving is null;
        Process = new Process(EntityName.Parse("native-save-menu"), null, ctx.World.MainProcess.FontSettings with
        {
            VerticalGlyphs = false,
            DefaultTextColor = Vector4.One,
            DefaultOutlineColor = null
        });
        ctx.World.AddEntity(new Thread(EntityName.Parse("main"), Process, default, isMain: true));
        ctx.InputContext.Consume(InputAction.Back);
        ctx.InputContext.ConsumeChoicePress(ctx.InputContext.ChoicePointerPressed);
        Rebuild(ctx);
    }

    internal Process Process { get; }

    internal bool Update(GameContext ctx)
    {
        InputContext input = ctx.InputContext;
        if (_waitingForOperation)
        {
            input.Consume(InputAction.Back);
            input.ConsumeChoicePress(input.ChoicePointerPressed);
            if (ctx.Saves.HasPendingOperation) return false;
            _waitingForOperation = false;
            if (ctx.Saves.LastError is null) return true;
            _message = ctx.Saves.LastError;
            Rebuild(ctx);
            return false;
        }

        if (_displayedError != ctx.Saves.LastError)
        {
            _displayedError = ctx.Saves.LastError;
            if (_displayedError is not null) _message = _displayedError;
            Rebuild(ctx);
        }

        if (input.Consume(InputAction.Back)) return Back(ctx);
        int direction = (input.Consume(InputAction.Down) ? 1 : 0) - (input.Consume(InputAction.Up) ? 1 : 0);
        if (direction != 0)
        {
            _selected = (_selected + direction + _rows.Count) % _rows.Count;
            _confirmOverwrite = null;
            _message = null;
            Rebuild(ctx);
        }

        bool pointer = input.ChoicePointerPressed;
        if (pointer || input.PointerMoved)
        {
            Vector2 position = Scrollbar.ToDesignPosition(ctx.RenderContext, input.MousePosition);
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!_rows[i].Contains(position)) continue;
                if (_selected != i)
                {
                    _selected = i;
                    _confirmOverwrite = null;
                    _message = null;
                    Rebuild(ctx);
                }
                if (pointer && input.ConsumeChoicePress(pointer: true)) return Activate(ctx);
                break;
            }
        }
        if (input.ConsumeChoicePress(pointer: false)) return Activate(ctx);
        return false;
    }

    private bool Activate(GameContext ctx)
    {
        Row row = _rows[_selected];
        if (!row.Enabled) return false;
        if (_saving is null)
        {
            if (_selected == 2) return true;
            _saving = _selected == 0;
            _selected = 0;
            _message = null;
            Rebuild(ctx);
            return false;
        }
        if (row.Slot is not { } slot) return Back(ctx);
        if (_saving.Value && ctx.Saves.SaveExists(slot) && _confirmOverwrite != slot)
        {
            _confirmOverwrite = slot;
            _message = "再次点击此存档位以覆盖 / Select again to overwrite";
            Rebuild(ctx);
            return false;
        }
        _confirmOverwrite = null;
        if (_saving.Value) ctx.Saves.RequestSave(slot);
        else ctx.Saves.RequestLoad(slot);
        if (!ctx.Saves.HasPendingOperation && ctx.Saves.LastError is { } error)
        {
            _message = error;
            Rebuild(ctx);
            return false;
        }
        _waitingForOperation = true;
        _message = _saving.Value ? "正在保存 / Saving…" : "正在读取 / Loading…";
        Rebuild(ctx);
        return false;
    }

    private bool Back(GameContext ctx)
    {
        if (!_hasHome || _saving is null) return true;
        _saving = null;
        _selected = 0;
        _confirmOverwrite = null;
        _message = null;
        Rebuild(ctx);
        return false;
    }

    private void Rebuild(GameContext ctx)
    {
        if (_content is not null) ctx.World.DestroyEntity(_content);
        _content = ctx.World.AddEntity(new BlankEntity(EntityName.Parse("content"), Process));
        _rows.Clear();
        float width = ctx.RenderContext.DesignResolution.Width;
        float height = ctx.RenderContext.DesignResolution.Height;
        float panelWidth = MathF.Min(760, width - 48);
        float left = (width - panelWidth) / 2;
        float rowLeft = left + 24;
        float rowWidth = panelWidth - 48;
        float top = MathF.Max(88, height * 0.20f);
        float rowHeight = MathF.Min(48, (height - top - 100) / 7 - 8);
        Rect("shade", 0, 0, width, height, new RgbaFloat(0, 0, 0, 0.82f), 0);
        Rect("panel", left, 24, panelWidth, height - 48, new RgbaFloat(0.075f, 0.09f, 0.12f, 1), 1);
        string title = _saving is null ? "存档与读档 / Save & Load" : _saving.Value ? "保存进度 / Save game" : "读取进度 / Load game";
        Text("title", title, rowLeft, 44, rowWidth, 42, 27, 3);

        if (_saving is null)
        {
            AddRow(null, "保存 / Save", !ReadLock(ctx, "SYSTEM_save_lock"));
            AddRow(null, "读取 / Load", !ReadLock(ctx, "SYSTEM_load_lock"));
        }
        else
        {
            for (uint slot = 1; slot <= 6; slot++)
            {
                bool exists = ctx.Saves.SaveExists(slot);
                DateTime? date = ctx.Saves.GetSlotDate(slot);
                string detail = exists ? date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "已有存档 / Saved" : "空 / Empty";
                AddRow(slot, $"{slot:00}     {detail}", _saving.Value || exists);
            }
        }
        AddRow(null, "返回 / Back", true);
        _selected = Math.Clamp(_selected, 0, _rows.Count - 1);
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            RgbaFloat color = i == _selected ? new(0.16f, 0.31f, 0.45f, 1) : new(0.12f, 0.15f, 0.20f, 1);
            Rect("row" + i, row.X, row.Y, row.Width, row.Height, color, 2);
            Text("label" + i, row.Label, row.X + 16, row.Y + 7, row.Width - 32, row.Height - 4, 22, 3,
                row.Enabled ? Vector4.One : new Vector4(0.5f, 0.53f, 0.57f, 1));
        }
        string footer = _message ?? "点击选择 · 方向键 / Enter · 返回 / Esc";
        Text("status", footer.Replace('<', '＜').Replace('>', '＞'), rowLeft, height - 76, rowWidth, 52, 17, 3);

        void AddRow(uint? slot, string label, bool enabled)
            => _rows.Add(new Row(slot, label, enabled, rowLeft, top + _rows.Count * (rowHeight + 8), rowWidth, rowHeight));

        void Rect(string name, float x, float y, float w, float h, RgbaFloat color, int priority)
        {
            var rect = new SolidColorRect(EntityName.Parse(name), _content, priority, color, new DesignSize(w, h));
            rect.Transform.Position = new Vector3(x, y, 0);
            ctx.World.AddEntity(rect);
        }

        void Text(string name, string text, float x, float y, float w, float h, uint size, int priority, Vector4? color = null)
        {
            var font = Process.FontSettings with { DefaultFontSize = new PtFontSize(size), DefaultTextColor = color ?? Vector4.One };
            var block = new TextBlock(EntityName.Parse(name), _content, priority, text, ctx.RenderContext.Text,
                new DesignSizeU((uint)MathF.Max(1, w), (uint)MathF.Max(1, h)), font, Vector4.Zero);
            block.Transform.Position = new Vector3(x, y, 0);
            ctx.World.AddEntity(block);
        }
    }

    private static bool ReadLock(GameContext ctx, string name) => ctx.VM.GetHostVariable(name).AsBool() ?? false;
}
