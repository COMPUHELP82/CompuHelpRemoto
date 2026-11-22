using System.Runtime.InteropServices;

namespace CompuHelpRemoto;

/// <summary>Simula mouse y teclado en el equipo controlado (SendInput de Windows).</summary>
public static class InputInjector
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOVE = 0x0001, ABSOLUTE = 0x8000, WHEEL = 0x0800;
    const uint KEYUP = 0x0002, EXTENDED = 0x0001;

    static void Send(INPUT i) => SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());

    public static void MoveTo(float nx, float ny)
    {
        nx = Math.Clamp(nx, 0f, 1f); ny = Math.Clamp(ny, 0f, 1f);
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = (int)(nx * 65535), dy = (int)(ny * 65535), dwFlags = MOVE | ABSOLUTE } }
        });
    }

    public static void Button(int button, bool down)
    {
        uint flag = button switch
        {
            1 => down ? 0x0008u : 0x0010u,  // derecho
            2 => down ? 0x0020u : 0x0040u,  // medio
            _ => down ? 0x0002u : 0x0004u,  // izquierdo
        };
        Send(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } });
    }

    public static void Wheel(int delta) =>
        Send(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = WHEEL } } });

    public static void Key(ushort vk, bool down, bool extended)
    {
        uint flags = (down ? 0 : KEYUP) | (extended ? EXTENDED : 0);
        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } }
        });
    }
}
