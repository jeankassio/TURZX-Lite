using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Threading;
using Microsoft.Win32;

namespace TurzxLite;

public sealed record SensorSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double? CpuTemperature,
    double? GpuTemperature,
    double? GpuPercent,
    string GpuName,
    string TemperatureHint)
{
    public string CpuName { get; init; } = string.Empty;
    public double? CpuClockMhz { get; init; }
    public double? CpuPowerWatts { get; init; }
    public double? GpuMemoryUsedGb { get; init; }
    public double? GpuMemoryTotalGb { get; init; }
    public double? GpuClockMhz { get; init; }
    public double? GpuMemoryClockMhz { get; init; }
    public double? GpuPowerWatts { get; init; }
    public double? GpuFanPercent { get; init; }
    public int LogicalProcessors { get; init; }
    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// Read-only, user-mode telemetry. Read from a worker, normally once a second.
/// Missing optional sensors are null; missing non-nullable measurements are NaN.
/// </summary>
public sealed class Telemetry : IDisposable
{
    private const int AidaBufferLimit = 64 * 1024;
    private static readonly string CpuModelName = ReadCpuName();
    private readonly object _gate = new();
    private readonly byte[] _aidaBuffer = new byte[AidaBufferLimit];
    private byte[]? _hwinfoBuffer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private SensorSnapshot? _snapshot;
    private TimeSpan _lastRead;
    private TimeSpan _nextNvmlAttempt;
    private NvmlReader? _nvml;
    private bool _disposed;
    private bool _hasCpuBaseline;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;

    public SensorSnapshot Read()
    {
        lock (_gate)
        {
            if (_disposed)
                return MissingSnapshot(Localization.T("Leitura de sensores encerrada."));

            var now = _clock.Elapsed;
            // Rendering media at a high frame rate never increases sensor polling.
            if (_snapshot is not null && now - _lastRead < TimeSpan.FromSeconds(1))
                return _snapshot;
            _lastRead = now;

            var cpu = ReadCpu();
            var (memoryPercent, memoryUsed, memoryTotal) = ReadMemory();
            var hwinfo = ReadHwinfo();
            var direct = DirectCpuSensors.Current;
            var cpuTemperature = direct.Temperature ?? hwinfo.CpuTemperature;
            var cpuLabel = hwinfo.CpuLabel;
            var cpuSource = "HWiNFO";
            if (!cpuTemperature.HasValue)
            {
                (cpuTemperature, cpuLabel) = ReadAidaCpuTemperature();
                cpuSource = "AIDA64";
            }
            if (direct.Temperature.HasValue) { cpuLabel = direct.Sensor; cpuSource = Localization.T("sensor integrado"); }

            if (_nvml is null && now >= _nextNvmlAttempt)
            {
                _nextNvmlAttempt = now + TimeSpan.FromSeconds(30);
                _nvml = NvmlReader.TryCreate();
            }

            var gpu = NvmlValues.Missing;
            var gpuName = string.Empty;
            if (_nvml is not null)
            {
                try
                {
                    gpu = _nvml.Read();
                    gpuName = _nvml.Name;
                }
                catch (Exception)
                {
                    // Unavailable native entry points and driver failures never escape to UI.
                    _nvml.Dispose();
                    _nvml = null;
                    _nextNvmlAttempt = now + TimeSpan.FromSeconds(30);
                }
            }

            var gpuSource = Localization.T("driver NVIDIA (NVML)");
            if (string.IsNullOrEmpty(gpuName))
            {
                gpu = gpu with { Temperature = hwinfo.GpuTemperature, Percent = hwinfo.GpuPercent };
                gpuName = hwinfo.GpuName;
                gpuSource = "HWiNFO";
            }
            var hint = cpuTemperature.HasValue
                ? Localization.Format("CPU: {0} ({1}).", cpuSource, cpuLabel)
                : Localization.Format("Temperatura da CPU indisponível. {0}", direct.Status);
            hint += gpu.Temperature.HasValue
                ? Localization.Format(" GPU: {0}.", gpuSource)
                : Localization.T(" Temperatura da GPU indisponível no driver NVIDIA/NVML e no HWiNFO.");

            return _snapshot = new SensorSnapshot(cpu, memoryPercent, memoryUsed,
                memoryTotal, cpuTemperature, gpu.Temperature, gpu.Percent, gpuName, hint)
            {
                CpuName = string.IsNullOrWhiteSpace(CpuModelName) ? direct.CpuName : CpuModelName,
                CpuClockMhz = direct.CpuClockMhz,
                CpuPowerWatts = direct.CpuPowerWatts,
                GpuMemoryUsedGb = gpu.MemoryUsedGb,
                GpuMemoryTotalGb = gpu.MemoryTotalGb,
                GpuClockMhz = gpu.ClockMhz,
                GpuMemoryClockMhz = gpu.MemoryClockMhz,
                GpuPowerWatts = gpu.PowerWatts,
                GpuFanPercent = gpu.FanPercent,
                LogicalProcessors = Environment.ProcessorCount,
                Uptime = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64))
            };
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cpu = registry.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false);
            var name = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
            return name.Length <= 240 ? name : name[..240];
        }
        catch (Exception) { return string.Empty; }
    }

    private double ReadCpu()
    {
        try
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
                return double.NaN;

            var idle = idleTime.Value;
            var kernel = kernelTime.Value;
            var user = userTime.Value;
            var value = double.NaN;
            if (_hasCpuBaseline && idle >= _lastIdle && kernel >= _lastKernel && user >= _lastUser)
            {
                var total = (double)(kernel - _lastKernel) + (user - _lastUser);
                if (total > 0)
                    value = Math.Clamp(100.0 * (1.0 - (idle - _lastIdle) / total), 0, 100);
            }
            _hasCpuBaseline = true;
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            return value;
        }
        catch (Exception)
        {
            _hasCpuBaseline = false;
            return double.NaN;
        }
    }

    private static (double Percent, double UsedGb, double TotalGb) ReadMemory()
    {
        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0 &&
                status.AvailablePhysical <= status.TotalPhysical)
            {
                var used = status.TotalPhysical - status.AvailablePhysical;
                const double gib = 1024.0 * 1024.0 * 1024.0;
                return (100.0 * used / status.TotalPhysical, used / gib, status.TotalPhysical / gib);
            }
        }
        catch (Exception) { }
        return (double.NaN, double.NaN, double.NaN);
    }

    private (double? Value, string Label) ReadAidaCpuTemperature()
    {
        try
        {
            // Open and release each time: retaining a handle would keep obsolete data
            // alive after AIDA64 closed. Never create a mapping on the producer's behalf.
            using var mapping = MemoryMappedFile.OpenExisting("AIDA64_SensorValues", MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var count = (int)Math.Min(view.Capacity, AidaBufferLimit);
            if (count <= 0)
                return (null, string.Empty);
            count = view.ReadArray(0, _aidaBuffer, 0, count);
            var length = Array.IndexOf(_aidaBuffer, (byte)0, 0, count);
            // Incomplete, oversized, or concurrently torn exports fail closed.
            if (length <= 0)
                return (null, string.Empty);

            var fragment = Encoding.UTF8.GetString(_aidaBuffer, 0, length);
            return ParseAidaCpuTemperature(fragment);
        }
        catch (Exception)
        {
            // AIDA64 is optional; absent permissions/mapping is normal.
            return (null, string.Empty);
        }
    }

    private static (double? Value, string Label) ParseAidaCpuTemperature(string fragment)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = AidaBufferLimit,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            using var input = new StringReader(fragment);
            using var reader = XmlReader.Create(input, settings);
            double? bestValue = null;
            var bestLabel = string.Empty;
            var bestPriority = int.MaxValue;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "temp")
                    continue;
                string id = string.Empty, label = string.Empty, text = string.Empty;
                using (var item = reader.ReadSubtree())
                {
                    item.Read();
                    while (item.Read())
                    {
                        if (item.NodeType != XmlNodeType.Element)
                            continue;
                        // ReadString leaves the reader on the end tag, so the next
                        // Read still visits the next sibling element.
                        switch (item.Name)
                        {
                            case "id": id = item.ReadString().Trim(); break;
                            case "label": label = item.ReadString().Trim(); break;
                            case "value": text = item.ReadString().Trim(); break;
                        }
                    }
                }
                var priority = id switch
                {
                    "TCPUPKG" => 0,
                    "TCPU1PKG" => 1,
                    "TCPU" => 2,
                    "TCPU1" => 3,
                    "TCPUDIO" => 4,
                    "TCPUTCTL" => 5,
                    _ => int.MaxValue
                };
                if (priority < bestPriority &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) &&
                    double.IsFinite(temperature) && temperature is >= -20 and <= 150)
                {
                    bestValue = temperature;
                    bestLabel = label.Length is > 0 and <= 100 ? label : id;
                    bestPriority = priority;
                }
            }
            return (bestValue, bestLabel);
        }
        catch (Exception)
        {
            // Torn or malformed XML is ignored, never repaired into invented readings.
            return (null, string.Empty);
        }
    }

    private sealed record HwinfoValues(double? CpuTemperature, string CpuLabel,
        double? GpuTemperature, double? GpuPercent, string GpuName)
    {
        public static readonly HwinfoValues Missing = new(null, string.Empty, null, null, string.Empty);
    }

    private HwinfoValues ReadHwinfo()
    {
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            // The publisher owns both objects. A bounded mutex acquisition avoids
            // mixing rows from two different updates without waiting on a busy sensor.
            using var mapping = MemoryMappedFile.OpenExisting("Global\\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            mutex = Mutex.OpenExisting("Global\\HWiNFO_SM2_MUTEX");
            try { acquired = mutex.WaitOne(2); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired || view.Capacity < 44) return HwinfoValues.Missing;

            const int limit = 8 * 1024 * 1024;
            var header = new byte[44];
            view.ReadArray(0, header, 0, header.Length);
            if (!TryHwinfoSize(header, view.Capacity, out var length)) return HwinfoValues.Missing;
            if (_hwinfoBuffer is null || _hwinfoBuffer.Length < length)
                _hwinfoBuffer = new byte[Math.Min(limit, Math.Max(length, 64 * 1024))];
            view.ReadArray(0, _hwinfoBuffer, 0, length);
            var after = new byte[44];
            view.ReadArray(0, after, 0, after.Length);
            if (!header.AsSpan().SequenceEqual(after) ||
                !header.AsSpan().SequenceEqual(_hwinfoBuffer.AsSpan(0, 44))) return HwinfoValues.Missing;
            // Release before decoding strings, keeping the producer's lock very short.
            mutex.ReleaseMutex();
            acquired = false;
            return ParseHwinfo(_hwinfoBuffer.AsSpan(0, length));
        }
        catch (Exception) { return HwinfoValues.Missing; }
        finally
        {
            if (acquired)
            {
                try { mutex?.ReleaseMutex(); } catch (Exception) { }
            }
            mutex?.Dispose();
        }
    }

    private static bool TryHwinfoSize(ReadOnlySpan<byte> data, long capacity, out int length)
    {
        length = 0;
        if (data.Length < 44 || BitConverter.ToUInt32(data[..4]) != 0x53695748 ||
            BitConverter.ToUInt32(data.Slice(4, 4)) is not (1 or 2)) return false;
        var poll = BitConverter.ToInt64(data.Slice(12, 8));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (poll > now + 5 || poll < now - 30) return false;
        var sensorOffset = BitConverter.ToUInt32(data.Slice(20, 4));
        var sensorStride = BitConverter.ToUInt32(data.Slice(24, 4));
        var sensorCount = BitConverter.ToUInt32(data.Slice(28, 4));
        var readingOffset = BitConverter.ToUInt32(data.Slice(32, 4));
        var readingStride = BitConverter.ToUInt32(data.Slice(36, 4));
        var readingCount = BitConverter.ToUInt32(data.Slice(40, 4));
        if (sensorOffset < 44 || readingOffset < 44 || sensorStride < 264 || readingStride < 316 ||
            sensorCount is 0 or > 1024 || readingCount is 0 or > 16384) return false;
        var sensorEnd = (ulong)sensorOffset + (ulong)sensorStride * sensorCount;
        var readingEnd = (ulong)readingOffset + (ulong)readingStride * readingCount;
        var end = Math.Max(sensorEnd, readingEnd);
        if (end > 8 * 1024 * 1024 || end > (ulong)capacity ||
            (sensorOffset < readingEnd && readingOffset < sensorEnd)) return false;
        length = (int)end;
        return true;
    }

    private static HwinfoValues ParseHwinfo(ReadOnlySpan<byte> data)
    {
        if (!TryHwinfoSize(data, data.Length, out _)) return HwinfoValues.Missing;
        var sensorOffset = (int)BitConverter.ToUInt32(data.Slice(20, 4));
        var sensorStride = (int)BitConverter.ToUInt32(data.Slice(24, 4));
        var sensorCount = BitConverter.ToUInt32(data.Slice(28, 4));
        var readingOffset = (int)BitConverter.ToUInt32(data.Slice(32, 4));
        var readingStride = (int)BitConverter.ToUInt32(data.Slice(36, 4));
        var readingCount = BitConverter.ToUInt32(data.Slice(40, 4));
        double? cpu = null, gpu = null, gpuLoad = null;
        var cpuPriority = int.MaxValue;
        var cpuLabel = string.Empty;
        var gpuName = string.Empty;
        uint? gpuSensorIndex = null;
        for (var i = 0; i < readingCount; i++)
        {
            var row = data.Slice(readingOffset + i * readingStride, 316);
            var type = BitConverter.ToUInt32(row[..4]);
            if (type is not (1 or 7)) continue; // temperature or utilization only
            var index = BitConverter.ToUInt32(row.Slice(4, 4));
            if (index >= sensorCount) continue;
            var sensor = data.Slice(sensorOffset + (int)index * sensorStride, 264);
            var name = HwinfoString(sensor.Slice(8, 128));
            var label = HwinfoString(row.Slice(12, 128));
            var unit = HwinfoString(row.Slice(268, 16));
            var value = BitConverter.ToDouble(row.Slice(284, 8));
            if (!double.IsFinite(value)) continue;
            if (type == 1 && unit is not ("°C" or "C")) continue;
            if (type == 7 && unit != "%") continue;
            if (type == 1 && value is >= -20 and <= 150 && name.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
            {
                var priority = label.ToUpperInvariant() switch
                {
                    "CPU PACKAGE" => 0,
                    "CPU (TCTL/TDIE)" => 1,
                    "CPU DIE (AVERAGE)" => 2,
                    "CPU (TDIE)" => 3,
                    _ => int.MaxValue
                };
                if (priority < cpuPriority)
                {
                    cpu = value;
                    cpuLabel = label;
                    cpuPriority = priority;
                }
            }
            if (!name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)) continue;
            if (gpuSensorIndex.HasValue && index != gpuSensorIndex.Value) continue;
            if (type == 1 && label.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase) && value is >= -20 and <= 150)
            {
                gpu = value;
                gpuName = name;
                gpuSensorIndex = index;
            }
            else if (type == 7 && label.Equals("GPU Core Load", StringComparison.OrdinalIgnoreCase) && value is >= 0 and <= 100)
            {
                gpuLoad = value;
                gpuName = name;
                gpuSensorIndex = index;
            }
        }
        return new HwinfoValues(cpu, cpuLabel, gpu, gpuLoad, gpuName);
    }

    private static string HwinfoString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }

    private static SensorSnapshot MissingSnapshot(string hint) => new(
        double.NaN, double.NaN, double.NaN, double.NaN, null, null, null, string.Empty, hint);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _nvml?.Dispose();
            _nvml = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private sealed record NvmlValues(double? Temperature, double? Percent, double? MemoryUsedGb,
        double? MemoryTotalGb, double? ClockMhz, double? MemoryClockMhz, double? PowerWatts, double? FanPercent)
    {
        public static readonly NvmlValues Missing = new(null, null, null, null, null, null, null, null);
    }

    private sealed class NvmlReader : IDisposable
    {
        private IntPtr _library;
        private readonly IntPtr _device;
        private readonly ShutdownDelegate _shutdown;
        private readonly TemperatureDelegate? _getTemperature;
        private readonly UtilizationDelegate? _getUtilization;
        private readonly MemoryInfoDelegate? _getMemoryInfo;
        private readonly ClockInfoDelegate? _getClockInfo;
        private readonly UnsignedMetricDelegate? _getPowerUsage;
        private readonly UnsignedMetricDelegate? _getFanSpeed;
        public string Name { get; }

        private NvmlReader(IntPtr library, IntPtr device, ShutdownDelegate shutdown,
            TemperatureDelegate? temperature, UtilizationDelegate? utilization,
            MemoryInfoDelegate? memoryInfo, ClockInfoDelegate? clockInfo,
            UnsignedMetricDelegate? powerUsage, UnsignedMetricDelegate? fanSpeed, string name)
        {
            _library = library;
            _device = device;
            _shutdown = shutdown;
            _getTemperature = temperature;
            _getUtilization = utilization;
            _getMemoryInfo = memoryInfo;
            _getClockInfo = clockInfo;
            _getPowerUsage = powerUsage;
            _getFanSpeed = fanSpeed;
            Name = name;
        }

        public static NvmlReader? TryCreate()
        {
            foreach (var path in LibraryPaths())
            {
                IntPtr library = IntPtr.Zero;
                ShutdownDelegate? shutdown = null;
                var initialized = false;
                try
                {
                    // Absolute paths avoid searching the application's directory or PATH.
                    if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out library)) continue;
                    var init = Required<InitDelegate>(library, "nvmlInit_v2");
                    shutdown = Required<ShutdownDelegate>(library, "nvmlShutdown");
                    var countDevices = Required<CountDelegate>(library, "nvmlDeviceGetCount_v2");
                    var getDevice = Required<DeviceDelegate>(library, "nvmlDeviceGetHandleByIndex_v2");
                    var temperature = Optional<TemperatureDelegate>(library, "nvmlDeviceGetTemperature");
                    var utilization = Optional<UtilizationDelegate>(library, "nvmlDeviceGetUtilizationRates");
                    var memoryInfo = Optional<MemoryInfoDelegate>(library, "nvmlDeviceGetMemoryInfo");
                    var clockInfo = Optional<ClockInfoDelegate>(library, "nvmlDeviceGetClockInfo");
                    var powerUsage = Optional<UnsignedMetricDelegate>(library, "nvmlDeviceGetPowerUsage");
                    var fanSpeed = Optional<UnsignedMetricDelegate>(library, "nvmlDeviceGetFanSpeed");
                    if (init() != 0) continue;
                    initialized = true;
                    if (countDevices(out var count) != 0) continue;
                    for (uint index = 0; index < Math.Min(count, 16u); index++)
                    {
                        if (getDevice(index, out var device) != 0 || device == IntPtr.Zero) continue;
                        var name = "NVIDIA GPU";
                        var getName = Optional<NameDelegate>(library, "nvmlDeviceGetName");
                        if (getName is not null)
                        {
                            var buffer = new StringBuilder(128);
                            if (getName(device, buffer, (uint)buffer.Capacity) == 0 && buffer.Length > 0)
                                name = buffer.ToString();
                        }
                        var result = new NvmlReader(library, device, shutdown, temperature, utilization,
                            memoryInfo, clockInfo, powerUsage, fanSpeed, name);
                        library = IntPtr.Zero; // ownership passes to the reader
                        return result;
                    }
                }
                catch (Exception) { }
                finally
                {
                    if (library != IntPtr.Zero)
                    {
                        if (initialized)
                        {
                            try { shutdown?.Invoke(); } catch (Exception) { }
                        }
                        NativeLibrary.Free(library);
                    }
                }
            }
            return null;
        }

        public NvmlValues Read()
        {
            double? temperature = null, percent = null;
            if (_getTemperature is not null && _getTemperature(_device, 0, out var degrees) == 0 && degrees <= 150)
                temperature = degrees;
            if (_getUtilization is not null && _getUtilization(_device, out var utilization) == 0 && utilization.Gpu <= 100)
                percent = utilization.Gpu;
            double? usedGb = null, totalGb = null, clockMhz = null, memoryClockMhz = null, watts = null, fanPercent = null;
            if (_getMemoryInfo is not null && _getMemoryInfo(_device, out var memory) == 0 &&
                memory.Total is > 0 and <= 16UL * 1024 * 1024 * 1024 * 1024 &&
                memory.Used <= memory.Total && memory.Free <= memory.Total)
            {
                const double gib = 1024.0 * 1024.0 * 1024.0;
                usedGb = memory.Used / gib;
                totalGb = memory.Total / gib;
            }
            // NVML clock enums: graphics=0, memory=2. Values already are MHz.
            if (_getClockInfo is not null)
            {
                if (_getClockInfo(_device, 0, out var graphicsClock) == 0 && graphicsClock <= 100_000) clockMhz = graphicsClock;
                if (_getClockInfo(_device, 2, out var memoryClock) == 0 && memoryClock <= 100_000) memoryClockMhz = memoryClock;
            }
            if (_getPowerUsage is not null && _getPowerUsage(_device, out var milliWatts) == 0 && milliWatts <= 5_000_000)
                watts = milliWatts / 1000.0;
            // NVML reports intended fan duty, not RPM; valid readings can exceed 100%.
            if (_getFanSpeed is not null && _getFanSpeed(_device, out var fan) == 0 && fan <= 1000) fanPercent = fan;
            return new NvmlValues(temperature, percent, usedGb, totalGb, clockMhz, memoryClockMhz, watts, fanPercent);
        }

        private static IEnumerable<string> LibraryPaths()
        {
            yield return Path.Combine(Environment.SystemDirectory, "nvml.dll");
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
            var nativeProgramFiles = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrWhiteSpace(nativeProgramFiles) &&
                !string.Equals(nativeProgramFiles, programFiles, StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(nativeProgramFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
        }

        private static T Required<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        private static T? Optional<T>(IntPtr library, string name) where T : Delegate =>
            NativeLibrary.TryGetExport(library, name, out var address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

        public void Dispose()
        {
            if (_library == IntPtr.Zero) return;
            try { _shutdown(); } catch (Exception) { }
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { public uint Gpu; public uint Memory; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInfo { public ulong Total; public ulong Free; public ulong Used; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ShutdownDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CountDelegate(out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DeviceDelegate(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate int NameDelegate(IntPtr device, StringBuilder name, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TemperatureDelegate(IntPtr device, uint sensorType, out uint temperature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UtilizationDelegate(IntPtr device, out Utilization utilization);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int MemoryInfoDelegate(IntPtr device, out MemoryInfo memory);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ClockInfoDelegate(IntPtr device, uint clockType, out uint clockMhz);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UnsignedMetricDelegate(IntPtr device, out uint value);
    }
}
