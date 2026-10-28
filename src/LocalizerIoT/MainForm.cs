using System.Diagnostics;
using System.Text;

namespace LocalizerIoT;

public sealed class MainForm : Form
{
    private readonly TextBox _iso = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _packageSource = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _fodSource = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _setupSource = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _donorSource = new() { Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _packages = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _index = new() { Minimum = 1, Maximum = 999, Value = 1, Width = 82 };
    private readonly CheckBox _allIndexes = new() { Text = "Обработать все редакции", Checked = true, AutoSize = true };
    private readonly Button _inspect = new() { Text = "Проверить ISO", AutoSize = true };
    private readonly Button _inspectDonor = new() { Text = "Проверить донор", AutoSize = true };
    private readonly Button _autoDownload = new() { Text = "Автоматически скачать RU", AutoSize = true };
    private readonly Button _browseCab = new() { Text = "Добавить CAB…", AutoSize = true };
    private readonly Button _run = new() { Text = "Создать русифицированный ISO", AutoSize = true };
    private readonly ProgressBar _progress = new() { Width = 240, Minimum = 0, Maximum = 100 };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
    private readonly Label _status = new() { AutoSize = true, Text = "Выберите исходный ISO Windows." };
    private bool _busy;

    public MainForm()
    {
        Text = "Localizer-IoT — русификация Windows IoT Enterprise LTSC";
        Width = 1080;
        Height = 800;
        MinimumSize = new Size(850, 650);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        BuildLayout();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _iso.DragEnter += OnDragEnter;
        _iso.DragDrop += (_, e) => HandleDrop(e, false);
        _packageSource.DragEnter += OnDragEnter;
        _packageSource.DragDrop += (_, e) => HandleDrop(e, true);
        _fodSource.DragEnter += OnDragEnter;
        _fodSource.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                var candidate = files.FirstOrDefault(p => Directory.Exists(p) ||
                    (File.Exists(p) && Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase)));
                if (candidate is not null) _fodSource.Text = candidate;
                var cabs = files.Where(p => File.Exists(p) && Path.GetExtension(p).Equals(".cab", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (cabs.Length > 0) AddCabFiles(cabs);
            }
        };
        _donorSource.DragEnter += OnDragEnter;
        _donorSource.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                var candidate = files.FirstOrDefault(p => Directory.Exists(p) ||
                    (File.Exists(p) && Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase)));
                if (candidate is not null) _donorSource.Text = candidate;
            }
        };
        _setupSource.DragEnter += OnDragEnter;
        _setupSource.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                var candidate = files.FirstOrDefault(p => Directory.Exists(p) ||
                    (File.Exists(p) && Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase)));
                if (candidate is not null) _setupSource.Text = candidate;
            }
        };
        _inspect.Click += async (_, _) => await InspectIsoAsync();
        _inspectDonor.Click += async (_, _) => await InspectRussianDonorAsync();
        _autoDownload.Click += async (_, _) => await AutoDownloadRussianPackagesAsync();
        _browseCab.Click += (_, _) => SelectCabFiles();
        _run.Click += async (_, _) => await RunLocalizationAsync();

        Log("Localizer-IoT: исходный ISO копируется во временную папку и не изменяется.");
        Log("Укажите отдельно Language Pack ISO и Languages and Optional Features ISO, если в поставке это разные носители.");
        Log("Все языковые и FOD источники должны соответствовать версии, базовой сборке и архитектуре целевого образа.");
        Log("Автоподбор RU использует UUP Dump JSON API и проверяет целостность файлов. Автоматическая доступность FOD и Setup/WinPE зависит от выпуска Windows.");
        Log("Русский ISO-донор можно указать отдельно: программа проверит его CAB-пакеты ru-RU и resources sources\\ru-ru без изменения исходных файлов.");
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 10 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 18));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 82));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "Русский язык для Windows IoT Enterprise LTSC\nВыбери ISO, укажи источник Language Pack / Features on Demand и запусти обработку. Исходный ISO не изменяется.",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 8)
        });

        var isoPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        isoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        isoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        isoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        isoPanel.Controls.Add(new Label { Text = "Исходный ISO:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        isoPanel.Controls.Add(_iso, 1, 0);
        var pickIso = new Button { Text = "Обзор…", AutoSize = true };
        pickIso.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "ISO-образы (*.iso)|*.iso", Title = "Выберите исходный ISO Windows" };
            if (d.ShowDialog(this) == DialogResult.OK) _iso.Text = d.FileName;
        };
        isoPanel.Controls.Add(pickIso, 2, 0);
        root.Controls.Add(isoPanel);

        var mediaPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 2, AutoSize = true };
        mediaPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mediaPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mediaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mediaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mediaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mediaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mediaPanel.Controls.Add(new Label { Text = "Language Pack ISO:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        mediaPanel.Controls.Add(_packageSource, 1, 0);
        var pickMediaIso = new Button { Text = "ISO…", AutoSize = true };
        pickMediaIso.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "ISO-образы (*.iso)|*.iso", Title = "Выберите ISO языковых пакетов Windows" };
            if (d.ShowDialog(this) == DialogResult.OK) _packageSource.Text = d.FileName;
        };
        var pickMediaFolder = new Button { Text = "Папка…", AutoSize = true };
        pickMediaFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Выберите папку с CAB языковых пакетов ru-RU" };
            if (d.ShowDialog(this) == DialogResult.OK) _packageSource.Text = d.SelectedPath;
        };
        mediaPanel.Controls.Add(pickMediaIso, 2, 0);
        mediaPanel.Controls.Add(pickMediaFolder, 3, 0);

        mediaPanel.Controls.Add(new Label { Text = "Features on Demand ISO:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        mediaPanel.Controls.Add(_fodSource, 1, 1);
        var pickFodIso = new Button { Text = "ISO…", AutoSize = true };
        pickFodIso.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "ISO-образы (*.iso)|*.iso", Title = "Выберите Languages and Optional Features / Features on Demand ISO" };
            if (d.ShowDialog(this) == DialogResult.OK) _fodSource.Text = d.FileName;
        };
        var pickFodFolder = new Button { Text = "Папка…", AutoSize = true };
        pickFodFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Выберите папку LanguagesAndOptionalFeatures с FOD-файлами" };
            if (d.ShowDialog(this) == DialogResult.OK) _fodSource.Text = d.SelectedPath;
        };
        mediaPanel.Controls.Add(pickFodIso, 2, 1);
        mediaPanel.Controls.Add(pickFodFolder, 3, 1);
        root.Controls.Add(mediaPanel);

        var setupPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true };
        setupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setupPanel.Controls.Add(new Label { Text = "Русские ресурсы Setup (опционально):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        setupPanel.Controls.Add(_setupSource, 1, 0);
        var pickSetupIso = new Button { Text = "ISO…", AutoSize = true };
        pickSetupIso.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "ISO-образы (*.iso)|*.iso", Title = "Выберите соответствующий Russian Windows ISO с sources\\ru-ru" };
            if (d.ShowDialog(this) == DialogResult.OK) _setupSource.Text = d.FileName;
        };
        var pickSetupFolder = new Button { Text = "Папка…", AutoSize = true };
        pickSetupFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Выберите распакованный Russian Windows media, содержащий sources\\ru-ru" };
            if (d.ShowDialog(this) == DialogResult.OK) _setupSource.Text = d.SelectedPath;
        };
        setupPanel.Controls.Add(pickSetupIso, 2, 0);
        setupPanel.Controls.Add(pickSetupFolder, 3, 0);
        root.Controls.Add(setupPanel);

        var donorPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 5, AutoSize = true };
        donorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        donorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        donorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        donorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        donorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        donorPanel.Controls.Add(new Label { Text = "Русский ISO-донор (опционально):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        donorPanel.Controls.Add(_donorSource, 1, 0);
        var pickDonorIso = new Button { Text = "ISO…", AutoSize = true };
        pickDonorIso.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "ISO-образы (*.iso)|*.iso", Title = "Выберите русский ISO-донор Windows 11 Enterprise LTSC 2024 или совместимого выпуска" };
            if (d.ShowDialog(this) == DialogResult.OK) _donorSource.Text = d.FileName;
        };
        var pickDonorFolder = new Button { Text = "Папка…", AutoSize = true };
        pickDonorFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Выберите распакованный русский Windows media с sources\\install.wim/esd, sources\\ru-ru и языковыми CAB, если они присутствуют" };
            if (d.ShowDialog(this) == DialogResult.OK) _donorSource.Text = d.SelectedPath;
        };
        donorPanel.Controls.Add(pickDonorIso, 2, 0);
        donorPanel.Controls.Add(pickDonorFolder, 3, 0);
        donorPanel.Controls.Add(_inspectDonor, 4, 0);
        root.Controls.Add(donorPanel);

        var packagePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        packagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        packagePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        packagePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        packagePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        packagePanel.Controls.Add(new Label { Text = "Дополнительные CAB (необязательно):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        packagePanel.Controls.Add(_browseCab, 1, 0);
        packagePanel.Controls.Add(_packages, 0, 1);
        packagePanel.SetColumnSpan(_packages, 2);
        _packages.Height = 70;
        root.Controls.Add(packagePanel);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        options.Controls.Add(_allIndexes);
        options.Controls.Add(new Label { Text = "Индекс (если отключены все редакции):", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
        options.Controls.Add(_index);
        options.Controls.Add(_inspect);
        options.Controls.Add(_autoDownload);
        options.Controls.Add(new Label { Text = "Выходной ISO:", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
        _output.Width = 250;
        _output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Windows-IoT-LTSC-ru.iso");
        options.Controls.Add(_output);
        var chooseOutput = new Button { Text = "Сохранить как…", AutoSize = true };
        chooseOutput.Click += (_, _) =>
        {
            using var d = new SaveFileDialog { Filter = "ISO-образ (*.iso)|*.iso", FileName = "Windows-IoT-LTSC-ru.iso", DefaultExt = "iso", AddExtension = true };
            if (d.ShowDialog(this) == DialogResult.OK) _output.Text = d.FileName;
        };
        options.Controls.Add(chooseOutput);
        root.Controls.Add(options);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        actions.Controls.Add(_run);
        actions.Controls.Add(_progress);
        root.Controls.Add(actions);

        root.Controls.Add(_log);
        root.Controls.Add(_status);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e) => HandleDrop(e, false);

    private void HandleDrop(DragEventArgs e, bool packageTarget)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (packageTarget)
        {
            var candidate = files.FirstOrDefault(f => Directory.Exists(f) || Path.GetExtension(f).Equals(".iso", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) { _packageSource.Text = candidate; Log("Источник пакетов выбран перетаскиванием: " + candidate); }
            var cabs = files.Where(f => File.Exists(f) && Path.GetExtension(f).Equals(".cab", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (cabs.Length > 0) AddCabFiles(cabs);
            return;
        }
        var iso = files.FirstOrDefault(f => File.Exists(f) && Path.GetExtension(f).Equals(".iso", StringComparison.OrdinalIgnoreCase));
        if (iso is not null) { _iso.Text = iso; Log("Исходный ISO выбран перетаскиванием: " + iso); }
        var directories = files.FirstOrDefault(Directory.Exists);
        if (directories is not null && string.IsNullOrWhiteSpace(_packageSource.Text))
        {
            _packageSource.Text = directories;
            Log("Папка пакетов выбрана перетаскиванием: " + directories);
        }
        var cabFiles = files.Where(f => File.Exists(f) && Path.GetExtension(f).Equals(".cab", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (cabFiles.Length > 0) AddCabFiles(cabFiles);
    }

    private void SelectCabFiles()
    {
        using var d = new OpenFileDialog
        {
            Filter = "Языковые CAB-пакеты (*.cab)|*.cab",
            Multiselect = true,
            Title = "Добавьте CAB-пакеты русского языка"
        };
        if (d.ShowDialog(this) == DialogResult.OK) AddCabFiles(d.FileNames);
    }

    private void AddCabFiles(IEnumerable<string> files)
    {
        var current = _packages.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (File.Exists(file) && Path.GetExtension(file).Equals(".cab", StringComparison.OrdinalIgnoreCase))
                current.Add(file);
        _packages.Text = string.Join(Environment.NewLine, current);
        Log($"Добавлено CAB-файлов вручную: {current.Count}.");
    }

    private async Task InspectIsoAsync()
    {
        if (!ValidateIso()) return;
        SetBusy(true);
        try
        {
            var path = _iso.Text.Trim();
            var indexes = await Task.Run(() => ImageServicingEngine.InspectIso(path, Log));
            if (indexes.Count > 0)
            {
                _index.Minimum = indexes.Min(i => i.Index);
                _index.Maximum = indexes.Max(i => i.Index);
                _index.Value = indexes[0].Index;
            }
            Log("Обнаруженные индексы:");
            foreach (var image in indexes)
                Log($"  #{image.Index}: {image.Name}; версия {image.Version}; архитектура {image.Architecture}");
            Log("Проверка завершена. По умолчанию обработаются все редакции.");
        }
        catch (Exception ex)
        {
            Log("ОШИБКА: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Не удалось проверить ISO", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task InspectRussianDonorAsync()
    {
        var path = _donorSource.Text.Trim();
        if (!IsValidMediaPath(path) || string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Выберите русский ISO-донор или папку с распакованными файлами Windows.",
                "ISO-донор не выбран", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true);
        try
        {
            await Task.Run(() => ImageServicingEngine.InspectRussianDonor(path, Log));
            Log("Проверка русского ISO-донора завершена.");
        }
        catch (Exception ex)
        {
            Log("Проверка русского ISO-донора не завершена: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Не удалось проверить ISO-донор", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task AutoDownloadRussianPackagesAsync()
    {
        if (!ValidateIso()) return;
        SetBusy(true);
        _progress.Value = 0;
        try
        {
            var result = await UupLanguagePackDownloader.DownloadRussianPackagesAsync(
                _iso.Text.Trim(), Log, p => Ui(() => _progress.Value = Math.Clamp(p, 0, 100)));
            _packageSource.Text = result.Directory;
            _fodSource.Text = result.Directory;
            Log($"Автоподбор завершён: база {result.LanguagePackBaseBuild}; подготовлено CAB: {result.PackagesReady}; языковых FOD CAB: {result.FeaturePackagesReady}.");
            MessageBox.Show(this,
                $"Русский Language Pack автоматически подготовлен.\n\n" +
                $"Совместимая база: {result.LanguagePackBaseBuild}\nАрхитектура: {result.Architecture}\n" +
                $"CAB всего: {result.PackagesReady}\nCAB языковых функций: {result.FeaturePackagesReady}\n\n" +
                "Источники заполнены автоматически. Полная русификация экранов Setup/WinPE зависит от наличия соответствующих ресурсов; результат будет указан в отчёте.",
                "Компоненты ru-RU подготовлены", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("Автоматическая загрузка RU не завершена: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Не удалось автоматически скачать русский язык", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task RunLocalizationAsync()
    {
        if (!ValidateIso()) return;
        var output = _output.Text.Trim();
        if (string.IsNullOrWhiteSpace(output) || !Path.GetExtension(output).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Укажите путь выходного файла с расширением .iso.");
            return;
        }
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_iso.Text.Trim()), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Выходной ISO должен иметь отдельный путь от исходного.");
            return;
        }
        if (!IsValidMediaPath(_packageSource.Text))
        {
            MessageBox.Show(this, "Источник Language Pack должен быть существующей папкой или ISO-файлом.");
            return;
        }
        if (!IsValidMediaPath(_fodSource.Text))
        {
            MessageBox.Show(this, "Источник Features on Demand должен быть существующей папкой или ISO-файлом.");
            return;
        }
        if (!IsValidMediaPath(_setupSource.Text))
        {
            MessageBox.Show(this, "Источник ресурсов Setup должен быть существующей папкой или ISO-файлом.");
            return;
        }
        if (!IsValidMediaPath(_donorSource.Text))
        {
            MessageBox.Show(this, "Русский ISO-донор должен быть существующей папкой или ISO-файлом.");
            return;
        }
        if (File.Exists(output) && MessageBox.Show(this, "Выходной ISO уже существует. Перезаписать?", "Подтверждение",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var cabFiles = _packages.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(File.Exists).ToArray();
        if (string.IsNullOrWhiteSpace(_packageSource.Text) && string.IsNullOrWhiteSpace(_donorSource.Text) && cabFiles.Length == 0)
        {
            SetBusy(true);
            _progress.Value = 0;
            try
            {
                Log("Не задан источник Language Pack или русский ISO-донор — запускаю автоматический поиск и скачивание ru-RU.");
                var result = await UupLanguagePackDownloader.DownloadRussianPackagesAsync(
                    _iso.Text.Trim(), Log, p => Ui(() => _progress.Value = Math.Clamp(p, 0, 100)));
                _packageSource.Text = result.Directory;
                if (string.IsNullOrWhiteSpace(_fodSource.Text)) _fodSource.Text = result.Directory;
                cabFiles = _packages.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(File.Exists).ToArray();
                Log($"Автоматически подготовлено CAB: {result.PackagesReady}; FOD CAB: {result.FeaturePackagesReady}.");
            }
            catch (Exception ex)
            {
                Log("Автозагрузка ru-RU не удалась: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Не удалось скачать языковой пакет", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally { SetBusy(false); }
        }

        var hasOscdimg = ImageServicingEngine.FindOscdimgPath() is not null;
        var extraWinPeRoots = new[] { _packageSource.Text.Trim(), _fodSource.Text.Trim(), _setupSource.Text.Trim(), _donorSource.Text.Trim() }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hasCompatibleWinPe = await Task.Run(() =>
            ImageServicingEngine.HasCompatibleWinPePackages(_iso.Text.Trim(), extraWinPeRoots, Log));

        if (!hasOscdimg || !hasCompatibleWinPe)
        {
            var missing = new List<string>();
            if (!hasOscdimg) missing.Add("oscdimg.exe для создания загрузочного ISO");
            if (!hasCompatibleWinPe) missing.Add("WinPE language components ru-RU, совместимые с boot.wim");
            var choice = MessageBox.Show(this,
                "Не обнаружены все компоненты для полной локализации Setup:\n- " + string.Join("\n- ", missing) +
                "\n\nСкачать официальный установщик ADK и WinPE add-on с Microsoft, проверить их цифровые подписи и установить совместимый набор? Установка может занять значительное место и время; целевая Windows обновляться не будет.",
                "Нужны совместимые инструменты ADK / WinPE", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (choice == DialogResult.Yes)
            {
                SetBusy(true);
                try
                {
                    _progress.Value = 0;
                    if (hasCompatibleWinPe)
                        await ImageServicingEngine.EnsureDeploymentToolsAsync(Log);
                    else
                        await ImageServicingEngine.EnsureCompatibleSetupToolsAsync(_iso.Text.Trim(), Log);
                    _progress.Value = 5;
                }
                catch (Exception ex)
                {
                    Log("Не удалось подготовить ADK/WinPE: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "Установка ADK/WinPE не завершена", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (ImageServicingEngine.FindOscdimgPath() is null) return;
                }
                finally { SetBusy(false); }
            }
            else if (!hasOscdimg)
            {
                Log("Создание ISO невозможно без oscdimg.exe; обработка остановлена.");
                return;
            }
            else if (!hasCompatibleWinPe)
            {
                Log("Пользователь пропустил установку WinPE add-on; установка Windows может остаться частично на английском.");
            }

            if (ImageServicingEngine.FindOscdimgPath() is null)
            {
                MessageBox.Show(this, "oscdimg.exe всё ещё не найден. Обработка не начата.");
                return;
            }
        }

        cabFiles = _packages.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(File.Exists).ToArray();
        var request = new LocalizationRequest(
            Path.GetFullPath(_iso.Text.Trim()),
            string.IsNullOrWhiteSpace(_packageSource.Text) ? null : Path.GetFullPath(_packageSource.Text.Trim()),
            string.IsNullOrWhiteSpace(_fodSource.Text) ? null : Path.GetFullPath(_fodSource.Text.Trim()),
            string.IsNullOrWhiteSpace(_setupSource.Text) ? null : Path.GetFullPath(_setupSource.Text.Trim()),
            string.IsNullOrWhiteSpace(_donorSource.Text) ? null : Path.GetFullPath(_donorSource.Text.Trim()),
            cabFiles,
            Path.GetFullPath(output),
            _allIndexes.Checked,
            (int)_index.Value);

        SetBusy(true);
        _progress.Value = 0;
        try
        {
            await Task.Run(() => ImageServicingEngine.Run(request, (p, message) =>
            {
                Ui(() => _progress.Value = Math.Clamp(p, 0, 100));
                Log(message);
            }));
            Ui(() => _progress.Value = 100);
            MessageBox.Show(this, "Операция завершена. Обязательно изучите отчёт рядом с ISO: он содержит предупреждения о недостающих или несовместимых компонентах. Проверьте загрузку в виртуальной машине до установки на устройство.",
                "Обработка завершена", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ОШИБКА: " + ex);
            MessageBox.Show(this, ex.Message, "Русификация не завершена", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private static bool IsValidMediaPath(string value)
    {
        var p = value.Trim();
        return string.IsNullOrWhiteSpace(p) || Directory.Exists(p) ||
            (File.Exists(p) && Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase));
    }

    private bool ValidateIso()
    {
        var path = _iso.Text.Trim();
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Выберите существующий ISO-файл Windows.", "ISO не выбран", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _inspect.Enabled = _inspectDonor.Enabled = _autoDownload.Enabled = _browseCab.Enabled = _run.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void Log(string message)
    {
        Ui(() =>
        {
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            _status.Text = message;
        });
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}