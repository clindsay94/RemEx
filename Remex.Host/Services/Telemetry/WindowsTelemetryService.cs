using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Host.Services.Telemetry;

[SupportedOSPlatform("windows")]
public class WindowsTelemetryService : ITelemetryService
{
    private const string HwInfoSharedMemoryName = "Global\\HWiNFO_SENS_SM2";
    private readonly ILogger<WindowsTelemetryService> _logger;
    private bool _hwinfoAvailable = true;

    // Performance Counters for fallback
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;

    // Network tracking for fallback
    private NetworkInterface? _activeNic;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastNetworkPoll = DateTime.MinValue;
    private readonly HashSet<string> _hwinfoGadgetLabels = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastGadgetLabelUpdate = DateTime.MinValue;
    private readonly TimeSpan GadgetLabelUpdateInterval = TimeSpan.FromSeconds(5);

    public WindowsTelemetryService(ILogger<WindowsTelemetryService> logger)
    {
        _logger = logger;
        InitializeFallbackCounters();
    }

    private void InitializeFallbackCounters()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");

            // Kick off the first read to initialize
            _cpuCounter.NextValue();

            _activeNic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                     nic.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            if (_activeNic != null)
            {
                var stats = _activeNic.GetIPv4Statistics();
                _lastBytesReceived = stats.BytesReceived;
                _lastBytesSent = stats.BytesSent;
                _lastNetworkPoll = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize WMI/PerformanceCounters for fallback telemetry.");
        }
    }

    public Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
    {
        var payload = ReadWmiFallback(); // Baseline fallback

        if (_hwinfoAvailable)
        {
            try
            {
                if (TryReadHwInfo(payload, out var updatedPayload))
                {
                    return Task.FromResult(updatedPayload);
                }
            }
            catch (FileNotFoundException)
            {
                // This happens when HWiNFO isn't running or shared memory is disabled.
                _logger.LogWarning("HWiNFO Shared Memory not found. Falling back to WMI/Performance Counters.");
                _hwinfoAvailable = false;
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Error reading HWiNFO Shared Memory.");
                _hwinfoAvailable = false;
            }
        }

        return Task.FromResult(payload);
    }

    private bool TryReadHwInfo(TelemetryPayload fallback, out TelemetryPayload result)
    {
        result = fallback;

        // Sync user's intended gadget labels every 5s before parsing shared memory
        UpdateGadgetLabels();

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(HwInfoSharedMemoryName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            // Read the header first
            int headerSize = Marshal.SizeOf(typeof(HWiNFO_SHARED_MEM2));
            byte[] headerBytes = new byte[headerSize];
            accessor.ReadArray(0, headerBytes, 0, headerSize);

            GCHandle headerHandle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
            HWiNFO_SHARED_MEM2 header;
            try
            {
                header = Marshal.PtrToStructure<HWiNFO_SHARED_MEM2>(headerHandle.AddrOfPinnedObject());
            }
            finally
            {
                headerHandle.Free();
            }

            if (header.dwSignature != 0x53695748) // "HWiS"
                return false;

            var sensors = new System.Collections.Generic.List<SensorReading>();

            int readingSize = (int)header.dwSizeOfReadingElement;
            byte[] readingBytes = new byte[readingSize];

            for (uint i = 0; i < header.dwNumReadingElements; i++)
            {
                long offset = header.dwOffsetOfReadingSection + (i * readingSize);
                accessor.ReadArray(offset, readingBytes, 0, readingSize);

                GCHandle elementHandle = GCHandle.Alloc(readingBytes, GCHandleType.Pinned);
                try
                {
                    var reading = Marshal.PtrToStructure<HWiNFO_READING_ELEMENT>(elementHandle.AddrOfPinnedObject());
                    var label = !string.IsNullOrWhiteSpace(reading.szLabelUser) ? reading.szLabelUser : reading.szLabelOrig;
                    
                    if (!string.IsNullOrWhiteSpace(label) && _hwinfoGadgetLabels.Contains(label))
                    {
                        sensors.Add(new SensorReading
                        {
                            Name = label,
                            Value = reading.Value,
                            Unit = reading.szUnit,
                            Category = DetermineCategory(label)
                        });
                    }
                }
                finally
                {
                    elementHandle.Free();
                }
            }

            result = new TelemetryPayload
            {
                Sensors = sensors,
                UptimeText = fallback.UptimeText
            };

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "HWiNFO parsing failed.");
            return false;
        }
    }

    private void UpdateGadgetLabels()
    {
        if (DateTime.UtcNow - _lastGadgetLabelUpdate < GadgetLabelUpdateInterval)
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HWiNFO64\VSB");
                if (key != null)
                {
                    _hwinfoGadgetLabels.Clear();
                    foreach (var valueName in key.GetValueNames())
                    {
                        if (valueName.StartsWith("Label", StringComparison.OrdinalIgnoreCase))
                        {
                            var label = key.GetValue(valueName) as string;
                            if (!string.IsNullOrWhiteSpace(label))
                            {
                                _hwinfoGadgetLabels.Add(label);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to read HWiNFO VSB registry keys for gadget labels.");
        }
        finally
        {
            _lastGadgetLabelUpdate = DateTime.UtcNow;
        }
    }

    private string DetermineCategory(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Other";
        var lower = label.ToLowerInvariant();

        if (lower.Contains("cpu") || lower.Contains("core") || lower.Contains("thread") || lower.Contains("ccd"))
            return "CPU";
        
        if (lower.Contains("gpu") || lower.Contains("pcie") || lower.Contains("video"))
            return "GPU";
            
        if (lower.Contains("mem") || lower.Contains("ram") || lower.Contains("virtual") || lower.Contains("physical") || lower.Contains("ddr"))
            return "Memory";

        if (lower.Contains("net") || lower.Contains("wi-fi") || lower.Contains("ethernet") || lower.Contains("adapter"))
            return "Network";

        if (lower.Contains("disk") || lower.Contains("drive") || lower.Contains("nvme") || lower.Contains("sata") || lower.Contains("ssd") || lower.Contains("hdd"))
            return "Disk";

        if (lower.Contains("motherboard") || lower.Contains("sys") || lower.Contains("fan") || lower.Contains("pump") || lower.Contains("vrm"))
            return "System";

        return "Other";
    }

    private TelemetryPayload ReadWmiFallback()
    {
        var sensors = new System.Collections.Generic.List<SensorReading>();

        try
        {
            var cpuValue = _cpuCounter?.NextValue() ?? 0;
            sensors.Add(new SensorReading { Name = "Total CPU Usage", Value = cpuValue, Unit = "%", Category = "CPU" });

            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                var usedMemBytes = memStatus.ullTotalPhys - memStatus.ullAvailPhys;
                sensors.Add(new SensorReading { Name = "Physical Memory Used", Value = usedMemBytes / 1e6, Unit = "MB", Category = "Memory" });
                sensors.Add(new SensorReading { Name = "Physical Memory Available", Value = memStatus.ullAvailPhys / 1e6, Unit = "MB", Category = "Memory" });
                sensors.Add(new SensorReading { Name = "Physical Memory Load", Value = memStatus.dwMemoryLoad, Unit = "%", Category = "Memory" });
            }

            // Disk Activity
            var diskRead = _diskReadCounter?.NextValue() ?? 0;
            var diskWrite = _diskWriteCounter?.NextValue() ?? 0;
            sensors.Add(new SensorReading { Name = "Disk Read Rate", Value = diskRead / 1e6, Unit = "MB/s", Category = "Disk" });
            sensors.Add(new SensorReading { Name = "Disk Write Rate", Value = diskWrite / 1e6, Unit = "MB/s", Category = "Disk" });

            // Network Activity
            if (_activeNic != null)
            {
                var stats = _activeNic.GetIPv4Statistics();
                var now = DateTime.UtcNow;
                var elapsedSeconds = (now - _lastNetworkPoll).TotalSeconds;

                if (elapsedSeconds > 0)
                {
                    var netDown = ((stats.BytesReceived - _lastBytesReceived) / 1024.0 / 1024.0) / elapsedSeconds;
                    var netUp = ((stats.BytesSent - _lastBytesSent) / 1024.0 / 1024.0) / elapsedSeconds;
                    sensors.Add(new SensorReading { Name = "Current DL Rate", Value = netDown >= 0 ? netDown : 0, Unit = "MB/s", Category = "Network" });
                    sensors.Add(new SensorReading { Name = "Current UP Rate", Value = netUp >= 0 ? netUp : 0, Unit = "MB/s", Category = "Network" });
                }

                _lastBytesReceived = stats.BytesReceived;
                _lastBytesSent = stats.BytesSent;
                _lastNetworkPoll = now;
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error reading fallback telemetry.");
        }

        return new TelemetryPayload
        {
            Sensors = sensors,
            UptimeText = GetUptime()
        };
    }

    private string GetUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
    }

    #region Interop Structs

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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HWiNFO_SHARED_MEM2
    {
        public uint dwSignature; // "HWiS"
        public uint dwVersion;
        public uint dwRevision;
        public long poll_time;
        public uint dwOffsetOfSensorSection;
        public uint dwSizeOfSensorElement;
        public uint dwNumSensorElements;
        public uint dwOffsetOfReadingSection;
        public uint dwSizeOfReadingElement;
        public uint dwNumReadingElements;
    }

    public enum SENSOR_READING_TYPE : int
    {
        SENSOR_TYPE_NONE = 0,
        SENSOR_TYPE_TEMP,
        SENSOR_TYPE_VOLT,
        SENSOR_TYPE_FAN,
        SENSOR_TYPE_CURRENT,
        SENSOR_TYPE_POWER,
        SENSOR_TYPE_CLOCK,
        SENSOR_TYPE_USAGE,
        SENSOR_TYPE_OTHER
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct HWiNFO_READING_ELEMENT
    {
        public SENSOR_READING_TYPE tReading;
        public uint dwSensorIndex;
        public uint dwReadingID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szLabelUser;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szLabelOrig;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string szUnit;
        public double Value;
        public double ValueMin;
        public double ValueMax;
        public double ValueAvg;
    }

    #endregion
}
