using System.Buffers;
using System.Text;

namespace Workbench.Windows;

public enum InputKind { KeyDown, KeyUp, ButtonDown, ButtonUp, Move, Wheel, Text, ReleaseAll }
public enum InputButton { Left, Right, Middle }
public readonly record struct InputLease(Guid Id, long Generation);
public readonly record struct InputStamp(Guid Host, uint Stream, uint Epoch, uint Scene);
public readonly record struct HeldInput(string? Key, InputButton? Button)
{
    public static HeldInput ForKey(string key) => new(key,null);
    public static HeldInput ForButton(InputButton button) => new(null,button);
}

// Internal M1 model, not a frozen public wire contract. Do not log instances or text bodies.
public sealed record InputCommand(InputLease Lease, long Sequence, InputStamp Stamp, uint DisplayedFrame,
    InputKind Kind, string? Key = null, InputButton? Button = null, double? U = null, double? V = null,
    int WheelX = 0, int WheelY = 0, string? Text = null, bool Repeat = false)
{
    public override string ToString() => $"InputCommand({Kind}, sequence={Sequence}, payload redacted)";
}

public static class InputCommandValidation
{
    public const long MaximumSequence = 9_007_199_254_740_991;
    public static bool IsValid(InputCommand command)
    {
        if (command.Sequence is < 1 or > MaximumSequence || !Enum.IsDefined(command.Kind)) return false;
        bool position = command.Kind is InputKind.ButtonDown or InputKind.Move or InputKind.Wheel;
        if (position)
        {
            if (command.U is not { } u || command.V is not { } v || !double.IsFinite(u) || !double.IsFinite(v)
                || u is < 0 or > 1 || v is < 0 or > 1) return false;
        }
        else if (command.U is not null || command.V is not null) return false;
        bool keyboard = command.Kind is InputKind.KeyDown or InputKind.KeyUp;
        if (keyboard ? !PhysicalKeyMap.TryGet(command.Key,out _) : command.Key is not null) return false;
        bool button = command.Kind is InputKind.ButtonDown or InputKind.ButtonUp;
        if (button ? command.Button is null || !Enum.IsDefined(command.Button.Value) : command.Button is not null) return false;
        if (command.Kind != InputKind.KeyDown && command.Repeat) return false;
        if (command.Kind == InputKind.Wheel)
        {
            if (command.WheelX is < -1200 or > 1200 || command.WheelY is < -1200 or > 1200
                || command.WheelX == 0 && command.WheelY == 0) return false;
        }
        else if (command.WheelX != 0 || command.WheelY != 0) return false;
        return command.Kind == InputKind.Text ? ValidText(command.Text) : command.Text is null;
    }

    public static bool ValidText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 8192) return false;
        ReadOnlySpan<char> remaining=text;
        int scalars=0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining,out var rune,out int consumed) != OperationStatus.Done
                || rune.Value == 0 || ++scalars > 4096) return false;
            remaining=remaining[consumed..];
        }
        return true;
    }
}

public readonly record struct PhysicalKey(ushort ScanCode, bool Extended = false);

/// <summary>Initial PC set-1 physical key map, keyed by browser KeyboardEvent.code, not character text.</summary>
public static class PhysicalKeyMap
{
    private static readonly IReadOnlyDictionary<string,PhysicalKey> keys=Build();
    public static bool TryGet(string? code,out PhysicalKey key)
    {
        key=default; return code is {Length:>0 and <=32} && keys.TryGetValue(code,out key);
    }
    private static Dictionary<string,PhysicalKey> Build()
    {
        var result=new Dictionary<string,PhysicalKey>(StringComparer.Ordinal);
        void Row(int first,string codes) { int scan=first; foreach(var code in codes.Split(' '))result.Add(code,new((ushort)scan++)); }
        Row(0x01,"Escape Digit1 Digit2 Digit3 Digit4 Digit5 Digit6 Digit7 Digit8 Digit9 Digit0 Minus Equal Backspace Tab");
        Row(0x10,"KeyQ KeyW KeyE KeyR KeyT KeyY KeyU KeyI KeyO KeyP BracketLeft BracketRight Enter ControlLeft");
        Row(0x1e,"KeyA KeyS KeyD KeyF KeyG KeyH KeyJ KeyK KeyL Semicolon Quote Backquote ShiftLeft Backslash");
        Row(0x2c,"KeyZ KeyX KeyC KeyV KeyB KeyN KeyM Comma Period Slash ShiftRight NumpadMultiply AltLeft Space CapsLock");
        Row(0x3b,"F1 F2 F3 F4 F5 F6 F7 F8 F9 F10");
        Row(0x47,"Numpad7 Numpad8 Numpad9 NumpadSubtract Numpad4 Numpad5 Numpad6 NumpadAdd Numpad1 Numpad2 Numpad3 Numpad0 NumpadDecimal");
        result.Add("ScrollLock",new(0x46));result.Add("IntlBackslash",new(0x56));result.Add("F11",new(0x57));result.Add("F12",new(0x58));
        foreach(var (code,scan) in new (string,ushort)[] { ("ControlRight",0x1d),("AltRight",0x38),("NumpadEnter",0x1c),
            ("NumpadDivide",0x35),("NumLock",0x45),("Home",0x47),("ArrowUp",0x48),("PageUp",0x49),
            ("ArrowLeft",0x4b),("ArrowRight",0x4d),("End",0x4f),("ArrowDown",0x50),("PageDown",0x51),("Insert",0x52),("Delete",0x53),
            ("ContextMenu",0x5d) })result.Add(code,new(scan,true));
        // Meta, Pause/Break, PrintScreen, media keys and unknown codes are explicitly unsupported in this increment.
        return result;
    }
}
