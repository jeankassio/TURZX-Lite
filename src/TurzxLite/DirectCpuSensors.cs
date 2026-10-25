using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace TurzxLite;

public sealed record CpuSensorReading(double? Temperature, string Status, string Sensor, DateTimeOffset At)
{
    public string CpuName { get; init; } = string.Empty;
    public double? CpuClockMhz { get; init; }
    public double? CpuPowerWatts { get; init; }
}

/// <summary>The normal UI owns this pipe. Only the optional CPU reader is elevated.</summary>
public static class DirectCpuSensors
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? lifetime;
    private static Task? receiver;
    private static Process? helper;
    private static CpuSensorReading latest = new(null, Localization.T("Clique em Ativar temperatura da CPU para ler sem outro monitor aberto."), "", DateTimeOffset.MinValue);
    public static bool Active { get { lock (Gate) return receiver is { IsCompleted: false }; } }
    public static CpuSensorReading Current
    {
        get
        {
            lock (Gate)
            {
                if ((latest.Temperature.HasValue || latest.CpuClockMhz.HasValue || latest.CpuPowerWatts.HasValue) &&
                    DateTimeOffset.UtcNow - latest.At > TimeSpan.FromSeconds(6))
                    return latest with { Temperature = null, CpuClockMhz = null, CpuPowerWatts = null,
                        Status = Localization.T("Leitura da CPU atrasada. Ative novamente o sensor.") };
                return latest;
            }
        }
    }

    public static async Task<bool> EnableAsync(bool restart = false)
    {
        if (restart)
        {
            lifetime?.Cancel();
            if (receiver != null) try { await receiver; } catch { }
        }
        CancellationToken token;
        lock (Gate)
        {
            if (receiver is { IsCompleted: false }) return true;
            lifetime?.Dispose();
            lifetime = new();
            token = lifetime.Token;
            latest = new(null, Localization.T("Aguardando autorização do Windows para o sensor da CPU…"), "", DateTimeOffset.UtcNow);
        }
        string pipeName = "TurzxLite.Cpu." + Guid.NewGuid().ToString("N");
        var pipeSecurity = new PipeSecurity();
        using (var owner = WindowsIdentity.GetCurrent())
            pipeSecurity.AddAccessRule(new PipeAccessRule(owner.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        pipeSecurity.SetAccessRuleProtection(true, false);
        var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, pipeSecurity);
        receiver = ReceiveAsync(server, token);
        try
        {
            var info = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--cpu-sensor-worker", Environment.ProcessId.ToString(), pipeName }) info.ArgumentList.Add(arg);
            helper = await Task.Run(() => Process.Start(info), token);
            return helper != null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            lifetime?.Cancel();
            lock (Gate) latest = new(null, Localization.T("Autorização cancelada. Clique em Ativar temperatura da CPU quando quiser."), "", DateTimeOffset.UtcNow);
            return false;
        }
        catch (Exception ex)
        {
            lifetime?.Cancel();
            lock (Gate) latest = new(null, Localization.Format("Não foi possível iniciar o sensor: {0}", ex.Message), "", DateTimeOffset.UtcNow);
            AppLog.Write(ex.ToString());
            return false;
        }
    }
    private static async Task ReceiveAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using (server)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                await server.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(server);
                while (await ReadLineBoundedAsync(reader, token) is string line)
                {
                    if (line.Length > 4096) throw new InvalidDataException(Localization.T("Resposta de sensor inválida."));
                    var reading = JsonSerializer.Deserialize<CpuSensorReading>(line);
                    reading = ValidateReading(reading);
                    if (reading is null) continue;
                    lock (Gate) latest = reading;
                }
                lock (Gate) latest = latest with { Temperature = null, CpuClockMhz = null, CpuPowerWatts = null,
                    Status = Localization.T("Sensor da CPU encerrado. Clique em Ativar para iniciar novamente.") };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    lock (Gate) latest = new(null, Localization.Format("Sensor da CPU: {0}", ex.Message), "", DateTimeOffset.UtcNow);
            }
        }
    }
    private static CpuSensorReading? ValidateReading(CpuSensorReading? reading)
    {
        if (reading is null || reading.Status is null || reading.Sensor is null ||
            reading.Status.Length > 1000 || reading.Sensor.Length > 160) return null;
        if (reading.Temperature is double t && (!double.IsFinite(t) || t is < -20 or > 150)) return null;
        var name = reading.CpuName ?? string.Empty;
        return reading with
        {
            CpuName = name.Length <= 240 ? name : name[..240],
            CpuClockMhz = BoundedMetric(reading.CpuClockMhz, 0, 20_000),
            CpuPowerWatts = BoundedMetric(reading.CpuPowerWatts, 0, 5000),
            At = DateTimeOffset.UtcNow
        };
    }
    private static double? BoundedMetric(double? value, double minimum, double maximum) =>
        value is double number && double.IsFinite(number) && number >= minimum && number <= maximum ? number : null;
    private static async Task<string?> ReadLineBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var line = new System.Text.StringBuilder(256);
        var next = new char[1];
        while (await reader.ReadAsync(next.AsMemory(), token) != 0)
        {
            if (next[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == 4096) throw new InvalidDataException(Localization.T("Resposta do sensor acima do limite."));
            line.Append(next[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
    public static void Stop()
    {
        lifetime?.Cancel();
        lock (Gate) latest = latest with { Temperature = null, CpuClockMhz = null, CpuPowerWatts = null,
            Status = Localization.T("Leitura direta da CPU encerrada.") };
    }

    public static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out int parentId) || parentId <= 0 ||
            !args[2].StartsWith("TurzxLite.Cpu.", StringComparison.Ordinal) || args[2].Length > 80) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        using var parent = OpenParent(parentId);
        if (parent is null) return 0; // The UI may close while its UAC prompt is open.
        try
        {
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 4;
        }
        catch (Exception) when (ParentExited(parent)) { return 0; }
        using var parentEnded = new CancellationTokenSource();
        _ = Task.Run(async () => { try { await parent.WaitForExitAsync(); } catch { } CancelSafely(parentEnded); });
        var token = parentEnded.Token;
        using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(15000, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested || ParentExited(parent)) { return 0; }
        catch (TimeoutException) when (ParentExited(parent)) { return 0; }
        catch (IOException ex) when (ParentExited(parent) || IsClosedPipe(ex)) { return 0; }
        int installing = 0;
        _ = Task.Run(async () =>
        {
            try { _ = await pipe.ReadAsync(new byte[1], token); } catch { }
            CancelSafely(parentEnded);
            await Task.Delay(5000);
            // Exit a stuck sensor call after its UI/pipe closes. Let an installer finish.
            if (Volatile.Read(ref installing) == 0) Environment.Exit(0);
        });
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        async Task Send(double? temperature, string status, string sensor = "", CpuTemperatureReader? source = null)
        {
            var reading = new CpuSensorReading(temperature, status, sensor, DateTimeOffset.UtcNow)
            {
                CpuName = source?.CpuName ?? string.Empty,
                CpuClockMhz = source?.ClockMhz,
                CpuPowerWatts = source?.PowerWatts
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(reading).AsMemory(), token);
        }
        try
        {
            if (!PawnIoInstaller.IsInstalled())
            {
                await Send(null, Localization.T("Instalando o driver oficial de sensores da CPU…"));
                Interlocked.Exchange(ref installing, 1);
                try { await PawnIoInstaller.InstallAsync(token); }
                finally { Interlocked.Exchange(ref installing, 0); }
            }
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            using var reader = new CpuTemperatureReader();
            bool logged = false;
            while (!token.IsCancellationRequested)
            {
                double? value = reader.Read();
                await Send(value, reader.Status, reader.SensorName, reader);
                if (value.HasValue && !logged) { AppLog.Write($"CPU direta ativa: {reader.SensorName}={value:0.0}°C; frequência média {reader.ClockMhz:0} MHz; potência {reader.PowerWatts:0.0} W; PawnIO; consulta de CPU somente a 1 Hz."); logged = true; }
                await Task.Delay(1000, token);
            }
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (IOException ex) when (token.IsCancellationRequested || !pipe.IsConnected || IsClosedPipe(ex))
        {
            // Closing/restarting the UI intentionally closes this one-way sensor
            // stream. It is a normal helper exit, not a CPU or installer failure.
            return 0;
        }
        catch (Exception ex)
        {
            try { await Send(null, Localization.Format("Não foi possível ativar a CPU: {0}", ex.GetBaseException().Message)); } catch { }
            AppLog.Write("CPU auxiliar: " + ex);
            return 1;
        }
    }
    private static bool IsClosedPipe(IOException exception) =>
        (exception.HResult & 0xFFFF) is 109 or 232 or 233; // BROKEN_PIPE / NO_DATA / PIPE_NOT_CONNECTED
    private static Process? OpenParent(int id)
    {
        try { return Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
    }
    private static bool ParentExited(Process parent)
    {
        try { return parent.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
    private static void CancelSafely(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

internal static class PawnIoInstaller
{
    internal const string DownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    internal const string Sha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    public static bool IsInstalled()
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version) && version >= new Version(2, 0);
    }
    public static async Task InstallAsync(CancellationToken token)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TurzxLite-Installer");
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(directory);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException(Localization.T("Diretório de instalação inválido."));
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admin);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { admin, system }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        // Fresh name below the protected directory avoids preexisting files/ACLs.
        string privateDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateDirectory);
        string path = Path.Combine(privateDirectory, "PawnIO-2.2.0.exe");
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            using var downloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            downloadDeadline.CancelAfter(TimeSpan.FromSeconds(90));
            var downloadToken = downloadDeadline.Token;
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 8_000_000) throw new IOException(Localization.T("Instalador maior que o esperado."));
            using var source = await response.Content.ReadAsStreamAsync(downloadToken);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                int read; long total = 0;
                while ((read = await source.ReadAsync(buffer, downloadToken)) != 0)
                {
                    total += read;
                    if (total > 8_000_000) throw new IOException(Localization.T("Instalador maior que o esperado."));
                    await output.WriteAsync(buffer.AsMemory(0, read), downloadToken);
                }
            }
            if (!ValidHash(path)) { File.Delete(path); throw new IOException(Localization.T("A verificação do instalador de sensores falhou.")); }
        }
        using var verifiedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(verifiedFile)) != Sha256) throw new IOException(Localization.T("Instalador de sensores inválido."));
        using var installer = Process.Start(new ProcessStartInfo(path, "-install -silent") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new IOException(Localization.T("Não foi possível iniciar o instalador de sensores."));
        // An installer must finish its own transaction, even if the parent UI exits.
        await installer.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        if (installer.ExitCode is not (0 or 3010) || !IsInstalled()) throw new IOException(Localization.Format("Instalação de sensores não concluída (código {0}).", installer.ExitCode));
    }
    private static bool ValidHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)) == Sha256;
    }
}
