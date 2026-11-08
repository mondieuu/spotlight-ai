using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Odak;

/// <summary>Decides whether a Windows key press was a lone tap. Holds no key history and stores nothing.</summary>
internal sealed class WinKeyTap
{
    public const int LWin = 0x5B, RWin = 0x5C;
    // Win+L can lock the PC before the release arrives: a press after this long counts as a new one.
    private const long StaleMs = 1000;

    private bool _down, _used;
    private long _last;

    /// <summary>Feeds one key event; true when the Windows key was just released without being part of a combination.</summary>
    public bool OnKey(int vk, bool isDown, bool modifierHeld, long nowMs)
    {
        if (vk is not (LWin or RWin))
        {
            if (isDown && _down) _used = true; // Win+E, Win+D…
            return false;
        }
        if (isDown)
        {
            if (!_down || nowMs - _last > StaleMs)
            {
                _down = true;
                _used = modifierHeld; // Ctrl+Win, Shift+Win…
            }
            _last = nowMs;
            return false;
        }
        if (!_down) return false;
        _down = false;
        return !_used;
    }
}

/// <summary>
/// Opens Spotlight AI with a tap of the Windows key, without the Start menu. The key is never blocked, so every
/// Win+ shortcut keeps working: just before a lone tap is released, a harmless unassigned key is injected, and
/// Windows then treats the press as a combination instead of opening Start.
///
/// Uses a low-level keyboard hook, installed only while this option is on. Only the Windows key's state is
/// kept; other keys only mark the press as a combination and are never recorded.
/// Windows does not send this hook the keys typed into a window running as administrator, so there the key
/// behaves normally.
/// </summary>
internal sealed class WinKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint LLKHF_INJECTED = 0x10, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
    private const ushort VK_UNASSIGNED = 0xFF;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly WinKeyTap _tap = new();
    private readonly HookProc _proc; // kept in a field so the GC cannot collect it while Windows holds it
    private readonly Action _onTap;
    private IntPtr _hook;

    public WinKeyHook(Action onTap)
    {
        _onTap = onTap;
        _proc = OnHook;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public bool IsActive => _hook != IntPtr.Zero;

    private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN, up = msg is WM_KEYUP or WM_SYSKEYUP;
            if ((key.flags & LLKHF_INJECTED) == 0 && (down || up))
            {
                bool modifier = down && key.vkCode is WinKeyTap.LWin or WinKeyTap.RWin && (Held(VK_SHIFT) || Held(VK_CONTROL) || Held(VK_MENU));
                if (_tap.OnKey((int)key.vkCode, down, modifier, Environment.TickCount64))
                {
                    MaskStartMenu();
                    _onTap();
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void MaskStartMenu()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, wVk = VK_UNASSIGNED },
            new INPUT { type = INPUT_KEYBOARD, wVk = VK_UNASSIGNED, dwFlags = KEYEVENTF_KEYUP },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
