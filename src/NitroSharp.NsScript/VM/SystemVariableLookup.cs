using System;

namespace NitroSharp.NsScript.VM;

public sealed class SystemVariableLookup
{
    private readonly NsScriptVM _vm;

    public int PresentProcess { get; private set; }
    private int _presentPreprocess;
    private int _presentText;

    private int _rButtonDown;
    private int _lButtonDown;
    private int _returnKeyDown;
    private int _escapeKeyDown;
    private int _backlogKeyDown;
    private int _x360ButtonStartDown;
    private int _x360ButtonADown;
    private int _x360ButtonBDown;
    private int _x360ButtonYDown;

    private int _x360ButtonLeftDown;
    private int _x360ButtonUpDown;
    private int _x360ButtonRightDown;
    private int _x360ButtonDownDown;

    private int _x360ButtonLbDown;
    private int _x360ButtonRbDown;
    private int _backlogEnable;
    private int _backlogRowMax;
    private int _backlogPositionX;
    private int _backlogPositionY;
    private int _backlogRowInterval;
    private int _backlogCharacterWidth;

    private int _positionXTextIcon;
    private int _positionYTextIcon;
    private int _savePath;

    private int _lastText;
    private int _skip;
    private int _skipExpress;
    private int _textAuto;
    private int _textErase;
    private int _textAutoLock;
    private int _menuLock;
    private int _skipLock;
    private int _backlogLock;
    private int _textInterval;
    private int _textSpeed;
    private int _textWaitKey;
    private int _autoWaitPerCharacter;

    private static readonly string[] s_audioChannels = ["bgm", "se", "voice"];
    private readonly int[] _audioEnabled = [-1, -1, -1];
    private readonly int[] _audioVolume = [-1, -1, -1];

    private ConstantValue _missingVariable = ConstantValue.Null;

    public SystemVariableLookup(NsScriptVM vm)
    {
        _vm = vm;
        Refresh();
    }

    internal void Refresh()
    {
        _savePath = Lookup("SYSTEM_save_path");

        _presentPreprocess = Lookup("SYSTEM_present_preprocess");
        _presentText = Lookup("SYSTEM_present_text");
        PresentProcess = Lookup("SYSTEM_present_process");
        _rButtonDown = Lookup("SYSTEM_r_button_down");
        _lButtonDown = Lookup("SYSTEM_l_button_down");
        _returnKeyDown = Lookup("SYSTEM_keydown_enter");
        _escapeKeyDown = Lookup("SYSTEM_keydown_esc");
        _backlogKeyDown = Lookup("SYSTEM_keydown_b");
        _x360ButtonStartDown = Lookup("SYSTEM_XBOX360_button_start_down");
        _x360ButtonADown = Lookup("SYSTEM_XBOX360_button_a_down");
        _x360ButtonBDown = Lookup("SYSTEM_XBOX360_button_b_down");
        _x360ButtonYDown = Lookup("SYSTEM_XBOX360_button_y_down");
        _x360ButtonLeftDown = Lookup("SYSTEM_XBOX360_button_left_down");
        _x360ButtonUpDown = Lookup("SYSTEM_XBOX360_button_up_down");
        _x360ButtonRightDown = Lookup("SYSTEM_XBOX360_button_right_down");
        _x360ButtonDownDown = Lookup("SYSTEM_XBOX360_button_down_down");
        _x360ButtonLbDown = Lookup("SYSTEM_XBOX360_button_lb_down");
        _x360ButtonRbDown = Lookup("SYSTEM_XBOX360_button_rb_down");

        _backlogEnable = Lookup("SYSTEM_backlog_enable");
        _backlogRowMax = Lookup("SYSTEM_backlog_row_max");
        _backlogPositionX = Lookup("SYSTEM_backlog_position_x");
        _backlogPositionY = Lookup("SYSTEM_backlog_position_y");
        _backlogRowInterval = Lookup("SYSTEM_backlog_row_interval");
        _backlogCharacterWidth = Lookup("SYSTEM_backlog_character_width");

        _positionXTextIcon = Lookup("SYSTEM_position_x_text_icon");
        _positionYTextIcon = Lookup("SYSTEM_position_y_text_icon");
        _lastText = Lookup("SYSTEM_last_text");
        _skip = Lookup("SYSTEM_skip");
        _skipExpress = _vm.GlobalsLookup.Flags.TryGetValue("SYSTEM_skip_express", out int express) ? express : -1;
        _textAuto = Lookup("SYSTEM_text_auto");
        _textErase = Lookup("SYSTEM_text_erase");
        _textAutoLock = Lookup("SYSTEM_text_auto_lock");

        _menuLock = Lookup("SYSTEM_menu_lock");
        _skipLock = Lookup("SYSTEM_skip_lock");
        _backlogLock = Lookup("SYSTEM_backlog_lock");
        _textInterval = _vm.GlobalsLookup.Variables.TryGetValue("SYSTEM_text_interval", out int interval) ? interval : -1;
        _textSpeed = _vm.GlobalsLookup.Flags.TryGetValue("SYSTEM_text_speed", out int speed) ? speed : -1;
        _textWaitKey = _vm.GlobalsLookup.Variables.TryGetValue("SYSTEM_text_waitkey", out int waitKey) ? waitKey : -1;
        _autoWaitPerCharacter = _vm.GlobalsLookup.Flags.TryGetValue("SYSTEM_auto_wait_per_character", out int autoWait) ? autoWait : -1;

        for (int i = 0; i < s_audioChannels.Length; i++)
        {
            _audioEnabled[i] = _vm.GlobalsLookup.Flags.TryGetValue("SYSTEM_sound_" + s_audioChannels[i], out int enabled) ? enabled : -1;
            _audioVolume[i] = _vm.GlobalsLookup.Flags.TryGetValue("SYSTEM_sound_volume_" + s_audioChannels[i], out int volume) ? volume : -1;
        }
    }

    public (ConstantValue? Enabled, ConstantValue? Volume) GetAudioSettings(NsAudioKind kind)
    {
        int index = kind switch
        {
            NsAudioKind.BackgroundMusic => 0,
            NsAudioKind.SoundEffect => 1,
            NsAudioKind.Voice => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (_audioEnabled[index] >= 0 ? _vm.GetFlag(_audioEnabled[index]) : null,
            _audioVolume[index] >= 0 ? _vm.GetFlag(_audioVolume[index]) : null);
    }

    public (ConstantValue? Interval, ConstantValue? Speed, ConstantValue? WaitKey, ConstantValue? AutoWaitPerCharacter) GetTextSettings()
        => (_textInterval >= 0 ? _vm.GetVariable(_textInterval) : null,
            _textSpeed >= 0 ? _vm.GetFlag(_textSpeed) : null,
            _textWaitKey >= 0 ? _vm.GetVariable(_textWaitKey) : null,
            _autoWaitPerCharacter >= 0 ? _vm.GetFlag(_autoWaitPerCharacter) : null);

    private int Lookup(string name)
    {
        if (!_vm.GlobalsLookup.TryLookupSystemVariable(name, out int index))
        {
            if (!_vm.GlobalsLookup.TryLookupSystemFlag(name, out index))
            {
                return -1;
            }
        }
        return index;
    }

    public ref ConstantValue CurrentSubroutineName => ref Var(PresentProcess);
    public ref ConstantValue CurrentDialogueBox => ref Var(_presentPreprocess);
    public ref ConstantValue CurrentDialogueBlock => ref Var(_presentText);
    public ref ConstantValue RightButtonDown => ref Var(_rButtonDown);
    public ref ConstantValue LeftButtonDown => ref Var(_lButtonDown);
    public ref ConstantValue ReturnKeyDown => ref Var(_returnKeyDown);
    public ref ConstantValue EscapeKeyDown => ref Var(_escapeKeyDown);
    public ref ConstantValue BacklogKeyDown => ref Var(_backlogKeyDown);
    public ref ConstantValue TextErase => ref Var(_textErase);

    public ref ConstantValue X360StartButtonDown => ref Var(_x360ButtonStartDown);
    public ref ConstantValue X360AButtonDown => ref Var(_x360ButtonADown);
    public ref ConstantValue X360BButtonDown => ref Var(_x360ButtonBDown);
    public ref ConstantValue X360YButtonDown => ref Var(_x360ButtonYDown);

    public ref ConstantValue X360LeftButtonDown => ref Var(_x360ButtonLeftDown);
    public ref ConstantValue X360UpButtonDown => ref Var(_x360ButtonUpDown);
    public ref ConstantValue X360RightButtonDown => ref Var(_x360ButtonRightDown);
    public ref ConstantValue X360DownButtonDown => ref Var(_x360ButtonDownDown);

    public ref ConstantValue X360LbButtonDown => ref Var(_x360ButtonLbDown);
    public ref ConstantValue X360RbButtonDown => ref Var(_x360ButtonRbDown);

    private ref ConstantValue Var(int index)
    {
        return ref index >= 0
            ? ref _vm.GetVariable(index)
            : ref _missingVariable;
    }

    private ref ConstantValue Flag(int index)
    {
        return ref index >= 0
            ? ref _vm.GetFlag(index)
            : ref _missingVariable;
    }

    public ref ConstantValue BacklogEnable => ref Var(_backlogEnable);
    public ref ConstantValue BacklogRowMax => ref Var(_backlogRowMax);
    public ref ConstantValue BacklogRowInterval => ref Var(_backlogRowInterval);
    public ref ConstantValue BacklogPositionX => ref Var(_backlogPositionX);
    public ref ConstantValue BacklogPositionY => ref Var(_backlogPositionY);
    public ref ConstantValue BacklogCharacterWidth => ref Var(_backlogCharacterWidth);

    public ref ConstantValue PositionXTextIcon => ref _vm.GetVariable(_positionXTextIcon);
    public ref ConstantValue PositionYTextIcon => ref _vm.GetVariable(_positionYTextIcon);

    public ref ConstantValue SavePath => ref Flag(_savePath);

    public ref ConstantValue LastText => ref Var(_lastText);

    public bool IsSkipping => _skip >= 0 && (_vm.GetVariable(_skip).AsBool() ?? false);
    public bool IsTextErased => _textErase >= 0 && (_vm.GetVariable(_textErase).AsBool() ?? false);

    public void CancelSkip()
    {
        if (_skip >= 0) _vm.GetVariable(_skip) = ConstantValue.False;
        if (_skipExpress >= 0) _vm.GetFlag(_skipExpress) = ConstantValue.False;
    }

    public ref ConstantValue Skip => ref Var(_skip);
    public ref ConstantValue TextAuto => ref Var(_textAuto);
    public ref ConstantValue TextAutoLock => ref Var(_textAutoLock);

    public ref ConstantValue MenuLock => ref Var(_menuLock);
    public ref ConstantValue SkipLock => ref Var(_skipLock);
    public ref ConstantValue BacklogLock => ref Var(_backlogLock);
}
