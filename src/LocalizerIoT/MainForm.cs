using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalizerIoT;

public sealed class MainForm : Form
{
    private readonly TextBox _iso = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _packages = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly NumericUpDown _index = new() { Minimum = 1, Maximum = 99, Value = 1, Width = 80 };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly Button _inspect = new() { Text = "Проверить ISO", AutoSize = true };
    private readonly Button _browsePackages = new() { Text = "Добавить CAB-пакеты…", AutoSize = true };
    private readonly Button _run = new() { Text = "Создать русифицированный ISO", AutoSize = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
    private readonly Label _status = new() { AutoSize = true, Text = "Выберите ISO Windows IoT Enterprise LTSC." };
    private string? _workRoot;
    private bool _busy;

    public MainForm()
    {
        Text = "Localizer-IoT — русификация Windows IoT Enterprise LTSC";
        Width = 1000; Height = 760; MinimumSize = new Size(820, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        BuildLayout();
        _iso.AllowDrop = true;
        _iso.DragEnter += OnDragEnter;
        _iso.DragDrop += OnDragDrop;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _inspect.Click += async (_, _) => await InspectIsoAsync();
        _browsePackages.Click += (_, _) => SelectPackages();
        _run.Click += async (_, _) => await RunLocalizationAsync();
        FormClosed += (_, _) => { try { if (_workRoot is not null) Directory.Delete(_workRoot, true); } catch { } };
        Log("Localizer-IoT запущен. Источник ISO не будет изменяться.");
        Log("Важно: соответствие языковых CAB пакетов версии/сборке/архитектуре проверяется DISM; несовместимые пакеты приведут к остановке.");
        if (!Program.IsAdministrator())
            Log("ПРИМЕЧАНИЕ: приложение не запущено от администратора. Для обслуживания образов потребуются права администратора.");
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 75));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var intro = new Label { Text = "Русский язык для Windows IoT Enterprise LTSC\nВыбери ISO, проверь редакции, добавь совместимые языковые CAB-пакеты и создай отдельный ISO.", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 0, 0, 8) };
        root.Controls.Add(intro);
        var isoPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        isoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        isoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        isoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        isoPanel.Controls.Add(new Label { Text = "Исходный ISO:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        isoPanel.Controls.Add(_iso, 1, 0);
        var pickIso = new Button { Text = "Обзор…", AutoSize = true };
        pickIso.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "ISO-образы (*.iso)|*.iso", Title = "Выберите ISO Windows" }; if (d.ShowDialog(this) == DialogResult.OK) _iso.Text = d.FileName; };
        isoPanel.Controls.Add(pickIso, 2, 0);
        root.Controls.Add(isoPanel);

        var pkgPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        pkgPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); pkgPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pkgPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); pkgPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pkgPanel.Controls.Add(new Label { Text = "Языковые пакеты (CAB/MSU):", AutoSize = true, Anchor = AnchorStyles.Top }, 0, 0);
        pkgPanel.Controls.Add(_browsePackages, 1, 0);
        pkgPanel.Controls.Add(_packages, 0, 1); pkgPanel.SetColumnSpan(_packages, 2);
        root.Controls.Add(pkgPanel);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        options.Controls.Add(new Label { Text = "Индекс редакции install.wim:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        options.Controls.Add(_index);
        options.Controls.Add(_inspect);
        options.Controls.Add(new Label { Text = "Выходной ISO:", AutoSize = true, Padding = new Padding(10, 7, 0, 0) });
        _output.Width = 280; _output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Windows-IoT-LTSC-ru.iso");
        options.Controls.Add(_output);
        root.Controls.Add(options);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.Add(_run);
        actions.Controls.Add(_progress);
        _progress.Width = 240;
        root.Controls.Add(actions);
        root.Controls.Add(_log);
        root.Controls.Add(_status);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var iso = files.FirstOrDefault(f => Path.GetExtension(f).Equals(".iso", StringComparison.OrdinalIgnoreCase));
        if (iso is not null) { _iso.Text = iso; Log("ISO выбран перетаскиванием: " + iso); }
        var cabs = files.Where(f => new[] { ".cab", ".msu" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (cabs.Length > 0) AddPackages(cabs);
    }

    private void SelectPackages()
    {
        using var d = new OpenFileDialog { Filter = "Пакеты языков (*.cab;*.msu)|*.cab;*.msu|Все файлы (*.*)|*.*", Multiselect = true, Title = "Выберите языковые пакеты и Features on Demand" };
        if (d.ShowDialog(this) == DialogResult.OK) AddPackages(d.FileNames);
    }

    private void AddPackages(IEnumerable<string> files)
    {
        var current = _packages.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) if (File.Exists(f)) current.Add(f);
        _packages.Text = string.Join(Environment.NewLine, current);
        Log($"Список пакетов: {current.Count} файл(ов).");
    }

    private async Task InspectIsoAsync()
    {
        if (!ValidateIso()) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => InspectIsoCore(_iso.Text.Trim()));
        }
        catch (Exception ex) { Log("ОШИБКА: " + ex.Message); MessageBox.Show(this, ex.Message, "Не удалось проверить ISO", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
    }

    private void InspectIsoCore(string iso)
    {
        Log("Проверяю ISO и ищу DISM/oscdimg…");
        var dism = FindDism();
        if (dism is null) throw new InvalidOperationException("DISM.exe не найден. Запустите программу на Windows 10/11 с компонентом DISM.");
        Log("DISM: " + dism);
        var mounted = RunPowerShell($"$i=Mount-DiskImage -ImagePath '{PsQuote(iso)}' -PassThru; ($i | Get-Volume).DriveLetter");
        var letter = mounted.Output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(letter)) throw new InvalidOperationException("Windows не смогла подключить ISO как виртуальный диск.");
        var root = letter + @":\";
        try
        {
            var images = Directory.GetFiles(Path.Combine(root, "sources"), "install.*", SearchOption.TopDirectoryOnly)
                .Where(f => new[] { ".wim", ".esd" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).FirstOrDefault();
            if (images is null) throw new InvalidOperationException("В ISO не найден sources\\install.wim или install.esd.");
            Log("Образ установки: " + images);
            var result = RunProcess(dism, $"/English /Get-WimInfo /WimFile:\"{images}\"", 180000);
            Log(result.Output);
            if (result.ExitCode != 0) throw new InvalidOperationException("DISM не смог прочитать install.wim/esd.");
            var indices = Regex.Matches(result.Output, @"Index\s*:\s*(\d+)")
                .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            if (indices.Length == 0) throw new InvalidOperationException("DISM не обнаружил индексы редакций.");
            Ui(() => { _index.Minimum = indices.Min(); _index.Maximum = indices.Max(); _index.Value = indices.Min(); });
            Log($"Найдено индексов редакций: {string.Join(", ", indices)}. Выбери нужный индекс перед обработкой.");
            Log("Примечание: если ISO содержит ESD, приложение преобразует выбранный индекс в отдельный WIM в рабочей папке.");
            Log("Поиск oscdimg: " + (FindOscdimg() ?? "не найден; для создания загрузочного ISO понадобится Windows ADK Deployment Tools."));
        }
        finally { RunPowerShell($"Dismount-DiskImage -ImagePath '{PsQuote(iso)}'"); }
    }

    private async Task RunLocalizationAsync()
    {
        if (!ValidateIso()) return;
        var packages = _packages.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(File.Exists).ToArray();
        if (packages.Length == 0)
        {
            MessageBox.Show(this, "Добавь совместимые языковые CAB/MSU-пакеты для той же версии, сборки и архитектуры Windows. Автоматическая загрузка каталога пакетов пока не реализована в этой сборке.", "Нужны языковые пакеты", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_output.Text) || !Path.GetExtension(_output.Text).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "Укажи путь выходного ISO с расширением .iso."); return; }
        var oscdimg = FindOscdimg();
        if (oscdimg is null)
        {
            MessageBox.Show(this, "Не найден oscdimg.exe. Установи Windows ADK → Deployment Tools (это требуется только для создания загрузочного ISO), затем запусти приложение снова. Исходный ISO не будет изменён.", "Не найден oscdimg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (File.Exists(_output.Text) && MessageBox.Show(this, "Выходной ISO уже существует. Перезаписать?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        SetBusy(true); _progress.Value = 0;
        try
        {
            await Task.Run(() => ServiceIso(_iso.Text.Trim(), packages, Path.GetFullPath(_output.Text.Trim()), (p, s) => { Ui(() => _progress.Value = Math.Clamp(p, 0, 100)); Log(s); }));
            Ui(() => _progress.Value = 100);
            MessageBox.Show(this, "Операция завершена. Проверь итоговый отчёт в журнале и протестируй ISO в виртуальной машине перед установкой на устройство.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Log("ОШИБКА: " + ex); MessageBox.Show(this, ex.Message, "Русификация не завершена", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
    }

    private void ServiceIso(string iso, string[] packages, string output, Action<int, string> report)
    {
        if (!Program.IsAdministrator()) throw new InvalidOperationException("Для DISM и подключения ISO запусти приложение от имени администратора.");
        var dism = FindDism() ?? throw new InvalidOperationException("DISM.exe не найден.");
        var oscdimg = FindOscdimg() ?? throw new InvalidOperationException("Не найден oscdimg.exe.");
        var work = Path.Combine(Path.GetTempPath(), "LocalizerIoT-" + Guid.NewGuid().ToString("N"));
        _workRoot = work; Directory.CreateDirectory(work);
        var mountDir = Path.Combine(work, "mounted");
        Directory.CreateDirectory(mountDir);
        var isoMounted = false;
        try
        {
            report(3, "Подключаю исходный ISO только для чтения…");
            var ps = RunPowerShell($"$i=Mount-DiskImage -ImagePath '{PsQuote(iso)}' -PassThru; ($i | Get-Volume).DriveLetter");
            var letter = ps.Output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(letter)) throw new InvalidOperationException("Не удалось подключить исходный ISO.");
            isoMounted = true;
            var sourceRoot = letter + @":\";
            var stage = Path.Combine(work, "iso");
            report(8, "Копирую файлы ISO в отдельную рабочую папку…");
            CopyDirectory(sourceRoot, stage);
            RunPowerShell($"Dismount-DiskImage -ImagePath '{PsQuote(iso)}'");
            isoMounted = false;

            var sources = Path.Combine(stage, "sources");
            var install = Path.Combine(sources, "install.wim");
            var esd = Path.Combine(sources, "install.esd");
            if (!File.Exists(install) && !File.Exists(esd)) throw new InvalidOperationException("Не найден install.wim/install.esd в скопированном ISO.");
            if (!File.Exists(install))
            {
                report(15, "Экспортирую выбранный индекс из install.esd в отдельный install.wim…");
                var export = RunProcess(dism, $"/Export-Image /SourceImageFile:\"{esd}\" /SourceIndex:{_index.Value} /DestinationImageFile:\"{install}\" /Compress:max /CheckIntegrity", 0);
                if (export.ExitCode != 0) throw new InvalidOperationException("DISM /Export-Image завершился с ошибкой:\n" + export.Output);
                File.Delete(esd);
            }

            report(24, "Проверяю индекс и монтирую образ Windows…");
            var info = RunProcess(dism, $"/English /Get-WimInfo /WimFile:\"{install}\" /Index:{_index.Value}", 180000);
            if (info.ExitCode != 0) throw new InvalidOperationException("Не удалось прочитать выбранный индекс. " + info.Output);
            Log(info.Output);
            var mount = RunProcess(dism, $"/Mount-Image /ImageFile:\"{install}\" /Index:{_index.Value} /MountDir:\"{mountDir}\" /CheckIntegrity", 0);
            if (mount.ExitCode != 0) throw new InvalidOperationException("Не удалось смонтировать образ: " + mount.Output);

            try
            {
                var count = packages.Length;
                for (var i = 0; i < count; i++)
                {
                    var ext = Path.GetExtension(packages[i]);
                    report(28 + (int)(45.0 * i / count), $"Добавляю пакет {i + 1}/{count}: {Path.GetFileName(packages[i])}");
                    var add = ext.Equals(".msu", StringComparison.OrdinalIgnoreCase)
                        ? RunProcess(dism, $"/Image:\"{mountDir}\" /Add-Package /PackagePath:\"{packages[i]}\"", 0)
                        : RunProcess(dism, $"/Image:\"{mountDir}\" /Add-Package /PackagePath:\"{packages[i]}\"", 0);
                    if (add.ExitCode != 0) throw new InvalidOperationException($"DISM не смог добавить пакет {packages[i]}:\n{add.Output}");
                    Log(add.Output);
                }

                report(77, "Устанавливаю системную локаль ru-RU…");
                var intl = RunProcess(dism, $"/Image:\"{mountDir}\" /Set-AllIntl:ru-RU", 0);
                if (intl.ExitCode != 0) Log("Предупреждение Set-AllIntl: " + intl.Output);
                var sys = RunProcess(dism, $"/Image:\"{mountDir}\" /Set-SysLocale:ru-RU", 0);
                if (sys.ExitCode != 0) Log("Предупреждение Set-SysLocale: " + sys.Output);
                var user = RunProcess(dism, $"/Image:\"{mountDir}\" /Set-UserLocale:ru-RU", 0);
                if (user.ExitCode != 0) Log("Предупреждение Set-UserLocale: " + user.Output);
                var input = RunProcess(dism, $"/Image:\"{mountDir}\" /Set-InputLocale:0419:00000419", 0);
                if (input.ExitCode != 0) Log("Предупреждение Set-InputLocale: " + input.Output);
                var timezone = RunProcess(dism, $"/Image:\"{mountDir}\" /Set-TimeZone:\"Russian Standard Time\"", 0);
                if (timezone.ExitCode != 0) Log("Предупреждение Set-TimeZone: " + timezone.Output);

                report(86, "Сохраняю изменения в install.wim…");
                var commit = RunProcess(dism, $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit /CheckIntegrity", 0);
                if (commit.ExitCode != 0) throw new InvalidOperationException("Не удалось сохранить образ: " + commit.Output);
            }
            catch
            {
                RunProcess(dism, $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 0);
                throw;
            }

            report(92, "Создаю загрузочный ISO с помощью oscdimg…");
            var bios = Path.Combine(stage, "boot", "etfsboot.com");
            var efi = Path.Combine(stage, "efi", "microsoft", "boot", "efisys.bin");
            var bootData = File.Exists(bios) && File.Exists(efi) ? $"-bootdata:2#p0,e,b\"{bios}\"#pEF,e,b\"{efi}\"" :
                File.Exists(bios) ? $"-b\"{bios}\"" : throw new InvalidOperationException("Не найдены загрузочные файлы Windows Setup (etfsboot.com/efisys.bin).");
            var mkiso = RunProcess(oscdimg, $"-m -o -u2 -udfver102 {bootData} \"{stage}\" \"{output}\"", 0);
            if (mkiso.ExitCode != 0) throw new InvalidOperationException("oscdimg завершился с ошибкой: " + mkiso.Output);
            report(100, "ISO создан: " + output);
            File.WriteAllText(output + ".report.txt", $"Source ISO: {iso}{Environment.NewLine}Output ISO: {output}{Environment.NewLine}Edition index: {_index.Value}{Environment.NewLine}Packages: {string.Join(Environment.NewLine, packages)}{Environment.NewLine}Note: WinPE/WinRE, OOBE resources, and other WIM indexes were not serviced by this initial build.", Encoding.UTF8);
        }
        finally
        {
            if (isoMounted) RunPowerShell($"Dismount-DiskImage -ImagePath '{PsQuote(iso)}'");
            try { RunProcess(dism, $"/Cleanup-Wim", 120000); } catch { }
            try { Directory.Delete(work, true); } catch { Log("Рабочая папка сохранена для диагностики: " + work); }
        }
    }

    private bool ValidateIso()
    {
        var path = _iso.Text.Trim();
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "Выбери существующий ISO-файл Windows.", "ISO не выбран", MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }
        return true;
    }

    private static string? FindDism()
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dism.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "dism.exe") };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOscdimg()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        foreach (var root in roots.Where(Directory.Exists))
        {
            var adk = Path.Combine(root, "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools");
            if (Directory.Exists(adk))
            {
                var hit = Directory.GetFiles(adk, "oscdimg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private static (int ExitCode, string Output) RunProcess(string exe, string args, int timeout)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (timeout > 0 && !p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } throw new TimeoutException($"Тайм-аут команды: {Path.GetFileName(exe)}"); }
        p.WaitForExit();
        return (p.ExitCode, output.ToString());
    }

    private static (int ExitCode, string Output) RunPowerShell(string command) =>
        RunProcess("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"", 120000);

    private static string PsQuote(string value) => value.Replace("'", "''");

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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _inspect.Enabled = _browsePackages.Enabled = _run.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void Log(string text)
    {
        Ui(() => { _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}"); _status.Text = text; });
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }
}
