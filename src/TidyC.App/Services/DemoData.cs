using System.IO;
using System.Text.Json;
using TidyC.Core.Clean;
using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.Services;

/// <summary>
/// Deterministic, clearly labelled demo snapshot (--demo). It exercises every
/// view without touching a real volume; the UI shows a 演示数据 badge whenever it
/// is active.
/// </summary>
internal static class DemoData
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    public static void Seed(AppServices services)
    {
        ScanResult scan = BuildScan();
        AppDataAnalysisReport report = BuildAppDataReport();
        CleanupPlan plan = BuildPlan(report);

        services.ApplyScan(scan, report, plan);
        SeedHistory(services.History);
        SeedQuarantine(services.Quarantine);
    }

    private static ScanResult BuildScan()
    {
        var root = new SpaceNode { Name = "C:\\", FullPath = "C:\\", Kind = NodeKind.Volume };

        SpaceNode windows = Dir(root, "Windows", 0);
        Dir(windows, "System32", 38100 * Mb);
        Dir(windows, "WinSxS", 14400 * Mb);
        Dir(windows, "SoftwareDistribution", 6800 * Mb);
        Dir(windows, "Temp", 2400 * Mb);
        Dir(windows, "Logs", 1600 * Mb);
        Dir(windows, "SysWOW64", 900 * Mb);

        SpaceNode users = Dir(root, "Users", 0);
        SpaceNode admin = Dir(users, "Admin", 0);
        SpaceNode appData = Dir(admin, "AppData", 0);
        SpaceNode local = Dir(appData, "Local", 0);
        SpaceNode tencent = Dir(local, "Tencent", 0);
        Dir(tencent, "WeChat", 19400 * Mb);
        SpaceNode google = Dir(local, "Google", 0);
        Dir(google, "Chrome", 12800 * Mb);
        Dir(local, "Packages", 6100 * Mb);
        Dir(local, "Microsoft", 3300 * Mb);
        SpaceNode roaming = Dir(appData, "Roaming", 0);
        SpaceNode roamingTencent = Dir(roaming, "Tencent", 0);
        Dir(roamingTencent, "WeChat", 9600 * Mb);
        Dir(roaming, "Code", 1200 * Mb);
        Dir(roaming, "Microsoft", 400 * Mb);
        Dir(appData, "LocalLow", 400 * Mb);
        Dir(admin, "Documents", 12600 * Mb);
        Dir(admin, "Desktop", 8400 * Mb);
        Dir(admin, "Downloads", 9800 * Mb);
        Dir(admin, "Pictures", 5200 * Mb);
        Dir(admin, "Videos", 2600 * Mb);
        Dir(users, "Public", 1200 * Mb);

        SpaceNode programFiles = Dir(root, "Program Files", 0);
        Dir(programFiles, "Microsoft", 6200 * Mb);
        Dir(programFiles, "Common Files", 2100 * Mb);
        Dir(programFiles, "Apps", 6300 * Mb);

        SpaceNode programFilesX86 = Dir(root, "Program Files (x86)", 0);
        Dir(programFilesX86, "Apps", 5800 * Mb);

        SpaceNode programData = Dir(root, "ProgramData", 0);
        Dir(programData, "Package Cache", 4800 * Mb);
        Dir(programData, "Microsoft", 5100 * Mb);
        Dir(programData, "Docker", 2500 * Mb);

        Dir(root, "System Volume Information", 4100 * Mb);
        Dir(root, "$Recycle.Bin", 2300 * Mb);
        Dir(root, "Recovery", 1100 * Mb);
        Dir(root, "TidyC_Quarantine", 14200 * Mb);
        Dir(root, "Windows.old", 18400 * Mb);

        root.Aggregate();
        TreeOps.ApplyCategories(root, "C:\\");

        var spectrum = new SpectrumAccumulator();
        foreach (SpaceNode node in root.SelfAndDescendants())
        {
            if (node.DirectAllocatedBytes > 0)
            {
                spectrum.AddForPath(node.FullPath, node.DirectAllocatedBytes);
            }
        }

        long attributed = root.AllocatedBytes;
        long blindspot = 4100 * Mb;
        long total = 512L * 1024 * Mb;

        var volume = new VolumeFacts
        {
            Root = "C:\\",
            FileSystem = "NTFS",
            SerialNumber = 0x8F94B2C0,
            ClusterSizeBytes = 4096,
            TotalBytes = total,
            FreeBytes = total - attributed - blindspot,
            IsNtfs = true,
            NtfsVolumeDataAvailable = true,
        };

        var scan = new ScanResult
        {
            ScanId = $"SCAN_{DateTime.Now:yyyyMMdd_HHmmss}",
            GeneratedAt = DateTime.Now,
            DriveRoot = "C:\\",
            VolumeSerialNumber = "0x8F94B2C0",
            ClusterSizeBytes = volume.ClusterSizeBytes,
            EngineType = ScanEngines.MftStreamReader,
            DurationSeconds = 1.42,
            IsElevated = true,
            TotalRecordsRead = 486_213,
            TotalFilesCount = root.FileCount,
            TotalDirectoriesCount = root.DirectoryCount,
            HardLinkDeduplicatedCount = 12_840,
            HardLinkSavedBytes = (long)(4.12 * Gb),
            ReparsePointsSkippedCount = 1_284,
            AccessDeniedCount = 0,
            Categories = spectrum.ToSpectrum(),
            Root = root,
            Volume = volume,
        };

        scan.TopLargestFiles = new List<FileFact>
        {
            Fact(@"C:\Users\Admin\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState\ext4.vhdx", 24_300 * Mb, 26_140 * Mb),
            Fact(@"C:\Users\Admin\Downloads\VisualStudio2022.iso", 9_400 * Mb, 9_400 * Mb),
            Fact(@"C:\Users\Admin\Documents\VM\Win11.vhdx", 15_800 * Mb, 16_200 * Mb),
            Fact(@"C:\hiberfil.sys", 12_700 * Mb, 12_700 * Mb),
            Fact(@"C:\Users\Admin\AppData\Roaming\Tencent\WeChat\Msg\Multi\MSG0.db", 14_640 * Mb, 14_650 * Mb),
            Fact(@"C:\pagefile.sys", 8_192 * Mb, 8_192 * Mb),
            Fact(@"C:\Users\Admin\AppData\Local\Docker\wsl\data\ext4.vhdx", 12_600 * Mb, 13_100 * Mb),
            Fact(@"C:\Users\Admin\AppData\Local\Tencent\WeChat\All Users\Cache\Media\video_2025.mp4", 4_820 * Mb, 4_900 * Mb),
            Fact(@"C:\Users\Admin\Downloads\素材包.zip", 3_600 * Mb, 3_600 * Mb),
            Fact(@"C:\Users\Admin\AppData\Local\Google\Chrome\User Data\Default\Cache\data_3", 2_400 * Mb, 2_420 * Mb),
        };

        scan.TopLargestFolders = TreeOps.TopDirectories(root, 50);
        scan.Reconciliation = TreeOps.Reconcile(volume, attributed, 29_800 * Mb);
        scan.Warnings.Add("演示数据：未访问真实磁盘，所有数字均为示例");
        return scan;
    }

    private static FileFact Fact(string path, long allocated, long logical) => new()
    {
        Path = path,
        AllocatedBytes = allocated,
        LogicalBytes = logical,
        LastWriteUtc = DateTime.Now.AddDays(-3),
        Category = TreeOps.CategoryOfPath(path),
        FileId = "0x000F" + (uint)path.GetHashCode(),
    };

    private static SpaceNode Dir(SpaceNode parent, string name, long bytes)
    {
        var node = new SpaceNode
        {
            Name = name,
            FullPath = PathKey.Combine(parent.FullPath, name),
            Kind = NodeKind.Directory,
            DirectAllocatedBytes = bytes,
            AllocatedBytes = bytes,
            DirectFileCount = bytes > 0 ? Math.Max(1, bytes / 1_800_000L) : 0,
            LastWriteUtc = DateTime.Now.AddDays(-2),
        };

        parent.AddChild(node);
        return node;
    }

    private static AppDataAnalysisReport BuildAppDataReport()
    {
        Dictionary<string, CleanRule> rules = BuiltInRules.All.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var report = new AppDataAnalysisReport
        {
            ScanId = $"APPDATA_{DateTime.Now:yyyyMMdd_HHmmss}",
            GeneratedAt = DateTime.Now,
            RulesetSignature = RuleValidator.ComputeSignature(BuiltInRules.All),
            TargetScopes = new List<string> { "Local", "Roaming", "LocalLow" },
            ScanDurationSeconds = 4.18,
        };

        var wechat = Group("WeChat", "tencent.wechat");
        Add(wechat, rules, "WeChat_MediaCache_70", @"Local\Tencent\WeChat\All Users\Cache", 4_820 * Mb, 2_410, RiskLevel.Green);
        Add(wechat, rules, "WeChat_MsgDb_71", @"Roaming\Tencent\WeChat\Msg\Multi", 14_640 * Mb, 34, RiskLevel.Red);

        var chrome = Group("Google Chrome", "google.chrome");
        Add(chrome, rules, "Chrome_Cache_01", @"Local\Google\Chrome\User Data\Default\Cache", 6_100 * Mb, 18_240, RiskLevel.Green);
        Add(chrome, rules, "Chrome_CodeCache_02", @"Local\Google\Chrome\User Data\Default\Code Cache", 2_400 * Mb, 4_180, RiskLevel.Green);
        Add(chrome, rules, "Chrome_GPUCache_03", @"Local\Google\Chrome\User Data\Default\GPUCache", 900 * Mb, 1_240, RiskLevel.Green);
        Add(chrome, rules, "Chrome_ServiceWorker_04", @"Local\Google\Chrome\User Data\Default\Service Worker", 1_600 * Mb, 3_410, RiskLevel.Yellow);
        Add(chrome, rules, "Chrome_Extensions_11", @"Local\Google\Chrome\User Data\Default\Extensions", 2_200 * Mb, 8_420, RiskLevel.Yellow);
        Add(chrome, rules, "Chrome_LocalStorage_06", @"Local\Google\Chrome\User Data\Default\Local Storage", 4_200 * Mb, 12_180, RiskLevel.Red);
        Add(chrome, rules, "Chrome_Cookies_09", @"Local\Google\Chrome\User Data\Default\Network\Cookies", 180 * Mb, 6, RiskLevel.Red);
        Add(chrome, rules, "Chrome_LoginData_10", @"Local\Google\Chrome\User Data\Default\Login Data", 45 * Mb, 4, RiskLevel.Red);
        Add(chrome, rules, "Chrome_IndexedDb_07", @"Local\Google\Chrome\User Data\Default\IndexedDB", 1_200 * Mb, 9_240, RiskLevel.Red);

        var edge = Group("Microsoft Edge", "microsoft.edge");
        Add(edge, rules, "Edge_Cache_20", @"Local\Microsoft\Edge\User Data\Default\Cache", 3_300 * Mb, 9_180, RiskLevel.Green);
        Add(edge, rules, "Edge_ServiceWorker_21", @"Local\Microsoft\Edge\User Data\Default\Service Worker", 1_100 * Mb, 2_140, RiskLevel.Yellow);
        Add(edge, rules, "Edge_Cookies_22", @"Local\Microsoft\Edge\User Data\Default\Network\Cookies", 90 * Mb, 5, RiskLevel.Red);

        var code = Group("Visual Studio Code", "microsoft.vscode");
        Add(code, rules, "VSCode_Cache_66", @"Roaming\Code\Cache", 800 * Mb, 2_180, RiskLevel.Green);
        Add(code, rules, "VSCode_Cache_66", @"Roaming\Code\CachedData", 500 * Mb, 1_840, RiskLevel.Green);
        Add(code, rules, "VSCode_Extensions_67", @"Profile\.vscode\extensions", 1_600 * Mb, 7_240, RiskLevel.Yellow);

        var npm = Group("npm 缓存", "npm");
        Add(npm, rules, "Npm_Cache_60", @"Local\npm-cache", 1_860 * Mb, 24_180, RiskLevel.Green);

        var uwp = Group("UWP 应用沙盒", "microsoft.uwp");
        Add(uwp, rules, "UWP_PackageData_77", @"Local\Packages", 6_100 * Mb, 41_280, RiskLevel.Red);

        var teams = Group("Microsoft Teams", "microsoft.teams");
        Add(teams, rules, "Teams_Cache_72", @"Roaming\Microsoft\Teams\Cache", 600 * Mb, 1_840, RiskLevel.Green);
        Add(teams, rules, "Teams_ServiceWorker_73", @"Roaming\Microsoft\Teams\Service Worker", 300 * Mb, 640, RiskLevel.Yellow);
        Add(teams, rules, "Teams_Cache_72", @"Roaming\Microsoft\Teams\Local Storage", 600 * Mb, 240, RiskLevel.Red);

        var firefox = Group("Mozilla Firefox", "mozilla.firefox");
        Add(firefox, rules, "Firefox_Cache_30", @"Local\Mozilla\Firefox\Profiles\8x2k.default\cache2", 1_400 * Mb, 8_420, RiskLevel.Green);
        Add(firefox, rules, "Firefox_Storage_31", @"Roaming\Mozilla\Firefox\Profiles\8x2k.default\storage", 800 * Mb, 3_240, RiskLevel.Red);

        var gray = Group("未识别应用", "unknown.misc");
        gray.Items.Add(GrayItem(@"Local\SomeTool\data", 2_400 * Mb, 1_240));
        gray.Items.Add(GrayItem(@"Roaming\LegacyApp\cache", 1_100 * Mb, 840));
        gray.Items.Add(GrayItem(@"LocalLow\Unity\Company\Product", 580 * Mb, 1_920));

        report.Applications = new List<ApplicationGroup> { wechat, chrome, edge, code, npm, uwp, teams, firefox, gray };
        foreach (ApplicationGroup group in report.Applications)
        {
            group.TotalBytes = group.Items.Sum(i => i.SizeBytes);
            group.Items = group.Items.OrderBy(i => i.RiskLevel).ThenByDescending(i => i.SizeBytes).ToList();
        }

        report.Applications = report.Applications.OrderByDescending(g => g.TotalBytes).ToList();
        report.Summary = new AppDataSummary
        {
            GreenSafeBytes = report.Applications.SelectMany(a => a.Items).Where(i => i.RiskLevel == RiskLevel.Green).Sum(i => i.SizeBytes),
            YellowCautionBytes = report.Applications.SelectMany(a => a.Items).Where(i => i.RiskLevel == RiskLevel.Yellow).Sum(i => i.SizeBytes),
            RedPreserveBytes = report.Applications.SelectMany(a => a.Items).Where(i => i.RiskLevel == RiskLevel.Red).Sum(i => i.SizeBytes),
            GrayUnrecognizedBytes = report.Applications.SelectMany(a => a.Items).Where(i => i.RiskLevel == RiskLevel.Gray).Sum(i => i.SizeBytes),
        };
        report.Summary.TotalSizeBytes = report.Summary.GreenSafeBytes + report.Summary.YellowCautionBytes +
                                        report.Summary.RedPreserveBytes + report.Summary.GrayUnrecognizedBytes;
        return report;
    }

    private static ApplicationGroup Group(string name, string id) => new() { AppName = name, AppId = id };

    private static void Add(
        ApplicationGroup group,
        Dictionary<string, CleanRule> rules,
        string ruleId,
        string relative,
        long bytes,
        long files,
        RiskLevel risk)
    {
        CleanRule? rule = rules.GetValueOrDefault(ruleId);
        string[] parts = relative.Split('\\', 2);
        string scopeRoot = parts[0] switch
        {
            "Local" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Profile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        };

        string absolute = parts.Length == 2 ? Path.Combine(scopeRoot, parts[1]) : scopeRoot;
        group.Items.Add(new AppDataItem
        {
            RelativePath = relative,
            AbsolutePath = absolute,
            SizeBytes = bytes,
            FileCount = files,
            RiskLevel = risk,
            Purpose = rule?.Purpose ?? "示例条目",
            Evidence = rule?.Evidence ?? "匹配 BuiltInRule: " + ruleId,
            Consequence = rule?.Consequence ?? "示例后果",
            RuleId = ruleId,
            IsDirectory = !relative.EndsWith("Cookies", StringComparison.OrdinalIgnoreCase) &&
                          !relative.EndsWith("Login Data", StringComparison.OrdinalIgnoreCase),
            LastWriteUtc = DateTime.Now.AddDays(-4),
        });
    }

    private static AppDataItem GrayItem(string relative, long bytes, long files)
    {
        string[] parts = relative.Split('\\', 2);
        string scopeRoot = parts[0] switch
        {
            "Local" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        };

        return new AppDataItem
        {
            RelativePath = relative,
            AbsolutePath = parts.Length == 2 ? Path.Combine(scopeRoot, parts[1]) : scopeRoot,
            SizeBytes = bytes,
            FileCount = files,
            RiskLevel = RiskLevel.Gray,
            Purpose = "未识别目录：知识库未收录其用途",
            Evidence = "未命中任何 BuiltInRule 白名单条目",
            Consequence = "默认保留，硬编码禁止勾选；确认用途前不得清理",
            RuleId = "gray.fallback",
            IsDirectory = true,
            LastWriteUtc = DateTime.Now.AddDays(-9),
        };
    }

    private static CleanupPlan BuildPlan(AppDataAnalysisReport report)
    {
        var plan = new CleanupPlan { GeneratedAt = DateTime.Now };
        foreach (ApplicationGroup group in report.Applications)
        {
            foreach (AppDataItem item in group.Items.Where(i => i.RiskLevel.IsSelectable()))
            {
                CleanRule? rule = BuiltInRules.All.FirstOrDefault(r => r.Id == item.RuleId);
                if (rule is null)
                {
                    continue;
                }

                plan.Candidates.Add(new CleanupCandidate
                {
                    Rule = rule,
                    Path = item.AbsolutePath,
                    SizeBytes = item.SizeBytes,
                    FileCount = item.FileCount,
                    IsDirectory = item.IsDirectory,
                });

                if (item.RiskLevel == RiskLevel.Green)
                {
                    plan.GreenBytes += item.SizeBytes;
                }
                else
                {
                    plan.YellowBytes += item.SizeBytes;
                }
            }
        }

        plan.Candidates.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return plan;
    }

    private static void SeedHistory(ScanHistoryService history)
    {
        // Deterministic demo history: replace whatever was there.
        try
        {
            if (File.Exists(history.StoragePath))
            {
                File.Delete(history.StoragePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        for (int i = 2; i >= 0; i--)
        {
            long growth = i switch
            {
                2 => 226_400L * Mb,
                1 => 229_800L * Mb,
                _ => 232_700L * Mb,
            };

            var scan = new ScanResult
            {
                ScanId = $"SCAN_{DateTime.Now.AddDays(-i * 15):yyyyMMdd_HHmmss}",
                GeneratedAt = DateTime.Now.AddDays(-i * 15),
                EngineType = ScanEngines.MftStreamReader,
                DurationSeconds = 1.3 + i * 0.1,
                TotalFilesCount = 486_213 - (i * 3_200),
                TotalDirectoriesCount = 78_420,
                Reconciliation = new VolumeReconciliation
                {
                    EngineAttributedBytes = growth,
                    NtfsPhysicalUsedBytes = growth + (4_100L * Mb),
                    TotalVolumeBytes = 512L * 1024 * Mb,
                },
                Categories = new StorageSpectrum
                {
                    WindowsSystemBytes = 64_200L * Mb + (i * 420L * Mb),
                    AppDataBytes = 55_200L * Mb - (i * 1_400L * Mb),
                    UserProfilesBytes = 43_600L * Mb - (i * 900L * Mb),
                    ProgramFilesBytes = 20_400L * Mb,
                    ProtectedSystemBytes = 6_400L * Mb,
                    QuarantineBytes = 14_200L * Mb,
                },
            };

            history.Append(scan);
        }
    }

    private static void SeedQuarantine(QuarantineManager manager)
    {
        string sandbox = Path.Combine(AppPaths.DataDirectory, "DemoSandbox");
        Directory.CreateDirectory(sandbox);

        DateTime now = DateTime.Now;
        SeedBatch(
            manager,
            sandbox,
            "BATCH_20251020_093000",
            now.AddDays(-5),
            "微信与浏览器缓存（14 天可逆）",
            "ACTIVE",
            null,
            new[]
            {
                (@"WeChat\Cache", 8_420L * Mb, 12_400),
                (@"Chrome\Cache", 5_780L * Mb, 8_900),
            });

        SeedBatch(
            manager,
            sandbox,
            "BATCH_20251024_140500",
            now.AddDays(-1),
            "开发缓存（npm / VS Code）",
            "ACTIVE",
            null,
            new[]
            {
                (@"npm-cache", 3_100L * Mb, 5_200),
                (@"vscode-cache", 1_240L * Mb, 2_100),
            });

        SeedBatch(
            manager,
            sandbox,
            "BATCH_20251002_101500",
            now.AddDays(-23),
            "已到期并物理抹除的旧批次",
            "PURGED",
            now.AddDays(-9),
            new[]
            {
                (@"old-temp", 4_100L * Mb, 3_400),
            });

        SeedJournal(Path.Combine(manager.Root, "logs"), "journal-batch-move.jsonl");
        SeedJournal(Path.Combine(manager.Root, "logs"), "journal-batch-restore.jsonl");
    }

    private static void SeedBatch(
        QuarantineManager manager,
        string sandbox,
        string batchId,
        DateTime createdAt,
        string description,
        string status,
        DateTime? purgedAt,
        (string OriginalRelative, long Size, int Files)[] items)
    {
        string batchFolder = Path.Combine(manager.Root, batchId);
        string payloadFolder = Path.Combine(batchFolder, "payload");
        Directory.CreateDirectory(payloadFolder);

        var batch = new QuarantineBatch
        {
            BatchId = batchId,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(14),
            Description = description,
            Status = status,
            PurgedAt = purgedAt,
            MoveDurationSeconds = 0.032,
        };

        int index = 1;
        long total = 0;
        foreach ((string originalRelative, long size, int files) in items)
        {
            string leaf = Path.GetFileName(originalRelative);
            string payloadPath = Path.Combine(payloadFolder, $"{index:D4}_{leaf}");
            File.WriteAllBytes(payloadPath, new byte[64 * 1024]);
            index++;

            string original = Path.Combine(sandbox, originalRelative);
            batch.Records.Add(new QuarantineRecord
            {
                OriginalPath = original,
                QuarantinePath = payloadPath,
                FileIndexHex = string.Empty,
                SizeBytes = size,
                Sha256Hash = string.Empty,
                IsDirectory = true,
                IsRestored = false,
                Purged = status == "PURGED",
                MovedAt = createdAt,
                PurgedAt = purgedAt,
            });

            batch.ItemCount++;
            total += size;
        }

        batch.TotalBytes = total;
        string manifest = Path.Combine(batchFolder, "batch.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(batch, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        }));
    }

    private static void SeedJournal(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        using var journal = new AppendOnlyJournal(directory, fileName);
        if (journal.Count > 0)
        {
            return;
        }

        journal.Append("ATOMIC_MOVE_BATCH_START", "TX_DEMO_01", new Dictionary<string, object?>
        {
            ["batch_id"] = "BATCH_20251020_093000",
            ["item_count"] = 2,
            ["retention_days"] = 14,
        });
        journal.Append("ATOMIC_MOVE_COMMITTED", "TX_DEMO_01", new Dictionary<string, object?>
        {
            ["file_id"] = "0x000F00000004B21A",
            ["source"] = @"C:\Users\Admin\AppData\Local\Tencent\WeChat\All Users\Cache",
            ["quarantine_path"] = @"C:\TidyC_Quarantine\BATCH_20251020_093000\payload\0001_Cache",
            ["size_bytes"] = 8_420L * Mb,
            ["status"] = "SUCCESS",
        });
        journal.Append("ATOMIC_MOVE_BATCH_COMMITTED", "TX_DEMO_01", new Dictionary<string, object?>
        {
            ["batch_id"] = "BATCH_20251020_093000",
            ["item_count"] = 2,
            ["total_bytes"] = 14_200L * Mb,
            ["status"] = "ACTIVE",
        });
    }
}
