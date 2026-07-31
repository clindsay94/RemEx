using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Agent.Services.Telemetry;

[SupportedOSPlatform("windows")]
public class WindowsTelemetryService : ITelemetryService, IDisposable
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
    private readonly object _networkLock = new();

    // Cached fallback payload to return when WMI/PerformanceCounter stalls
    private TelemetryPayload? _cachedFallback;
    private static readonly TimeSpan WmiFallbackTimeout = TimeSpan.FromSeconds(2);

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

    public async Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default)
    {
        // Run WMI/PerformanceCounter reads on a separate thread with a timeout
        // to prevent thread-pool starvation when WMI is slow/stalled.
        TelemetryPayload payload;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(WmiFallbackTimeout);
            payload = await Task.Run(ReadWmiFallback, timeoutCts.Token);
            _cachedFallback = payload;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("WMI/PerformanceCounter telemetry timed out after {Timeout}s — returning cached data.",
                WmiFallbackTimeout.TotalSeconds);
            payload = _cachedFallback ?? new TelemetryPayload();
        }

        if (_hwinfoAvailable)
        {
            try
            {
                if (TryReadHwInfo(payload, out var updatedPayload))
                {
                    return EnsureRamTotal(updatedPayload);
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

        return EnsureRamTotal(payload);
    }

    /// <summary>
    /// Guarantees the payload carries a <see cref="MetricKind.RamTotalGb"/> reading so the curated
    /// RAM Total card binds instead of sitting on "Collecting Data". HWiNFO frequently does not expose
    /// a "Physical Memory Total" sensor, and the WMI path emits only used/available/load — yet total
    /// physical RAM is a static machine fact already available from <c>GlobalMemoryStatusEx</c>. We
    /// inject it only when no producer supplied one, so a real HWiNFO total (if present) still wins.
    /// </summary>
    private TelemetryPayload EnsureRamTotal(TelemetryPayload payload)
    {
        if (payload.Sensors.Any(s => s.Kind == MetricKind.RamTotalGb))
            return payload;

        try
        {
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                // bytes → GiB, matching the used-value convention (FormatSensorValue divides MB by 1024).
                var totalGb = Math.Round(memStatus.ullTotalPhys / 1073741824.0, 2);
                var sensors = new System.Collections.Generic.List<SensorReading>(payload.Sensors)
                {
                    new SensorReading
                    {
                        Name = "Physical Memory Total",
                        Value = totalGb,
                        Unit = "GB",
                        Category = "Memory",
                        Source = "System",
                        Kind = MetricKind.RamTotalGb,
                        Id = "sys:mem:total"
                    }
                };
                return payload with { Sensors = sensors };
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to inject RamTotalGb reading.");
        }

        return payload;
    }

    private bool TryReadHwInfo(TelemetryPayload fallback, out TelemetryPayload result)
    {
        result = fallback;

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

            // Read the parent-sensor section so each reading can be categorized/grouped/named by the
            // actual device HWiNFO groups it under (drive letter + model, CPU/GPU model, etc.) — far
            // more reliable than the reading's own generic label.
            var parentNames = new string[header.dwNumSensorElements];
            int sensorSize = (int)header.dwSizeOfSensorElement;
            byte[] sensorBytes = new byte[sensorSize];
            for (uint si = 0; si < header.dwNumSensorElements; si++)
            {
                accessor.ReadArray((long)header.dwOffsetOfSensorSection + (long)si * sensorSize, sensorBytes, 0, sensorSize);
                var sensorHandle = GCHandle.Alloc(sensorBytes, GCHandleType.Pinned);
                try
                {
                    var se = Marshal.PtrToStructure<HWiNFO_SENSOR_ELEMENT>(sensorHandle.AddrOfPinnedObject());
                    parentNames[si] = !string.IsNullOrWhiteSpace(se.szSensorNameUser) ? se.szSensorNameUser : se.szSensorNameOrig;
                }
                finally
                {
                    sensorHandle.Free();
                }
            }

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
                    
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        var parent = reading.dwSensorIndex < parentNames.Length
                            ? (parentNames[reading.dwSensorIndex] ?? string.Empty)
                            : string.Empty;
                        var (category, group, dev) = ClassifyDevice(parent, label);
                        var kind = ClassifyKind(category, reading.tReading, label, reading.szUnit);
                        var value = FormatSensorValue(NormalizeValueForKind(kind, reading.Value, reading.szUnit), reading.szUnit, reading.tReading);
                        var canon = MetricUnits.Canonical(kind);
                        // Canonical unit when we recognize the metric; otherwise the normalized host unit.
                        // A card renders this value+unit, so a bogus "ms"/blank unit on an Unknown sensor
                        // can never reach a kind-bound load/temperature card (the 1089.0ms bug class).
                        var unit = canon.Length > 0 ? canon : NormalizeUnit(reading.szUnit, reading.Value);

                        // Filter out obvious bogus readings (e.g. 115°C, 127°C, -66°C, -128°C) 
                        // which are common placeholder values for disconnected sensors.
                        if (reading.tReading == SENSOR_READING_TYPE.SENSOR_TYPE_TEMP)
                        {
                            if (value <= -50 || value >= 112)
                                continue;
                        }

                        sensors.Add(new SensorReading
                        {
                            Name = DeviceName(dev, label),
                            Value = value,
                            Unit = unit,
                            Category = category,
                            Source = "HWInfo",
                            Kind = kind,
                            Id = $"hwi:{reading.dwSensorIndex:x}:{reading.dwReadingID:x}",
                            Group = group
                        });
                    }
                }
                finally
                {
                    elementHandle.Free();
                }
            }

            // When HWiNFO data is available, filter out WindowsPerf sensors that overlap
            // with HWiNFO data by category. If HWiNFO provides ANY sensor in a category,
            // all WindowsPerf sensors in that same category are removed.
            if (sensors.Count > 0)
            {
                var hwinfoCategories = sensors.Select(s => s.Category).ToHashSet();

                var filteredFallback = fallback.Sensors
                    .Where(s => s.Source != "WindowsPerf" || !hwinfoCategories.Contains(s.Category))
                    .ToList();

                filteredFallback.AddRange(sensors);
                fallback = fallback with { Sensors = filteredFallback };
            }

            result = fallback;

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

    private static double FormatSensorValue(double value, string unit, SENSOR_READING_TYPE readingType)
    {
        // Convert MB to GB for large memory values
        if (unit.Equals("MB", StringComparison.OrdinalIgnoreCase) && value >= 1024)
            return Math.Round(value / 1024.0, 2);

        // Voltages keep decimal precision
        if (readingType == SENSOR_READING_TYPE.SENSOR_TYPE_VOLT)
            return Math.Round(value, 3);

        // Everything else: round to integer
        return Math.Round(value);
    }

    private static string NormalizeUnit(string unit, double originalValue)
    {
        if (unit.Equals("MB", StringComparison.OrdinalIgnoreCase) && originalValue >= 1024)
            return "GB";
        return unit;
    }

    /// <summary>
    /// Classifies an HWiNFO reading into a semantic <see cref="MetricKind"/> from its reading TYPE
    /// (authoritative, from HWiNFO itself) plus a label hint to split CPU/GPU/memory/network. Anything
    /// unrecognized stays <see cref="MetricKind.Unknown"/> — the safe sink that never binds a curated card.
    /// </summary>
    private static MetricKind ClassifyHwInfo(SENSOR_READING_TYPE t, string label)
    {
        var l = label.ToLowerInvariant();
        bool cpu = l.Contains("cpu") || l.Contains("core") || l.Contains("processor") || l.Contains("ccd");
        bool gpu = l.Contains("gpu") || l.Contains("graphics") || l.Contains("video");
        bool mem = l.Contains("memory") || l.Contains("ram") || l.Contains("physical") || l.Contains("ddr");
        bool net = l.Contains("network") || l.Contains("throughput") || l.Contains("bandwidth")
                   || l.Contains("nic") || l.Contains("ethernet") || l.Contains("wi-fi");
        return t switch
        {
            SENSOR_READING_TYPE.SENSOR_TYPE_USAGE when gpu => MetricKind.GpuLoad,
            SENSOR_READING_TYPE.SENSOR_TYPE_USAGE when mem => MetricKind.RamLoad,
            SENSOR_READING_TYPE.SENSOR_TYPE_USAGE when cpu => MetricKind.CpuLoad,
            SENSOR_READING_TYPE.SENSOR_TYPE_TEMP when gpu  => MetricKind.GpuTempC,
            SENSOR_READING_TYPE.SENSOR_TYPE_TEMP when cpu  => MetricKind.CpuTempC,
            SENSOR_READING_TYPE.SENSOR_TYPE_TEMP           => MetricKind.TempC,
            SENSOR_READING_TYPE.SENSOR_TYPE_CLOCK          => MetricKind.ClockMhz,
            SENSOR_READING_TYPE.SENSOR_TYPE_POWER          => MetricKind.PowerW,
            SENSOR_READING_TYPE.SENSOR_TYPE_FAN            => MetricKind.FanRpm,
            SENSOR_READING_TYPE.SENSOR_TYPE_VOLT           => MetricKind.VoltageV,
            SENSOR_READING_TYPE.SENSOR_TYPE_OTHER when net => MetricKind.NetThroughputMbps,
            _                                              => MetricKind.Unknown,
        };
    }

    /// <summary>
    /// Scales a value into the metric's canonical unit where the host reports a compatible-but-different
    /// unit. Currently only network throughput needs it (HWiNFO may report KB/s or MB/s; canonical is Mbps).
    /// </summary>
    private static double NormalizeValueForKind(MetricKind kind, double value, string hostUnit)
    {
        if (kind is MetricKind.NetThroughputMbps or MetricKind.NetDownMbps or MetricKind.NetUpMbps)
        {
            var u = hostUnit.ToLowerInvariant();
            if (u.Contains("kb")) return value * 0.008;   // KB/s → Mbps
            if (u.Contains("mb")) return value * 8.0;     // MB/s → Mbps
            return value;                                  // already Mbit/s
        }
        return value;
    }

    /// <summary>
    /// Semantic <see cref="MetricKind"/> for a reading. Uses the proven reading-type map
    /// (<see cref="ClassifyHwInfo"/>), then rescues memory readings HWiNFO reports as type OTHER
    /// (Physical Memory Load/Used/Total) so the RAM card can bind — the fix for the empty RAM card.
    /// </summary>
    private static MetricKind ClassifyKind(string category, SENSOR_READING_TYPE t, string label, string hostUnit)
    {
        var k = ClassifyHwInfo(t, label);
        if (k != MetricKind.Unknown) return k;
        if (category == "Memory")
        {
            var l = label.ToLowerInvariant();
            var u = (hostUnit ?? string.Empty).ToLowerInvariant();
            if (u == "%" && l.Contains("physical") && l.Contains("load")) return MetricKind.RamLoad;
            if ((u == "mb" || u == "gb") && l.Contains("physical") && l.Contains("used")) return MetricKind.RamUsedGb;
            if ((u == "mb" || u == "gb") && l.Contains("physical") && l.Contains("total")) return MetricKind.RamTotalGb;
        }
        return MetricKind.Unknown;
    }

    /// <summary>
    /// Buckets a reading into (coarse category, device group, short device tag) from HWiNFO's PARENT
    /// sensor name. The group keys the picker's collapsible sections (each drive its own); the tag
    /// prefixes the display name. This is what turns 166 "Other" readings into real categories.
    /// </summary>
    private static (string category, string group, string? dev) ClassifyDevice(string parent, string label)
    {
        var p = (parent ?? string.Empty).ToLowerInvariant();
        var l = label.ToLowerInvariant();
        var letter = DriveLetter(parent ?? string.Empty);
        bool driveish = p.StartsWith("drive:") || p.StartsWith("s.m.a.r.t") || p.Contains("ssd")
                        || p.Contains("nvme") || p.Contains("st8000") || p.Contains("pcie ssd");
        if (driveish)
        {
            var model = ShortModel(parent ?? string.Empty);
            var g = letter != null ? $"{model} [{letter}:]" : model;
            return ("Disk", g, g);
        }
        if (p.Contains("gpu") || p.Contains("geforce") || p.Contains("radeon") || p.Contains("nvidia") || p.Contains("rtx"))
            return ("GPU", "GPU", "GPU");
        if (p.StartsWith("network:"))
        {
            var sub = (p.Contains("wi-fi") || p.Contains("wifi")) ? "Wi-Fi" : "Ethernet";
            return ("Network", $"Network ({sub})", sub);
        }
        if (p.Contains("nuvoton") || p.Contains("nct") || p.Contains("chipset"))
            return ("Motherboard", "Motherboard", "MB");
        if (p.StartsWith("cpu [") || p.Contains("ryzen") || p.Contains("core i") || p.Contains("processor"))
            return ("CPU", "CPU", CpuShort(parent ?? string.Empty));
        if (p.Contains("dimm") || p.Contains("ddr") || p.Contains("memory timings")
            || (p.StartsWith("system:") && (l.Contains("memory") || l.Contains("page file"))))
            return ("Memory", "Memory", "RAM");
        if (p.Contains("presentmon"))
            return ("Performance", "Performance", "FPS");
        return ("Other", "Other", null);
    }

    /// <summary>Device-prefixes a label, avoiding a double prefix when the label already names the device.</summary>
    private static string DeviceName(string? dev, string label)
    {
        if (string.IsNullOrEmpty(dev)) return label;
        var first = dev.ToLowerInvariant().Split(' ')[0];
        if (!string.IsNullOrEmpty(first) && label.ToLowerInvariant().Contains(first)) return label;
        return $"{dev} · {label}";
    }

    /// <summary>Extracts a drive letter from a parent name like "... [C:]"; null if none.</summary>
    private static string? DriveLetter(string parent)
    {
        for (int i = parent.IndexOf('['); i >= 0 && i + 3 < parent.Length; i = parent.IndexOf('[', i + 1))
            if (char.IsLetter(parent[i + 1]) && parent[i + 2] == ':' && parent[i + 3] == ']')
                return char.ToUpperInvariant(parent[i + 1]).ToString();
        return null;
    }

    /// <summary>Short drive model: strips the "Drive:"/"S.M.A.R.T.:" prefix, "(serial)", "[C:]", and "SSD".</summary>
    private static string ShortModel(string parent)
    {
        var s = parent;
        int c = s.IndexOf(':');
        if (c >= 0 && c < 12) s = s.Substring(c + 1);
        s = StripBetween(s, '(', ')');
        s = StripBetween(s, '[', ']');
        s = s.Replace("SSD", " ").Replace("Series", " ");
        return string.Join(" ", s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StripBetween(string s, char open, char close)
    {
        int a;
        while ((a = s.IndexOf(open)) >= 0)
        {
            int b = s.IndexOf(close, a + 1);
            if (b < 0) break;
            s = s.Remove(a, b - a + 1);
        }
        return s;
    }

    /// <summary>Short CPU model: "CPU [#0]: AMD Ryzen 7 9800X3D" → "Ryzen 7 9800X3D".</summary>
    private static string CpuShort(string parent)
    {
        int c = parent.LastIndexOf(':');
        var s = c >= 0 ? parent.Substring(c + 1) : parent;
        return s.Replace("AMD", " ").Replace("Intel", " ").Trim();
    }

    private TelemetryPayload ReadWmiFallback()
    {
        var sensors = new System.Collections.Generic.List<SensorReading>();

        // CPU — isolated so PerformanceCounter failures don't wipe all sensors
        try
        {
            var cpuValue = _cpuCounter?.NextValue() ?? 0;
            sensors.Add(new SensorReading { Name = "Total CPU Usage", Value = cpuValue, Unit = "%", Category = "CPU", Source = "WindowsPerf", Kind = MetricKind.CpuLoad, Id = "wmi:cpu:load" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read CPU performance counter.");
        }

        // Memory — uses P/Invoke, very reliable
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                var usedMemBytes = memStatus.ullTotalPhys - memStatus.ullAvailPhys;
                sensors.Add(new SensorReading { Name = "Physical Memory Used", Value = usedMemBytes / 1e6, Unit = "MB", Category = "Memory", Source = "WindowsPerf" });
                sensors.Add(new SensorReading { Name = "Physical Memory Available", Value = memStatus.ullAvailPhys / 1e6, Unit = "MB", Category = "Memory", Source = "WindowsPerf" });
                sensors.Add(new SensorReading { Name = "Physical Memory Load", Value = memStatus.dwMemoryLoad, Unit = "%", Category = "Memory", Source = "WindowsPerf", Kind = MetricKind.RamLoad, Id = "wmi:mem:load" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read memory status.");
        }

        // Disk Activity
        try
        {
            var diskRead = _diskReadCounter?.NextValue() ?? 0;
            var diskWrite = _diskWriteCounter?.NextValue() ?? 0;
            sensors.Add(new SensorReading { Name = "Disk Read Rate", Value = diskRead / 1e6, Unit = "MB/s", Category = "Disk", Source = "WindowsPerf" });
            sensors.Add(new SensorReading { Name = "Disk Write Rate", Value = diskWrite / 1e6, Unit = "MB/s", Category = "Disk", Source = "WindowsPerf" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read disk performance counters.");
        }

        // Network Activity
        try
        {
            lock (_networkLock)
            {
                if (_activeNic != null)
                {
                    var stats = _activeNic.GetIPv4Statistics();
                    var now = DateTime.UtcNow;
                    var elapsedSeconds = (now - _lastNetworkPoll).TotalSeconds;

                    if (elapsedSeconds > 0)
                    {
                        var netDown = ((stats.BytesReceived - _lastBytesReceived) / 1024.0 / 1024.0) / elapsedSeconds;
                        var netUp = ((stats.BytesSent - _lastBytesSent) / 1024.0 / 1024.0) / elapsedSeconds;
                        sensors.Add(new SensorReading { Name = "Current DL Rate", Value = netDown >= 0 ? netDown : 0, Unit = "MB/s", Category = "Network", Source = "WindowsPerf" });
                        sensors.Add(new SensorReading { Name = "Current UP Rate", Value = netUp >= 0 ? netUp : 0, Unit = "MB/s", Category = "Network", Source = "WindowsPerf" });
                    }

                    _lastBytesReceived = stats.BytesReceived;
                    _lastBytesSent = stats.BytesSent;
                    _lastNetworkPoll = now;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read network counters.");
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
    public struct HWiNFO_SENSOR_ELEMENT
    {
        public uint dwSensorID;
        public uint dwSensorInst;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szSensorNameOrig;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szSensorNameUser;
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

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cpuCounter?.Dispose();
        _ramCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        GC.SuppressFinalize(this);
    }
}
