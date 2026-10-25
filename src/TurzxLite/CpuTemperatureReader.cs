using System.Diagnostics;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace TurzxLite;

/// <summary>
/// Direct CPU temperatures for the elevated sensor helper only. Uses the official
/// PawnIO driver through LibreHardwareMonitor 0.9.6; never installs a driver.
/// </summary>
public sealed class CpuTemperatureReader : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Computer? _computer;
    private IHardware[] _cpus = [];
    private TimeSpan _lastRead = TimeSpan.MinValue;
    private TimeSpan _nextOpenAttempt;
    private double? _temperature;
    private string _status = Localization.T("Aguardando leitura direta da CPU.");
    private string _sensorName = string.Empty;
    private string _cpuName = string.Empty;
    private double? _clockMhz;
    private double? _powerWatts;
    private bool _disposed;

    public string Status { get { lock (_gate) return _status; } }
    public string SensorName { get { lock (_gate) return _sensorName; } }
    public string CpuName { get { lock (_gate) return _cpuName; } }
    public double? ClockMhz { get { lock (_gate) return _clockMhz; } }
    public double? PowerWatts { get { lock (_gate) return _powerWatts; } }

    public double? Read()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            var now = _clock.Elapsed;
            if (_lastRead != TimeSpan.MinValue && now - _lastRead < TimeSpan.FromSeconds(1))
                return _temperature;
            _lastRead = now;

            if (_computer is null)
            {
                if (now < _nextOpenAttempt) return null;
                _nextOpenAttempt = now + TimeSpan.FromSeconds(30);
                if (!TryOpen()) return null;
            }

            try
            {
                var readings = new List<(string Name, double? Value)>();
                var performance = new List<(SensorType Type, string Name, double? Value)>();
                foreach (var cpu in _cpus)
                {
                    cpu.Update();
                    foreach (var sensor in cpu.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature)
                            readings.Add((sensor.Name, sensor.Value));
                        else if (sensor.SensorType is SensorType.Clock or SensorType.Power)
                            performance.Add((sensor.SensorType, sensor.Name, sensor.Value));
                    }
                }
                var result = SelectTemperature(readings);
                _temperature = result.Value;
                _sensorName = result.Name;
                var metrics = SelectPerformanceMetrics(performance);
                _clockMhz = metrics.ClockMhz;
                _powerWatts = metrics.PowerWatts;
                _status = _temperature.HasValue
                    ? Localization.Format("CPU: {0} · leitura direta por PawnIO.", result.Name)
                    : Localization.T("O driver PawnIO não retornou uma temperatura válida da CPU. Verifique se o driver está ativo e o auxiliar possui permissão de administrador.");
                return _temperature;
            }
            catch (Exception ex)
            {
                _temperature = null;
                _sensorName = string.Empty;
                _clockMhz = null;
                _powerWatts = null;
                _status = Localization.Format("Leitura direta da CPU indisponível: {0}", ShortMessage(ex));
                CloseComputer();
                _nextOpenAttempt = now + TimeSpan.FromSeconds(30);
                return null;
            }
        }
    }

    private bool TryOpen()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                _status = Localization.T("A leitura direta da CPU exige o auxiliar de sensores como administrador.");
                return false;
            }

            // LHM's PawnIo.Version is cached by a type initializer. Read the actual
            // installation key here so a missing installation can be retried safely.
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var installed = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO", false);
            if (!Version.TryParse(installed?.GetValue("DisplayVersion") as string, out var version) ||
                version < new Version(2, 0))
            {
                _status = Localization.T("Instale o driver oficial PawnIO 2.2.0 para habilitar a temperatura direta da CPU.");
                return false;
            }

            // Explicitly disable every other category. In particular, avoid EC,
            // motherboard, SMART, USB-controller and SMBus sensor polling.
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsBatteryEnabled = false,
                IsControllerEnabled = false,
                IsGpuEnabled = false,
                IsPowerMonitorEnabled = false,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsStorageEnabled = false
            };
            _computer.Open();
            _cpus = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToArray();
            if (_cpus.Length == 0)
            {
                _status = Localization.T("Processador sem suporte para leitura direta de temperatura.");
                CloseComputer();
                return false;
            }
            var cpuName = _cpus[0].Name?.Trim() ?? string.Empty;
            _cpuName = cpuName.Length <= 240 ? cpuName : cpuName[..240];
            return true;
        }
        catch (Exception ex)
        {
            _status = Localization.Format("Não foi possível iniciar a leitura direta da CPU: {0}", ShortMessage(ex));
            CloseComputer();
            return false;
        }
    }

    private sealed record TemperatureSelection(double? Value, string Name);

    private static TemperatureSelection SelectTemperature(IReadOnlyList<(string Name, double? Value)> readings)
    {
        double? package = null, coreMaximum = null;
        var coreName = string.Empty;
        foreach (var (name, value) in readings)
        {
            if (value is not double degrees || !double.IsFinite(degrees) || degrees is < -20 or > 150)
                continue;
            if (name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
            {
                if (!package.HasValue || degrees > package.Value) package = degrees;
                continue;
            }

            // Intel's hybrid-core labels are P-Core #n and E-Core #n. Explicitly
            // reject temperature deltas and aggregate sensors, which are also
            // exposed with SensorType.Temperature but are not fresh core readings.
            if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TjMax", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Delta", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsCoreName(name)) continue;
            if (!coreMaximum.HasValue || degrees > coreMaximum.Value)
            {
                coreMaximum = degrees;
                coreName = name;
            }
        }
        if (package.HasValue) return new TemperatureSelection(package, "CPU Package");
        if (coreMaximum.HasValue) return new TemperatureSelection(coreMaximum, Localization.Format("Núcleo mais quente ({0})", coreName));
        return new TemperatureSelection(null, string.Empty);
    }

    private static bool IsCoreName(string name) =>
        name.Equals("CPU Core", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase);

    private sealed record PerformanceMetrics(double? ClockMhz, double? PowerWatts);

    private static PerformanceMetrics SelectPerformanceMetrics(IReadOnlyList<(SensorType Type, string Name, double? Value)> readings)
    {
        double clockSum = 0, powerSum = 0;
        int clockCount = 0, packageCount = 0;
        foreach (var (type, name, value) in readings)
        {
            if (value is not double number || !double.IsFinite(number)) continue;
            // Mean of the reported positive per-core clocks. The bus clock and
            // temperature deltas are not CPU frequency measurements.
            if (type == SensorType.Clock && IsCoreName(name) && number is > 0 and <= 20_000)
            {
                clockSum += number;
                clockCount++;
            }
            else if (type == SensorType.Power && name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) &&
                     number is >= 0 and <= 5000)
            {
                powerSum += number;
                packageCount++;
            }
        }
        return new PerformanceMetrics(clockCount > 0 ? clockSum / clockCount : null,
            packageCount > 0 && powerSum <= 5000 ? powerSum : null);
    }

    private static string ShortMessage(Exception ex)
    {
        var text = ex.GetBaseException().Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 240 ? text : text[..240];
    }

    private void CloseComputer()
    {
        try { _computer?.Close(); } catch (Exception) { }
        _computer = null;
        _cpus = [];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _temperature = null;
            _sensorName = string.Empty;
            _clockMhz = null;
            _powerWatts = null;
            _status = Localization.T("Leitura direta da CPU encerrada.");
            CloseComputer();
        }
    }
}
