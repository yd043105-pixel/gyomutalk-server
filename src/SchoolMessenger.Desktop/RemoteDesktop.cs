using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SchoolMessenger.Desktop;

public static class RemoteDesktop
{
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformation(nint handle, int index, System.Text.StringBuilder text, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Key; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    static readonly HashSet<int> keys = [];
    internal static int InputSize => Marshal.SizeOf<INPUT>();
    static readonly HashSet<string> buttons = [];
    static void CheckDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) throw new InvalidOperationException("잠금 또는 보안 화면이어서 지원을 종료합니다.");
        try
        {
            var name = new System.Text.StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, 512, out _) || !name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("보안 화면에서는 원격 지원을 사용할 수 없습니다.");
        }
        finally { CloseDesktop(desktop); }
    }
    public static byte[] Capture()
    {
        CheckDesktop();
        var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("화면을 공유할 수 없습니다.");
        using var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(full)) graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height));
        var scale = Math.Min(1d, 1280d / Math.Max(width, height));
        using var image = new Bitmap(full, Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        foreach (var quality in new long[] { 45, 25, 10 })
        {
            using var stream = new MemoryStream(); using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality); image.Save(stream, codec, parameters);
            if (stream.Length <= 262144) return stream.ToArray();
        }
        throw new InvalidOperationException("화면 데이터가 너무 큽니다. 화면 해상도를 낮춰주세요.");
    }
    static void Inject(INPUT input)
    { if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) throw new InvalidOperationException("Windows가 입력을 허용하지 않았습니다. 관리자 권한 화면은 직접 조작하세요."); }
    public static void Apply(RemoteInput input)
    {
        if (input.Kind != "release") CheckDesktop();
        if (input.Kind == "text")
        {
            foreach (var ch in input.Text!)
            {
                Inject(new INPUT { Type = 1, Data = new InputUnion { Key = new KEYBDINPUT { Scan = ch, Flags = 4 } } });
                Inject(new INPUT { Type = 1, Data = new InputUnion { Key = new KEYBDINPUT { Scan = ch, Flags = 4 | 2 } } });
            }
            return;
        }
        if (input.Kind == "key")
        {
            Inject(new INPUT { Type = 1, Data = new InputUnion { Key = new KEYBDINPUT { VirtualKey = (ushort)input.Value, Flags = input.Down ? 0u : 2u } } });
            if (input.Down) keys.Add(input.Value); else keys.Remove(input.Value); return;
        }
        if (input.Kind == "release") { Release(); return; }
        var x = (int)(input.X * Math.Max(0, GetSystemMetrics(0) - 1)); var y = (int)(input.Y * Math.Max(0, GetSystemMetrics(1) - 1));
        // Normalize primary-monitor coordinates against the entire virtual desktop.
        var virtualX = GetSystemMetrics(76); var virtualY = GetSystemMetrics(77); var virtualWidth = GetSystemMetrics(78); var virtualHeight = GetSystemMetrics(79);
        Inject(new INPUT { Data = new InputUnion { Mouse = new MOUSEINPUT { X = (int)((x - virtualX) * 65535d / Math.Max(1, virtualWidth - 1)), Y = (int)((y - virtualY) * 65535d / Math.Max(1, virtualHeight - 1)), Flags = 0x8000 | 0x4000 | 1 } } });
        uint flags = input.Kind switch { "left" => input.Down ? 2u : 4u, "right" => input.Down ? 8u : 16u, "wheel" => 0x800u, _ => 0 };
        if (flags != 0) Inject(new INPUT { Data = new InputUnion { Mouse = new MOUSEINPUT { MouseData = unchecked((uint)input.Value), Flags = flags } } });
        if (input.Kind is "left" or "right") { if (input.Down) buttons.Add(input.Kind); else buttons.Remove(input.Kind); }
    }
    public static void Release()
    {
        foreach (var key in keys.ToArray()) { try { Apply(new RemoteInput("key", Value: key, Down: false)); } catch { } } keys.Clear();
        foreach (var button in buttons.ToArray()) { try { Inject(new INPUT { Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = button == "left" ? 4u : 16u } } }); } catch { } } buttons.Clear();
    }
    public static bool ValidFrame(byte[] jpeg)
    {
        if (jpeg.Length is < 4 or > 262144 || jpeg[0] != 255 || jpeg[1] != 216) return false;
        var offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset++] != 255) return false;
            while (offset < jpeg.Length && jpeg[offset] == 255) offset++;
            if (offset >= jpeg.Length) return false; var marker = jpeg[offset++];
            if (marker is 0xd9 or 0xda) return false;
            var length = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (length < 2 || offset + length > jpeg.Length) return false;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8) return false;
                var height = (jpeg[offset + 3] << 8) | jpeg[offset + 4]; var width = (jpeg[offset + 5] << 8) | jpeg[offset + 6];
                return width is > 0 and <= 1280 && height is > 0 and <= 1280;
            }
            offset += length;
        }
        return false;
    }
}
