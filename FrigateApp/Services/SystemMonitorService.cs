using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FrigateApp.Services;

/// <summary>
/// Сервис мониторинга системных ресурсов (CPU, RAM, GPU).
/// </summary>
public class SystemMonitorService : IDisposable
{
    private readonly PerformanceCounter? _cpuCounter;
    private readonly PerformanceCounter? _ramCounter;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private DateTime _lastCpuCheck = DateTime.MinValue;
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
    
    // Для Linux CPU мониторинга
    private long _lastTotalCpuTime;
    private long _lastIdleCpuTime;

    public event Action<double, double, double>? MetricsUpdated; // CPU%, RAM%, GPU%

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public SystemMonitorService()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use", true);
                
                // Первый вызов NextValue() для инициализации счетчиков
                _cpuCounter?.NextValue();
                _ramCounter?.NextValue();
            }
        }
        catch (Exception ex)
        {
            // Логируем ошибку для отладки
            Debug.WriteLine($"SystemMonitor init error: {ex.Message}");
        }
    }

    /// <summary>Запустить мониторинг с заданным интервалом обновления.</summary>
    public void Start(TimeSpan interval)
    {
        if (_cts != null) return;
        
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () => await MonitorLoopAsync(interval, _cts.Token).ConfigureAwait(false));
    }

    /// <summary>Остановить мониторинг.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        // Начальная задержка для стабилизации счетчиков
        await Task.Delay(500, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var cpu = GetCpuUsage();
                var ram = GetRamUsage();
                var gpu = GetGpuUsage();

                Debug.WriteLine($"SystemMonitor: CPU={cpu:F1}%, RAM={ram:F1}%, GPU={gpu:F1}%");
                MetricsUpdated?.Invoke(cpu, ram, gpu);

                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SystemMonitor loop error: {ex.Message}");
            }
        }
    }

    private double GetCpuUsage()
    {
        // Linux: читаем /proc/stat
        if (OperatingSystem.IsLinux())
        {
            return GetCpuUsageLinux();
        }
        
        // Windows: используем PerformanceCounter
        try
        {
            if (_cpuCounter != null)
            {
                var value = _cpuCounter.NextValue();
                if (value > 0)
                    return Math.Round(Math.Max(0, Math.Min(100, value)), 1);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CPU counter error: {ex.Message}");
        }
        
        // Альтернативный метод - суммируем CPU всех процессов
        try
        {
            var currentTime = DateTime.UtcNow;
            var totalProcessorTime = TimeSpan.Zero;
            
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    totalProcessorTime += process.TotalProcessorTime;
                    process.Dispose();
                }
                catch { }
            }

            if (_lastCpuCheck != DateTime.MinValue)
            {
                var timeDiff = (currentTime - _lastCpuCheck).TotalMilliseconds;
                var cpuDiff = (totalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;
                var cpuUsage = (cpuDiff / (Environment.ProcessorCount * timeDiff)) * 100;
                
                _lastCpuCheck = currentTime;
                _lastTotalProcessorTime = totalProcessorTime;
                
                return Math.Round(Math.Max(0, Math.Min(100, cpuUsage)), 1);
            }
            
            _lastCpuCheck = currentTime;
            _lastTotalProcessorTime = totalProcessorTime;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CPU fallback error: {ex.Message}");
        }
        
        return 0;
    }

    private double GetCpuUsageLinux()
    {
        try
        {
            if (!File.Exists("/proc/stat"))
                return 0;

            var lines = File.ReadAllLines("/proc/stat");
            var cpuLine = lines.FirstOrDefault(l => l.StartsWith("cpu "));
            if (cpuLine == null)
                return 0;

            var parts = cpuLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                return 0;

            // cpu user nice system idle iowait irq softirq steal guest guest_nice
            long user = long.Parse(parts[1]);
            long nice = long.Parse(parts[2]);
            long system = long.Parse(parts[3]);
            long idle = long.Parse(parts[4]);
            long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;
            long irq = parts.Length > 6 ? long.Parse(parts[6]) : 0;
            long softirq = parts.Length > 7 ? long.Parse(parts[7]) : 0;

            long totalCpuTime = user + nice + system + idle + iowait + irq + softirq;
            long idleCpuTime = idle + iowait;

            if (_lastTotalCpuTime > 0)
            {
                long totalDiff = totalCpuTime - _lastTotalCpuTime;
                long idleDiff = idleCpuTime - _lastIdleCpuTime;

                if (totalDiff > 0)
                {
                    double cpuUsage = ((double)(totalDiff - idleDiff) / totalDiff) * 100.0;
                    _lastTotalCpuTime = totalCpuTime;
                    _lastIdleCpuTime = idleCpuTime;
                    return Math.Round(Math.Max(0, Math.Min(100, cpuUsage)), 1);
                }
            }

            _lastTotalCpuTime = totalCpuTime;
            _lastIdleCpuTime = idleCpuTime;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Linux CPU error: {ex.Message}");
        }

        return 0;
    }

    private double GetRamUsage()
    {
        // Linux: читаем /proc/meminfo
        if (OperatingSystem.IsLinux())
        {
            return GetRamUsageLinux();
        }
        
        // Windows: используем PerformanceCounter
        try
        {
            if (_ramCounter != null)
            {
                var value = _ramCounter.NextValue();
                if (value > 0)
                    return Math.Round(Math.Max(0, Math.Min(100, value)), 1);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RAM counter error: {ex.Message}");
        }
        
        // Альтернативный метод через WinAPI
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    return Math.Round((double)memStatus.dwMemoryLoad, 1);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RAM WinAPI error: {ex.Message}");
            }
        }
        
        // Еще один fallback
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            var installedMemory = gcInfo.TotalAvailableMemoryBytes;
            if (installedMemory > 0)
            {
                var usedMemory = installedMemory - gcInfo.MemoryLoadBytes;
                return Math.Round((double)usedMemory / installedMemory * 100, 1);
            }
        }
        catch { }
        
        return 0;
    }

    private double GetRamUsageLinux()
    {
        try
        {
            if (!File.Exists("/proc/meminfo"))
                return 0;

            var lines = File.ReadAllLines("/proc/meminfo");
            long memTotal = 0, memFree = 0, buffers = 0, cached = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("MemTotal:"))
                    memTotal = ParseMemInfoValue(line);
                else if (line.StartsWith("MemFree:"))
                    memFree = ParseMemInfoValue(line);
                else if (line.StartsWith("Buffers:"))
                    buffers = ParseMemInfoValue(line);
                else if (line.StartsWith("Cached:"))
                    cached = ParseMemInfoValue(line);
            }

            if (memTotal > 0)
            {
                // Используемая память = Total - Free - Buffers - Cached
                long memUsed = memTotal - memFree - buffers - cached;
                double usage = ((double)memUsed / memTotal) * 100.0;
                return Math.Round(Math.Max(0, Math.Min(100, usage)), 1);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Linux RAM error: {ex.Message}");
        }

        return 0;
    }

    private static long ParseMemInfoValue(string line)
    {
        // Формат: "MemTotal:       16384000 kB"
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out long value))
            return value;
        return 0;
    }

    private double GetGpuUsage()
    {
        // Linux: пробуем nvidia-smi
        if (OperatingSystem.IsLinux())
        {
            return GetGpuUsageLinux();
        }
        
        // Windows: пробуем разные методы
        if (OperatingSystem.IsWindows())
        {
            return GetGpuUsageWindows();
        }
        
        return 0;
    }

    private double GetGpuUsageWindows()
    {
        // Метод 1: Пробуем nvidia-smi на Windows (работает если установлена NVIDIA GPU)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1000);
                
                if (!string.IsNullOrWhiteSpace(output) && double.TryParse(output.Trim(), out double gpuUsage))
                {
                    return Math.Round(Math.Max(0, Math.Min(100, gpuUsage)), 1);
                }
            }
        }
        catch
        {
            // nvidia-smi не найден
        }

        // Метод 2: Performance Counter для GPU Engine
        try
        {
            var category = PerformanceCounterCategory.GetCategories()
                .FirstOrDefault(c => c.CategoryName == "GPU Engine");
            
            if (category != null)
            {
                var instances = category.GetInstanceNames();
                if (instances.Length > 0)
                {
                    // Берем первый экземпляр GPU
                    var instanceName = instances.FirstOrDefault(i => i.Contains("engtype_3D") || i.Contains("3D"));
                    if (instanceName != null)
                    {
                        using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instanceName);
                        var value = counter.NextValue();
                        if (value > 0)
                            return Math.Round(Math.Max(0, Math.Min(100, value)), 1);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GPU PerformanceCounter error: {ex.Message}");
        }

        // Метод 3: Пробуем через WMI (Management Objects)
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var loadPercentage = obj["LoadPercentage"];
                if (loadPercentage != null && double.TryParse(loadPercentage.ToString(), out double load))
                {
                    return Math.Round(Math.Max(0, Math.Min(100, load)), 1);
                }
            }
        }
        catch
        {
            // WMI не поддерживает GPU load на этой системе
        }

        return 0;
    }

    private double GetGpuUsageLinux()
    {
        try
        {
            // Проверяем наличие nvidia-smi
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1000);
                
                if (double.TryParse(output.Trim(), out double gpuUsage))
                {
                    return Math.Round(Math.Max(0, Math.Min(100, gpuUsage)), 1);
                }
            }
        }
        catch
        {
            // nvidia-smi не установлен или ошибка
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        
        _cpuCounter?.Dispose();
        _ramCounter?.Dispose();
    }
}
