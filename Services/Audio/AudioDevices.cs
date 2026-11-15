using System.Runtime.InteropServices;

namespace RadioDeck.Services.Audio;

/// <summary>Windows wave device names. Devices are stored by name because their numbers change when hardware is plugged.</summary>
public static class AudioDevices
{
    /// <summary>Device number that lets Windows choose its default device.</summary>
    public const int DefaultDevice = -1;

    public static IReadOnlyList<string> GetInputNames()
    {
        var names = new List<string>();
        try
        {
            var count = waveInGetNumDevs();
            for (var i = 0; i < count; i++)
                if (waveInGetDevCaps((IntPtr)i, out var caps, Marshal.SizeOf<WaveInCaps>()) == 0)
                    names.Add(caps.Name ?? "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return names;
    }

    public static IReadOnlyList<string> GetOutputNames()
    {
        var names = new List<string>();
        try
        {
            var count = waveOutGetNumDevs();
            for (var i = 0; i < count; i++)
                if (waveOutGetDevCaps((IntPtr)i, out var caps, Marshal.SizeOf<WaveOutCaps>()) == 0)
                    names.Add(caps.Name ?? "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return names;
    }

    /// <summary>Device number for a stored name; the default device when the name is empty or no longer present.</summary>
    public static int ResolveInput(string? name, out bool found) => Resolve(GetInputNames(), name, out found);

    public static int ResolveOutput(string? name, out bool found) => Resolve(GetOutputNames(), name, out found);

    private static int Resolve(IReadOnlyList<string> names, string? name, out bool found)
    {
        found = true;
        if (string.IsNullOrWhiteSpace(name)) return DefaultDevice;
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        found = false;
        return DefaultDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort Mid, Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort Mid, Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int waveInGetDevCaps(IntPtr deviceId, out WaveInCaps caps, int size);
    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCaps(IntPtr deviceId, out WaveOutCaps caps, int size);
}
