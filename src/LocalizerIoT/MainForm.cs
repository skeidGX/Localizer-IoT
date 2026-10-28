using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO.Compression;

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
    private readonly Button _cancelDownload = new() { Text = "Отменить загрузку", AutoSize = true, Enabled = false };
    private readonly Button _saveProfile = new() { Text = "Сохранить профиль…", AutoSize = true };
    private readonly Button _loadProfile = new() { Text = "Загрузить профиль…", AutoSize = true };
    private readonly Button _exportBundle = new() { Text = "Экспорт офлайн-пакета…", AutoSize = true };
    private readonly CheckBox _useWindowsUpdateForCapabilities = new() { Text = "Разрешить Windows Update для недостающих FOD (не обновляет ОС)", AutoSize = true, Checked = false };
    private readonly Label _isoSummary = new() { AutoSize = true, Text = "Исходный ISO ещё не выбран." };
    private readonly Label _sourceSummary = new() { AutoSize = true, Text = "Языковые источники: не выбраны." };
    private readonly Label _progressDetail = new() { AutoSize = false, Height = 24, Text = "Готово к работе.", AutoEllipsis = true };
    private readonly Label _progressPercent = new() { AutoSize = true, Text = "0%", TextAlign = ContentAlignment.MiddleRight };
    private readonly ToolTip _helpTips = new() { AutoPopDelay = 12000, InitialDelay = 500, ReshowDelay = 100, ShowAlways = true };
    private readonly Button _clearLog = new() { Text = "Очистить", AutoSize = true };
    private readonly Button _saveLog = new() { Text = "Сохранить журнал…", AutoSize = true };
    private readonly Button _copyLog = new() { Text = "Копировать", AutoSize = true };
    private TabControl? _workspaceTabs;
    private TabPage? _logTabPage;

    private CancellationTokenSource? _operationCts;
    private readonly ProgressBar _progress = new() { Width = 240, Minimum = 0, Maximum = 100 };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
    private readonly Label _status = new() { AutoSize = true, Text = "Выберите исходный ISO Windows." };
    private bool _busy;

    public MainForm()
    {
        Text = "Localizer-IoT Alpha 0.9.0.1 — русификация Windows IoT Enterprise LTSC";
        Width = 1180;
        Height = 860;
        MinimumSize = new Size(940, 700);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(35, 47, 62);
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
        _cancelDownload.Click += (_, _) => CancelCurrentDownload();
        _saveProfile.Click += (_, _) => SaveProfile();
        _loadProfile.Click += (_, _) => LoadProfile();
        _exportBundle.Click += async (_, _) => await ExportOfflineBundleAsync();
        _clearLog.Click += (_, _) => _log.Clear();
        _copyLog.Click += (_, _) => CopyLogToClipboard();
        _saveLog.Click += (_, _) => SaveLogToFile();
        _iso.TextChanged += (_, _) => RefreshInputSummary();
        _packageSource.TextChanged += (_, _) => RefreshInputSummary();
        _fodSource.TextChanged += (_, _) => RefreshInputSummary();
        _setupSource.TextChanged += (_, _) => RefreshInputSummary();
        _donorSource.TextChanged += (_, _) => RefreshInputSummary();
        _output.TextChanged += (_, _) => RefreshInputSummary();
        _allIndexes.CheckedChanged += (_, _) => _index.Enabled = !_allIndexes.Checked;
        _index.Enabled = !_allIndexes.Checked;
        RefreshInputSummary();

        Log("Localizer-IoT: исходный ISO копируется во временную папку и не изменяется.");
        Log("Укажите отдельно Language Pack ISO и Languages and Optional Features ISO, если в поставке это разные носители.");
        Log("Все языковые и FOD источники должны соответствовать версии, базовой сборке и архитектуре целевого образа.");
        Log("Автоподбор RU использует UUP Dump JSON API, проверяет размер и SHA-1, поддерживает повторные попытки и продолжение загрузок.");
        Log("По умолчанию Windows Update не используется. Необязательный флажок разрешает его только для языковых FOD-возможностей; пакеты обновлений ОС не интегрируются.");
        Log("Русский ISO-донор можно указать отдельно: программа проверит его CAB-пакеты ru-RU и resources sources\\ru-ru без изменения исходных файлов.");
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 86,
            MinimumSize = new Size(0, 86),
            BackColor = Color.FromArgb(25, 43, 67),
            Padding = new Padding(18, 12, 18, 12),
            Margin = new Padding(0, 0, 0, 12)
        };
        var headerGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = header.BackColor
        };
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var mark = new Label
        {
            Text = "LI",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(35, 125, 155),
            Margin = new Padding(0, 0, 14, 0)
        };
        var titleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = header.BackColor,
            Margin = new Padding(0)
        };
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleStack.Controls.Add(new Label
        {
            Text = "Localizer-IoT",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 1)
        }, 0, 0);
        titleStack.Controls.Add(new Label
        {
            Text = "Русификация установочного образа Windows IoT Enterprise LTSC",
            AutoSize = true,
            ForeColor = Color.FromArgb(205, 218, 232),
            Font = new Font("Segoe UI", 9.5f),
            Margin = new Padding(1, 0, 0, 0)
        }, 0, 1);
        var versionBadge = new Label
        {
            Text = "ALPHA 0.9.0.1",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(220, 241, 247),
            BackColor = Color.FromArgb(45, 70, 98),
            Padding = new Padding(10, 7, 10, 7),
            Margin = new Padding(8, 0, 0, 0),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        headerGrid.Controls.Add(mark, 0, 0);
        headerGrid.Controls.Add(titleStack, 1, 0);
        headerGrid.Controls.Add(versionBadge, 2, 0);
        header.Controls.Add(headerGrid);
        root.Controls.Add(header, 0, 0);

        _workspaceTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Point(16, 8),
            Margin = new Padding(0, 0, 0, 12),
            HotTrack = true
        };
        var imageTab = new TabPage("1. Исходный образ") { BackColor = Color.White, UseVisualStyleBackColor = false, AutoScroll = true };
        var sourcesTab = new TabPage("2. Языковые источники") { BackColor = Color.White, UseVisualStyleBackColor = false, AutoScroll = true };
        var settingsTab = new TabPage("3. Параметры") { BackColor = Color.White, UseVisualStyleBackColor = false, AutoScroll = true };
        var logTab = new TabPage("Журнал и диагностика") { BackColor = Color.White, UseVisualStyleBackColor = false };
        _logTabPage = logTab;
        _workspaceTabs.TabPages.AddRange(new[] { imageTab, sourcesTab, settingsTab, logTab });
        root.Controls.Add(_workspaceTabs, 0, 1);

        TableLayoutPanel CreateStack(TabPage page)
        {
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 0,
                Padding = new Padding(12),
                BackColor = Color.White,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(stack);
            return stack;
        }

        GroupBox Section(string title, Control content)
        {
            var group = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 10, 12, 12),
                Margin = new Padding(0, 0, 0, 12),
                ForeColor = Color.FromArgb(38, 58, 82),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            content.Dock = DockStyle.Top;
            content.Margin = new Padding(0);
            content.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            group.Controls.Add(content);
            return group;
        }

        Button StyleButton(Button button, bool primary = false, bool accent = false)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(207, 216, 226);
            button.BackColor = primary ? Color.FromArgb(29, 91, 160) :
                accent ? Color.FromArgb(222, 241, 244) : Color.FromArgb(249, 250, 252);
            button.ForeColor = primary ? Color.White :
                accent ? Color.FromArgb(26, 94, 112) : Color.FromArgb(42, 58, 78);
            button.Padding = primary ? new Padding(15, 7, 15, 7) : new Padding(8, 4, 8, 4);
            button.Margin = new Padding(4);
            button.Cursor = Cursors.Hand;
            if (primary) button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            return button;
        }

        void AddSection(TableLayoutPanel stack, string title, Control content)
        {
            var row = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(Section(title, content), 0, row);
        }

        // Tab 1 — select source image, editions and output.
        var imageStack = CreateStack(imageTab);
        var sourceImageGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(0, 2, 0, 2)
        };
        sourceImageGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        sourceImageGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceImageGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceImageGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourceImageGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourceImageGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourceImageGrid.Controls.Add(new Label
        {
            Text = "Установочный ISO",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(3, 8, 8, 4)
        }, 0, 0);
        sourceImageGrid.Controls.Add(_iso, 1, 0);
        var pickIso = StyleButton(new Button { Text = "Обзор…", AutoSize = true });
        pickIso.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "ISO-образы Windows (*.iso)|*.iso", Title = "Выберите исходный ISO Windows" };
            if (d.ShowDialog(this) == DialogResult.OK) _iso.Text = d.FileName;
        };
        sourceImageGrid.Controls.Add(pickIso, 2, 0);
        sourceImageGrid.Controls.Add(_isoSummary, 1, 1);
        sourceImageGrid.SetColumnSpan(_isoSummary, 2);
        _isoSummary.ForeColor = Color.FromArgb(83, 101, 122);
        _isoSummary.Margin = new Padding(3, 6, 3, 10);
        sourceImageGrid.Controls.Add(StyleButton(_inspect, accent: true), 1, 2);
        sourceImageGrid.Controls.Add(new Label
        {
            Text = "Проверит редакции, сборку и архитектуру. Исходный ISO не изменяется.",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(91, 107, 126),
            Margin = new Padding(10, 8, 3, 4)
        }, 2, 2);
        AddSection(imageStack, "Исходный образ Windows", sourceImageGrid);

        var resultGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 4,
            Padding = new Padding(0, 2, 0, 2)
        };
        resultGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        resultGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        resultGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < 4; row++) resultGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        resultGrid.Controls.Add(new Label { Text = "Редакции", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 4), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) }, 0, 0);
        var indexOptions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        indexOptions.Controls.Add(_allIndexes);
        indexOptions.Controls.Add(new Label { Text = "Индекс:", AutoSize = true, Padding = new Padding(10, 4, 2, 0) });
        indexOptions.Controls.Add(_index);
        resultGrid.Controls.Add(indexOptions, 1, 0);
        resultGrid.Controls.Add(new Label { Text = "Обычно оставляйте все редакции.", AutoSize = true, ForeColor = Color.FromArgb(91, 107, 126), Margin = new Padding(10, 5, 3, 3) }, 2, 0);
        resultGrid.Controls.Add(new Label { Text = "Выходной ISO", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 4), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) }, 0, 1);
        resultGrid.Controls.Add(_output, 1, 1);
        var chooseOutput = StyleButton(new Button { Text = "Сохранить как…", AutoSize = true });
        chooseOutput.Click += (_, _) =>
        {
            using var d = new SaveFileDialog
            {
                Filter = "ISO-образ (*.iso)|*.iso",
                FileName = Path.GetFileName(_output.Text),
                DefaultExt = "iso",
                AddExtension = true,
                Title = "Куда сохранить новый ISO"
            };
            if (d.ShowDialog(this) == DialogResult.OK) _output.Text = d.FileName;
        };
        resultGrid.Controls.Add(chooseOutput, 2, 1);
        var resultNote = new Label
        {
            Text = "Английский останется языком отката (UILanguageFallback=en-US). Выберите отдельный путь — источник не перезаписывается.",
            AutoSize = true,
            ForeColor = Color.FromArgb(73, 100, 119),
            Padding = new Padding(0, 4, 0, 4),
            Margin = new Padding(3, 6, 3, 3)
        };
        resultGrid.Controls.Add(resultNote, 1, 2);
        resultGrid.SetColumnSpan(resultNote, 2);
        resultGrid.Controls.Add(new Label { Text = "Быстрый старт", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 4), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) }, 0, 3);
        var hintPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        hintPanel.Controls.Add(new Label { Text = "1  Выберите ISO", AutoSize = true, Padding = new Padding(0, 4, 14, 2) });
        hintPanel.Controls.Add(new Label { Text = "2  Найдите ru-RU", AutoSize = true, Padding = new Padding(0, 4, 14, 2) });
        hintPanel.Controls.Add(new Label { Text = "3  Создайте новый ISO", AutoSize = true, Padding = new Padding(0, 4, 0, 2) });
        resultGrid.Controls.Add(hintPanel, 1, 3);
        resultGrid.SetColumnSpan(hintPanel, 2);
        AddSection(imageStack, "Редакции и результат", resultGrid);

        // Tab 2 — package acquisition and optional resource sources.
        var sourceStack = CreateStack(sourcesTab);
        var autoGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 2, 0, 2) };
        autoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        autoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        autoGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        autoGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        autoGrid.Controls.Add(StyleButton(_autoDownload, accent: true), 0, 0);
        autoGrid.Controls.Add(new Label
        {
            Text = "Найти совместимый ru-RU в UUP Dump. Файлы проверяются; LCU/SSU/KB/MSU не добавляются.",
            AutoSize = true,
            ForeColor = Color.FromArgb(83, 101, 122),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 7, 2, 5)
        }, 1, 0);
        autoGrid.Controls.Add(_sourceSummary, 1, 1);
        _sourceSummary.Margin = new Padding(8, 5, 2, 6);
        _sourceSummary.ForeColor = Color.FromArgb(83, 101, 122);
        AddSection(sourceStack, "Автоматический подбор языка", autoGrid);

        var mediaGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 2, 0, 2),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        mediaGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddMediaSourceRow(mediaGrid, 0, "Language Pack", _packageSource,
            "Выберите Language Pack ISO", "Выберите папку с CAB языковых пакетов ru-RU");
        AddMediaSourceRow(mediaGrid, 1, "Features on Demand", _fodSource,
            "Выберите Languages and Optional Features / FoD ISO", "Выберите папку LanguagesAndOptionalFeatures / FoD");
        AddMediaSourceRow(mediaGrid, 2, "Ресурсы Windows Setup", _setupSource,
            "Выберите Russian Windows ISO с sources\\ru-ru", "Выберите распакованный media с sources\\ru-ru");
        AddMediaSourceRow(mediaGrid, 3, "Русский ISO-донор", _donorSource,
            "Выберите русский ISO-донор", "Выберите распакованный русский Windows media", _inspectDonor);
        AddSection(sourceStack, "Источники (ISO или распакованная папка)", mediaGrid);

        var cabGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 2, 0, 2) };
        cabGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cabGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cabGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cabGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cabGrid.Controls.Add(new Label
        {
            Text = "Укажите отдельные CAB только при необходимости. Программа проверит архитектуру и совместимость.",
            AutoSize = true,
            ForeColor = Color.FromArgb(83, 101, 122),
            Margin = new Padding(3, 6, 3, 6)
        }, 0, 0);
        cabGrid.Controls.Add(StyleButton(_browseCab), 1, 0);
        _packages.Dock = DockStyle.Fill;
        _packages.MinimumSize = new Size(0, 72);
        _packages.Height = 76;
        _packages.Font = new Font("Consolas", 9);
        cabGrid.Controls.Add(_packages, 0, 1);
        cabGrid.SetColumnSpan(_packages, 2);
        AddSection(sourceStack, "Дополнительные CAB (необязательно)", cabGrid);

        // Tab 3 — policy, saved profiles and offline package.
        var settingsStack = CreateStack(settingsTab);
        var safetyGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 2, 0, 2) };
        safetyGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) safetyGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        safetyGrid.Controls.Add(_useWindowsUpdateForCapabilities, 0, 0);
        safetyGrid.Controls.Add(new Label
        {
            Text = "По умолчанию всё обслуживается офлайн. Эта опция касается только языковых FOD установленной Windows и не включает LCU/SSU/KB/MSU, обновление версии ОС или получение FOD для WinRE.",
            AutoSize = true,
            MaximumSize = new Size(980, 0),
            ForeColor = Color.FromArgb(83, 101, 122),
            Margin = new Padding(3, 7, 3, 7)
        }, 0, 1);
        safetyGrid.Controls.Add(new Label
        {
            Text = "Внимание: если флажок включён, DISM может обращаться к источнику Windows Update/WSUS, настроенному на этом компьютере.",
            AutoSize = true,
            MaximumSize = new Size(980, 0),
            ForeColor = Color.FromArgb(151, 94, 28),
            Margin = new Padding(3, 3, 3, 8)
        }, 0, 2);
        AddSection(settingsStack, "Политика обновлений и источников", safetyGrid);

        var profileGrid = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 2) };
        profileGrid.Controls.Add(StyleButton(_saveProfile));
        profileGrid.Controls.Add(StyleButton(_loadProfile));
        profileGrid.Controls.Add(new Label
        {
            Text = "Профиль хранит пути и настройки, но не копирует ISO или пакеты.",
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            ForeColor = Color.FromArgb(83, 101, 122),
            Margin = new Padding(10, 10, 3, 3)
        });
        AddSection(settingsStack, "Профили проекта", profileGrid);

        var bundleGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 2, 0, 2) };
        bundleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bundleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bundleGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bundleGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bundleGrid.Controls.Add(StyleButton(_exportBundle, accent: true), 0, 0);
        bundleGrid.Controls.Add(new Label
        {
            Text = "Собирает найденные русские CAB и Setup resources из распакованных папок в ZIP с SHA-256 манифестом; обновления Windows исключаются.",
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = Color.FromArgb(83, 101, 122),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(10, 7, 3, 5)
        }, 1, 0);
        bundleGrid.Controls.Add(new Label
        {
            Text = "ISO-файлы автоматически не распаковываются: сначала укажите папку или локальный кеш.",
            AutoSize = true,
            ForeColor = Color.FromArgb(83, 101, 122),
            Margin = new Padding(3, 8, 3, 3)
        }, 0, 1);
        bundleGrid.SetColumnSpan(bundleGrid.GetControlFromPosition(0, 1)!, 2);
        AddSection(settingsStack, "Офлайн-пакет компонентов", bundleGrid);

        AddSection(settingsStack, "Перед развёртыванием", new Label
        {
            Text = "Если совместимых WinPE/WinRE ресурсов нет, программа должна вывести предупреждение, а не внедрять пакеты другой архитектуры или базовой сборки. Перед реальной установкой протестируйте ISO в виртуальной машине.",
            AutoSize = true,
            MaximumSize = new Size(1020, 0),
            ForeColor = Color.FromArgb(83, 101, 122),
            Padding = new Padding(3, 4, 3, 8)
        });

        // Tab 4 — searchable/copyable operations log.
        var logLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
            BackColor = Color.White
        };
        logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logToolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        logToolbar.Controls.Add(StyleButton(_copyLog));
        logToolbar.Controls.Add(StyleButton(_saveLog));
        logToolbar.Controls.Add(StyleButton(_clearLog));
        logLayout.Controls.Add(logToolbar, 0, 0);
        _log.Dock = DockStyle.Fill;
        _log.Font = new Font("Consolas", 9);
        _log.BackColor = Color.FromArgb(248, 250, 252);
        _log.ForeColor = Color.FromArgb(35, 47, 62);
        _log.BorderStyle = BorderStyle.FixedSingle;
        logLayout.Controls.Add(_log, 0, 1);
        logTab.Controls.Add(logLayout);

        // Persistent footer keeps the current stage, progress and primary action visible.
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            BackColor = Color.White,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < 3; row++) footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status.AutoSize = false;
        _status.Height = 30;
        _status.Dock = DockStyle.Fill;
        _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = new Font("Segoe UI", 9.25f, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(47, 72, 95);
        _status.BackColor = Color.FromArgb(231, 237, 244);
        _status.Padding = new Padding(9, 5, 9, 5);
        _status.Margin = new Padding(0, 0, 0, 4);
        _progressDetail.Dock = DockStyle.Fill;
        _progressDetail.ForeColor = Color.FromArgb(83, 101, 122);
        _progressDetail.Margin = new Padding(0, 0, 6, 2);
        _progress.Width = 300;
        _progress.MinimumSize = new Size(220, 18);
        _progress.Dock = DockStyle.Fill;
        _progressPercent.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _progressPercent.ForeColor = Color.FromArgb(83, 101, 122);
        _progressPercent.Margin = new Padding(8, 2, 8, 2);
        StyleButton(_run, primary: true);
        _run.Text = "Создать русифицированный ISO";
        _run.MinimumSize = new Size(248, 42);
        StyleButton(_cancelDownload);
        _cancelDownload.MinimumSize = new Size(150, 40);
        footer.Controls.Add(_status, 0, 0);
        footer.SetColumnSpan(_status, 4);
        footer.Controls.Add(_progressDetail, 0, 1);
        footer.Controls.Add(_progress, 1, 1);
        footer.Controls.Add(_progressPercent, 2, 1);
        footer.Controls.Add(_run, 0, 2);
        footer.Controls.Add(_cancelDownload, 1, 2);
        footer.SetColumnSpan(_cancelDownload, 3);
        root.Controls.Add(footer, 0, 2);

        foreach (var textBox in new[] { _iso, _packageSource, _fodSource, _setupSource, _donorSource, _output })
        {
            textBox.Dock = DockStyle.Fill;
            textBox.Height = 28;
            textBox.Margin = new Padding(3, 4, 3, 4);
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.BackColor = Color.White;
            textBox.ForeColor = Color.FromArgb(35, 47, 62);
            textBox.Font = new Font("Segoe UI", 9.5f);
        }
        _packages.BackColor = Color.White;
        _packages.ForeColor = Color.FromArgb(35, 47, 62);
        _helpTips.SetToolTip(_iso, "Исходный английский или многоязычный Windows ISO. Оригинал не изменяется.");
        _helpTips.SetToolTip(_inspect, "Читает индексы установочного WIM/ESD, версии Windows и архитектуру.");
        _helpTips.SetToolTip(_autoDownload, "Ищет подходящую базовую сборку, загружает только языковые компоненты и проверяет файлы.");
        _helpTips.SetToolTip(_packageSource, "Основной Language Pack. Можно указать официальный LP ISO или распакованную папку.");
        _helpTips.SetToolTip(_fodSource, "Источник языковых Features on Demand: OCR, речь, шрифты и рукописный ввод.");
        _helpTips.SetToolTip(_setupSource, "Дополнительные ресурсы русской установки Windows в sources\\ru-ru.");
        _helpTips.SetToolTip(_donorSource, "Совместимый русский ISO используется как источник найденных ресурсов, а не как целевой образ.");
        _helpTips.SetToolTip(_useWindowsUpdateForCapabilities, "По умолчанию отключено. Не разрешает интеграцию LCU/SSU и не обслуживает WinRE.");
        _helpTips.SetToolTip(_run, "Создаёт новый загрузочный ISO. Исходный файл не перезаписывается.");
        _helpTips.SetToolTip(_cancelDownload, "Останавливает поиск и загрузку языка, но не прерывает запущенное обслуживание DISM.");
        _allIndexes.Margin = new Padding(2, 5, 2, 3);
        _index.Margin = new Padding(2, 3, 2, 3);
        _output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Windows-IoT-LTSC-ru.iso");
    }

    private void AddMediaSourceRow(TableLayoutPanel table, int rowIndex, string caption, TextBox target,
        string isoTitle, string folderTitle, Button? diagnosticButton = null)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = diagnosticButton is null ? 4 : 5,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 5),
            Padding = new Padding(0, 1, 0, 1)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (diagnosticButton is not null) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        row.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            MaximumSize = new Size(162, 0),
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold),
            ForeColor = Color.FromArgb(47, 64, 84),
            Margin = new Padding(3, 8, 6, 4)
        }, 0, 0);
        target.Dock = DockStyle.Fill;
        row.Controls.Add(target, 1, 0);

        var isoButton = new Button { Text = "ISO…", AutoSize = true };
        StylePickerButton(isoButton);
        isoButton.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "ISO-образы (*.iso)|*.iso", Title = isoTitle };
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
        };
        row.Controls.Add(isoButton, 2, 0);

        var folderButton = new Button { Text = "Папка…", AutoSize = true };
        StylePickerButton(folderButton);
        folderButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = folderTitle };
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        };
        row.Controls.Add(folderButton, 3, 0);

        if (diagnosticButton is not null)
        {
            diagnosticButton.Text = "Проверить";
            diagnosticButton.AutoSize = true;
            StylePickerButton(diagnosticButton, true);
            row.Controls.Add(diagnosticButton, 4, 0);
        }

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(row, 0, rowIndex);
    }

    private static void StylePickerButton(Button button, bool accent = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(207, 216, 226);
        button.BackColor = accent ? Color.FromArgb(222, 241, 244) : Color.FromArgb(249, 250, 252);
        button.ForeColor = accent ? Color.FromArgb(26, 94, 112) : Color.FromArgb(42, 58, 78);
        button.Padding = new Padding(8, 4, 8, 4);
        button.Margin = new Padding(3);
        button.Cursor = Cursors.Hand;
    }

    private void RefreshInputSummary()
    {
        var iso = _iso.Text.Trim();
        if (File.Exists(iso) && Path.GetExtension(iso).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(iso);
                _isoSummary.Text = $"Выбран: {info.Name}  ·  {info.Length / 1073741824d:0.00} ГиБ";
                _isoSummary.ForeColor = Color.FromArgb(34, 119, 86);
            }
            catch
            {
                _isoSummary.Text = "Исходный ISO выбран.";
                _isoSummary.ForeColor = Color.FromArgb(83, 101, 122);
            }
        }
        else
        {
            _isoSummary.Text = "Выберите ISO-файл Windows или перетащите его сюда.";
            _isoSummary.ForeColor = Color.FromArgb(151, 94, 28);
        }

        var paths = new[] { _packageSource.Text.Trim(), _fodSource.Text.Trim(), _setupSource.Text.Trim(), _donorSource.Text.Trim() };
        var selected = paths.Count(p => !string.IsNullOrWhiteSpace(p));
        var valid = paths.Count(p => string.IsNullOrWhiteSpace(p) || Directory.Exists(p) ||
            (File.Exists(p) && Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase)));
        _sourceSummary.Text = selected == 0
            ? "Отдельные источники не выбраны — можно попробовать автоматический поиск ru-RU."
            : $"Дополнительные источники: {selected} из 4  ·  {(valid == paths.Length ? "пути проверены" : "проверьте доступность выбранных путей")}";
        _sourceSummary.ForeColor = valid == paths.Length ? Color.FromArgb(83, 101, 122) : Color.FromArgb(170, 70, 54);
    }

    private void SetProgressUi(int value)
    {
        value = Math.Clamp(value, _progress.Minimum, _progress.Maximum);
        _progress.Value = value;
        _progressPercent.Text = $"{value}%";
    }

    private void CopyLogToClipboard()
    {
        try
        {
            if (string.IsNullOrEmpty(_log.Text))
            {
                Log("Журнал пуст.");
                return;
            }
            Clipboard.SetText(_log.Text);
            Log("Журнал скопирован в буфер обмена.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось скопировать журнал", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveLogToFile()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Текстовый журнал (*.log;*.txt)|*.log;*.txt|Все файлы (*.*)|*.*",
            FileName = "Localizer-IoT.log",
            DefaultExt = "log",
            AddExtension = true,
            Title = "Сохранить журнал обработки"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, _log.Text, new UTF8Encoding(false));
            Log("Журнал сохранён: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось сохранить журнал", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        SetProgressUi(0);
        try
        {
            var result = await DownloadRussianPackagesWithCancellationAsync();
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
        catch (OperationCanceledException)
        {
            Log("Загрузка ru-RU отменена пользователем. Незавершённые части файлов сохранены для возможного продолжения.");
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
        if (string.IsNullOrWhiteSpace(_packageSource.Text) && cabFiles.Length == 0 && !DonorFolderHasCoreLanguagePack())
        {
            SetBusy(true);
            SetProgressUi(0);
            try
            {
                Log("Не найден отдельный основной Language Pack CAB — запускаю автоматический поиск и скачивание ru-RU.");
                var result = await DownloadRussianPackagesWithCancellationAsync();
                _packageSource.Text = result.Directory;
                if (string.IsNullOrWhiteSpace(_fodSource.Text)) _fodSource.Text = result.Directory;
                cabFiles = _packages.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(File.Exists).ToArray();
                Log($"Автоматически подготовлено CAB: {result.PackagesReady}; FOD CAB: {result.FeaturePackagesReady}.");
            }
            catch (OperationCanceledException)
            {
                Log("Автоматический поиск/загрузка ru-RU отменены. Частичные файлы сохранены для продолжения.");
                return;
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
            .Where(p => Directory.Exists(p) ||
                (File.Exists(p) && Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
                    SetProgressUi(0);
                    if (hasCompatibleWinPe)
                        await ImageServicingEngine.EnsureDeploymentToolsAsync(Log);
                    else
                        await ImageServicingEngine.EnsureCompatibleSetupToolsAsync(_iso.Text.Trim(), Log);
                    SetProgressUi(5);
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
            (int)_index.Value,
            _useWindowsUpdateForCapabilities.Checked);

        SetBusy(true);
        SetProgressUi(0);
        try
        {
            await Task.Run(() => ImageServicingEngine.Run(request, (p, message) =>
            {
                Ui(() => SetProgressUi(Math.Clamp(p, 0, 100)));
                Log(message);
            }));
            Ui(() => SetProgressUi(100));
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
        _saveProfile.Enabled = _loadProfile.Enabled = _exportBundle.Enabled = !busy;
        _clearLog.Enabled = _saveLog.Enabled = _copyLog.Enabled = !busy;
        if (busy && _workspaceTabs is not null && _logTabPage is not null) _workspaceTabs.SelectedTab = _logTabPage;
        if (_workspaceTabs is not null) _workspaceTabs.Enabled = !busy;
        _cancelDownload.Enabled = busy && _operationCts is not null;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private async Task ExportOfflineBundleAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Офлайн-пакет языковых компонентов (*.zip)|*.zip",
            DefaultExt = "zip",
            AddExtension = true,
            FileName = "Localizer-IoT-ru-RU-offline.zip",
            Title = "Экспортировать найденные языковые компоненты и ресурсы Setup"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var roots = new[] { _packageSource.Text.Trim(), _fodSource.Text.Trim(), _setupSource.Text.Trim(), _donorSource.Text.Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            MessageBox.Show(this,
                "Для экспорта укажите хотя бы одну папку с распакованными языковыми компонентами. ISO-файлы не распаковываются автоматически этим экспортом; кеш, созданный автоматической загрузкой, уже подходит.",
                "Нет папок-источников", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var output = Path.GetFullPath(dialog.FileName);
        SetBusy(true);
        SetProgressUi(0);
        var temp = output + ".building";
        try
        {
            await Task.Run(() =>
            {
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var setupResources = 0;
                var scanned = 0;
                for (var i = 0; i < roots.Length; i++)
                {
                    var root = roots[i];
                    IEnumerable<string> sourceFiles;
                    try { sourceFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray(); }
                    catch (Exception ex) { Log("Не удалось просканировать источник для офлайн-пакета " + root + ": " + ex.Message); continue; }

                    foreach (var file in sourceFiles)
                    {
                        scanned++;
                        if (scanned % 250 == 0) Ui(() => SetProgressUi(Math.Min(35, _progress.Value + 1)));
                        var normalized = file.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        var relative = Path.GetRelativePath(root, file);
                        var lower = normalized.ToLowerInvariant();
                        var name = Path.GetFileName(file).ToLowerInvariant();
                        var isUpdate = System.Text.RegularExpressions.Regex.IsMatch(name, @"kb\d{6,}", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                            name.Contains("rollupfix") || name.Contains("cumulative") || name.Contains("servicingstack") ||
                            System.Text.RegularExpressions.Regex.IsMatch(name, @"(^|[-_.])lcu([-_.]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                            System.Text.RegularExpressions.Regex.IsMatch(name, @"(^|[-_.])ssu([-_.]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                            Path.GetExtension(file).Equals(".msu", StringComparison.OrdinalIgnoreCase);
                        if (isUpdate) continue;

                        var setupMarker = Path.DirectorySeparatorChar + "sources" + Path.DirectorySeparatorChar + "ru-ru" + Path.DirectorySeparatorChar;
                        var winPeMarker = Path.DirectorySeparatorChar + "winpe_ocs" + Path.DirectorySeparatorChar + "ru-ru" + Path.DirectorySeparatorChar;
                        var isSetupResource = lower.Contains(setupMarker, StringComparison.OrdinalIgnoreCase);
                        var isWinPeResource = lower.Contains(winPeMarker, StringComparison.OrdinalIgnoreCase);
                        var isRussianCab = Path.GetExtension(file).Equals(".cab", StringComparison.OrdinalIgnoreCase) &&
                            (lower.Contains("ru-ru") || lower.Contains("ru_ru") || lower.Contains("russian") || isWinPeResource) &&
                            (name.Contains("language") || name.Contains("languagepack") || name.Contains("language-pack") ||
                             name.Contains("languagefeature") || name.Contains("language-feature") || name.Contains("winpe-") ||
                             name.Equals("lp.cab") || isWinPeResource);
                        if (!isSetupResource && !isRussianCab) continue;
                        var prefix = "source-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var entry = prefix + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
                        if (files.TryAdd(entry, file) && isSetupResource) setupResources++;
                    }
                }

                if (files.Count == 0)
                    throw new InvalidOperationException("В выбранных папках не найдены отдельные ru-RU CAB или ресурсы sources\\ru-ru. Проверьте источники.");
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                var report = new StringBuilder();
                report.AppendLine("Localizer-IoT offline language bundle");
                report.AppendLine("Created UTC: " + DateTimeOffset.UtcNow.ToString("O"));
                report.AppendLine("OS update payloads included: no");
                report.AppendLine("Entries: " + files.Count);
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                {
                    var index = 0;
                    foreach (var item in files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var info = new FileInfo(item.Value);
                        using var input = new FileStream(item.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
                        input.Position = 0;
                        var entry = zip.CreateEntry(item.Key, CompressionLevel.NoCompression);
                        using (var dest = entry.Open()) input.CopyTo(dest);
                        report.AppendLine($"{info.Length}\t{hash}\t{item.Key}");
                        index++;
                        if (index % 20 == 0) Ui(() => SetProgressUi(35 + (int)(60.0 * index / files.Count)));
                    }
                    var manifest = zip.CreateEntry("bundle-manifest.txt", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                        writer.Write(report.ToString());
                }
            });
            File.Move(temp, output, true);
            SetProgressUi(100);
            Log("Офлайн-пакет создан: " + output);
            MessageBox.Show(this, "Офлайн-пакет создан. Внутри — найденные русские Language Pack/FOD/WinPE CAB и ресурсы sources\\ru-ru, если они были в папках-источниках. LCU/SSU/KB/MSU не включаются. Проверьте bundle-manifest.txt.",
                "Экспорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            Log("Не удалось создать офлайн-пакет: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Экспорт офлайн-пакета не завершён", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false); }
    }

    private sealed record LocalizationProfile(
        int FormatVersion,
        string? SourceIso,
        string? PackageSource,
        string? FodSource,
        string? SetupSource,
        string? DonorSource,
        string? ExplicitCabFiles,
        string? OutputIso,
        bool AllIndexes,
        int SelectedIndex,
        bool AllowWindowsUpdateForCapabilities);

    private void SaveProfile()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Профиль Localizer-IoT (*.json)|*.json",
            DefaultExt = "json",
            AddExtension = true,
            FileName = "Localizer-IoT-profile.json",
            Title = "Сохранить настройки источников и русификации"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var profile = new LocalizationProfile(
                1, _iso.Text.Trim(), _packageSource.Text.Trim(), _fodSource.Text.Trim(),
                _setupSource.Text.Trim(), _donorSource.Text.Trim(), _packages.Text,
                _output.Text.Trim(), _allIndexes.Checked, (int)_index.Value,
                _useWindowsUpdateForCapabilities.Checked);
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Log("Профиль сохранён: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось сохранить профиль", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Профиль Localizer-IoT (*.json)|*.json|Все файлы (*.*)|*.*",
            Title = "Загрузить настройки источников и русификации"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var profile = JsonSerializer.Deserialize<LocalizationProfile>(File.ReadAllText(dialog.FileName))
                ?? throw new InvalidDataException("Файл не содержит профиль Localizer-IoT.");
            if (profile.FormatVersion != 1) throw new InvalidDataException("Неподдерживаемая версия профиля: " + profile.FormatVersion);
            _iso.Text = profile.SourceIso ?? "";
            _packageSource.Text = profile.PackageSource ?? "";
            _fodSource.Text = profile.FodSource ?? "";
            _setupSource.Text = profile.SetupSource ?? "";
            _donorSource.Text = profile.DonorSource ?? "";
            _packages.Text = profile.ExplicitCabFiles ?? "";
            _output.Text = profile.OutputIso ?? "";
            _allIndexes.Checked = profile.AllIndexes;
            _index.Value = Math.Clamp(profile.SelectedIndex, (int)_index.Minimum, (int)_index.Maximum);
            _useWindowsUpdateForCapabilities.Checked = profile.AllowWindowsUpdateForCapabilities;
            Log("Профиль загружен. Перед запуском проверьте доступность указанных файлов/папок: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось загрузить профиль", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CancelCurrentDownload()
    {
        if (_operationCts is null || _operationCts.IsCancellationRequested) return;
        _operationCts.Cancel();
        _cancelDownload.Enabled = false;
        Log("Запрошена отмена загрузки. Уже подтверждённые файлы кэша сохранятся; частично скачанный файл будет доступен для продолжения.");
    }

    private async Task<AutoLanguagePackageSource> DownloadRussianPackagesWithCancellationAsync()
    {
        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        _cancelDownload.Enabled = true;
        try
        {
            return await UupLanguagePackDownloader.DownloadRussianPackagesAsync(
                _iso.Text.Trim(), Log, p => Ui(() => SetProgressUi(Math.Clamp(p, 0, 100))), cts.Token);
        }
        finally
        {
            _operationCts = null;
            _cancelDownload.Enabled = false;
        }
    }

    private bool DonorFolderHasCoreLanguagePack()
    {
        var path = _donorSource.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        try
        {
            return Directory.EnumerateFiles(path, "*.cab", SearchOption.AllDirectories)
                .Take(20000).Any(p =>
                {
                    var n = Path.GetFileName(p);
                    return n.Contains("Client-LanguagePack", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("Client_LanguagePack", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("ClientLanguagePack", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("Language-Pack", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("LanguagePack", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch (Exception ex)
        {
            Log("Не удалось проверить CAB в папке донора; будет выполнен автоматический поиск Language Pack: " + ex.Message);
            return false;
        }
    }

    private void Log(string message)
    {
        Ui(() =>
        {
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            _status.Text = message;
            _progressDetail.Text = message;
            var lower = message.ToLowerInvariant();
            if (lower.Contains("ошибка") || lower.Contains("error") || lower.Contains("не удалось") || lower.Contains("остановлена"))
            {
                _status.ForeColor = Color.FromArgb(150, 49, 42);
                _status.BackColor = Color.FromArgb(252, 234, 231);
            }
            else if (lower.Contains("предупреждение") || lower.Contains("warning") || lower.Contains("частично"))
            {
                _status.ForeColor = Color.FromArgb(137, 83, 22);
                _status.BackColor = Color.FromArgb(255, 244, 222);
            }
            else
            {
                _status.ForeColor = Color.FromArgb(47, 72, 95);
                _status.BackColor = Color.FromArgb(231, 237, 244);
            }
        });
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _helpTips.Dispose();
        base.Dispose(disposing);
    }
}