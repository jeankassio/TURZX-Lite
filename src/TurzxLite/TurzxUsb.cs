using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TurzxLite;

public sealed record DisplayDevice(string Path, string Name, string Serial)
{
    public override string ToString() => $"{Name} ({Serial})";
}

/// <summary>
/// Minimal, serialized WinUSB transport for the TURZX 1CBE:0028 480 x 480 display.
/// It queries firmware and sends display-mode, brightness, image and H.264 stream commands.
/// No firmware updates, USB resets or cooling-control commands are implemented.
/// </summary>
public sealed class TurzxUsb : IDisposable
{
    private const string HardwareId = "vid_1cbe&pid_0028";
    private const byte OutPipe = 0x01;
    private const byte InPipe = 0x81;
    private const uint TransferTimeout = 1500;
    private const int TransactionDeadline = 2000;
    private const uint PipeTransferTimeoutPolicy = 3;
    private const int MaxJpegBytes = 1024 * 1024;
    private static readonly Guid UsbDeviceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly Guid WinUsbGuid = new("88BAE032-5A81-49F0-BC3D-A4FF138216D6");
    private static readonly byte[] DesKey = "slv3tuzx"u8.ToArray();
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly uint _timestampBase = unchecked((uint)(DateTime.UtcNow - DateTime.Today.AddDays(-1)).TotalMilliseconds);
    private uint _lastTimestamp;
    private SafeFileHandle? _file;
    private IntPtr _usb;
    private bool _disposed;
    private int _videoChunkBytes;

    public string Firmware { get; private set; } = "";

    public static IReadOnlyList<DisplayDevice> Enumerate()
    {
        var devices = new Dictionary<string, DisplayDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (Guid interfaceGuid in RegisteredInterfaceGuids())
        {
            Guid guid = interfaceGuid;
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) continue;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break; // ERROR_NO_MORE_ITEMS
                        throw new Win32Exception(error, "Falha ao enumerar as interfaces USB.");
                    }
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint bytes, IntPtr.Zero);
                    if (bytes < 6 || bytes > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)bytes);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, bytes, out _, IntPtr.Zero))
                            continue;
                        string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path is null || !path.Contains(HardwareId, StringComparison.OrdinalIgnoreCase)) continue;
                        string[] parts = path.Split('#');
                        string serial = parts.Length > 2 ? parts[2] : "desconhecido";
                        // A device can appear under both registered GUIDs. Retain one path per serial.
                        devices.TryAdd(serial, new DisplayDevice(path, "TURZX redondo 480 × 480", serial));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }
        return devices.Values.OrderBy(device => device.Serial, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<Guid> RegisteredInterfaceGuids()
    {
        // Read existing registration only; never install or modify a driver.
        var guids = new HashSet<Guid>();
        using RegistryKey? hardware = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1CBE&PID_0028");
        if (hardware is not null)
        {
            foreach (string instance in hardware.GetSubKeyNames())
            {
                using RegistryKey? parameters = hardware.OpenSubKey(instance + @"\Device Parameters");
                foreach (string name in new[] { "DeviceInterfaceGUID", "DeviceInterfaceGUIDs" })
                {
                    object? value = parameters?.GetValue(name);
                    IEnumerable<string> values = value is string[] many ? many : value is string one ? new[] { one } : Array.Empty<string>();
                    foreach (string candidate in values)
                        if (Guid.TryParse(candidate, out Guid guid)) guids.Add(guid);
                }
            }
        }
        guids.Add(UsbDeviceGuid);
        guids.Add(WinUsbGuid);
        return guids;
    }

    public static bool OriginalIsRunning()
    {
        foreach (Process process in Process.GetProcessesByName("TURZX"))
        {
            using (process)
                if (!process.HasExited) return true;
        }
        return false;
    }

    /// <summary>Open, send only command 10 (GetVer), read the reply, then close.</summary>
    public static string ProbeFirmware(DisplayDevice device)
    {
        using var connection = new TurzxUsb();
        lock (connection._gate)
        {
            connection.Open(device);
            connection.Firmware = connection.QueryFirmware();
            return connection.Firmware;
        }
    }

    public void Connect(DisplayDevice device, int brightness)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(brightness, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(brightness, 100);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_usb != IntPtr.Zero) throw new InvalidOperationException("O display já está conectado.");
            try
            {
                Open(device);
                Firmware = QueryFirmware();
                DateTime now = DateTime.Now;
                byte[] mode = [(byte)(now.Year >> 8), (byte)now.Year, (byte)now.Month, (byte)now.Day,
                    (byte)now.Hour, (byte)now.Minute, (byte)now.Second, 2];
                Exchange(51, mode); // Host-rendered display mode, with the local clock.
                Exchange(52, [0]); // Match the original theme-start setup before full frames.
                Exchange(14, [(byte)brightness]);
            }
            catch
            {
                CloseHandles();
                throw;
            }
        }
    }

    private void Open(DisplayDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(device);
        if (!device.Path.Contains(HardwareId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Este controlador aceita somente TURZX USB 1CBE:0028.", nameof(device));
        if (OriginalIsRunning())
            throw new InvalidOperationException("Feche o TURZX original, inclusive o ícone ao lado do relógio, antes de conectar.");
        _file = Native.CreateFile(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (_file.IsInvalid) throw NativeError("Não foi possível abrir o display USB");
        try
        {
            if (!Native.WinUsb_Initialize(_file, out _usb)) throw NativeError("Não foi possível iniciar o WinUSB");
            if (!Native.WinUsb_QueryInterfaceSettings(_usb, 0, out InterfaceDescriptor descriptor))
                throw NativeError("Não foi possível ler a interface USB");
            bool hasOut = false, hasIn = false;
            for (byte index = 0; index < descriptor.Endpoints; index++)
            {
                if (!Native.WinUsb_QueryPipe(_usb, 0, index, out PipeInformation pipe))
                    throw NativeError("Não foi possível ler os endpoints USB");
                hasOut |= pipe.Id == OutPipe && pipe.Type is 2 or 3;
                hasIn |= pipe.Id == InPipe && pipe.Type is 2 or 3;
            }
            if (!hasOut || !hasIn) throw new IOException("O display não possui os endpoints 0x01 e 0x81 esperados.");
            uint timeout = TransferTimeout;
            if (!Native.WinUsb_SetPipePolicy(_usb, OutPipe, PipeTransferTimeoutPolicy, 4, ref timeout) ||
                !Native.WinUsb_SetPipePolicy(_usb, InPipe, PipeTransferTimeoutPolicy, 4, ref timeout))
                throw NativeError("Não foi possível configurar o limite de tempo do USB");
        }
        catch
        {
            CloseHandles();
            throw;
        }
    }

    private string QueryFirmware()
    {
        byte[] response = Exchange(10, []);
        if (response.Length < 40) throw new IOException("Resposta incompleta à consulta de firmware.");
        string firmware = Encoding.UTF8.GetString(response, 8, 32).TrimEnd('\0');
        if (!firmware.StartsWith("turzx_", StringComparison.OrdinalIgnoreCase))
            throw new IOException("O dispositivo respondeu com um firmware TURZX desconhecido.");
        return firmware;
    }

    public void SendJpeg(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length < 4 || jpeg.Length > MaxJpegBytes || jpeg[0] != 0xFF || jpeg[1] != 0xD8 ||
            jpeg[^2] != 0xFF || jpeg[^1] != 0xD9)
            throw new ArgumentException("O quadro precisa ser um JPEG completo de até 1 MiB.", nameof(jpeg));
        lock (_gate) Exchange(101, jpeg);
    }

    public void SetBrightness(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        lock (_gate) Exchange(14, [(byte)percent]);
    }

    /// <summary>Set the native decoder's FPS and query its maximum stream chunk size.</summary>
    public int BeginVideo(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 60);
        lock (_gate)
        {
            // Original 꾆.ꬁ(byte), lines 3798-3818: command 15, FPS at header[8].
            Exchange(15, [(byte)fps]);
            byte[] reply = Exchange(17, []);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8, 4));
            if (length == 0) length = 202752; // Original 꾆.ꭚ(), lines 1219-1222.
            if (length > MaxJpegBytes) throw new IOException("A tela informou um tamanho de bloco H.264 inválido.");
            return _videoChunkBytes = checked((int)length);
        }
    }

    /// <summary>Send one Annex-B chunk. The returned byte is decoder queue depth, not HTTP/status 200.</summary>
    public int SendVideoChunk(ReadOnlySpan<byte> data, bool endOfFile = false)
    {
        lock (_gate)
        {
            if (_videoChunkBytes <= 0) throw new InvalidOperationException("Inicie o vídeo antes de enviar os blocos.");
            if (data.IsEmpty || data.Length > _videoChunkBytes)
                throw new ArgumentException("Bloco H.264 vazio ou maior que o limite informado pela tela.", nameof(data));
            return Exchange(121, data, endOfFile)[8];
        }
    }

    public int GetVideoQueueDepth()
    {
        lock (_gate) return Exchange(122, [])[8];
    }

    public void StopVideo()
    {
        lock (_gate)
        {
            _videoChunkBytes = 0;
            if (!_disposed && _usb != IntPtr.Zero) Exchange(123, []);
        }
    }

    /// <summary>Send an alpha PNG above native video; this opcode accepts PNG, not JPEG.</summary>
    public void SendOverlayPng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 33 || png.Length > MaxJpegBytes ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != 480 ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != 480)
            throw new ArgumentException("A sobreposição precisa ser um PNG de 480 × 480 e até 1 MiB.", nameof(png));
        lock (_gate) Exchange(102, png);
    }

    private byte[] Exchange(byte command, ReadOnlySpan<byte> payload, bool endOfFile = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_usb == IntPtr.Zero) throw new InvalidOperationException("Conecte o display primeiro.");
        uint timestamp = unchecked(_timestampBase + (uint)_clock.ElapsedMilliseconds);
        if (timestamp == _lastTimestamp) timestamp++;
        _lastTimestamp = timestamp;
        byte[] packet = BuildPacket(command, payload, timestamp, endOfFile);
        try
        {
            var elapsed = Stopwatch.StartNew();
            // Write the command exactly once. Empty USB packets and residual replies may precede its ACK.
            if (!Native.WinUsb_WritePipe(_usb, OutPipe, packet, (uint)packet.Length, out uint written, IntPtr.Zero))
                throw NativeError("Falha ou tempo esgotado ao enviar dados ao display");
            if (written != packet.Length) throw new IOException($"Envio USB incompleto: {written}/{packet.Length} bytes.");
            byte[] reply = ReadMatchingReply(command, timeout => ReadNative(timeout) ?? [], elapsed);
            DrainResidualReplies(elapsed);
            return reply;
        }
        catch
        {
            // A failed transaction stops this connection instead of accumulating frames or resending.
            CloseHandles();
            throw;
        }
    }

    // Shared with offline tests so zero-length packets and stale replies can be exercised without USB I/O.
    internal static byte[] ReadMatchingReply(byte command, Func<uint, byte[]> read, Stopwatch elapsed,
        int deadlineMilliseconds = TransactionDeadline)
    {
        int minimumLength = command switch { 10 => 40, 17 => 12, 121 or 122 => 9, _ => 8 };
        int lastLength = 0;
        int lastCommand = -1;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            long remaining = deadlineMilliseconds - elapsed.ElapsedMilliseconds;
            if (remaining <= 0) break;
            byte[] reply = read((uint)Math.Min(TransferTimeout, remaining));
            lastLength = reply.Length;
            lastCommand = reply.Length == 0 ? -1 : reply[0];
            if (reply.Length >= minimumLength && reply[0] == command) return reply;
            // Do not resend the command: consume ZLPs or old command replies within the original deadline.
        }
        throw new IOException($"O display não confirmou o comando {command} dentro do limite de {deadlineMilliseconds} ms " +
            $"(última resposta: {lastLength} bytes, comando {lastCommand}). Reconecte pelo aplicativo.");
    }

    private byte[]? ReadNative(uint timeout)
    {
        if (!Native.WinUsb_SetPipePolicy(_usb, InPipe, PipeTransferTimeoutPolicy, 4, ref timeout))
            throw NativeError("Não foi possível configurar o limite de tempo da resposta USB");
        byte[] reply = new byte[512];
        if (!Native.WinUsb_ReadPipe(_usb, InPipe, reply, (uint)reply.Length, out uint read, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 121 or 1460) return null; // Transfer deadline; the caller owns the total deadline.
            throw new Win32Exception(error, "Falha ao aguardar a resposta do display");
        }
        return reply.AsSpan(0, checked((int)read)).ToArray();
    }

    private void DrainResidualReplies(Stopwatch elapsed)
    {
        // Original 꾆.Read calls LibUsbDotNet.ReadFlush, which drains with 10-ms reads after every ACK.
        // Bound that cleanup to 8 reads / 40 ms, also inside the transaction's 2-second total deadline.
        long stopAt = Math.Min(TransactionDeadline, elapsed.ElapsedMilliseconds + 40);
        for (int count = 0; count < 8; count++)
        {
            long remaining = stopAt - elapsed.ElapsedMilliseconds;
            if (remaining <= 0 || ReadNative((uint)Math.Min(10, remaining)) is null) break;
        }
    }

    /// <summary>Build the documented 500-byte DES-CBC header, 512-byte envelope and optional JPEG payload.</summary>
    public static byte[] BuildPacket(byte command, ReadOnlySpan<byte> payload, uint timestamp, bool endOfFile = false)
    {
        bool bulkPayload = command is 101 or 102 or 121;
        int expectedLength = command switch { 10 or 17 or 122 or 123 => 0, 14 or 15 => 1, 51 => 8, 52 => 1, 101 or 102 or 121 => payload.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(command), "Comando fora do conjunto mínimo permitido.") };
        if (payload.Length != expectedLength || (bulkPayload && payload.Length > MaxJpegBytes) || (endOfFile && command != 121))
            throw new ArgumentException("Tamanho de dados inválido para o comando.", nameof(payload));
        byte[] header = new byte[500];
        header[0] = command;
        header[2] = 0x1A;
        header[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), timestamp);
        if (bulkPayload) BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)payload.Length);
        else payload.CopyTo(header.AsSpan(8));
        if (command == 121 && endOfFile) header[12] = 1;
        using DES des = DES.Create();
        des.Key = DesKey;
        des.IV = DesKey;
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = des.CreateEncryptor();
        byte[] encrypted = encryptor.TransformFinalBlock(header, 0, header.Length);
        byte[] packet = new byte[512 + (bulkPayload ? payload.Length : 0)];
        encrypted.CopyTo(packet, 0);
        packet[510] = 0xA1;
        packet[511] = 0x1A;
        if (bulkPayload) payload.CopyTo(packet.AsSpan(512));
        return packet;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Leave the last displayed image intact; closing does not send additional device commands.
            CloseHandles();
        }
    }

    private void CloseHandles()
    {
        _videoChunkBytes = 0;
        if (_usb != IntPtr.Zero)
        {
            Native.WinUsb_Free(_usb);
            _usb = IntPtr.Zero;
        }
        _file?.Dispose();
        _file = null;
    }

    private static Win32Exception NativeError(string message) => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData
    {
        public uint Size;
        public Guid Guid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct InterfaceDescriptor
    {
        public byte Length, DescriptorType, InterfaceNumber, AlternateSetting, Endpoints,
            InterfaceClass, InterfaceSubClass, InterfaceProtocol, Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PipeInformation
    {
        public int Type;
        public byte Id;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Free(IntPtr usb);
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_QueryInterfaceSettings(IntPtr usb, byte alternate, out InterfaceDescriptor descriptor);
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_QueryPipe(IntPtr usb, byte alternate, byte index, out PipeInformation pipe);
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint length, ref uint value);
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    }
}
