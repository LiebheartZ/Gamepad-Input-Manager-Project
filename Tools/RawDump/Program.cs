using System.Runtime.InteropServices;
using System.Text;

// Vuelca lo que la capa GAMEPAD de SDL reporta (lo mismo que ve GDM),
// tras aÃ±adir el mismo mapeo extra que usa Sdl2GamepadDetectionService.
// AdemÃ¡s prueba la vibraciÃ³n a los ~4 segundos de iniciar.

const string DeviceName = "Xbox One S Controller";
const string DeviceGuid = "0300da7b5e040000e002000000000000";
const string MappingBody =
    "a:b0,b:b1,back:b4,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1," +
    "guide:b5,leftshoulder:b9,leftstick:b7,lefttrigger:a2," +
    "leftx:a0,lefty:a1,rightshoulder:b10,rightstick:b8,righttrigger:a5," +
    "rightx:a3,righty:a4,start:b6,x:b2,y:b3,";

// args[0] = máscara de hints a aplicar: x=HIDAPI_XBOX, h=HIDAPI, i=XINPUT_ENABLED=0
if (args.Length > 0)
{
    if (args[0].Contains('x')) SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_XBOX", "1");
    if (args[0].Contains('h')) SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
    if (args[0].Contains('i')) SDL.SDL_SetHint("SDL_XINPUT_ENABLED", "0");
    if (args[0].Contains('g')) SDL.SDL_SetHint("SDL_JOYSTICK_GAMEINPUT", "0");
    if (args[0].Contains('G')) SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_GIP", "1");
    if (args[0].Contains('o')) SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_XBOX_ONE", "1");
    if (args[0].Contains('r')) SDL.SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
    if (args[0].Contains('w')) SDL.SDL_SetHint("SDL_JOYSTICK_WGI", "0");
}

if (SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_GAMECONTROLLER) < 0)
{
    Console.WriteLine($"SDL_Init fallÃ³: {Err()}");
    return 1;
}

Native.SDL_JoystickEventState(Native.SDL_ENABLE);

int count = SDL.SDL_NumJoysticks();
Console.WriteLine($"Joysticks: {count}");

// Primero anadir mapeos extra ANTES de abrir handles (AddMapping puede re-enumerar).
for (int i = 0; i < count; i++)
{
    if (SDL.SDL_IsGameController(i) == 1) continue;
    IntPtr namePtr0 = SDL.SDL_JoystickNameForIndex(i);
    string name0 = namePtr0 != IntPtr.Zero ? Marshal.PtrToStringUTF8(namePtr0) ?? "?" : "?";
    SdlJoystickGuid guid0 = SDL.SDL_JoystickGetDeviceGUID(i);
    var guidBuf0 = new StringBuilder(33);
    SDL.SDL_JoystickGetGUIDString(guid0, guidBuf0, guidBuf0.Capacity);
    string guid0s = guidBuf0.ToString();
    if (!name0.Contains("Xbox", StringComparison.OrdinalIgnoreCase) && !guid0s.Contains("e002", StringComparison.OrdinalIgnoreCase)) continue;
    string mapping = $"{guid0s},{name0},{MappingBody}";
    int r = SDL.SDL_GameControllerAddMapping(mapping);
    Console.WriteLine($"[{i}] {name0} -> AddMapping result={r} err={Err()}");
    Console.WriteLine($"    -> IsGameController={SDL.SDL_IsGameController(i)}");
    IntPtr mapPtr0 = SDL.SDL_GameControllerMappingForIndex(i);
    string map0 = mapPtr0 != IntPtr.Zero ? Marshal.PtrToStringUTF8(mapPtr0) ?? "?" : "?";
    Console.WriteLine($"    -> MAPEOS SDL: {map0}");
}

count = SDL.SDL_NumJoysticks();
Console.WriteLine($"Joysticks tras mapeo: {count}");
var pads = new List<(int Index, IntPtr Joystick, IntPtr Gamepad, string Name)>();

for (int i = 0; i < count; i++)
{
    IntPtr j = SDL.SDL_JoystickOpen(i);
    if (j == IntPtr.Zero) continue;
    IntPtr namePtr = SDL.SDL_JoystickName(j);
    string name = namePtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(namePtr) ?? "?" : "?";
    int nb = Native.SDL_JoystickNumButtons(j);
    int na = Native.SDL_JoystickNumAxes(j);
    int nh = Native.SDL_JoystickNumHats(j);
    IntPtr pathPtr = SDL.SDL_JoystickPath(j);
    string jpath = pathPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(pathPtr) ?? "?" : "?";
    Console.WriteLine($"[{i}] {name} â€” botones={nb} ejes={na} hats={nh} path={jpath}");

    IntPtr gc = IntPtr.Zero;
    if (SDL.SDL_IsGameController(i) == 1)
    {
        gc = SDL.SDL_GameControllerOpen(i);
        Console.WriteLine($"    -> gamepad abierto");
    }

    pads.Add((i, j, gc, name));
}

Console.WriteLine("Pulsa los botones/ sticks / gatillos / cruceta. A los ~4s pruebo vibraciÃ³n. Ctrl+C para salir.");

var lastButtons = new Dictionary<int, bool[]>();
var lastAxes = new Dictionary<int, short[]>();
var lastHats = new Dictionary<(int, int), int>();
var lastGpButtons = new Dictionary<int, bool[]>();
var lastGpAxes = new Dictionary<int, short[]>();
var rumbleTested = false;
var sw = System.Diagnostics.Stopwatch.StartNew();

string[] gpButtonNames =
[
    "A", "B", "X", "Y", "Back", "Guide", "Start", "L3", "R3", "LB", "RB",
    "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "Misc1", "Paddle1", "Paddle2", "Paddle3", "Paddle4", "Touchpad"
];
string[] gpAxisNames = ["LeftX", "LeftY", "RightX", "RightY", "LTrigger", "RTrigger"];

while (true)
{
    SDL.SDL_PumpEvents();
    SDL.SDL_JoystickUpdate();
    SDL.SDL_GameControllerUpdate();

    if (!rumbleTested && sw.ElapsedMilliseconds > 4000)
    {
        rumbleTested = true;
        foreach (var p in pads.Where(p => p.Gamepad != IntPtr.Zero))
        {
            int r1 = SDL.SDL_GameControllerRumble(p.Gamepad, 40000, 40000, 1500);
            Console.WriteLine($"[{p.Name}] SDL_GameControllerRumble -> {r1} err={Err()}");
            if (r1 != 0)
            {
                int has = Native.SDL_JoystickHasRumble(p.Joystick);
                int r2 = Native.SDL_JoystickRumble(p.Joystick, 40000, 40000, 1500);
                Console.WriteLine($"[{p.Name}] JoystickHasRumble={has} SDL_JoystickRumble -> {r2} err={Err()}");
            }
        }
        if (pads.All(p => p.Gamepad == IntPtr.Zero))
        {
            Console.WriteLine("NingÃºn gamepad abierto: no se pudo probar vibraciÃ³n.");
        }
    }

    foreach (var (index, jhandle, gchandle, name) in pads)
    {
        int nb = Native.SDL_JoystickNumButtons(jhandle);
        if (!lastButtons.TryGetValue(index, out bool[]? buttons))
        {
            buttons = new bool[nb];
            lastButtons[index] = buttons;
        }
        for (int b = 0; b < nb; b++)
        {
            bool pressed = Native.SDL_JoystickGetButton(jhandle, b) != 0;
            if (pressed != buttons[b])
            {
                buttons[b] = pressed;
                Console.WriteLine($"[{name}] RAW boton b{b} = {(pressed ? "ON" : "off")}");
            }
        }

        int na = Native.SDL_JoystickNumAxes(jhandle);
        if (!lastAxes.TryGetValue(index, out short[]? axes))
        {
            axes = new short[na];
            lastAxes[index] = axes;
        }
        for (int a = 0; a < na; a++)
        {
            short v = Native.SDL_JoystickGetAxis(jhandle, a);
            if (v != axes[a] && (v > 28000 || v < -28000))
            {
                axes[a] = v;
                Console.WriteLine($"[{name}] RAW eje a{a} = {v}");
            }
            else if (v != axes[a])
            {
                axes[a] = v;
            }
        }

        int nh = Native.SDL_JoystickNumHats(jhandle);
        for (int h = 0; h < nh; h++)
        {
            int hat = Native.SDL_JoystickGetHat(jhandle, h);
            int prev = lastHats.TryGetValue((index, h), out int lh) ? lh : -1;
            if (hat != prev)
            {
                lastHats[(index, h)] = hat;
                Console.WriteLine($"[{name}] RAW hat h{h} = {hat}");
            }
        }

        if (gchandle != IntPtr.Zero)
        {
            if (!lastGpButtons.TryGetValue(index, out bool[]? gpb))
            {
                gpb = new bool[21];
                lastGpButtons[index] = gpb;
            }
            for (int b = 0; b < gpb.Length; b++)
            {
                if (SDL.SDL_GameControllerHasButton(gchandle, b) == 0) continue;
                bool pressed = SDL.SDL_GameControllerGetButton(gchandle, b) != 0;
                if (pressed != gpb[b])
                {
                    gpb[b] = pressed;
                    string bn = b < gpButtonNames.Length ? gpButtonNames[b] : $"btn{b}";
                    Console.WriteLine($"[{name}] GAMEPAD {bn} = {(pressed ? "ON" : "off")}");
                }
            }

            if (!lastGpAxes.TryGetValue(index, out short[]? gpa))
            {
                gpa = new short[6];
                lastGpAxes[index] = gpa;
            }
            for (int a = 0; a < gpa.Length; a++)
            {
                if (SDL.SDL_GameControllerHasAxis(gchandle, a) == 0) continue;
                short v = SDL.SDL_GameControllerGetAxis(gchandle, a);
                if (v != gpa[a] && (v > 28000 || v < -28000))
                {
                    gpa[a] = v;
                    string an = a < gpAxisNames.Length ? gpAxisNames[a] : $"axis{a}";
                    Console.WriteLine($"[{name}] GAMEPAD {an} = {v}");
                }
                else if (v != gpa[a])
                {
                    gpa[a] = v;
                }
            }
        }
    }
    Thread.Sleep(5);
}

static string Err()
{
    IntPtr p = SDL.SDL_GetError();
    return p != IntPtr.Zero ? Marshal.PtrToStringUTF8(p) ?? "?" : "";
}

static string GuidToString(SdlJoystickGuid guid)
{
    var sb = new StringBuilder(32);
    sb.Append(guid.Low.ToString("x16"));
    sb.Append(guid.High.ToString("x16"));
    return sb.ToString();
}

[StructLayout(LayoutKind.Sequential)]
struct SdlJoystickGuid
{
    public ulong Low;
    public ulong High;
}



static class SDL
{
    public const int SDL_INIT_JOYSTICK = 0x00000200;
    public const int SDL_INIT_GAMECONTROLLER = 0x00002000;

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_JoystickPath(IntPtr joystick);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_JoystickNameForIndex(int deviceIndex);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern SdlJoystickGuid SDL_JoystickGetDeviceGUID(int deviceIndex);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickGetGUIDString")]
    public static extern void SDL_JoystickGetGUIDString(SdlJoystickGuid guid, StringBuilder pszGUID, int cbGUID);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GameControllerMappingForIndex(int joystickIndex);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_Init(uint flags);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GetError();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_NumJoysticks();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_JoystickOpen(int deviceIndex);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_JoystickName(IntPtr joystick);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_JoystickUpdate();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_PumpEvents();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_IsGameController(int joystickIndex);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GameControllerAddMapping([MarshalAs(UnmanagedType.LPUTF8Str)] string mappingString);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GameControllerOpen(int joystickIndex);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_GameControllerUpdate();

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GameControllerHasButton(IntPtr gameController, int button);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern byte SDL_GameControllerGetButton(IntPtr gameController, int button);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GameControllerHasAxis(IntPtr gameController, int axis);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern short SDL_GameControllerGetAxis(IntPtr gameController, int axis);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GameControllerMapping(IntPtr gameController);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GameControllerRumble(IntPtr gameController, ushort lowFrequencyRumble, ushort highFrequencyRumble, uint durationMs);
}

static class Native
{
    public const int SDL_ENABLE = 1;

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickEventState(int state);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickNumButtons(IntPtr joystick);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickNumAxes(IntPtr joystick);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickNumHats(IntPtr joystick);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern byte SDL_JoystickGetButton(IntPtr joystick, int button);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern short SDL_JoystickGetAxis(IntPtr joystick, int axis);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickGetHat(IntPtr joystick, int hat);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickHasRumble(IntPtr joystick);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickRumble(IntPtr joystick, ushort lowFrequencyRumble, ushort highFrequencyRumble, uint durationMs);
}
