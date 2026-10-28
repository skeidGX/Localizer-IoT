using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalizerIoT;

internal sealed record AutoLanguagePackageSource(
    string Directory,
    int TargetBuild,
    int LanguagePackBaseBuild,
    string Architecture,
    int PackagesReady,
    int FeaturePackagesReady);

/// <summary>
/// Best-effort Russian language package discovery via the official UUP dump JSON API.
/// Payload URLs returned by the API are required to point to Microsoft delivery hosts,
/// and each downloaded payload is checked against the SHA-1 value published by the API.
/// </summary>
internal static class UupLanguagePackDownloader
{
    private const string ApiRoot = "https://api.uupdump.net/";
    private const string ConverterCommit = "4c02e02d86d2f01e4ef556bc6a080382bd21cae4";
    private const string ImageXBlob = "8fee64362f920090eaea15bc37cc176b37a3cbb6";
    private const string SxsExpandBlob = "bd6c0cb36943989c9c1305d38932ee65f8c2c7b2";
    private const string CabArcBlob = "a757a9492aaa3a52027ba13420e43ee11d8eb9a9";

    private static readonly HttpClient Client = CreateClient();

    private sealed record UupBuild(string Id, string Build, string Architecture, string Title, long Created);
    private sealed record UupFile(string Name, string Sha1, long Size, string Url);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromHours(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Localizer-IoT/1.0");
        return client;
    }

    public static async Task<AutoLanguagePackageSource> DownloadRussianPackagesAsync(
        string sourceIso, Action<string> log, Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceIso))
            throw new FileNotFoundException("Исходный ISO не найден.", sourceIso);

        log("Читаю версию и архитектуру образа, чтобы подобрать базовую версию языкового пакета…");
        var imageInfos = await Task.Run(() => ImageServicingEngine.InspectIso(sourceIso, log), cancellationToken);
        var image = imageInfos.FirstOrDefault()
            ?? throw new InvalidOperationException("Не удалось прочитать метаданные исходного ISO.");
        var targetBuild = ParseOsBuild(image.Version);
        if (targetBuild <= 0)
            throw new InvalidOperationException("Не удалось определить номер сборки Windows в ISO: " + image.Version);
        if (image.Architecture.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Не удалось определить архитектуру исходного ISO.");

        var arch = NormalizeArchitecture(image.Architecture);
        var baseBuild = GetLanguagePackBaseBuild(targetBuild);
        log($"Образ: {image.Name}; версия {image.Version}; сборка {targetBuild}; архитектура {arch}. Ищу ru-RU на базе {baseBuild}.");
        progress?.Invoke(3);

        var builds = await FindBuildCandidatesAsync(baseBuild, arch, log, cancellationToken);
        if (builds.Count == 0)
            throw new InvalidOperationException(
                $"UUP Dump не вернул подходящий выпуск для базы {baseBuild} и архитектуры {arch}. " +
                "Автоматический подбор недоступен для этой версии; укажите совместимый официальный Language Pack ISO или папку с CAB.");

        var cacheBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Localizer-IoT", "Cache", "ru-RU", $"{baseBuild}-{arch}");
        Directory.CreateDirectory(cacheBase);

        var errors = new List<string>();
        foreach (var build in builds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheRoot = Path.Combine(cacheBase, Regex.Replace(build.Id, @"[^A-Za-z0-9_-]", "_"));
            Directory.CreateDirectory(cacheRoot);
            try
            {
                log($"Проверяю источник: {build.Title} ({build.Build}, {build.Architecture}).");
                if (!await HasRussianLanguageAsync(build.Id, cancellationToken))
                {
                    log("  ru-RU в этом наборе не найден; пробую следующий источник.");
                    continue;
                }

                var files = await GetRussianLanguageFilesAsync(build.Id, cancellationToken);
                var core = files.Where(f => IsCoreLanguagePackFile(f.Name)).ToArray();
                if (core.Length == 0)
                {
                    log("  В манифесте не найден основной Client Language Pack ru-RU; пробую следующий источник.");
                    continue;
                }

                var ready = 0;
                var fodReady = 0;
                var cacheOk = true;
                var esdFiles = new List<string>();
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    cancellationToken.ThrowIfCancellationRequested();
                    var safeName = Path.GetFileName(file.Name);
                    if (string.IsNullOrWhiteSpace(safeName))
                        continue;
                    var localPath = Path.Combine(cacheRoot, safeName);
                    log($"Скачивание ({i + 1}/{files.Count}): {safeName}");
                    progress?.Invoke(Math.Clamp(5 + (int)(35.0 * i / Math.Max(1, files.Count)), 5, 40));
                    try
                    {
                        await DownloadAndVerifyFileAsync(file, localPath, cancellationToken);
                        if (Path.GetExtension(localPath).Equals(".esd", StringComparison.OrdinalIgnoreCase))
                            esdFiles.Add(localPath);
                        else if (Path.GetExtension(localPath).Equals(".cab", StringComparison.OrdinalIgnoreCase))
                        {
                            ready++;
                            if (IsFeaturePackageFile(localPath)) fodReady++;
                        }
                    }
                    catch (Exception ex)
                    {
                        cacheOk = false;
                        errors.Add($"{safeName}: {ex.Message}");
                        log($"  Не удалось получить {safeName}: {ex.Message}");
                    }
                }

                if (esdFiles.Count > 0)
                {
                    var converter = await EnsureConverterToolsAsync(log, cancellationToken);
                    for (var i = 0; i < esdFiles.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var esd = esdFiles[i];
                        var cab = Path.ChangeExtension(esd, ".cab");
                        if (File.Exists(cab) && new FileInfo(cab).Length > 0)
                        {
                            ready++;
                            if (IsFeaturePackageFile(cab)) fodReady++;
                            continue;
                        }
                        log($"Преобразую языковой ESD в CAB ({i + 1}/{esdFiles.Count}): {Path.GetFileName(esd)}");
                        progress?.Invoke(Math.Clamp(42 + (int)(20.0 * i / Math.Max(1, esdFiles.Count)), 42, 62));
                        await ConvertEsdToCabAsync(esd, cab, cacheRoot, converter, log, cancellationToken);
                        ready++;
                        if (IsFeaturePackageFile(cab)) fodReady++;
                    }
                }

                var readyCore = Directory.EnumerateFiles(cacheRoot, "*.cab", SearchOption.TopDirectoryOnly)
                    .Any(IsCoreLanguagePackFile);
                if (!readyCore)
                {
                    cacheOk = false;
                    errors.Add($"Для выпуска {build.Build} не удалось подготовить основной CAB-пакет ru-RU.");
                    continue;
                }

                var allFeatureCabs = Directory.EnumerateFiles(cacheRoot, "*.cab", SearchOption.TopDirectoryOnly)
                    .Count(IsFeaturePackageFile);
                if (allFeatureCabs == 0)
                    log("В наборе нет явно именованных Language Features CAB. OCR, речь и рукописный ввод потребуют дополнительного совместимого FOD-источника.");

                progress?.Invoke(65);
                log($"Русский Language Pack готов. CAB в кеше: {Directory.EnumerateFiles(cacheRoot, "*.cab").Count()}; Language Features CAB: {allFeatureCabs}.");
                if (!cacheOk)
                    log("Некоторые дополнительные пакеты не скачаны. Основной Language Pack имеется, но результат не будет гарантированно полным.");

                return new AutoLanguagePackageSource(cacheRoot, targetBuild, baseBuild, arch,
                    Directory.EnumerateFiles(cacheRoot, "*.cab", SearchOption.TopDirectoryOnly).Count(), allFeatureCabs);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{build.Build}: {ex.Message}");
                log($"Источник {build.Build} не подошёл: {ex.Message}");
            }
        }

        var detail = errors.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, errors.Take(5));
        throw new InvalidOperationException(
            $"Не удалось автоматически подготовить совместимый русский Language Pack для базы {baseBuild}. " +
            "Это бывает, если нужный выпуск отсутствует в UUP Dump, исходный ISO слишком старый или сервер не отдаёт языковые файлы. " +
            "Укажите официальный Language Pack ISO / Languages and Optional Features ISO вручную." + detail);
    }

    private static async Task<List<UupBuild>> FindBuildCandidatesAsync(
        int baseBuild, string architecture, Action<string> log, CancellationToken cancellationToken)
    {
        var all = new Dictionary<string, UupBuild>(StringComparer.OrdinalIgnoreCase);
        foreach (var search in new[] { $"{baseBuild}.1", baseBuild.ToString() }.Distinct())
        {
            var url = ApiRoot + "listid.php?search=" + Uri.EscapeDataString(search) + "&sortByDate=0";
            try
            {
                using var doc = await GetJsonAsync(url, cancellationToken);
                if (!doc.RootElement.TryGetProperty("response", out var response) ||
                    !response.TryGetProperty("builds", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    var id = GetString(item, "uuid");
                    var build = GetString(item, "build");
                    var arch = NormalizeArchitecture(GetString(item, "arch"));
                    if (string.IsNullOrWhiteSpace(id) || ParseUupBuildNumber(build) != baseBuild ||
                        !arch.Equals(architecture, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var title = GetString(item, "title");
                    var created = GetLong(item, "created");
                    all[id] = new UupBuild(id, build, arch, title, created);
                }
            }
            catch (Exception ex)
            {
                log($"UUP Dump: поиск по {search} завершился ошибкой: {ex.Message}");
            }
        }

        // Prefer the initial servicing baseline rather than downloading a newer cumulative release.
        return all.Values
            .OrderBy(b => GetBuildRevision(b.Build))
            .ThenBy(b => b.Created)
            .Take(12)
            .ToList();
    }

    private static async Task<bool> HasRussianLanguageAsync(string id, CancellationToken cancellationToken)
    {
        var url = ApiRoot + "listlangs.php?id=" + Uri.EscapeDataString(id);
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("langList", out var langs) || langs.ValueKind != JsonValueKind.Array)
            return false;
        return langs.EnumerateArray().Any(x =>
            x.ValueKind == JsonValueKind.String &&
            x.GetString()?.Equals("ru-ru", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static async Task<List<UupFile>> GetRussianLanguageFilesAsync(string id, CancellationToken cancellationToken)
    {
        var url = ApiRoot + "get.php?id=" + Uri.EscapeDataString(id) + "&lang=ru-ru";
        using var doc = await GetJsonAsync(url, cancellationToken);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("UUP Dump не вернул список файлов.");

        var result = new List<UupFile>();
        foreach (var property in files.EnumerateObject())
        {
            var name = property.Name;
            if (!IsRussianLanguagePackageFile(name))
                continue;
            if (IsWindowsUpdateFile(name))
                continue;
            if (!property.Value.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
                continue;
            var downloadUrl = u.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(downloadUrl) || !IsAllowedPayloadUrl(downloadUrl))
                continue;
            var extension = Path.GetExtension(name);
            if (!extension.Equals(".esd", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".cab", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(new UupFile(name, GetString(property.Value, "sha1"),
                GetLong(property.Value, "size"), downloadUrl));
        }

        return result.OrderBy(f => IsCoreLanguagePackFile(f.Name) ? 0 :
            IsFeaturePackageFile(f.Name) ? 1 : 2).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsRussianLanguagePackageFile(string name)
    {
        if (!name.Contains("ru-ru", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("ru_ru", StringComparison.OrdinalIgnoreCase))
            return false;
        return Regex.IsMatch(name, "(language.?pack|language.?features?|languageexperiencepack)", RegexOptions.IgnoreCase);
    }

    private static bool IsCoreLanguagePackFile(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName);
        return name.Contains("Client-LanguagePack", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Client_LanguagePack", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ClientLanguagePack", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Language-Pack", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("LanguagePack", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFeaturePackageFile(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName);
        return name.Contains("LanguageFeatures-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("LanguageFeature-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Language-Features-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Language-Feature-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsUpdateFile(string name) =>
        Regex.IsMatch(name, @"KB\d{6,}", RegexOptions.IgnoreCase) ||
        name.Contains("RollupFix", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Cumulative", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ServicingStack", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(name, @"(^|[-_.])LCU([-_.]|$)", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(name, @"(^|[-_.])SSU([-_.]|$)", RegexOptions.IgnoreCase);

    private static bool IsAllowedPayloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host;
        return host.Equals("download.windowsupdate.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".download.windowsupdate.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadAndVerifyFileAsync(
        UupFile file, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && await VerifySha1Async(destination, file.Sha1, file.Size, cancellationToken))
            return;

        if (File.Exists(destination)) File.Delete(destination);
        var temp = destination + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await Client.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var local = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 256, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await remote.CopyToAsync(local, cancellationToken);

            if (file.Size > 0 && new FileInfo(temp).Length != file.Size)
                throw new InvalidDataException($"размер не совпадает с манифестом ({new FileInfo(temp).Length} / {file.Size} байт)");
            if (!await VerifySha1Async(temp, file.Sha1, file.Size, cancellationToken))
                throw new InvalidDataException("SHA-1 не совпадает с манифестом UUP Dump.");
            File.Move(temp, destination, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static async Task<bool> VerifySha1Async(
        string path, string expectedSha1, long expectedSize, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;
        if (string.IsNullOrWhiteSpace(expectedSha1)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 256, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ConverterTools(string Root, string ImageX, string SxsExpand, string CabArc);

    private static async Task<ConverterTools> EnsureConverterToolsAsync(
        Action<string> log, CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Localizer-IoT", "Tools", "ESD2CAB");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);

        var imagex = Path.Combine(bin, "imagex64.exe");
        var sxs = Path.Combine(bin, "SxSExpand.exe");
        var cabarc = Path.Combine(bin, "cabarc.exe");

        log("Проверяю вспомогательные инструменты конвертации ESD в CAB; они загружаются отдельно и проверяются по Git blob SHA-1.");
        await DownloadPinnedToolAsync("imagex64.exe", imagex, ImageXBlob, log, cancellationToken);
        await DownloadPinnedToolAsync("SxSExpand.exe", sxs, SxsExpandBlob, log, cancellationToken);
        await DownloadPinnedToolAsync("cabarc.exe", cabarc, CabArcBlob, log, cancellationToken);
        return new ConverterTools(root, imagex, sxs, cabarc);
    }

    private static async Task DownloadPinnedToolAsync(
        string name, string destination, string expectedBlobSha, Action<string> log, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && await VerifyGitBlobAsync(destination, expectedBlobSha, cancellationToken))
            return;

        if (File.Exists(destination)) File.Delete(destination);
        var url = $"https://raw.githubusercontent.com/abbodi1406/BatUtil/{ConverterCommit}/ESD2CAB-CAB2ESD/bin/{Uri.EscapeDataString(name)}";
        log("Загружаю преобразователь " + name + " из закреплённой версии репозитория abbodi1406/BatUtil.");
        var temp = destination + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var local = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await remote.CopyToAsync(local, cancellationToken);
            if (!await VerifyGitBlobAsync(temp, expectedBlobSha, cancellationToken))
                throw new InvalidDataException($"Проверка целостности {name} не пройдена. Запуск инструмента отменён.");
            File.Move(temp, destination, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static async Task<bool> VerifyGitBlobAsync(string path, string expectedBlobSha, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var header = System.Text.Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        var payload = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, payload, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, payload, header.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(payload)).Equals(expectedBlobSha, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ConvertEsdToCabAsync(
        string esdPath, string cabPath, string cacheRoot, ConverterTools tools,
        Action<string> log, CancellationToken cancellationToken)
    {
        if (File.Exists(cabPath)) File.Delete(cabPath);
        var work = Path.Combine(cacheRoot, "convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dism.exe");
            var apply = await RunProcessAsync(dism,
                new[] { "/Apply-Image", "/ImageFile:" + esdPath, "/Index:1", "/ApplyDir:" + work, "/CheckIntegrity" },
                Environment.CurrentDirectory, cancellationToken);
            if (apply.ExitCode != 0)
            {
                log("DISM не смог распаковать language ESD; пробую ImageX из закреплённого инструментария.");
                Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                var imagex = await RunProcessAsync(tools.ImageX,
                    new[] { "/APPLY", esdPath, "1", work, "/NOACL", "ALL", "/NOTADMIN", "/TEMP", Path.GetTempPath() },
                    Environment.CurrentDirectory, cancellationToken);
                if (imagex.ExitCode != 0)
                    throw new InvalidOperationException("Не удалось распаковать language ESD ни DISM, ни ImageX. " +
                        ShortOutput(apply.Output) + Environment.NewLine + ShortOutput(imagex.Output));
            }

            var rootManifests = Directory.GetFiles(work, "*.manifest", SearchOption.TopDirectoryOnly);
            if (rootManifests.Length > 0)
            {
                var expanded = Path.Combine(work, "_sxs");
                Directory.CreateDirectory(expanded);
                foreach (var manifest in rootManifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(manifest);
                    var result = await RunProcessAsync(tools.SxsExpand,
                        new[] { name, Path.Combine("_sxs", name) }, work, cancellationToken);
                    var expandedManifest = Path.Combine(expanded, name);
                    if (result.ExitCode == 0 && File.Exists(expandedManifest))
                        File.Copy(expandedManifest, manifest, true);
                }
                try { Directory.Delete(expanded, true); } catch { }
            }

            log("Упаковываю компонент в CAB (LZX:21)...");
            var cab = await RunProcessAsync(tools.CabArc,
                new[] { "-m", "LZX:21", "-r", "-p", "N", Path.GetFullPath(cabPath), "*.*" },
                work, cancellationToken);
            if (cab.ExitCode != 0 || !File.Exists(cabPath) || new FileInfo(cabPath).Length < 1024)
                throw new InvalidOperationException("Не удалось собрать CAB из ESD: " + ShortOutput(cab.Output));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private static async Task<ProcessResult> RunProcessAsync(
        string executable, IEnumerable<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        lock (output) return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("response", out var res) &&
            res.ValueKind == JsonValueKind.Object && res.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.String ? error.GetString() : error.ToString();
            doc.Dispose();
            throw new InvalidOperationException("UUP Dump API: " + message);
        }
        return doc;
    }

    private static int GetLanguagePackBaseBuild(int targetBuild)
    {
        if (targetBuild is >= 19041 and <= 19045) return 19041;
        if (targetBuild is 22621 or 22631) return 22621;
        return targetBuild;
    }

    private static int ParseOsBuild(string version)
    {
        var match = Regex.Match(version ?? "", @"(?<!\d)(?:\d+\.){2}(?<build>\d+)(?:\.\d+)?");
        return match.Success && int.TryParse(match.Groups["build"].Value, out var result) ? result : 0;
    }

    private static int ParseUupBuildNumber(string version)
    {
        var match = Regex.Match(version ?? "", @"^\s*(?<build>\d+)(?:\.|$)");
        return match.Success && int.TryParse(match.Groups["build"].Value, out var result) ? result : 0;
    }

    private static int GetBuildRevision(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[1], out var result) ? result : 0;
    }

    private static string NormalizeArchitecture(string architecture)
    {
        if (architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase) ||
            architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)) return "x64";
        if (architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
        if (architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
            architecture.Equals("i386", StringComparison.OrdinalIgnoreCase)) return "x86";
        return architecture;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        return long.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static string ShortOutput(string output)
    {
        var trimmed = output.Trim();
        return trimmed.Length <= 1400 ? trimmed : trimmed[^1400..];
    }
}
