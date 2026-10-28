using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalizerIoT;

internal sealed record ImageIndexInfo(int Index, string Name, string Architecture, string Version);

internal sealed record LocalizationRequest(
    string SourceIso,
    string? PackageSource,
    string? FodSource,
    string? RussianSetupMedia,
    string[] ExplicitCabFiles,
    string OutputIso,
    bool AllIndexes,
    int SelectedIndex);

internal static class ImageServicingEngine
{
    private sealed record ProcResult(int ExitCode, string Output);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<ImageIndexInfo> InspectIso(string isoPath, Action<string> log)
    {
        if (!File.Exists(isoPath)) throw new FileNotFoundException("ISO-файл не найден.", isoPath);
        var dism = FindDism() ?? throw new InvalidOperationException("DISM.exe не найден. Запустите программу в Windows 10/11.");
        string? mountLetter = null;
        try
        {
            log("Подключаю ISO для чтения структуры…");
            mountLetter = MountIso(isoPath);
            var root = mountLetter + @":\";
            var sources = Path.Combine(root, "sources");
            var imageFile = new[] { "install.wim", "install.esd" }
                .Select(n => Path.Combine(sources, n)).FirstOrDefault(File.Exists);
            if (imageFile is null) throw new InvalidOperationException("В sources не найден install.wim или install.esd. Разделённые install.swm пока не поддерживаются для обслуживания.");
            log("Образ установки: " + imageFile);
            var info = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + imageFile);
            log(info.Output);
            EnsureSuccess(info, "Не удалось прочитать список редакций в образе установки.");
            var indices = Regex.Matches(info.Output, @"(?m)^\s*Index\s*:\s*(\d+)", RegexOptions.IgnoreCase)
                .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i).ToArray();
            var found = new List<ImageIndexInfo>();
            foreach (var index in indices)
            {
                var one = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + imageFile, "/Index:" + index);
                if (one.ExitCode != 0) continue;
                found.Add(ParseImageInfo(index, one.Output));
            }
            if (found.Count == 0) throw new InvalidOperationException("DISM не обнаружил ни одного индекса редакции.");
            return found;
        }
        finally
        {
            if (mountLetter is not null) TryDismountIso(isoPath);
        }
    }

    public static void Run(LocalizationRequest request, Action<int, string> report)
    {
        var warnings = new List<string>();
        var dism = FindDism() ?? throw new InvalidOperationException("DISM.exe не найден.");
        var oscdimg = FindOscdimg() ?? throw new InvalidOperationException("oscdimg.exe не найден. Установите Windows ADK → Deployment Tools.");
        var sourceIso = Path.GetFullPath(request.SourceIso);
        var outputIso = Path.GetFullPath(request.OutputIso);
        if (!File.Exists(sourceIso)) throw new FileNotFoundException("Исходный ISO не найден.", sourceIso);
        if (PathComparer.Equals(sourceIso, outputIso))
            throw new InvalidOperationException("Выходной ISO не может совпадать с исходным файлом.");
        if (!IsAdministrator()) throw new InvalidOperationException("Для обслуживания образов запустите программу от имени администратора.");
        var outputDirectory = Path.GetDirectoryName(outputIso)!;
        Directory.CreateDirectory(outputDirectory);
        var tempOutputIso = Path.Combine(outputDirectory,
            Path.GetFileNameWithoutExtension(outputIso) + ".building-" + Guid.NewGuid().ToString("N") + ".iso");
        var tempReport = tempOutputIso + ".report.txt";
        var sourceSize = new FileInfo(sourceIso).Length;
        var tempRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var outputRoot = Path.GetPathRoot(outputDirectory)!;
        var tempDrive = new DriveInfo(tempRoot);
        var outputDrive = new DriveInfo(outputRoot);
        var sameDrive = PathComparer.Equals(tempDrive.Name, outputDrive.Name);
        var gib = 1024L * 1024 * 1024;
        var requiredTemp = (long)(sourceSize * (sameDrive ? 3.0 : 2.2)) + 3 * gib;
        var requiredOutput = sameDrive ? requiredTemp : sourceSize + gib;
        if (tempDrive.AvailableFreeSpace < requiredTemp || outputDrive.AvailableFreeSpace < requiredOutput)
        {
            throw new IOException($"Недостаточно свободного места. Требуется ориентировочно {requiredTemp / gib} ГБ на диске временных файлов и {requiredOutput / gib} ГБ на диске результата; сейчас доступно {tempDrive.AvailableFreeSpace / gib} ГБ и {outputDrive.AvailableFreeSpace / gib} ГБ соответственно.");
        }

        var work = Path.Combine(Path.GetTempPath(), "LocalizerIoT-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string? targetIsoLetter = null;
        string? packageIsoLetter = null;
        string? packageRoot = null;
        string? fodIsoLetter = null;
        string? setupMediaLetter = null;
        string? setupMediaRoot = null;
        var mountDir = Path.Combine(work, "mount");
        var currentMount = false;

        try
        {
            report(2, "Проверяю исходный образ и источники языковых компонентов…");
            targetIsoLetter = MountIso(sourceIso);
            var sourceRoot = targetIsoLetter + @":\";
            var stage = Path.Combine(work, "distribution");
            report(5, "Копирую ISO в отдельную рабочую папку. Исходный файл не изменяется…");
            CopyDirectory(sourceRoot, stage);
            TryDismountIso(sourceIso);
            targetIsoLetter = null;

            var packageRoots = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.PackageSource))
            {
                var p = request.PackageSource.Trim();
                if (Directory.Exists(p)) packageRoots.Add(Path.GetFullPath(p));
                else if (File.Exists(p) && Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    packageIsoLetter = MountIso(Path.GetFullPath(p));
                    packageRoot = packageIsoLetter + @":\";
                    packageRoots.Add(packageRoot);
                }
                else throw new InvalidOperationException("Источник языковых пакетов должен быть существующей папкой или ISO-файлом.");
            }
            if (!string.IsNullOrWhiteSpace(request.RussianSetupMedia))
            {
                var setupMedia = request.RussianSetupMedia.Trim();
                if (Directory.Exists(setupMedia)) setupMediaRoot = Path.GetFullPath(setupMedia);
                else if (File.Exists(setupMedia) && Path.GetExtension(setupMedia).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    setupMediaLetter = MountIso(Path.GetFullPath(setupMedia));
                    setupMediaRoot = setupMediaLetter + @":\";
                }
                else throw new InvalidOperationException("Источник ресурсов Windows Setup должен быть существующей папкой или ISO-файлом.");
            }
            if (!string.IsNullOrWhiteSpace(request.FodSource))
            {
                var fodSource = request.FodSource.Trim();
                var sameAsPackageSource = !string.IsNullOrWhiteSpace(request.PackageSource) &&
                    PathComparer.Equals(Path.GetFullPath(fodSource), Path.GetFullPath(request.PackageSource.Trim()));
                if (!sameAsPackageSource)
                {
                    if (Directory.Exists(fodSource)) packageRoots.Add(Path.GetFullPath(fodSource));
                    else if (File.Exists(fodSource) && Path.GetExtension(fodSource).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        fodIsoLetter = MountIso(Path.GetFullPath(fodSource));
                        packageRoots.Add(fodIsoLetter + @":\");
                    }
                    else throw new InvalidOperationException("Источник Features on Demand должен быть существующей папкой или ISO-файлом.");
                }
            }
            packageRoots.AddRange(request.ExplicitCabFiles.Where(File.Exists).Select(Path.GetDirectoryName).Where(p => p is not null).Cast<string>());
            packageRoots = packageRoots.Distinct(PathComparer).ToList();

            var targetImageInfo = DetectFirstImageInfoFromMedia(stage, dism);
            var architecture = targetImageInfo.Architecture;
            if (architecture.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Не удалось определить архитектуру целевого образа. Обработка остановлена, чтобы не подобрать языковые пакеты другой архитектуры.");
            var targetBuild = GetBuildVersion(targetImageInfo.Version);
            if (targetBuild is null)
                throw new InvalidOperationException("Не удалось определить базовую версию целевого образа Windows. Обработка остановлена, чтобы не использовать несовместимые пакеты.");
            report(7, $"Целевая Windows: {targetImageInfo.Name}; версия {targetImageInfo.Version}; сборка {targetBuild}; архитектура {architecture}.");

            var discoveredPackages = DiscoverRussianPackages(packageRoots, request.ExplicitCabFiles);
            var packages = FilterForArchitecture(discoveredPackages, architecture, report);
            var capabilitySources = FindCapabilitySources(packageRoots);
            var windowsPackages = packages.Where(p => !IsWinPePackage(p) && !IsLanguageFeatureSatellite(p)).Distinct(PathComparer)
                .OrderByDescending(IsCoreLanguagePack).ThenBy(p => p, PathComparer).ToArray();
            if (!windowsPackages.Any(IsCoreLanguagePack))
                throw new InvalidOperationException("Не найден основной CAB-пакет русского языка для Windows (например, Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab). Укажите подходящий Language Pack ISO или папку с пакетами ru-RU. Неподходящие пакеты автоматически не скачиваются.");
            report(8, $"Обнаружена архитектура: {architecture}. Основных CAB/FOD-кандидатов: {packages.Count}; пакетов для Windows: {windowsPackages.Length}.");

            CopyRussianSetupResources(packageRoots, setupMediaRoot, stage, targetImageInfo.Version, architecture, dism, warnings, report);

            var sources = Path.Combine(stage, "sources");
            var installWim = Path.Combine(sources, "install.wim");
            var installEsd = Path.Combine(sources, "install.esd");
            if (!File.Exists(installWim) && !File.Exists(installEsd))
                throw new InvalidOperationException("После копирования не найден sources\\install.wim или sources\\install.esd.");

            var installIndices = new List<int>();
            if (File.Exists(installEsd))
            {
                var esdInfo = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + installEsd);
                EnsureSuccess(esdInfo, "Не удалось прочитать install.esd.");
                var sourceIndexes = Regex.Matches(esdInfo.Output, @"(?m)^\s*Index\s*:\s*(\d+)", RegexOptions.IgnoreCase)
                    .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i).ToArray();
                var selected = request.AllIndexes ? sourceIndexes : sourceIndexes.Where(i => i == request.SelectedIndex).ToArray();
                if (selected.Length == 0) throw new InvalidOperationException($"Индекс {request.SelectedIndex} отсутствует в install.esd.");
                if (File.Exists(installWim)) File.Delete(installWim);
                for (var n = 0; n < selected.Length; n++)
                {
                    report(10 + n * 5 / Math.Max(1, selected.Length), $"Экспортирую редакцию {selected[n]} из ESD в WIM ({n + 1}/{selected.Length})…");
                    var dest = Run(dism, "/Export-Image", "/SourceImageFile:" + installEsd, "/SourceIndex:" + selected[n],
                        "/DestinationImageFile:" + installWim, "/Compress:max", "/CheckIntegrity");
                    EnsureSuccess(dest, $"Не удалось экспортировать индекс {selected[n]} из install.esd.");
                    installIndices.Add(n + 1);
                }
                File.Delete(installEsd);
                report(15, $"ESD преобразован. В итоговом install.wim будет редакций: {installIndices.Count}.");
            }
            else
            {
                var wimInfo = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + installWim);
                EnsureSuccess(wimInfo, "Не удалось прочитать install.wim.");
                var all = Regex.Matches(wimInfo.Output, @"(?m)^\s*Index\s*:\s*(\d+)", RegexOptions.IgnoreCase)
                    .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i).ToArray();
                installIndices.AddRange(request.AllIndexes ? all : all.Where(i => i == request.SelectedIndex));
                if (installIndices.Count == 0) throw new InvalidOperationException($"Индекс {request.SelectedIndex} отсутствует в install.wim.");
            }

            Directory.CreateDirectory(mountDir);
            for (var i = 0; i < installIndices.Count; i++)
            {
                var index = installIndices[i];
                var progress = 18 + (int)(48.0 * i / Math.Max(1, installIndices.Count));
                report(progress, $"Обслуживаю install.wim, индекс {index} ({i + 1}/{installIndices.Count})…");
                var imageInfoResult = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + installWim, "/Index:" + index);
                EnsureSuccess(imageInfoResult, $"Не удалось прочитать install.wim индекс {index}.");
                var imageInfo = ParseImageInfo(index, imageInfoResult.Output);
                report(progress, $"Редакция {index}: {imageInfo.Name}; версия {imageInfo.Version}; архитектура {imageInfo.Architecture}.");
                var mount = Run(dism, "/Mount-Image", "/ImageFile:" + installWim, "/Index:" + index, "/MountDir:" + mountDir, "/CheckIntegrity");
                EnsureSuccess(mount, $"Не удалось смонтировать install.wim индекс {index}.");
                currentMount = true;
                try
                {
                    ApplyCabPackages(dism, mountDir, windowsPackages, imageInfo.Version, imageInfo.Architecture, warnings, report, progress, 25, 40);
                    TryAddLanguageCapabilities(dism, mountDir, packages.ToArray(), capabilitySources, warnings, report, progress);
                    SetRussianDefaults(dism, mountDir, warnings, report, progress);
                    TryServiceWinRe(dism, mountDir, work, windowsPackages, packages.ToArray(), capabilitySources, warnings, report, progress);
                    var langIni = Run(dism, "/Image:" + mountDir, "/Gen-LangINI", "/Distribution:" + stage);
                    if (langIni.ExitCode != 0) AddWarning(warnings, "DISM /Gen-LangINI не сработал: " + ShortOutput(langIni.Output));
                    var unmount = Run(dism, "/Unmount-Image", "/MountDir:" + mountDir, "/Commit", "/CheckIntegrity");
                    EnsureSuccess(unmount, $"Не удалось сохранить install.wim индекс {index}.");
                    currentMount = false;
                }
                catch
                {
                    if (currentMount)
                    {
                        Run(dism, "/Unmount-Image", "/MountDir:" + mountDir, "/Discard");
                        currentMount = false;
                    }
                    throw;
                }
            }

            CreateAutounattendIfMissing(stage, architecture, warnings, report);
            ServiceBootWimIfPossible(dism, stage, architecture, packageRoots, warnings, report);
            report(88, "Готовлю список языков установки и проверяю выходные файлы…");
            var langIniPath = Path.Combine(sources, "lang.ini");
            if (!File.Exists(langIniPath))
            {
                File.WriteAllText(langIniPath,
                    "[Available UI Languages]\r\nen-US = 3\r\nru-RU = 0\r\n\r\n[Fallback Languages]\r\nen-US = en-us\r\n",
                    new UTF8Encoding(false));
                AddWarning(warnings, "DISM не сгенерировал lang.ini; записан базовый список языков. Проверьте этот файл для конкретного образа.");
            }
            ServiceLangIniForBoot(dism, stage, architecture, warnings);

            report(93, "Создаю загрузочный ISO…");
            var bios = Path.Combine(stage, "boot", "etfsboot.com");
            var efiCandidates = new[]
            {
                Path.Combine(stage, "efi", "microsoft", "boot", "efisys.bin"),
                Path.Combine(stage, "efi", "microsoft", "boot", "efisys_noprompt.bin")
            };
            var efi = efiCandidates.FirstOrDefault(File.Exists);
            var bootArgs = new List<string> { "-m", "-o", "-u2", "-udfver102", "-lWIN_IOT_RU" };
            var bootData = File.Exists(bios) && efi is not null
                ? "-bootdata:2#p0,e,b" + bios + "#pEF,e,b" + efi
                : File.Exists(bios) ? "-b" + bios
                : throw new InvalidOperationException("Не найден boot\\etfsboot.com; невозможно создать BIOS-загрузочный ISO.");
            bootArgs.Add(bootData);
            var bootOrder = CreateBootOrderFile(stage, work);
            if (bootOrder is not null) bootArgs.Add("-yo" + bootOrder);
            bootArgs.Add(stage);
            bootArgs.Add(tempOutputIso);
            var isoBuild = Run(oscdimg, bootArgs.ToArray());
            if (isoBuild.ExitCode != 0) throw new InvalidOperationException("oscdimg не смог создать ISO:\r\n" + isoBuild.Output);
            if (!File.Exists(tempOutputIso) || new FileInfo(tempOutputIso).Length < 1024 * 1024)
                throw new InvalidOperationException("oscdimg сообщил об успехе, но выходной ISO отсутствует или выглядит повреждённым (меньше 1 МиБ).");

            var reportText = new StringBuilder();
            reportText.AppendLine("Localizer-IoT report");
            reportText.AppendLine("Source ISO: " + sourceIso);
            reportText.AppendLine("Output ISO: " + outputIso);
            reportText.AppendLine("Architecture: " + architecture);
            reportText.AppendLine("Image indexes serviced: " + string.Join(", ", installIndices));
            reportText.AppendLine("Language packages:");
            foreach (var package in windowsPackages) reportText.AppendLine("  " + package);
            reportText.AppendLine("Warnings / incomplete localization:");
            foreach (var warning in warnings) reportText.AppendLine("  - " + warning);
            reportText.AppendLine("Important: boot/WinRE servicing may be partial depending on the availability and matching version of WinPE language optional components.");
            File.WriteAllText(tempReport, reportText.ToString(), new UTF8Encoding(false));
            File.Move(tempOutputIso, outputIso, true);
            File.Move(tempReport, outputIso + ".report.txt", true);

            report(100, warnings.Count == 0
                ? "ISO создан. Итоговый отчёт сохранён рядом с ISO."
                : $"ISO создан с {warnings.Count} предупреждением(ями). Проверьте отчёт рядом с ISO.");
        }
        finally
        {
            if (currentMount)
            {
                try { Run(dism, "/Unmount-Image", "/MountDir:" + mountDir, "/Discard"); } catch { }
            }
            if (targetIsoLetter is not null) TryDismountIso(sourceIso);
            if (packageIsoLetter is not null && !string.IsNullOrWhiteSpace(request.PackageSource))
                TryDismountIso(Path.GetFullPath(request.PackageSource));
            if (setupMediaLetter is not null && !string.IsNullOrWhiteSpace(request.RussianSetupMedia))
                TryDismountIso(Path.GetFullPath(request.RussianSetupMedia));
            if (fodIsoLetter is not null && !string.IsNullOrWhiteSpace(request.FodSource))
                TryDismountIso(Path.GetFullPath(request.FodSource));
            try { Run(dism, "/Cleanup-Wim"); } catch { }
            try { Directory.Delete(work, true); } catch { }
            try { if (File.Exists(tempOutputIso)) File.Delete(tempOutputIso); } catch { }
            try { if (File.Exists(tempReport)) File.Delete(tempReport); } catch { }
        }
    }

    private static void ApplyCabPackages(string dism, string mountDir, string[] packages, string targetVersion, string targetArchitecture,
        List<string> warnings, Action<int, string> report, int baseProgress, int range, int offset)
    {
        var apply = packages.Where(p => !IsLanguageFeatureSatellite(p)).ToArray();
        if (apply.Length == 0) throw new InvalidOperationException("Не найдено применимых CAB-пакетов русского языка.");
        for (var i = 0; i < apply.Length; i++)
        {
            var package = apply[i];
            var p = Math.Clamp(baseProgress + offset + (int)(range * (double)i / apply.Length), 0, 96);
            report(p, $"Проверяю совместимость и добавляю языковой пакет {i + 1}/{apply.Length}: {Path.GetFileName(package)}");
            ValidatePackageCompatibility(dism, package, targetVersion, targetArchitecture, warnings);
            var add = Run(dism, "/Image:" + mountDir, "/Add-Package", "/PackagePath:" + package);
            if (add.ExitCode != 0)
                throw new InvalidOperationException($"DISM не смог добавить пакет {Path.GetFileName(package)}. Возможны несовпадение версии/архитектуры или отсутствие зависимости.\r\n{ShortOutput(add.Output)}");
        }
    }

    private static void ValidatePackageCompatibility(string dism, string package, string targetVersion, string targetArchitecture, List<string> warnings)
    {
        var info = Run(dism, "/English", "/Get-PackageInfo", "/PackagePath:" + package);
        if (info.ExitCode != 0)
        {
            AddWarning(warnings, $"Не удалось заранее прочитать метаданные CAB {Path.GetFileName(package)}; совместимость проверит DISM при установке.");
            return;
        }

        var identityMatch = Regex.Match(info.Output, @"(?im)^\s*Package Identity\s*:\s*(.+)$");
        if (!identityMatch.Success) identityMatch = Regex.Match(info.Output, @"(?im)^\s*Identity\s*:\s*(.+)$");
        if (!identityMatch.Success) return;
        var identity = identityMatch.Groups[1].Value.Trim();

        var targetBuild = GetBuildVersion(targetVersion);
        var packageVersionMatch = Regex.Match(identity, @"~(?<version>\d+\.\d+\.\d+(?:\.\d+)?)\s*$");
        if (targetBuild is not null && packageVersionMatch.Success)
        {
            var packageVersion = packageVersionMatch.Groups["version"].Value;
            var packageBuild = GetBuildVersion(packageVersion);
            if (packageBuild is not null && !AreCompatibleLanguagePackageBuilds(packageBuild, targetBuild))
                throw new InvalidOperationException(
                    $"CAB {Path.GetFileName(package)} имеет базовую версию {packageVersion}, а выбранный образ — {targetVersion}. Это не известная совместимая пара выпусков; пакет не устанавливается.");
        }

        var archMatch = Regex.Match(identity, @"~(?<arch>amd64|x86|arm64)~", RegexOptions.IgnoreCase);
        if (archMatch.Success)
        {
            var packageArch = archMatch.Groups["arch"].Value.ToLowerInvariant();
            var targetArch = targetArchitecture.Equals("x64", StringComparison.OrdinalIgnoreCase) ? "amd64" : targetArchitecture.ToLowerInvariant();
            if (!packageArch.Equals(targetArch, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"CAB {Path.GetFileName(package)} рассчитан на {packageArch}, а образ имеет архитектуру {targetArchitecture}.");
        }
    }

    private static string? GetBuildVersion(string version)
    {
        var match = Regex.Match(version ?? "", @"(?<build>\d+\.\d+\.\d+)(?:\.\d+)?");
        return match.Success ? match.Groups["build"].Value : null;
    }

    private static bool AreCompatibleLanguagePackageBuilds(string packageBuild, string targetBuild)
    {
        if (packageBuild.Equals(targetBuild, StringComparison.OrdinalIgnoreCase)) return true;

        // Windows 10 2004 through 22H2 share the 19041 servicing baseline.
        // Official language/FOD media for this family is commonly versioned 19041.1,
        // while LTSC 2021 / 21H2 images may report build 19044.
        var packageParts = packageBuild.Split('.');
        var targetParts = targetBuild.Split('.');
        if (packageParts.Length != 3 || targetParts.Length != 3) return false;
        if (!packageParts[0].Equals(targetParts[0], StringComparison.Ordinal) ||
            !packageParts[1].Equals(targetParts[1], StringComparison.Ordinal)) return false;

        if (int.TryParse(packageParts[2], out var packageMajorBuild) &&
            int.TryParse(targetParts[2], out var targetMajorBuild))
        {
            if (packageMajorBuild == 19041 && targetMajorBuild is >= 19041 and <= 19045) return true;
            // Windows 11 23H2 is an enablement release based on the 22621 servicing baseline.
            if (packageMajorBuild == 22621 && targetMajorBuild is 22621 or 22631) return true;
        }
        return false;
    }

    private static ImageIndexInfo ReadImageInfo(string dism, string imageFile, int index)
    {
        var detailed = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + imageFile, "/Index:" + index);
        EnsureSuccess(detailed, $"DISM не смог прочитать индекс {index} в образе {imageFile}.");
        return ParseImageInfo(index, detailed.Output);
    }

    private static void TryAddLanguageCapabilities(string dism, string mountDir, string[] packages, string[] capabilitySources, List<string> warnings,
        Action<int, string> report, int progress)
    {
        var fodPackagesExist = packages.Any(IsLanguageFeatureSatellite);
        if (!fodPackagesExist)
        {
            AddWarning(warnings, "В предоставленном источнике не обнаружены Russian Language Features on Demand; OCR/речь/рукописный ввод могут остаться на английском или отсутствовать.");
            return;
        }

        var capabilities = new[]
        {
            "Language.Basic~~~ru-RU~0.0.1.0",
            "Language.Fonts~~~und-Latn~0.0.1.0",
            "Language.OCR~~~ru-RU~0.0.1.0",
            "Language.TextToSpeech~~~ru-RU~0.0.1.0",
            "Language.Speech~~~ru-RU~0.0.1.0",
            "Language.Handwriting~~~ru-RU~0.0.1.0"
        };
        foreach (var cap in capabilities)
        {
            var sources = capabilitySources;
            if (sources.Length == 0)
            {
                AddWarning(warnings, $"Не найден источник FOD для {cap}.");
                continue;
            }
            var done = false;
            foreach (var source in sources)
            {
                var result = Run(dism, "/Image:" + mountDir, "/Add-Capability", "/CapabilityName:" + cap,
                    "/Source:" + source, "/LimitAccess");
                if (result.ExitCode == 0 || result.Output.Contains("already installed", StringComparison.OrdinalIgnoreCase))
                {
                    report(progress + 2, $"Компонент языка добавлен/уже установлен: {cap}");
                    done = true;
                    break;
                }
                if (!result.Output.Contains("0x800f081f", StringComparison.OrdinalIgnoreCase) &&
                    !result.Output.Contains("source files could not be found", StringComparison.OrdinalIgnoreCase))
                    AddWarning(warnings, $"DISM не смог добавить FOD {cap}: {ShortOutput(result.Output)}");
            }
            if (!done) AddWarning(warnings, $"FOD {cap} не установлен: отсутствует совместимый источник или компонент не поддерживается данным выпуском.");
        }
    }

    private static string[] FindCapabilitySources(IEnumerable<string> roots)
    {
        var found = new HashSet<string>(PathComparer);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            found.Add(root);
            foreach (var dirName in new[] { "LanguagesAndOptionalFeatures", "Languages and Optional Features", "FeaturesOnDemand", "FOD", "FoD" })
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, dirName, SearchOption.AllDirectories).Take(10))
                        found.Add(dir);
                }
                catch { }
            }
        }
        return found.ToArray();
    }

    private static void SetRussianDefaults(string dism, string mountDir, List<string> warnings, Action<int, string> report, int progress)
    {
        report(progress + 3, "Настраиваю язык интерфейса, регион и клавиатуру ru-RU…");
        TryIntl(dism, mountDir, "/Set-AllIntl:ru-RU", warnings);
        TryIntl(dism, mountDir, "/Set-UILang:ru-RU", warnings);
        TryIntl(dism, mountDir, "/Set-SysLocale:ru-RU", warnings);
        TryIntl(dism, mountDir, "/Set-UserLocale:ru-RU", warnings);
        TryIntl(dism, mountDir, "/Set-InputLocale:0419:00000419;0409:00000409", warnings);
        TryIntl(dism, mountDir, "/Set-TimeZone:Russian Standard Time", warnings);
    }

    private static void TryIntl(string dism, string mountDir, string setting, List<string> warnings)
    {
        var r = Run(dism, "/Image:" + mountDir, setting);
        if (r.ExitCode != 0) AddWarning(warnings, $"Не удалось применить {setting}: {ShortOutput(r.Output)}");
    }

    private static void TryServiceWinRe(string dism, string windowsMount, string work, string[] windowsPackages,
        string[] allLanguagePackages, string[] capabilitySources, List<string> warnings, Action<int, string> report, int progress)
    {
        var winre = Path.Combine(windowsMount, "Windows", "System32", "Recovery", "winre.wim");
        if (!File.Exists(winre))
        {
            report(progress + 5, "WinRE не найден внутри этого индекса; пропускаю встроенную среду восстановления.");
            return;
        }
        var languagePacks = windowsPackages.Where(IsCoreLanguagePack).ToArray();
        if (languagePacks.Length == 0)
        {
            AddWarning(warnings, "WinRE найден, но основной русский Language Pack не обнаружен; WinRE не локализован.");
            return;
        }
        var winreCopy = Path.Combine(work, "winre-copy-" + Guid.NewGuid().ToString("N") + ".wim");
        var winreMount = Path.Combine(work, "winre-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(winreMount);
        var isMounted = false;
        try
        {
            File.Copy(winre, winreCopy, true);
            var mount = Run(dism, "/Mount-Image", "/ImageFile:" + winreCopy, "/Index:1", "/MountDir:" + winreMount, "/CheckIntegrity");
            if (mount.ExitCode != 0) throw new InvalidOperationException(ShortOutput(mount.Output));
            isMounted = true;
            var winreInfo = ReadFirstImageInfo(dism, winreCopy);
            foreach (var cab in languagePacks)
            {
                ValidatePackageCompatibility(dism, cab, winreInfo.Version, winreInfo.Architecture, warnings);
                var add = Run(dism, "/Image:" + winreMount, "/Add-Package", "/PackagePath:" + cab);
                if (add.ExitCode != 0)
                    throw new InvalidOperationException($"Не удалось добавить русский Language Pack в WinRE ({Path.GetFileName(cab)}): {ShortOutput(add.Output)}");
            }
            TryAddLanguageCapabilities(dism, winreMount, allLanguagePackages, capabilitySources, warnings, report, progress + 2);
            var intl = Run(dism, "/Image:" + winreMount, "/Set-AllIntl:ru-RU");
            if (intl.ExitCode != 0) AddWarning(warnings, "Не удалось настроить locale ru-RU в WinRE: " + ShortOutput(intl.Output));
            var commit = Run(dism, "/Unmount-Image", "/MountDir:" + winreMount, "/Commit", "/CheckIntegrity");
            EnsureSuccess(commit, "Не удалось зафиксировать локализацию WinRE.");
            isMounted = false;
            File.Copy(winreCopy, winre, true);
            report(progress + 6, "WinRE обработан. Проверьте предупреждения о недостающих language FOD.");
        }
        catch (Exception ex)
        {
            AddWarning(warnings, "WinRE остался без гарантированной полной локализации: " + ex.Message);
            if (isMounted) { Run(dism, "/Unmount-Image", "/MountDir:" + winreMount, "/Discard"); isMounted = false; }
        }
        finally
        {
            if (isMounted) { try { Run(dism, "/Unmount-Image", "/MountDir:" + winreMount, "/Discard"); } catch { } }
            try { Directory.Delete(winreMount, true); } catch { }
            try { if (File.Exists(winreCopy)) File.Delete(winreCopy); } catch { }
        }
    }

    private static void ServiceBootWimIfPossible(string dism, string stage, string architecture, List<string> packageRoots,
        List<string> warnings, Action<int, string> report)
    {
        var bootWim = Path.Combine(stage, "sources", "boot.wim");
        if (!File.Exists(bootWim))
        {
            AddWarning(warnings, "boot.wim отсутствует; локализация Windows Setup/WinPE не выполнена.");
            return;
        }
        var winpeRoot = FindWinPePackageRoot(packageRoots);
        if (winpeRoot is null || !HasRequiredSetupLanguagePackages(winpeRoot))
            winpeRoot = FindAdkWinPePackageRoot(architecture) ?? winpeRoot;
        if (winpeRoot is null)
        {
            AddWarning(warnings, "Не найдены совместимые по структуре WinPE language optional components ru-RU в Language/FoD media или ADK. boot.wim оставлен без изменений.");
            return;
        }
        var winpePackages = GetWinPeCabs(winpeRoot);
        var lp = winpePackages.FirstOrDefault(p => Path.GetFileName(p).Equals("lp.cab", StringComparison.OrdinalIgnoreCase));
        var setup = winpePackages.FirstOrDefault(p => Path.GetFileName(p).Contains("WinPE-Setup_ru-ru", StringComparison.OrdinalIgnoreCase));
        var setupClient = winpePackages.FirstOrDefault(p => Path.GetFileName(p).Contains("WinPE-Setup-Client_ru-ru", StringComparison.OrdinalIgnoreCase));
        if (lp is null || setup is null || setupClient is null)
        {
            AddWarning(warnings, "Для Windows Setup нужны lp.cab, WinPE-Setup_ru-ru.cab и WinPE-Setup-Client_ru-ru.cab из совместимого ADK/WinPE language media; boot.wim оставлен без изменений.");
            return;
        }

        var info = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + bootWim);
        if (info.ExitCode != 0) { AddWarning(warnings, "Не удалось прочитать boot.wim; Windows Setup останется без локализации."); return; }
        var indices = Regex.Matches(info.Output, @"(?m)^\s*Index\s*:\s*(\d+)", RegexOptions.IgnoreCase)
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i).ToArray();
        if (!indices.Contains(2))
        {
            AddWarning(warnings, "В boot.wim нет стандартного индекса 2 Windows Setup. Локализация boot.wim пропущена, чтобы не изменить неизвестный образ.");
            return;
        }
        ImageIndexInfo bootSetupInfo;
        try { bootSetupInfo = ReadImageInfo(dism, bootWim, 2); }
        catch (Exception ex)
        {
            AddWarning(warnings, "Не удалось определить версию boot.wim index 2: " + ex.Message);
            return;
        }
        try
        {
            ValidatePackageCompatibility(dism, lp, bootSetupInfo.Version, bootSetupInfo.Architecture, warnings);
            ValidatePackageCompatibility(dism, setup, bootSetupInfo.Version, bootSetupInfo.Architecture, warnings);
            ValidatePackageCompatibility(dism, setupClient, bootSetupInfo.Version, bootSetupInfo.Architecture, warnings);
        }
        catch (Exception ex)
        {
            AddWarning(warnings, "WinPE language packages don't match boot.wim (" + bootSetupInfo.Version + "/" + bootSetupInfo.Architecture + "); boot.wim оставлен без изменений: " + ex.Message);
            return;
        }
        var mountDir = Path.Combine(Path.GetTempPath(), "LocalizerIoT-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mountDir);
        var failed = false;
        for (var pos = 0; pos < indices.Length; pos++)
        {
            var index = indices[pos];
            var mount = Run(dism, "/Mount-Image", "/ImageFile:" + bootWim, "/Index:" + index, "/MountDir:" + mountDir, "/CheckIntegrity");
            if (mount.ExitCode != 0) { AddWarning(warnings, $"Не удалось смонтировать boot.wim index {index}: {ShortOutput(mount.Output)}"); failed = true; break; }
            var mounted = true;
            try
            {
                var setupIndex = index == 2;
                var required = setupIndex ? new[] { lp, setup, setupClient } : new[] { lp };
                foreach (var cab in required)
                {
                    var add = Run(dism, "/Image:" + mountDir, "/Add-Package", "/PackagePath:" + cab);
                    if (add.ExitCode != 0)
                        throw new InvalidOperationException($"Не применён обязательный WinPE пакет {Path.GetFileName(cab)}: {ShortOutput(add.Output)}");
                }
                // Add only the required language packs to boot.wim. Other WinPE optional components should
                // only be localized when their neutral packages are present and the component is installed.
                var intl = Run(dism, "/Image:" + mountDir, "/Set-AllIntl:ru-RU");
                if (intl.ExitCode != 0) AddWarning(warnings, "Не удалось установить locale ru-RU в boot.wim: " + ShortOutput(intl.Output));
                if (setupIndex)
                {
                    var setUi = Run(dism, "/Image:" + mountDir, "/Set-SetupUILang:ru-RU", "/Distribution:" + stage);
                    if (setUi.ExitCode != 0) AddWarning(warnings, "Не удалось установить язык Windows Setup: " + ShortOutput(setUi.Output));
                    var generatedLangIni = Path.Combine(stage, "sources", "lang.ini");
                    if (File.Exists(generatedLangIni))
                    {
                        var toBoot = Path.Combine(mountDir, "sources", "lang.ini");
                        Directory.CreateDirectory(Path.GetDirectoryName(toBoot)!);
                        File.Copy(generatedLangIni, toBoot, true);
                    }
                }
                var unmount = Run(dism, "/Unmount-Image", "/MountDir:" + mountDir, "/Commit", "/CheckIntegrity");
                EnsureSuccess(unmount, $"Не удалось сохранить boot.wim index {index}.");
                mounted = false;
                report(80 + (int)(5.0 * (pos + 1) / Math.Max(1, indices.Length)), $"Сохранён boot.wim index {index}.");
            }
            catch (Exception ex)
            {
                if (mounted) Run(dism, "/Unmount-Image", "/MountDir:" + mountDir, "/Discard");
                failed = true;
                AddWarning(warnings, $"boot.wim index {index} оставлен без изменений из-за несовместимого WinPE пакета: {ex.Message}");
                break;
            }
        }
        try { Run(dism, "/Cleanup-Wim"); } catch { }
        try { Directory.Delete(mountDir, true); } catch { }
        if (failed) AddWarning(warnings, "Локализация boot.wim неполная. Проверьте matching ADK/WinPE add-on для версии целевого Windows.");
    }

    private static void ServiceLangIniForBoot(string dism, string stage, string architecture, List<string> warnings)
    {
        var langIni = Path.Combine(stage, "sources", "lang.ini");
        if (File.Exists(langIni))
        {
            var content = File.ReadAllText(langIni);
            if (!content.Contains("ru-RU", StringComparison.OrdinalIgnoreCase))
                AddWarning(warnings, "sources\\lang.ini не содержит ru-RU. Проверьте список языков после запуска DISM /Gen-LangINI.");
        }
    }

    private static void CreateAutounattendIfMissing(string stage, string architecture, List<string> warnings, Action<int, string> report)
    {
        var path = Path.Combine(stage, "autounattend.xml");
        if (File.Exists(path))
        {
            AddWarning(warnings, "В исходном ISO уже был autounattend.xml; он сохранён без изменений во избежание потери пользовательских настроек. Проверьте его locale settings вручную.");
            return;
        }
        var arch = architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" :
            architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "x86" : "amd64";
        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">
  <settings pass=""windowsPE"">
    <component name=""Microsoft-Windows-International-Core-WinPE"" processorArchitecture=""{arch}"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"">
      <SetupUILanguage><UILanguage>ru-RU</UILanguage></SetupUILanguage>
      <InputLocale>0419:00000419;0409:00000409</InputLocale><SystemLocale>ru-RU</SystemLocale><UILanguage>ru-RU</UILanguage><UserLocale>ru-RU</UserLocale><UILanguageFallback>en-US</UILanguageFallback>
    </component>
  </settings>
  <settings pass=""specialize"">
    <component name=""Microsoft-Windows-International-Core"" processorArchitecture=""{arch}"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"">
      <InputLocale>0419:00000419;0409:00000409</InputLocale><SystemLocale>ru-RU</SystemLocale><UILanguage>ru-RU</UILanguage><UserLocale>ru-RU</UserLocale><UILanguageFallback>en-US</UILanguageFallback>
    </component>
    <component name=""Microsoft-Windows-Shell-Setup"" processorArchitecture=""{arch}"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"">
      <TimeZone>Russian Standard Time</TimeZone>
    </component>
  </settings>
  <settings pass=""oobeSystem"">
    <component name=""Microsoft-Windows-International-Core"" processorArchitecture=""{arch}"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"">
      <InputLocale>0419:00000419;0409:00000409</InputLocale><SystemLocale>ru-RU</SystemLocale><UILanguage>ru-RU</UILanguage><UserLocale>ru-RU</UserLocale><UILanguageFallback>en-US</UILanguageFallback>
    </component>
  </settings>
</unattend>";
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        report(86, "Добавлен autounattend.xml только с региональными параметрами ru-RU (страницы выбора диска/учётной записи не скрываются).");
    }

    private static void CopyRussianSetupResources(List<string> packageRoots, string? setupMediaRoot, string stage,
        string targetVersion, string targetArchitecture, string dism, List<string> warnings, Action<int, string> report)
    {
        var candidateRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(setupMediaRoot)) candidateRoots.Add(setupMediaRoot);
        candidateRoots.AddRange(packageRoots);
        foreach (var root in candidateRoots.Distinct(PathComparer))
        {
            var russianSources = Path.Combine(root, "sources", "ru-ru");
            if (!Directory.Exists(russianSources)) continue;

            var setupImage = new[] { Path.Combine(root, "sources", "install.wim"), Path.Combine(root, "sources", "install.esd") }
                .FirstOrDefault(File.Exists);
            if (setupImage is not null)
            {
                try
                {
                    var info = ReadFirstImageInfo(dism, setupImage);
                    var targetBuild = GetBuildVersion(targetVersion);
                    var mediaBuild = GetBuildVersion(info.Version);
                    if (targetBuild is not null && mediaBuild is not null && !targetBuild.Equals(mediaBuild, StringComparison.OrdinalIgnoreCase))
                    {
                        AddWarning(warnings, $"Ресурсы Setup из {root} не скопированы: база {info.Version} не совпадает с целевой Windows {targetVersion}.");
                        continue;
                    }
                    if (!info.Architecture.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
                        !info.Architecture.Equals(targetArchitecture, StringComparison.OrdinalIgnoreCase))
                    {
                        AddWarning(warnings, $"Ресурсы Setup из {root} не скопированы: архитектура {info.Architecture} не совпадает с целевой {targetArchitecture}.");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    AddWarning(warnings, $"Не удалось проверить совместимость ресурсов Setup из {root}: {ex.Message}. Ресурсы не скопированы.");
                    continue;
                }
            }
            else
            {
                AddWarning(warnings, $"В источнике ресурсов Setup {root} нет install.wim/esd; его версию нельзя проверить автоматически.");
            }

            var target = Path.Combine(stage, "sources", "ru-ru");
            CopyDirectory(russianSources, target);
            report(9, "Скопированы локализованные ресурсы Windows Setup: sources\\ru-ru.");
            return;
        }
        AddWarning(warnings, "Не найдены совместимые локализованные файлы Windows Setup в sources\\ru-ru. При отсутствии этих файлов часть экранов Setup может остаться английской.");
    }

    private static string[] DiscoverRussianPackages(IEnumerable<string> packageRoots, IEnumerable<string> explicitFiles)
    {
        var found = new HashSet<string>(PathComparer);
        foreach (var file in explicitFiles)
        {
            if (!File.Exists(file)) continue;
            if (Path.GetExtension(file).Equals(".msu", StringComparison.OrdinalIgnoreCase) || IsLikelyWindowsUpdatePackage(file))
                throw new InvalidOperationException("Пакеты обновления не применяются: ты просил не обновлять Windows. Удали MSU/LCU/KB/SSU CAB и выбирай только CAB языкового пакета или FOD.");
            if (Path.GetExtension(file).Equals(".cab", StringComparison.OrdinalIgnoreCase) && IsPotentialLanguageComponent(file))
                found.Add(Path.GetFullPath(file));
        }
        foreach (var root in packageRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.cab", SearchOption.AllDirectories))
                    if (IsRussianPath(file) && !IsLikelyWindowsUpdatePackage(file) && IsPotentialLanguageComponent(file))
                        found.Add(Path.GetFullPath(file));
            }
            catch { /* inaccessible folder is reported by the lack of packages below */ }
        }
        return found.OrderBy(p => p, PathComparer).ToArray();
    }

    private static List<string> FilterForArchitecture(string[] packages, string architecture, Action<int, string> report)
    {
        var kept = new List<string>();
        foreach (var package in packages)
        {
            var name = Path.GetFileName(package).ToLowerInvariant();
            var packageArch = name.Contains("arm64") ? "arm64" : name.Contains("x64") || name.Contains("amd64") ? "x64" :
                Regex.IsMatch(name, @"(^|[_-])x86([_.-]|$)") ? "x86" : "";
            var normalizedTarget = architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase) ? "x64" : architecture;
            if (!string.IsNullOrEmpty(packageArch) && !packageArch.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                report(8, $"Пропускаю CAB другой архитектуры: {Path.GetFileName(package)}");
                continue;
            }
            kept.Add(package);
        }
        return kept;
    }

    private static bool HasRequiredSetupLanguagePackages(string winpeRoot)
    {
        var files = GetWinPeCabs(winpeRoot).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToArray();
        return files.Any(n => n.Equals("lp.cab", StringComparison.OrdinalIgnoreCase)) &&
            files.Any(n => n.Contains("WinPE-Setup_ru-ru", StringComparison.OrdinalIgnoreCase)) &&
            files.Any(n => n.Contains("WinPE-Setup-Client_ru-ru", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetWinPeCabs(string winpeRoot)
    {
        if (!Directory.Exists(winpeRoot)) return Array.Empty<string>();
        return Directory.GetFiles(winpeRoot, "*.cab", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetFileName(p).Equals("lp.cab", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(p).Contains("ru-ru", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p).Equals("lp.cab", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p, PathComparer).ToArray();
    }

    private static string? FindWinPePackageRoot(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var lp in Directory.EnumerateFiles(root, "lp.cab", SearchOption.AllDirectories).Take(100))
                {
                    var dir = Path.GetDirectoryName(lp)!;
                    if (dir.Contains("WinPE_OCs", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(dir).Equals("ru-ru", StringComparison.OrdinalIgnoreCase)) return dir;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? FindAdkWinPePackageRoot(string architecture)
    {
        var archFolder = architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" :
            architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "x86" : "amd64";
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        foreach (var root in roots.Where(Directory.Exists))
        {
            var adk = Path.Combine(root, "Windows Kits", "10", "Assessment and Deployment Kit", "Windows Preinstallation Environment", archFolder, "WinPE_OCs", "ru-ru");
            if (File.Exists(Path.Combine(adk, "lp.cab"))) return adk;
        }
        return null;
    }

    private static bool IsRussianPath(string path) =>
        path.Contains("ru-ru", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("ru_ru", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("russian", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyWindowsUpdatePackage(string path)
    {
        var name = Path.GetFileName(path);
        return Regex.IsMatch(name, @"KB\d{6,}", RegexOptions.IgnoreCase) ||
               name.Contains("Package_for_RollupFix", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ServicingStack", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Cumulative", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(name, @"(^|[-_.])LCU([-_.]|$)", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(name, @"(^|[-_.])SSU([-_.]|$)", RegexOptions.IgnoreCase);
    }

    private static bool IsPotentialLanguageComponent(string path)
    {
        if (IsLikelyWindowsUpdatePackage(path)) return false;
        if (IsCoreLanguagePack(path) || IsWinPePackage(path) || IsLanguageFeatureSatellite(path)) return true;
        var name = Path.GetFileName(path);
        return IsRussianPath(path) &&
            (name.Contains("LanguagePack", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Language-Pack", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("LanguageFeature", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("FoD", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Features-On-Demand", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCoreLanguagePack(string path)
    {
        var name = Path.GetFileName(path);
        return IsRussianPath(path) &&
            (name.Contains("Language-Pack", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("LanguagePack", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Client-Language-Pack", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Server-Language-Pack", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWinPePackage(string path) =>
        path.Contains("WinPE_OCs", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("WinPE-", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("lp.cab", StringComparison.OrdinalIgnoreCase);

    private static bool IsLanguageFeatureSatellite(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains("LanguageFeatures-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("LanguageFeature-", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageIndexInfo DetectFirstImageInfoFromMedia(string stage, string dism)
    {
        var candidate = new[] { Path.Combine(stage, "sources", "install.wim"), Path.Combine(stage, "sources", "install.esd") }
            .FirstOrDefault(File.Exists);
        if (candidate is null) throw new InvalidOperationException("Не найден install.wim или install.esd для определения версии/архитектуры.");
        return ReadFirstImageInfo(dism, candidate);
    }

    private static ImageIndexInfo ReadFirstImageInfo(string dism, string imageFile)
    {
        var info = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + imageFile);
        EnsureSuccess(info, "DISM не смог прочитать метаданные образа " + imageFile);
        var index = Regex.Matches(info.Output, @"(?m)^\s*Index\s*:\s*(\d+)", RegexOptions.IgnoreCase)
            .Select(m => int.Parse(m.Groups[1].Value)).FirstOrDefault();
        if (index <= 0) throw new InvalidOperationException("В образе отсутствуют индексы: " + imageFile);
        var detailed = Run(dism, "/English", "/Get-WimInfo", "/WimFile:" + imageFile, "/Index:" + index);
        EnsureSuccess(detailed, "DISM не смог прочитать первый индекс образа " + imageFile);
        return ParseImageInfo(index, detailed.Output);
    }

    private static ImageIndexInfo ParseImageInfo(int index, string output)
    {
        static string Value(string text, string key)
        {
            var m = Regex.Match(text, @"(?im)^\s*" + Regex.Escape(key) + @"\s*:\s*(.+)$");
            return m.Success ? m.Groups[1].Value.Trim() : "unknown";
        }
        var name = Value(output, "Name");
        var arch = Value(output, "Architecture");
        if (arch.Contains("ARM64", StringComparison.OrdinalIgnoreCase)) arch = "arm64";
        else if (arch.Contains("x86", StringComparison.OrdinalIgnoreCase)) arch = "x86";
        else if (arch.Contains("x64", StringComparison.OrdinalIgnoreCase) || arch.Contains("amd64", StringComparison.OrdinalIgnoreCase)) arch = "x64";
        var version = Value(output, "Version");
        return new ImageIndexInfo(index, name, arch, version);
    }

    private static string? CreateBootOrderFile(string stage, string work)
    {
        var paths = new[]
        {
            "boot\\bcd", "boot\\boot.sdi", "boot\\bootfix.bin", "boot\\bootsect.exe",
            "boot\\etfsboot.com", "boot\\memtest.efi", "boot\\memtest.exe",
            "boot\\en-us\\bootsect.exe.mui", "boot\\fonts\\chs_boot.ttf", "boot\\fonts\\cht_boot.ttf",
            "boot\\fonts\\jpn_boot.ttf", "boot\\fonts\\kor_boot.ttf", "boot\\fonts\\wgl4_boot.ttf",
            "sources\\boot.wim"
        };
        var existing = paths.Where(p => File.Exists(Path.Combine(stage, p))).ToArray();
        if (!existing.Any(p => p.Equals("sources\\boot.wim", StringComparison.OrdinalIgnoreCase))) return null;
        var file = Path.Combine(work, "BootOrder.txt");
        File.WriteAllLines(file, existing, new UTF8Encoding(false));
        return file;
    }

    private static string MountIso(string isoPath)
    {
        var escaped = isoPath.Replace("'", "''");
        var command = "$ErrorActionPreference='Stop'; $img=Mount-DiskImage -ImagePath '" + escaped +
                      "' -PassThru; ($img | Get-Volume | Where-Object { $_.DriveLetter } | Select-Object -First 1 -ExpandProperty DriveLetter)";
        var p = RunPowerShell(command);
        EnsureSuccess(p, "Не удалось подключить ISO: " + isoPath);
        var letter = p.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
            .LastOrDefault(s => Regex.IsMatch(s, @"^[A-Za-z]$"));
        if (letter is null) throw new InvalidOperationException("Windows подключила ISO, но буква диска не определена: " + isoPath);
        return letter;
    }

    private static void TryDismountIso(string isoPath)
    {
        try
        {
            var escaped = isoPath.Replace("'", "''");
            RunPowerShell("$ErrorActionPreference='Continue'; Dismount-DiskImage -ImagePath '" + escaped + "' -ErrorAction SilentlyContinue");
        }
        catch { }
    }

    private static ProcResult RunPowerShell(string command) =>
        Run("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command);

    private static ProcResult Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var sb = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return new ProcResult(process.ExitCode, sb.ToString());
    }

    private static void EnsureSuccess(ProcResult result, string message)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException(message + Environment.NewLine + ShortOutput(result.Output));
    }

    private static string ShortOutput(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length <= 1800) return trimmed;
        return trimmed[^1800..];
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning, StringComparer.OrdinalIgnoreCase)) warnings.Add(warning);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static string? FindDism()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(windows, "System32", "dism.exe"),
            Path.Combine(windows, "Sysnative", "dism.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record SetupKitRelease(string Name, string AdkUrl, string WinPeUrl);

    public static bool HasCompatibleWinPePackages(string sourceIso, IEnumerable<string> extraRoots, Action<string> log)
    {
        string? mounted = null;
        try
        {
            var dism = FindDism() ?? throw new InvalidOperationException("DISM.exe не найден.");
            mounted = MountIso(sourceIso);
            var bootWim = Path.Combine(mounted + @":\", "sources", "boot.wim");
            if (!File.Exists(bootWim))
            {
                log("В исходном ISO нет sources\\boot.wim — отдельная локализация WinPE не требуется.");
                return true;
            }

            var bootInfo = ReadImageInfo(dism, bootWim, 2);
            var roots = new List<string>();
            foreach (var root in extraRoots.Where(Directory.Exists))
            {
                var found = FindWinPePackageRoot(new[] { root });
                if (found is not null) roots.Add(found);
            }

            var adkRoot = FindAdkWinPePackageRoot(bootInfo.Architecture);
            if (adkRoot is not null) roots.Add(adkRoot);

            foreach (var root in roots.Distinct(PathComparer))
            {
                if (!HasRequiredSetupLanguagePackages(root)) continue;
                var files = GetWinPeCabs(root);
                var lp = files.FirstOrDefault(p => Path.GetFileName(p).Equals("lp.cab", StringComparison.OrdinalIgnoreCase));
                var setup = files.FirstOrDefault(p => Path.GetFileName(p).Contains("WinPE-Setup_ru-ru", StringComparison.OrdinalIgnoreCase));
                var client = files.FirstOrDefault(p => Path.GetFileName(p).Contains("WinPE-Setup-Client_ru-ru", StringComparison.OrdinalIgnoreCase));
                if (lp is null || setup is null || client is null) continue;

                var checkWarnings = new List<string>();
                try
                {
                    ValidatePackageCompatibility(dism, lp, bootInfo.Version, bootInfo.Architecture, checkWarnings);
                    ValidatePackageCompatibility(dism, setup, bootInfo.Version, bootInfo.Architecture, checkWarnings);
                    ValidatePackageCompatibility(dism, client, bootInfo.Version, bootInfo.Architecture, checkWarnings);
                    if (checkWarnings.Count == 0)
                    {
                        log($"Найден совместимый WinPE language source: {root} (boot.wim {bootInfo.Version}/{bootInfo.Architecture}).");
                        return true;
                    }
                    log($"WinPE source {root} не прошёл проверку метаданных: {string.Join("; ", checkWarnings)}");
                }
                catch (Exception ex)
                {
                    log($"WinPE source {root} несовместим с boot.wim: {ex.Message}");
                }
            }

            log($"Не найден проверенный WinPE source ru-RU для boot.wim {bootInfo.Version}/{bootInfo.Architecture}.");
            return false;
        }
        catch (Exception ex)
        {
            log("Не удалось автоматически проверить WinPE language source: " + ex.Message);
            return false;
        }
        finally
        {
            if (mounted is not null) TryDismountIso(sourceIso);
        }
    }

    public static async Task EnsureCompatibleSetupToolsAsync(string sourceIso, Action<string> log)
    {
        var dism = FindDism() ?? throw new InvalidOperationException("DISM.exe не найден.");
        var kitInfo = await Task.Run(() =>
        {
            string? mounted = null;
            try
            {
                mounted = MountIso(sourceIso);
                var bootWim = Path.Combine(mounted + @":\", "sources", "boot.wim");
                if (!File.Exists(bootWim))
                    throw new InvalidOperationException("В ISO отсутствует sources\\boot.wim; нельзя определить версию WinPE.");
                return ReadImageInfo(dism, bootWim, 2);
            }
            finally
            {
                if (mounted is not null) TryDismountIso(sourceIso);
            }
        });

        var build = ParseBootBuild(kitInfo.Version);
        var release = SelectSetupKitRelease(build);
        if (release is null)
            throw new InvalidOperationException(
                $"Автоматическая установка WinPE language add-on не настроена для build {kitInfo.Version}. " +
                "Используйте ADK/WinPE add-on соответствующего выпуска вручную.");

        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var adkInstaller = Path.Combine(Path.GetTempPath(), "LocalizerIoT-adksetup-" + Guid.NewGuid().ToString("N") + ".exe");
        var peInstaller = Path.Combine(Path.GetTempPath(), "LocalizerIoT-adkwinpesetup-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            log($"Подходящий набор для Windows Setup: {release.Name}; boot.wim build {build}, архитектура {kitInfo.Architecture}.");
            log("Загружаю официальный Windows ADK bootstrapper и WinPE add-on с Microsoft; целевая Windows не обновляется.");
            await DownloadSignedBootstrapperAsync(release.AdkUrl, adkInstaller, "Windows ADK", log);
            await DownloadSignedBootstrapperAsync(release.WinPeUrl, peInstaller, "Windows PE add-on", log);

            log("Устанавливаю Deployment Tools совместимого ADK…");
            var installAdk = await Task.Run(() => Run(adkInstaller, "/quiet", "/installpath", installRoot, "/features", "OptionId.DeploymentTools"));
            if (installAdk.ExitCode != 0 && installAdk.ExitCode != 3010)
                throw new InvalidOperationException("Установка совместимого ADK завершилась с кодом " + installAdk.ExitCode + ". " + ShortOutput(installAdk.Output));

            log("Устанавливаю WinPE optional components и ru-RU язык для WinPE…");
            var installPe = await Task.Run(() => Run(peInstaller, "/quiet", "/installpath", installRoot));
            if (installPe.ExitCode != 0 && installPe.ExitCode != 3010)
                throw new InvalidOperationException("Установка WinPE add-on завершилась с кодом " + installPe.ExitCode + ". " + ShortOutput(installPe.Output));

            var archFolder = kitInfo.Architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" :
                kitInfo.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "x86" : "amd64";
            var ruRoot = Path.Combine(installRoot, "Assessment and Deployment Kit", "Windows Preinstallation Environment",
                archFolder, "WinPE_OCs", "ru-ru");
            if (!HasRequiredSetupLanguagePackages(ruRoot))
                throw new InvalidOperationException(
                    "Установщики завершились, но обязательные WinPE CAB ru-RU не найдены: " + ruRoot +
                    ". Возможно, Microsoft изменил структуру установщика или выбранный add-on не содержит эти компоненты.");

            if (FindOscdimg() is null)
                throw new InvalidOperationException("Не найден oscdimg.exe после установки compatible ADK Deployment Tools.");

            var check = await Task.Run(() => HasCompatibleWinPePackages(sourceIso, Array.Empty<string>(), log));
            if (!check)
                throw new InvalidOperationException("После установки WinPE add-on метаданные пакетов всё ещё не совпадают с boot.wim. Образ Setup не следует изменять.");
            log("Совместимый ADK/WinPE установлен и проверен.");
        }
        finally
        {
            try { if (File.Exists(adkInstaller)) File.Delete(adkInstaller); } catch { }
            try { if (File.Exists(peInstaller)) File.Delete(peInstaller); } catch { }
        }
    }

    private static async Task DownloadSignedBootstrapperAsync(string url, string destination, string label, Action<string> log)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using (var remote = await response.Content.ReadAsStreamAsync())
        await using (var local = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            await remote.CopyToAsync(local);

        log($"Проверяю подпись Microsoft у установщика {label}…");
        var escaped = destination.Replace("'", "''");
        var signatureCommand = "$sig = Get-AuthenticodeSignature -LiteralPath '" + escaped +
            "'; if ($sig.Status -ne 'Valid' -or $null -eq $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { Write-Error 'The Microsoft Authenticode signature is not valid.'; exit 51 }";
        var signature = await Task.Run(() => Run("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", signatureCommand));
        EnsureSuccess(signature, $"Не удалось подтвердить цифровую подпись Microsoft у установщика {label}. Установка отменена.");
    }

    private static int ParseBootBuild(string version)
    {
        var match = Regex.Match(version ?? "", @"(?:^|\.)10\.0\.(?<build>\d+)(?:\.|$)|(?:^|\.)0\.(?<b2>\d+)(?:\.|$)");
        var text = match.Groups["build"].Success ? match.Groups["build"].Value : match.Groups["b2"].Value;
        return int.TryParse(text, out var build) ? build : 0;
    }

    private static SetupKitRelease? SelectSetupKitRelease(int build)
    {
        if (build == 17763)
            return new SetupKitRelease("Windows 10 version 1809 / LTSC 2019", "https://go.microsoft.com/fwlink/?linkid=2026036", "https://go.microsoft.com/fwlink/?linkid=2022233");
        if (build is >= 19041 and <= 19045)
            return new SetupKitRelease("Windows 10 version 2004–22H2 / LTSC 2021", "https://go.microsoft.com/fwlink/?linkid=2120254", "https://go.microsoft.com/fwlink/?linkid=2120253");
        if (build is 22621 or 22631)
            return new SetupKitRelease("Windows 11 versions 22H2/23H2", "https://go.microsoft.com/fwlink/?linkid=2196127", "https://go.microsoft.com/fwlink/?linkid=2196224");
        if (build is >= 26100 and <= 26299)
            return new SetupKitRelease("Windows 11 version 24H2 or newer compatible servicing baseline", "https://go.microsoft.com/fwlink/?linkid=2289980", "https://go.microsoft.com/fwlink/?linkid=2289981");
        return null;
    }

    public static string? FindOscdimgPath() => FindOscdimg();

    public static async Task EnsureDeploymentToolsAsync(Action<string> log)
    {
        if (FindOscdimg() is not null)
        {
            log("oscdimg.exe уже доступен.");
            return;
        }

        var installerPath = Path.Combine(Path.GetTempPath(), "LocalizerIoT-adksetup-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            log("Загружаю официальный установщик Windows ADK с сервера Microsoft…");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var response = await client.GetAsync("https://go.microsoft.com/fwlink/?linkid=2289980", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var remote = await response.Content.ReadAsStreamAsync())
            await using (var local = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
                await remote.CopyToAsync(local);

            log("Проверяю цифровую подпись установщика Microsoft…");
            var escaped = installerPath.Replace("'", "''");
            var signatureCommand = "$sig = Get-AuthenticodeSignature -LiteralPath '" + escaped +
                "'; if ($sig.Status -ne 'Valid' -or $null -eq $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { Write-Error 'The Microsoft Authenticode signature is not valid.'; exit 51 }";
            var signature = Run("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", signatureCommand);
            EnsureSuccess(signature, "Не удалось подтвердить цифровую подпись установщика ADK. Установка отменена.");

            var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
            log("Устанавливаю только Windows ADK Deployment Tools. Это не обновляет целевую Windows; установщик может скачать компоненты ADK.");
            var install = Run(installerPath, "/quiet", "/installpath", installRoot, "/features", "OptionId.DeploymentTools");
            if (install.ExitCode != 0 && install.ExitCode != 3010)
                throw new InvalidOperationException("Установка Windows ADK завершилась с кодом " + install.ExitCode + ". " + ShortOutput(install.Output));

            var executable = FindOscdimg();
            if (executable is null)
                throw new InvalidOperationException("Установщик завершился, но oscdimg.exe не найден. Проверьте журнал ADK Setup и путь установки.");
            log("Deployment Tools установлены. oscdimg.exe: " + executable);
        }
        finally
        {
            try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        }
    }

    private static string? FindOscdimg()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        foreach (var root in roots.Where(Directory.Exists))
        {
            var adk = Path.Combine(root, "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools");
            if (!Directory.Exists(adk)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(adk, "oscdimg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { }
        }
        return null;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}