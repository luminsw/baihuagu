using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TaskRunner.Contracts.LocalModels;

namespace TaskRunner.Services
{
    /// <summary>
    /// 跨平台硬件信息检测服务
    /// </summary>
    public class HardwareInfoService
    {
        private readonly ILogger<HardwareInfoService> _logger;
        private readonly IMemoryCache _cache;
        private const string CacheKey = "hardware_info";

        /// <summary>
        /// GPU 规格数据库：关键字 -> (计算单元数, 显存带宽 GB/s, FP16 TFLOPS)
        /// 用于根据 GPU 名称匹配规格参数，进而估算 AI 推理性能。
        /// </summary>
        private static readonly Dictionary<string, (int CudaCores, double BandwidthGbps, double TflopsFp16)> GpuSpecDb = new(StringComparer.OrdinalIgnoreCase)
        {
            // NVIDIA RTX 50 系列 (Blackwell)
            ["rtx 5090"] = (21760, 1792, 450),
            ["rtx 5080"] = (10752, 960, 220),
            ["rtx 5070 ti"] = (8960, 896, 180),
            ["rtx 5070"] = (6144, 672, 140),
            ["rtx 5060 ti"] = (4608, 448, 100),
            ["rtx 5060"] = (3840, 384, 80),

            // NVIDIA RTX 40 系列 (Ada Lovelace)
            ["rtx 4090"] = (16384, 1008, 330),
            ["rtx 4080 super"] = (10240, 736, 260),
            ["rtx 4080"] = (9728, 717, 230),
            ["rtx 4070 ti super"] = (8448, 672, 200),
            ["rtx 4070 ti"] = (7680, 504, 160),
            ["rtx 4070 super"] = (7168, 504, 150),
            ["rtx 4070"] = (5888, 504, 120),
            ["rtx 4060 ti"] = (4352, 288, 90),
            ["rtx 4060"] = (3072, 272, 65),
            ["rtx 4050"] = (2560, 192, 45),

            // NVIDIA RTX 30 系列 (Ampere)
            ["rtx 3090 ti"] = (10752, 1008, 160),
            ["rtx 3090"] = (10496, 936, 140),
            ["rtx 3080 ti"] = (10240, 912, 135),
            ["rtx 3080"] = (8704, 760, 120),
            ["rtx 3070 ti"] = (6144, 608, 85),
            ["rtx 3070"] = (5888, 448, 80),
            ["rtx 3060 ti"] = (4864, 448, 65),
            ["rtx 3060"] = (3584, 360, 50),
            ["rtx 3050"] = (2560, 224, 35),

            // NVIDIA RTX 20 系列 (Turing)
            ["rtx 2080 ti"] = (4352, 616, 55),
            ["rtx 2080 super"] = (3072, 496, 40),
            ["rtx 2080"] = (2944, 448, 35),
            ["rtx 2070 super"] = (2560, 448, 32),
            ["rtx 2070"] = (2304, 448, 28),
            ["rtx 2060 super"] = (2176, 448, 27),
            ["rtx 2060"] = (1920, 336, 22),

            // AMD RX 7000 系列 (RDNA 3)
            ["rx 7900 xtx"] = (96, 960, 123),
            ["rx 7900 xt"] = (84, 800, 95),
            ["rx 7900 gre"] = (80, 640, 85),
            ["rx 7800 xt"] = (60, 624, 65),
            ["rx 7700 xt"] = (54, 432, 50),
            ["rx 7600"] = (32, 288, 30),
            ["rx 7600 xt"] = (32, 288, 32),

            // AMD RX 6000 系列 (RDNA 2)
            ["rx 6950 xt"] = (80, 576, 45),
            ["rx 6900 xt"] = (80, 512, 45),
            ["rx 6800 xt"] = (72, 512, 40),
            ["rx 6800"] = (60, 512, 35),
            ["rx 6750 xt"] = (40, 432, 25),
            ["rx 6700 xt"] = (40, 384, 25),
            ["rx 6650 xt"] = (32, 280, 20),
            ["rx 6600 xt"] = (32, 256, 18),
            ["rx 6600"] = (28, 224, 16),

            // Intel Arc 系列
            ["arc a770"] = (32, 560, 35),
            ["arc a750"] = (28, 512, 30),
            ["arc a580"] = (24, 512, 25),
            ["arc a380"] = (8, 186, 8),
            ["arc a310"] = (6, 124, 5),

            // Apple Silicon
            ["m3 ultra"] = (80, 819, 28),
            ["m3 max"] = (40, 409.6, 16.4),
            ["m3 pro"] = (18, 204.8, 8),
            ["m3"] = (10, 102.4, 4.1),
            ["m4 max"] = (40, 546, 16.2),
            ["m4 pro"] = (20, 273, 9),
            ["m4"] = (10, 120, 4.6),
            ["m2 ultra"] = (76, 800, 25),
            ["m2 max"] = (38, 400, 13),
            ["m2 pro"] = (19, 200, 6),
            ["m2"] = (10, 100, 4),
            ["m1 ultra"] = (64, 800, 21),
            ["m1 max"] = (32, 400, 10),
            ["m1 pro"] = (16, 200, 5),
            ["m1"] = (8, 68, 3),
        };

        public HardwareInfoService(ILogger<HardwareInfoService> logger, IMemoryCache cache)
        {
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// 获取当前硬件信息（启动时预热缓存，服务运行期间不变，手动刷新才更新）
        /// </summary>
        public HardwareInfoDto GetHardwareInfo()
        {
            if (_cache.TryGetValue(CacheKey, out HardwareInfoDto? cached) && cached != null)
            {
                _logger.LogDebug("硬件信息命中缓存");
                return cached;
            }

            var info = DetectHardwareInfo();
            _cache.Set(CacheKey, info);
            return info;
        }

        /// <summary>
        /// 强制重新检测硬件信息（用于手动刷新）
        /// </summary>
        public HardwareInfoDto RefreshHardwareInfo()
        {
            _cache.Remove(CacheKey);
            return GetHardwareInfo();
        }

        /// <summary>
        /// 预热缓存（应用启动时调用）
        /// </summary>
        public void WarmupCache()
        {
            if (_cache.TryGetValue(CacheKey, out _)) return;
            var info = DetectHardwareInfo();
            _cache.Set(CacheKey, info);
            _logger.LogInformation("硬件信息缓存已预热");
        }

        private HardwareInfoDto DetectHardwareInfo()
        {
            var info = new HardwareInfoDto
            {
                OsPlatform = GetOsName(),
                OsVersion = RuntimeInformation.OSDescription,
                IsWsl = DetectWsl(),
                Cpu = GetCpuInfo(),
                Memory = GetMemoryInfo(),
                Gpus = GetGpuInfo(),
            };

            // 检测系统内存带宽（集成显卡的"显存带宽"就是系统内存带宽）
            var systemMemoryBandwidth = DetectSystemMemoryBandwidth();

            // 为集成显卡补充显存估算（集成显卡共享系统内存，lspci 等工具获取的 BAR 大小不准确）
            foreach (var gpu in info.Gpus)
            {
                if (gpu.IsIntegrated && (!gpu.VramBytes.HasValue || gpu.VramBytes.Value < 512L * 1024 * 1024))
                {
                    // 估算为系统内存的 50%（集成显卡通常可使用大量系统内存作为显存）
                    gpu.VramBytes = info.Memory.TotalBytes / 2;
                }

                // ---- AI 算力与性能估算 ----
                MatchGpuSpecs(gpu);

                // 集成显卡未匹配到规格库时，使用系统内存带宽（而非根据显存大小瞎猜）
                if (!gpu.MemoryBandwidthGBps.HasValue && gpu.IsIntegrated && !gpu.IsAppleSilicon)
                {
                    gpu.MemoryBandwidthGBps = systemMemoryBandwidth ?? EstimateBandwidthFromCpu(info.Cpu);
                }

                EstimateGpuPerformance(gpu);
            }

            // 如果没有任何 GPU，或只有集成显卡，给 HardwareInfoDto 附加 CPU 估算
            if (info.Gpus.Count == 0 || info.Gpus.All(g => g.IsIntegrated && !g.IsAppleSilicon))
            {
                info.EstimatedCpuTokensPerSecond = EstimateCpuTokensPerSecond(info.Cpu, info.Memory);
            }

            // 磁盘空间（使用应用所在目录）
            var diskInfo = GetDiskSpace();
            info.TotalDiskSpaceBytes = diskInfo.Total;
            info.AvailableDiskSpaceBytes = diskInfo.Available;

            _logger.LogInformation(
                "硬件检测完成: CPU={Cpu}, 核心={Cores}, 内存={MemGiB:F1}GB, GPU={GpuCount}, 平台={Platform}",
                info.Cpu.Name, info.Cpu.LogicalProcessorCount, info.Memory.TotalGiB,
                info.Gpus.Count, info.OsPlatform);

            return info;
        }

        /// <summary>
        /// 根据硬件信息判定硬件等级
        /// </summary>
        public static HardwareTier GetHardwareTier(HardwareInfoDto hardware)
        {
            var gpus = hardware.Gpus;
            var totalRamGiB = hardware.Memory.TotalGiB;

            // 检测是否有独立显卡及其显存
            var dedicatedGpu = gpus.FirstOrDefault(g => !g.IsIntegrated && !g.IsAppleSilicon);
            if (dedicatedGpu != null && dedicatedGpu.VramGiB.HasValue)
            {
                var vram = dedicatedGpu.VramGiB.Value;
                if (vram >= 16) return HardwareTier.TopTierGpu;
                if (vram >= 8) return HardwareTier.HighEndGpu;
                if (vram >= 4) return HardwareTier.MidRangeGpu;
                return HardwareTier.LowEndGpu;
            }

            // Apple Silicon 统一内存
            var appleGpu = gpus.FirstOrDefault(g => g.IsAppleSilicon);
            if (appleGpu != null && appleGpu.VramGiB.HasValue)
            {
                var vram = appleGpu.VramGiB.Value;
                if (vram >= 16) return HardwareTier.TopTierGpu;
                if (vram >= 8) return HardwareTier.HighEndGpu;
                if (vram >= 4) return HardwareTier.MidRangeGpu;
                return HardwareTier.LowEndGpu;
            }

            // 集成显卡（Intel/AMD iGPU）
            var igpu = gpus.FirstOrDefault(g => g.IsIntegrated);
            if (igpu != null)
            {
                // 有集成显卡但无法确定显存，看内存
                if (totalRamGiB >= 16) return HardwareTier.LowEndGpu;
                return HardwareTier.CpuOnly;
            }

            // 无任何 GPU 信息，按内存判断
            if (totalRamGiB >= 32) return HardwareTier.LowEndGpu;
            return HardwareTier.CpuOnly;
        }

        #region CPU Detection

        private CpuInfoDto GetCpuInfo()
        {
            var cpu = new CpuInfoDto
            {
                LogicalProcessorCount = Environment.ProcessorCount,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            };

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    EnrichCpuInfoWindows(cpu);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    EnrichCpuInfoLinux(cpu);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    EnrichCpuInfoMac(cpu);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CPU 信息检测失败");
            }

            if (string.IsNullOrEmpty(cpu.Name))
                cpu.Name = $"Unknown {cpu.Architecture} CPU";

            return cpu;
        }

        private void EnrichCpuInfoWindows(CpuInfoDto cpu)
        {
            var output = RunCommand("wmic", "cpu get Name,NumberOfCores,NumberOfLogicalProcessors /value", 5000);
            if (string.IsNullOrEmpty(output)) return;

            cpu.Name = ExtractWmicValue(output, "Name") ?? cpu.Name;
            if (int.TryParse(ExtractWmicValue(output, "NumberOfCores"), out var cores))
                cpu.CoreCount = cores;
            if (int.TryParse(ExtractWmicValue(output, "NumberOfLogicalProcessors"), out var logical))
                cpu.LogicalProcessorCount = logical;
        }

        private void EnrichCpuInfoLinux(CpuInfoDto cpu)
        {
            // 优先 lscpu，更可靠。使用 LC_ALL=C 确保输出为英文，不受系统语言影响
            var lscpu = RunCommand("lscpu", "", 5000, new Dictionary<string, string> { ["LC_ALL"] = "C" });
            if (!string.IsNullOrEmpty(lscpu))
            {
                cpu.Name = ExtractLineValue(lscpu, "Model name:") ?? cpu.Name;
                if (int.TryParse(ExtractLineValue(lscpu, "Core(s) per socket:"), out var coresPerSocket))
                {
                    if (int.TryParse(ExtractLineValue(lscpu, "Socket(s):"), out var sockets))
                        cpu.CoreCount = coresPerSocket * sockets;
                }
                if (int.TryParse(ExtractLineValue(lscpu, "CPU(s):"), out var cpus))
                    cpu.LogicalProcessorCount = cpus;
                cpu.MaxFrequencyMHz = ExtractLineValue(lscpu, "CPU max MHz:") ?? ExtractLineValue(lscpu, "CPU MHz:");
                return;
            }

            // 回退到 /proc/cpuinfo
            try
            {
                var cpuinfo = File.ReadAllText("/proc/cpuinfo");
                var modelName = ExtractRegex(cpuinfo, @"model name\s*:\s*(.+)", RegexOptions.Multiline);
                if (!string.IsNullOrEmpty(modelName))
                    cpu.Name = modelName;

                var physicalIds = Regex.Matches(cpuinfo, @"physical id\s*:\s*(\d+)")
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .Count();
                var coresPerCpu = ExtractRegex(cpuinfo, @"cpu cores\s*:\s*(\d+)", RegexOptions.Multiline);
                if (int.TryParse(coresPerCpu, out var cpc) && physicalIds > 0)
                    cpu.CoreCount = cpc * physicalIds;
                else
                    cpu.CoreCount = cpu.LogicalProcessorCount;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取 /proc/cpuinfo 失败");
            }
        }

        private void EnrichCpuInfoMac(CpuInfoDto cpu)
        {
            cpu.Name = RunCommand("sysctl", "-n machdep.cpu.brand_string", 5000)?.Trim() ?? cpu.Name;
            if (int.TryParse(RunCommand("sysctl", "-n hw.physicalcpu", 5000)?.Trim(), out var phys))
                cpu.CoreCount = phys;
            if (int.TryParse(RunCommand("sysctl", "-n hw.logicalcpu", 5000)?.Trim(), out var log))
                cpu.LogicalProcessorCount = log;
        }

        #endregion

        #region Memory Detection

        private MemoryInfoDto GetMemoryInfo()
        {
            var mem = new MemoryInfoDto();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    EnrichMemoryWindows(mem);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    EnrichMemoryLinux(mem);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    EnrichMemoryMac(mem);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "内存信息检测失败");
            }
            return mem;
        }

        private void EnrichMemoryWindows(MemoryInfoDto mem)
        {
            var output = RunCommand("wmic", "computersystem get TotalPhysicalMemory /value", 5000);
            if (long.TryParse(ExtractWmicValue(output, "TotalPhysicalMemory"), out var total))
                mem.TotalBytes = total;

            // Available memory via WMI
            var osOutput = RunCommand("wmic", "os get FreePhysicalMemory /value", 5000);
            if (long.TryParse(ExtractWmicValue(osOutput, "FreePhysicalMemory"), out var freeKb))
                mem.AvailableBytes = freeKb * 1024;
        }

        private void EnrichMemoryLinux(MemoryInfoDto mem)
        {
            try
            {
                var meminfo = File.ReadAllText("/proc/meminfo");
                mem.TotalBytes = ParseMeminfoKB(meminfo, "MemTotal") * 1024;
                mem.AvailableBytes = ParseMeminfoKB(meminfo, "MemAvailable") * 1024;

                // 如果 MemAvailable 不存在（旧内核），用 MemFree + Buffers + Cached
                if (mem.AvailableBytes == 0)
                {
                    var free = ParseMeminfoKB(meminfo, "MemFree");
                    var buffers = ParseMeminfoKB(meminfo, "Buffers");
                    var cached = ParseMeminfoKB(meminfo, "Cached");
                    mem.AvailableBytes = (free + buffers + cached) * 1024;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取 /proc/meminfo 失败");
            }
        }

        private void EnrichMemoryMac(MemoryInfoDto mem)
        {
            if (long.TryParse(RunCommand("sysctl", "-n hw.memsize", 5000)?.Trim(), out var total))
                mem.TotalBytes = total;

            // vm_statistics64 for available memory
            var vmStats = RunCommand("vm_stat", "", 5000);
            if (!string.IsNullOrEmpty(vmStats))
            {
                var pageSize = 4096L; // default
                if (long.TryParse(RunCommand("sysctl", "-n vm.pagesize", 5000)?.Trim(), out var ps))
                    pageSize = ps;

                var freePages = ParseVmStat(vmStats, "Pages free");
                var inactivePages = ParseVmStat(vmStats, "Pages inactive");
                mem.AvailableBytes = (freePages + inactivePages) * pageSize;
            }
        }

        #endregion

        #region GPU Detection

        private List<GpuInfoDto> GetGpuInfo()
        {
            var gpus = new List<GpuInfoDto>();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    gpus.AddRange(GetGpuInfoWindows());
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    gpus.AddRange(GetGpuInfoLinux());
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    gpus.AddRange(GetGpuInfoMac());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPU 信息检测失败");
            }
            return gpus;
        }

        private List<GpuInfoDto> GetGpuInfoWindows()
        {
            var list = new List<GpuInfoDto>();

            // 优先尝试 nvidia-smi
            var nvidiaGpus = GetNvidiaGpuInfo();
            if (nvidiaGpus.Count > 0)
                return nvidiaGpus;

            // 回退到 WMI
            var output = RunCommand("wmic", "path win32_VideoController get Name,AdapterRAM /value", 5000);
            if (string.IsNullOrEmpty(output)) return list;

            var entries = output.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var name = ExtractWmicValue(entry, "Name");
                if (string.IsNullOrEmpty(name)) continue;

                var gpu = new GpuInfoDto
                {
                    Name = name,
                    Vendor = InferVendor(name),
                };

                if (long.TryParse(ExtractWmicValue(entry, "AdapterRAM"), out var ram))
                {
                    // WMI 有时返回 0xFFFFFFFF 表示大于 4GB
                    if (ram > 0 && ram < 0xFFFFFFFF)
                        gpu.VramBytes = ram;
                }

                gpu.IsIntegrated = IsIntegratedGpu(name);
                list.Add(gpu);
            }

            return list;
        }

        private List<GpuInfoDto> GetGpuInfoLinux()
        {
            var list = new List<GpuInfoDto>();

            // 优先 nvidia-smi
            var nvidiaGpus = GetNvidiaGpuInfo();
            list.AddRange(nvidiaGpus);

            // 非 NVIDIA GPU 通过 lspci 检测
            if (list.Count == 0)
            {
                var lspci = RunCommand("lspci", "", 5000);
                if (!string.IsNullOrEmpty(lspci))
                {
                    var vgaLines = lspci.Split('\n')
                        .Where(l => l.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
                                    l.Contains("3D controller", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var line in vgaLines)
                    {
                        var name = ExtractLspciDeviceName(line);
                        if (string.IsNullOrEmpty(name)) continue;

                        var gpu = new GpuInfoDto
                        {
                            Name = name,
                            Vendor = InferVendor(name),
                            IsIntegrated = IsIntegratedGpu(name),
                        };

                        // 尝试从 lspci -v -s {bus} 获取内存信息
                        var bus = line.Split(' ')[0];
                        if (!string.IsNullOrEmpty(bus))
                        {
                            var detail = RunCommand("lspci", $"-v -s {bus}", 5000);
                            if (!string.IsNullOrEmpty(detail))
                            {
                                var prefetch = Regex.Match(detail, @"prefetchable\)\s*\[size=(\d+)([KMGT])\]");
                                if (prefetch.Success)
                                {
                                    gpu.VramBytes = ParseSizeWithUnit(
                                        prefetch.Groups[1].Value,
                                        prefetch.Groups[2].Value);
                                }
                            }
                        }

                        list.Add(gpu);
                    }
                }
            }

            return list;
        }

        private List<GpuInfoDto> GetGpuInfoMac()
        {
            var list = new List<GpuInfoDto>();
            var output = RunCommand("system_profiler", "SPDisplaysDataType", 8000);
            if (string.IsNullOrEmpty(output)) return list;

            // Apple Silicon: 统一内存，显存 = 系统内存
            var isAppleSilicon = output.Contains("Apple M", StringComparison.OrdinalIgnoreCase);

            var chipMatches = Regex.Matches(output, @"Chipset Model:\s*(.+)", RegexOptions.Multiline);
            var vramMatches = Regex.Matches(output, @"VRAM \(Total\):\s*(.+)", RegexOptions.Multiline);

            for (int i = 0; i < chipMatches.Count; i++)
            {
                var name = chipMatches[i].Groups[1].Value.Trim();
                var gpu = new GpuInfoDto
                {
                    Name = name,
                    Vendor = isAppleSilicon ? "Apple" : InferVendor(name),
                    IsAppleSilicon = isAppleSilicon && name.StartsWith("Apple M", StringComparison.OrdinalIgnoreCase),
                };

                if (i < vramMatches.Count)
                {
                    var vramStr = vramMatches[i].Groups[1].Value.Trim();
                    gpu.VramBytes = ParseMacMemoryString(vramStr);
                }

                // Apple Silicon 统一内存：显存等于系统内存的一部分（通常动态分配）
                if (gpu.IsAppleSilicon && !gpu.VramBytes.HasValue)
                {
                    // 使用系统总内存作为上限标记
                    if (long.TryParse(RunCommand("sysctl", "-n hw.memsize", 5000)?.Trim(), out var totalMem))
                    {
                        gpu.VramBytes = totalMem; // 统一内存架构
                    }
                }

                list.Add(gpu);
            }

            return list;
        }

        private List<GpuInfoDto> GetNvidiaGpuInfo()
        {
            var list = new List<GpuInfoDto>();
            // 扩展查询字段：名称、显存总量、驱动版本、最大图形时钟、PCIe 链路代数
            var output = RunCommand("nvidia-smi",
                "--query-gpu=name,memory.total,driver_version,clocks.max.graphics,pcie.link.gen.max --format=csv,noheader", 5000);
            if (string.IsNullOrEmpty(output)) return list;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length < 2) continue;

                var gpu = new GpuInfoDto
                {
                    Name = parts[0],
                    Vendor = "NVIDIA",
                };

                gpu.VramBytes = ParseNvidiaMemory(parts[1]);
                if (parts.Length >= 3)
                    gpu.DriverVersion = parts[2];
                if (parts.Length >= 4 && double.TryParse(parts[3].Replace(" MHz", "").Trim(), out var clock))
                    gpu.MaxClockMHz = clock;

                list.Add(gpu);
            }

            return list;
        }

        /// <summary>
        /// 根据 GPU 型号名称匹配规格数据库，补全计算单元、带宽、算力等参数
        /// </summary>
        private static void MatchGpuSpecs(GpuInfoDto gpu)
        {
            var nameLower = gpu.Name.ToLowerInvariant();
            string? matchedKey = null;

            // 优先完全匹配（最长的关键字优先，避免 "rtx 4070" 匹配到 "rtx 4070 ti"）
            foreach (var key in GpuSpecDb.Keys.OrderByDescending(k => k.Length))
            {
                if (nameLower.Contains(key))
                {
                    matchedKey = key;
                    break;
                }
            }

            if (matchedKey != null)
            {
                var spec = GpuSpecDb[matchedKey];
                gpu.ComputeUnits = spec.CudaCores;
                gpu.MemoryBandwidthGBps = spec.BandwidthGbps;
                gpu.EstimatedTflopsFp16 = spec.TflopsFp16;
                gpu.EstimatedTflopsInt8 = spec.TflopsFp16 * 2;
                gpu.EstimatedTflopsInt4 = spec.TflopsFp16 * 4;
            }
            else if (!gpu.IsIntegrated && !gpu.IsAppleSilicon)
            {
                // 未匹配到数据库的独立显卡，根据 Vendor 和 VRAM 粗略推算带宽
                gpu.MemoryBandwidthGBps = InferBandwidthFromVram(gpu);
            }
            // 集成显卡和 Apple Silicon 的带宽由 DetectHardwareInfo 另行处理
        }

        /// <summary>
        /// 检测系统内存带宽（GB/s）。集成显卡没有独立显存，其带宽就是系统内存带宽。
        /// 公式：带宽 = 内存频率(MT/s) × 通道数 × 8 / 1000
        /// </summary>
        private double? DetectSystemMemoryBandwidth()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var output = RunCommand("dmidecode", "-t memory", 8000);
                    if (!string.IsNullOrEmpty(output))
                    {
                        var speeds = new List<double>();
                        string? currentType = null;
                        foreach (var line in output.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
                            {
                                currentType = trimmed.Substring(5).Trim();
                            }
                            else if ((trimmed.StartsWith("Speed:", StringComparison.OrdinalIgnoreCase) ||
                                      trimmed.StartsWith("Configured Memory Speed:", StringComparison.OrdinalIgnoreCase)) &&
                                     currentType != null &&
                                     (currentType.Contains("DDR", StringComparison.OrdinalIgnoreCase) ||
                                      currentType.Contains("LPDDR", StringComparison.OrdinalIgnoreCase) ||
                                      currentType.Contains("SDRAM", StringComparison.OrdinalIgnoreCase)))
                            {
                                var speedMatch = Regex.Match(trimmed, @"(\d+)\s*MT/s");
                                if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, out var speed) && speed > 0)
                                {
                                    speeds.Add(speed);
                                }
                            }
                        }

                        if (speeds.Count > 0)
                        {
                            var avgSpeed = speeds.Average();
                            var bandwidth = avgSpeed * speeds.Count * 8 / 1000;
                            _logger.LogInformation("检测到系统内存带宽: {Bandwidth:F1} GB/s ({Channels} 通道, {Speed:F0} MT/s)",
                                bandwidth, speeds.Count, avgSpeed);
                            return bandwidth;
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var output = RunCommand("wmic", "memorychip get Speed /value", 5000);
                    if (!string.IsNullOrEmpty(output))
                    {
                        var speeds = new List<double>();
                        foreach (Match match in Regex.Matches(output, @"Speed=(\d+)"))
                        {
                            if (double.TryParse(match.Groups[1].Value, out var speed) && speed > 0)
                                speeds.Add(speed);
                        }
                        if (speeds.Count > 0)
                        {
                            var avgSpeed = speeds.Average();
                            var bandwidth = avgSpeed * speeds.Count * 8 / 1000;
                            _logger.LogInformation("检测到系统内存带宽: {Bandwidth:F1} GB/s ({Channels} 通道, {Speed:F0} MHz)",
                                bandwidth, speeds.Count, avgSpeed);
                            return bandwidth;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "检测系统内存带宽失败");
            }

            return null;
        }

        /// <summary>
        /// 未匹配到规格库时，根据显存大小和厂商经验推算带宽（仅用于独立显卡）
        /// </summary>
        private static double? InferBandwidthFromVram(GpuInfoDto gpu)
        {
            if (!gpu.VramGiB.HasValue) return null;
            var vram = gpu.VramGiB.Value;
            var vendor = gpu.Vendor.ToLowerInvariant();

            // 经验公式：每 GB 显存约对应 40-80 GB/s 带宽（因代际和位宽差异大，仅作粗略估计）
            // 注意：此公式不适用于集成显卡，集成显卡应使用 DetectSystemMemoryBandwidth()
            return vendor switch
            {
                "nvidia" => vram * 65,
                "amd" => vram * 55,
                "intel" => vram * 35,
                "apple" => vram * 12, // Apple 统一内存按总内存算，带宽与内存封装有关
                _ => vram * 50,
            };
        }

        /// <summary>
        /// 无法检测内存带宽时，根据 CPU 信息做保守估算
        /// </summary>
        private static double EstimateBandwidthFromCpu(CpuInfoDto cpu)
        {
            var name = cpu.Name.ToLowerInvariant();
            var cores = cpu.LogicalProcessorCount;

            // 根据 CPU 代际和平台做经验估算
            // 移动/低压平台（LPDDR）
            // 注意：Intel 官方名称是 "Core(TM) Ultra"，Contains("core ultra") 匹配不到中间的 (tm)
            if ((name.Contains("core") && name.Contains("ultra")) || name.Contains("ryzen ai"))
                return 100; // LPDDR5X 双通道 ~100 GB/s
            if (name.Contains("apple m"))
                return 100; // Apple Silicon 基础款 ~100 GB/s

            // 桌面平台（DDR4/DDR5 双通道）
            if (name.Contains("ddr5") || name.Contains("i9-13") || name.Contains("i9-14") || name.Contains("i9-15"))
                return 80;
            if (name.Contains("ryzen 9") || name.Contains("ryzen 7"))
                return 70;

            // 服务器平台
            if (cores >= 32)
                return 200;

            // 保守默认值
            return cores >= 8 ? 60 : 40;
        }

        /// <summary>
        /// 估算 GPU 的 Llama-3-8B Q4_K_M 推理速度（tokens/秒）
        /// 核心公式：瓶颈是显存带宽，每生成 1 token 需要读取全部模型权重
        /// </summary>
        private static void EstimateGpuPerformance(GpuInfoDto gpu)
        {
            const double UtilizationFactor = 0.13;   // 框架效率系数（llama.cpp 等约 13% 的带宽利用率）
            const double AppleUtilizationFactor = 0.16; // Apple Silicon 统一内存效率稍高

            if (gpu.MemoryBandwidthGBps.HasValue && gpu.MemoryBandwidthGBps.Value > 0)
            {
                double factor = gpu.IsAppleSilicon ? AppleUtilizationFactor : UtilizationFactor;
                // 集成显卡效率更低
                if (gpu.IsIntegrated && !gpu.IsAppleSilicon)
                    factor = 0.08;

                gpu.EstimatedTokensPerSecond = Math.Round(gpu.MemoryBandwidthGBps.Value * factor, 1);
            }
            else if (gpu.VramGiB.HasValue)
            {
                // 连带宽都推算不出时，按显存大小做非常粗略的分级估算
                gpu.EstimatedTokensPerSecond = gpu.VramGiB.Value switch
                {
                    >= 24 => 100,
                    >= 16 => 70,
                    >= 12 => 50,
                    >= 8 => 35,
                    >= 6 => 25,
                    >= 4 => 15,
                    _ => 5,
                };
            }
        }

        /// <summary>
        /// 估算纯 CPU 推理速度（无 GPU 或仅集成显卡时）
        /// </summary>
        private static double EstimateCpuTokensPerSecond(CpuInfoDto cpu, MemoryInfoDto memory)
        {
            // CPU 推理受限于内存带宽和 SIMD 能力，经验值：双通道 DDR4/5 约 1-8 tok/s
            var cores = cpu.LogicalProcessorCount;
            if (cores <= 4) return 2;
            if (cores <= 8) return 4;
            if (cores <= 16) return 7;
            return 10; // 高端桌面/服务器 CPU
        }

        #endregion

        #region Disk Space

        private (long Total, long Available) GetDiskSpace()
        {
            try
            {
                var path = AppDomain.CurrentDomain.BaseDirectory;
                var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
                if (drive.IsReady)
                    return (drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取磁盘空间失败");
            }
            return (0, 0);
        }

        #endregion

        #region Helpers

        private static string GetOsName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
            return "Unknown";
        }

        private static bool DetectWsl()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;
            try
            {
                var release = File.ReadAllText("/proc/sys/kernel/osrelease");
                return release.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                       release.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private string? RunCommand(string fileName, string arguments, int timeoutMs, Dictionary<string, string>? env = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                if (env != null)
                {
                    foreach (var kv in env)
                    {
                        psi.EnvironmentVariables[kv.Key] = kv.Value;
                    }
                }

                using var process = Process.Start(psi);
                if (process == null) return null;

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                return process.StandardOutput.ReadToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "命令执行失败: {Cmd} {Args}", fileName, arguments);
                return null;
            }
        }

        private static string? ExtractWmicValue(string output, string key)
        {
            var match = Regex.Match(output, $"{key}=\\s*(.+?)(?:\\r?\\n|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        private static string? ExtractLineValue(string text, string prefix)
        {
            var lines = text.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(prefix.Length).Trim();
            }
            return null;
        }

        private static string? ExtractRegex(string text, string pattern, RegexOptions options = RegexOptions.None)
        {
            var match = Regex.Match(text, pattern, options);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        /// <summary>
        /// 从 lspci 输出行中提取干净的设备名称
        /// lspci 格式: "bus:dev.func Class: Vendor Device (rev xx)"
        /// </summary>
        private static string ExtractLspciDeviceName(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return "";

            // 去掉前导的 bus ID（第一个空格之前的部分）
            var afterBus = line;
            var firstSpace = line.IndexOf(' ');
            if (firstSpace >= 0)
                afterBus = line.Substring(firstSpace + 1).TrimStart();

            // 去掉 Class 名称（第一个冒号之前的部分）
            var firstColon = afterBus.IndexOf(':');
            if (firstColon >= 0)
                afterBus = afterBus.Substring(firstColon + 1).TrimStart();

            // 去掉 (rev xx) 后缀
            var revIdx = afterBus.IndexOf("(rev ", StringComparison.OrdinalIgnoreCase);
            if (revIdx >= 0)
                afterBus = afterBus.Substring(0, revIdx).Trim();

            return afterBus;
        }

        private static long ParseMeminfoKB(string text, string key)
        {
            var pattern = $"{key}\\s*:\\s*(\\d+)\\s*kB";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return match.Success && long.TryParse(match.Groups[1].Value, out var val) ? val : 0;
        }

        private static long ParseVmStat(string text, string key)
        {
            var pattern = $"{key}\\s*:\\s*(\\d+)";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return match.Success && long.TryParse(match.Groups[1].Value, out var val) ? val : 0;
        }

        private static long? ParseNvidiaMemory(string text)
        {
            // "11264 MiB" or "11.2 GB"
            var match = Regex.Match(text, @"([\d.]+)\s*(MiB|MB|GiB|GB)");
            if (!match.Success) return null;

            var val = double.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value.ToUpperInvariant();
            return unit.StartsWith("GI") || unit.StartsWith("GB")
                ? (long)(val * 1024 * 1024 * 1024)
                : (long)(val * 1024 * 1024);
        }

        private static long? ParseMacMemoryString(string text)
        {
            var match = Regex.Match(text, @"([\d.]+)\s*(MB|GB|TB)");
            if (!match.Success) return null;

            var val = double.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value.ToUpperInvariant();
            return unit switch
            {
                "MB" => (long)(val * 1024 * 1024),
                "GB" => (long)(val * 1024 * 1024 * 1024),
                "TB" => (long)(val * 1024L * 1024 * 1024 * 1024),
                _ => null
            };
        }

        private static long ParseSizeWithUnit(string value, string unit)
        {
            if (!double.TryParse(value, out var val)) return 0;
            return unit.ToUpperInvariant() switch
            {
                "K" => (long)(val * 1024),
                "M" => (long)(val * 1024 * 1024),
                "G" => (long)(val * 1024 * 1024 * 1024),
                "T" => (long)(val * 1024L * 1024 * 1024 * 1024),
                _ => 0
            };
        }

        private static string InferVendor(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("rtx") || lower.Contains("quadro"))
                return "NVIDIA";
            if (lower.Contains("amd") || lower.Contains("radeon") || lower.Contains("firepro"))
                return "AMD";
            if (lower.Contains("intel") || lower.Contains("arc") || lower.Contains("iris") || lower.Contains("hd graphics") || lower.Contains("uhd"))
                return "Intel";
            if (lower.Contains("apple"))
                return "Apple";
            return "Unknown";
        }

        private static bool IsIntegratedGpu(string name)
        {
            var lower = name.ToLowerInvariant();
            return lower.Contains("intel") &&
                   (lower.Contains("hd graphics") || lower.Contains("uhd") || lower.Contains("iris") || lower.Contains("intel graphics")) ||
                   lower.Contains("integrated");
        }

        #endregion
    }
}
