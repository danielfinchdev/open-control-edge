using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace EdgeWidget.Services;

/// CPU and NVIDIA GPU sensors through LibreHardwareMonitorLib 0.9.6, sharing one Computer instance.
///
/// CPU, verified on this machine (Intel Core i7-8750H, /intelcpu/0):
///   Temperature "CPU Package", "CPU Core #1" .. "CPU Core #6" (plus "Core Max", "Core Average", "... Distance to TjMax"),
///   Load "CPU Total". Temperatures need administrator rights and the PawnIO driver; unelevated they read null.
/// GPU, verified on this machine on 2026-09-13 (NVIDIA GeForce GTX 1050, /gpu-nvidia/0, readable unelevated):
///   Temperature "GPU Core" (also "GPU Hot Spot"), Load "GPU Core",
///   SmallData "GPU Memory Used" and "GPU Memory Total" in MB.
///   Sensors are matched by type and name: the "GPU Bus" and "GPU Memory" loads share the identifier /gpu-nvidia/0/load/3.
internal sealed class HardwareSensorService : IDisposable
{
    private const string CoreSensorPrefix = "CPU Core #";

    /// Above this the package is running hot enough to be worth a line in the log.
    private const double HighTemperature = 90;
    private static readonly TimeSpan HighLogInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private Computer? _computer;
    private double? _maxCpuTemperature;
    private DateTime _lastHighLog = DateTime.MinValue;
    private bool _disposed;

    public DateTime SessionStart { get; } = DateTime.Now;

    /// Runs on a worker thread; LHM access is serialized. Never throws.
    public Task<HardwareSnapshot> SampleAsync() => Task.Run(Sample);

    private HardwareSnapshot Sample()
    {
        lock (_gate)
        {
            if (_disposed)
                return new HardwareSnapshot(new CpuSnapshot(null, null, _maxCpuTemperature, null, "Detenido"), GpuSnapshot.NotDetected);

            try
            {
                if (_computer is null)
                {
                    var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
                    computer.Open();
                    _computer = computer;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Sensors", ex);
                return new HardwareSnapshot(
                    new CpuSnapshot(null, null, _maxCpuTemperature, null, "Error leyendo los sensores"), GpuSnapshot.NotDetected);
            }

            // Each device is read on its own, so a failure in one never blanks the other.
            return new HardwareSnapshot(SampleCpu(_computer), SampleGpu(_computer));
        }
    }

    private CpuSnapshot SampleCpu(Computer computer)
    {
        try
        {
            IHardware? cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            if (cpu is null) return new CpuSnapshot(null, null, _maxCpuTemperature, null, "No se detecta la CPU");

            cpu.Update();

            double? temperature = ReadCpuTemperature(cpu);
            double? load = Finite(FindValue(cpu, SensorType.Load, "CPU Total"));

            if (temperature is double t)
            {
                _maxCpuTemperature = _maxCpuTemperature is double max ? Math.Max(max, t) : t;
                NoteHighTemperature(t, load);
            }

            string? message = temperature is null ? ExplainMissingCpuTemperature() : null;
            return new CpuSnapshot(cpu.Name, temperature, _maxCpuTemperature, load, message);
        }
        catch (Exception ex)
        {
            Log.Error("CPU", ex);
            return new CpuSnapshot(null, null, _maxCpuTemperature, null, "Error leyendo los sensores");
        }
    }

    private static GpuSnapshot SampleGpu(Computer computer)
    {
        try
        {
            IHardware? gpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia);
            if (gpu is null) return GpuSnapshot.NotDetected;

            gpu.Update();

            double? temperature = ValidTemperature(FindValue(gpu, SensorType.Temperature, "GPU Core"));
            double? load = Finite(FindValue(gpu, SensorType.Load, "GPU Core"));
            double? memoryUsed = Finite(FindValue(gpu, SensorType.SmallData, "GPU Memory Used"));
            double? memoryTotal = Finite(FindValue(gpu, SensorType.SmallData, "GPU Memory Total"));

            string? message = temperature is null ? "Sensor de temperatura no disponible" : null;
            return new GpuSnapshot(true, gpu.Name, temperature, load, memoryUsed, memoryTotal, message);
        }
        catch (Exception ex)
        {
            Log.Error("GPU", ex);
            return new GpuSnapshot(true, null, null, null, null, null, "Error leyendo los sensores");
        }
    }

    /// One line in the log whenever the package runs hot, at most one a minute. The peaks nobody is watching —
    /// the burst right after logging in above all — leave a trace that can be read afterwards.
    private void NoteHighTemperature(double celsius, double? load)
    {
        if (celsius < HighTemperature) return;

        DateTime now = DateTime.UtcNow;
        if (now - _lastHighLog < HighLogInterval) return;
        _lastHighLog = now;
        Log.Warn("CPU", $"temperatura alta: {celsius:0} °C"
            + (load is double percent ? $", carga {percent:0} %" : string.Empty));
    }

    private static float? FindValue(IHardware hardware, SensorType type, string name) =>
        hardware.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value;

    /// "CPU Package" if available, otherwise the mean of the per-core sensors.
    private static double? ReadCpuTemperature(IHardware cpu)
    {
        var temperatures = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();

        if (ValidTemperature(temperatures.FirstOrDefault(s => s.Name == "CPU Package")?.Value) is double package)
            return package;

        var cores = temperatures
            .Where(s => IsCoreSensor(s.Name))
            .Select(s => ValidTemperature(s.Value))
            .OfType<double>()
            .ToList();
        return cores.Count > 0 ? cores.Average() : null;
    }

    /// Exactly "CPU Core #N" — excludes "CPU Core #N Distance to TjMax", "Core Max" and "Core Average".
    private static bool IsCoreSensor(string name) =>
        name.StartsWith(CoreSensorPrefix, StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(CoreSensorPrefix.Length), out _);

    private static double? Finite(float? value) =>
        value is float v && float.IsFinite(v) ? v : null;

    private static double? ValidTemperature(float? value) =>
        Finite(value) is double v && v > 0 ? v : null;

    private static string ExplainMissingCpuTemperature()
    {
        if (!LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled) return "PawnIO no está instalado";
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return "Requiere ejecutar como administrador";
        return "Sensor de temperatura no disponible";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer?.Close(); }
            catch (Exception ex) { Log.Error("Sensors", ex); }
            _computer = null;
        }
    }
}
