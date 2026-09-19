using System.Diagnostics;
using System.Net;
using AirTake.Core;
using QRCoder;

namespace AirTake.Desktop;

internal sealed class MainWindow : Form
{
    private static readonly Color Background = Color.FromArgb(14, 18, 27);
    private static readonly Color Surface = Color.FromArgb(25, 32, 46);
    private static readonly Color TextColor = Color.FromArgb(231, 237, 244);
    private static readonly Color Muted = Color.FromArgb(155, 169, 190);
    private static readonly Color Accent = Color.FromArgb(70, 211, 163);
    private readonly bool smoke;
    private readonly AppOptions options = Storage.LoadOptions();
    private readonly Identity identity = Storage.LoadIdentity();
    private readonly FifineRecorder audio = new();
    private ReceiverHost? receiver;
    private readonly ComboBox address = Combo();
    private readonly NumericUpDown port = Number(1024, 65535, 48721);
    private readonly ComboBox resolution = Combo();
    private readonly ComboBox fps = Combo();
    private readonly NumericUpDown bitrate = Number(10, 200, 120);
    private readonly NumericUpDown buffer = Number(128, 4096, 512);
    private readonly CheckBox previewEnabled = Check("Предпросмотр на ПК · 1 кадр/с");
    private readonly NumericUpDown exposure = Number(-2, 2, 0, 1);
    private readonly CheckBox autofocus = Check("Непрерывный автофокус", true);
    private readonly NumericUpDown focus = Number(0, 1, 0.5m, 2);
    private readonly CheckBox autoWhite = Check("Автоматический баланс белого", true);
    private readonly NumericUpDown white = Number(2000, 9000, 5500);
    private readonly ComboBox microphone = Combo();
    private readonly CheckBox videoOnly = Check("Записывать без звука (явно)");
    private readonly NumericUpDown audioOffset = Number(-2000, 2000, 0);
    private readonly TextBox output = Edit();
    private readonly TextBox ffmpeg = Edit();
    private readonly CheckBox keepSources = Check("Сохранять исходные фрагменты и WAV");
    private readonly Label estimates = LabelText("");
    private readonly Label state = LabelText("Ожидание iPhone", 11, Accent);
    private readonly Label metrics = LabelText("4K / 120 — запрашиваемый режим, не подтверждённый замер.", 11);
    private readonly Label connectionInfo = LabelText("Подключение по QR-коду в AirTake Camera.");
    private readonly Label previewHint = LabelText("AIRTAKE CAMERA\n\nПодключите iPhone в разделе «Подключение».\nПредпросмотр по умолчанию выключен: ресурсы — записи.", 14, Muted);
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly PictureBox qr = new() { Width = 272, Height = 272, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Background, ForeColor = Muted, BorderStyle = BorderStyle.None };
    private readonly Button record = ButtonText("REC  /  ЗАПИСАТЬ", null, Accent);
    private readonly Button stop = ButtonText("ОСТАНОВИТЬ");
    private readonly Button apply = ButtonText("СОХРАНИТЬ / ПЕРЕЗАПУСТИТЬ");
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private byte[]? lastPreview;
    private bool closing;
    private bool operating;
    private string lastStatus = "";

    public MainWindow(bool smoke)
    {
        this.smoke = smoke;
        Text = "AirTake · Wireless Recorder";
        Size = new Size(1220, 900);
        MinimumSize = new Size(1020, 760);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = Background;
        ForeColor = TextColor;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 3, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        var brand = LabelText("AirTake", 29, Accent); brand.Font = new Font("Segoe UI Semibold", 29); brand.Dock = DockStyle.Fill;
        header.Controls.Add(brand, 0, 0);
        state.Dock = DockStyle.Fill; state.TextAlign = ContentAlignment.MiddleRight;
        header.Controls.Add(state, 1, 0);
        root.Controls.Add(header, 0, 0);
        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 388));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        BuildTabs(); middle.Controls.Add(tabs, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 0, 0), RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        previewPanel.Controls.Add(preview);
        previewHint.Dock = DockStyle.Fill; previewHint.TextAlign = ContentAlignment.MiddleCenter; previewHint.BackColor = Color.Black;
        previewPanel.Controls.Add(previewHint); previewHint.BringToFront();
        right.Controls.Add(previewPanel, 0, 0);
        metrics.Dock = DockStyle.Fill; metrics.Padding = new Padding(12); metrics.BackColor = Surface;
        right.Controls.Add(metrics, 0, 1);
        right.Controls.Add(log, 0, 2);
        middle.Controls.Add(right, 1, 0);
        root.Controls.Add(middle, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 18, 0, 0), WrapContents = false };
        record.Width = 225; stop.Width = 190; apply.Width = 322;
        footer.Controls.AddRange([record, stop, apply]);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        FillSettings();
        record.Click += async (_, _) => await Safely(async () => { if (receiver is null) await RestartAsync(); receiver!.RequestRecording(true); });
        stop.Click += async (_, _) => await Safely(() => { receiver?.RequestRecording(false); return Task.CompletedTask; });
        apply.Click += async (_, _) => await Safely(RestartAsync);
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        Shown += async (_, _) => { if (!smoke) await Safely(RestartAsync); };
        FormClosing += ClosingAsync;
    }
    private void BuildTabs()
    {
        var camera = Tab("Камера");
        Add(camera, LabelText("ВИДЕО", 12, Accent));
        resolution.Items.AddRange(["4K · 3840 × 2160", "1080p · 1920 × 1080"]);
        fps.Items.AddRange(["30", "60", "120"]);
        Add(camera, Row("Разрешение", resolution), Row("Кадров/с", fps), Row("Битрейт, Мбит/с", bitrate), Row("Буфер iPhone, МиБ", buffer), previewEnabled);
        Add(camera, LabelText("120 FPS включается только при реальной поддержке камерой и аппаратным HEVC-энкодером. Без подмены кадрами-дубликатами.", 9, Muted));
        Add(camera, Row("Экспокоррекция EV", exposure), autofocus, Row("Ручной фокус 0…1", focus), autoWhite, Row("Баланс белого, K", white));
        Add(camera, LabelText("Основная задняя камера 1× · HEVC Main / SDR\nСтабилизация выключена · горизонтальный кадр", 9, Muted));
        autofocus.CheckedChanged += (_, _) => focus.Enabled = !autofocus.Checked;
        autoWhite.CheckedChanged += (_, _) => white.Enabled = !autoWhite.Checked;

        var connection = Tab("Подключение");
        Add(connection, LabelText("IPHONE → WI-FI → ПК", 12, Accent), Row("IPv4 компьютера", address), Row("TCP-порт", port), connectionInfo, qr);
        Add(connection, ButtonText("Скопировать код сопряжения", (_, _) => { if (receiver is not null) Clipboard.SetText(identity.PairingUri(options.ListenAddress, options.Port)); }));
        Add(connection, ButtonText("Разрешить в брандмауэре", async (_, _) => await Safely(AllowFirewallAsync)));
        Add(connection, LabelText("Сканируйте QR внутри AirTake Camera, не обычной камерой. Оба устройства должны быть в одной локальной сети. QR содержит секрет доступа — не публикуйте его.", 9, Muted));

        var sound = Tab("Звук");
        Add(sound, LabelText("МИКРОФОН НА WINDOWS", 12, Accent), microphone, ButtonText("Обновить список микрофонов", (_, _) => RefreshMicrophones()), videoOnly, Row("Сдвиг звука, мс", audioOffset));
        Add(sound, LabelText("Выберите свой Fifine. Положительный сдвиг задерживает звук. Автосинхронизация использует часы устройств; поправка компенсирует задержку драйвера. Для точной настройки сделайте хлопок.", 10, Muted));
        Add(sound, LabelText("Исходник: WAV в формате устройства.\nГотовый MP4: AAC, 48 кГц, 192 кбит/с.\nМикрофон iPhone не используется.", 10));

        var disk = Tab("Диск");
        Add(disk, LabelText("ЗАПИСЬ И ВОССТАНОВЛЕНИЕ", 12, Accent), LabelText("Папка записи"), output);
        Add(disk, ButtonText("Выбрать папку", (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = output.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath; }));
        Add(disk, ButtonText("Открыть записи", async (_, _) => await Safely(() => { Directory.CreateDirectory(output.Text); Process.Start(new ProcessStartInfo(output.Text) { UseShellExecute = true }); return Task.CompletedTask; })));
        Add(disk, keepSources, estimates, LabelText("FFmpeg (пусто = комплектный tools/ffmpeg.exe)", 9, Muted), ffmpeg);
        Add(disk, ButtonText("Выбрать ffmpeg.exe", (_, _) => { using var dialog = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe" }; if (dialog.ShowDialog(this) == DialogResult.OK) ffmpeg.Text = dialog.FileName; }));
        Add(disk, ButtonText("Восстановить незавершённую запись", async (_, _) => await Safely(RecoverAsync)));
        bitrate.ValueChanged += (_, _) => UpdateEstimates();
    }
    private FlowLayoutPanel Tab(string title)
    {
        var tab = new TabPage(title) { BackColor = Surface, ForeColor = TextColor, Padding = new Padding(10) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        tab.Controls.Add(panel); tabs.TabPages.Add(tab); return panel;
    }
    private static void Add(FlowLayoutPanel panel, params Control[] controls)
    {
        foreach (var control in controls) { if (control != null) { control.Margin = new Padding(0, 0, 0, 12); panel.Controls.Add(control); } }
    }
    private static Control Row(string label, Control control)
    {
        var row = new TableLayoutPanel { Width = 340, Height = 37, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        var text = LabelText(label, 9); text.Dock = DockStyle.Fill; text.TextAlign = ContentAlignment.MiddleLeft;
        control.Dock = DockStyle.Fill; row.Controls.Add(text, 0, 0); row.Controls.Add(control, 1, 0); return row;
    }
    private static Label LabelText(string text, float size = 10, Color? color = null) => new() { Text = text, AutoSize = false, Width = 340, Height = text.Count(c => c == '\n') * 22 + (text.Length > 110 ? 90 : text.Length > 65 ? 58 : 32), ForeColor = color ?? TextColor, Font = new Font("Segoe UI", size) };
    private static Button ButtonText(string text, EventHandler? click = null, Color? color = null)
    {
        var button = new Button { Text = text, Width = 340, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = color ?? Surface, ForeColor = color.HasValue ? Background : TextColor, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9) };
        button.FlatAppearance.BorderColor = Color.FromArgb(64, 79, 103); if (click is not null) button.Click += click; return button;
    }
    private static ComboBox Combo() => new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Background, ForeColor = TextColor, FlatStyle = FlatStyle.Flat };
    private static TextBox Edit() => new() { Width = 340, BackColor = Background, ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle };
    private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0) => new() { Width = 140, Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = decimals > 0 ? (decimal)Math.Pow(10, -decimals) : 1, BackColor = Background, ForeColor = TextColor };
    private static CheckBox Check(string text, bool value = false) => new() { Text = text, Width = 340, Height = 42, Checked = value, ForeColor = TextColor };
    private void FillSettings()
    {
        address.Items.AddRange(Storage.Addresses());
        address.SelectedItem = options.ListenAddress;
        if (address.SelectedIndex < 0 && address.Items.Count > 0) address.SelectedIndex = 0;
        port.Value = Math.Clamp(options.Port, 1024, 65535);
        resolution.SelectedIndex = options.Capture.Width == 3840 ? 0 : 1;
        fps.SelectedItem = options.Capture.Fps.ToString(); if (fps.SelectedIndex < 0) fps.SelectedItem = "120";
        bitrate.Value = Math.Clamp(options.Capture.BitrateMbps, 10, 200);
        buffer.Value = Math.Clamp(options.Capture.BufferMiB, 128, 4096);
        previewEnabled.Checked = options.Capture.Preview;
        exposure.Value = (decimal)Math.Clamp(options.Capture.ExposureBias, -2, 2);
        autofocus.Checked = options.Capture.Focus < 0;
        focus.Value = (decimal)Math.Clamp(options.Capture.Focus, 0, 1); focus.Enabled = !autofocus.Checked;
        autoWhite.Checked = options.Capture.WhiteBalanceKelvin == 0;
        white.Value = Math.Clamp(options.Capture.WhiteBalanceKelvin, 2000, 9000); white.Enabled = !autoWhite.Checked;
        videoOnly.Checked = options.VideoOnly;
        audioOffset.Value = Math.Clamp(options.AudioOffsetMs, -2000, 2000);
        output.Text = options.OutputDirectory; ffmpeg.Text = options.FfmpegPath; keepSources.Checked = options.KeepSources;
        RefreshMicrophones(); UpdateEstimates();
    }
    private void RefreshMicrophones()
    {
        var selected = (microphone.SelectedItem as AudioDevice)?.Id ?? options.MicrophoneId;
        microphone.Items.Clear();
        try
        {
            var devices = FifineRecorder.Devices(); microphone.Items.AddRange(devices);
            microphone.SelectedItem = devices.FirstOrDefault(d => d.Id == selected) ?? devices.FirstOrDefault(d => d.Name.Contains("fifine", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) { AppendLog("Microphone enumeration: " + exception.Message); }
    }
    private void UpdateEstimates() => estimates.Text = $"При {bitrate.Value} Мбит/с: ≈ {bitrate.Value * 7.5m:N0} МБ/мин · {bitrate.Value * 0.45m:N1} ГБ/ч.\nНа финальную сборку нужно ещё столько же свободного места. Реальный размер зависит от битрейта.";
    private void SaveSettings()
    {
        options.ListenAddress = address.SelectedItem?.ToString() ?? throw new InvalidOperationException("Нет локального IPv4-адреса. Подключите ПК к сети.");
        options.Port = (int)port.Value;
        options.OutputDirectory = Path.GetFullPath(output.Text);
        options.MicrophoneId = (microphone.SelectedItem as AudioDevice)?.Id ?? "";
        options.VideoOnly = videoOnly.Checked;
        options.AudioOffsetMs = (int)audioOffset.Value;
        options.KeepSources = keepSources.Checked;
        options.FfmpegPath = ffmpeg.Text.Trim();
        options.Capture = new CaptureOptions { Width = resolution.SelectedIndex == 0 ? 3840 : 1920, Height = resolution.SelectedIndex == 0 ? 2160 : 1080, Fps = int.Parse(fps.SelectedItem!.ToString()!), BitrateMbps = (int)bitrate.Value, BufferMiB = (int)buffer.Value, Preview = previewEnabled.Checked, ExposureBias = (double)exposure.Value, Focus = autofocus.Checked ? -1 : (double)focus.Value, WhiteBalanceKelvin = autoWhite.Checked ? 0 : (int)white.Value };
        options.Capture.Validate(); Storage.SaveOptions(options);
    }
    private async Task RestartAsync()
    {
        if (receiver?.Busy == true) throw new InvalidOperationException("Настройки заблокированы до завершения записи и передачи.");
        if (receiver is not null) { await receiver.DisposeAsync(); receiver = null; }
        SaveSettings();
        receiver = new ReceiverHost(options, identity, audio);
        receiver.Log += AppendLog;
        await receiver.StartAsync();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(identity.PairingUri(options.ListenAddress, options.Port), QRCodeGenerator.ECCLevel.M);
        using var image = new PngByteQRCode(data);
        using var stream = new MemoryStream(image.GetGraphic(6));
        using var original = Image.FromStream(stream);
        var old = qr.Image; qr.Image = new Bitmap(original); old?.Dispose();
        connectionInfo.Text = $"{options.ListenAddress}:{options.Port}\nHTTPS · сертификат закреплён по SHA-256";
    }
    private void RefreshStatus()
    {
        if (smoke) { record.Enabled = false; return; }
        var host = receiver;
        record.Enabled = !operating && host is { PhoneConnected: true, Busy: false };
        stop.Enabled = host?.DesiredRecording == true;
        apply.Enabled = !operating && host?.Busy != true;
        tabs.Enabled = !operating && host?.Busy != true;
        if (host is null) return;
        state.Text = host.PhoneConnected ? (host.DesiredRecording ? "REC  ·  iPHONE ПОДКЛЮЧЁН" : "iPHONE ПОДКЛЮЧЁН") : "ОЖИДАНИЕ iPHONE";
        state.ForeColor = host.DesiredRecording ? Color.Salmon : Accent;
        var t = host.Telemetry;
        long free = 0; try { free = TakeStore.FreeBytes(options.OutputDirectory); } catch (IOException) { }
        metrics.Text = $"Режим: {(string.IsNullOrEmpty(t.Mode) ? "не подтверждён" : t.Mode)}  ·  фактически {t.Fps:F1} FPS\n" +
            $"Пропуски: {(host.PhoneConnected ? t.Dropped.ToString() : "—")}  ·  очередь iPhone: {t.PendingBytes / 1048576d:F1} МиБ  ·  HEVC HW: {(t.HardwareEncoder ? "да" : "не проверен")}\n" +
            $"Нагрев: {t.Thermal}  ·  заряд: {(t.Battery < 0 ? "—" : (t.Battery * 100).ToString("F0") + "%")}  ·  свободно: {free / 1e9:F1} ГБ\n" +
            $"Fifine / выбранный вход: {(audio.Peak > 0 ? Math.Min(audio.Peak, 1) * 100 : 0):F0}%  ·  {t.Status}";
        if (host.Status != lastStatus) { lastStatus = host.Status; AppendLog(host.Status); }
        var bytes = host.Preview;
        if (bytes is not null && !ReferenceEquals(bytes, lastPreview))
        {
            try
            {
                using var stream = new MemoryStream(bytes); using var image = Image.FromStream(stream);
                var old = preview.Image; preview.Image = new Bitmap(image); old?.Dispose();
                lastPreview = bytes; previewHint.Visible = false;
            }
            catch (ArgumentException) { AppendLog("Повреждённый кадр предпросмотра пропущен; видеозапись не затронута."); }
        }
    }
    private void AppendLog(string message)
    {
        Storage.Log(message);
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => AppendLogOnUi(message))); return; }
        AppendLogOnUi(message);
    }
    private void AppendLogOnUi(string message)
    {
        if (log.TextLength > 50000) log.Text = log.Text[^25000..];
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }
    private async Task Safely(Func<Task> action)
    {
        if (operating) return;
        operating = true;
        try { await action(); }
        catch (Exception exception) { AppendLog(exception.ToString()); MessageBox.Show(this, exception.Message, "AirTake", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { operating = false; RefreshStatus(); }
    }
    private Task AllowFirewallAsync()
    {
        SaveSettings();
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate AirTake.exe.");
        var escaped = exe.Replace("'", "''");
        var script = $"Get-NetFirewallRule -DisplayName 'AirTake Receiver' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; New-NetFirewallRule -DisplayName 'AirTake Receiver' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {options.Port} -LocalAddress '{IPAddress.Parse(options.ListenAddress)}' -RemoteAddress LocalSubnet -Profile Private -Program '{escaped}'";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(script);
        Process.Start(start);
        AppendLog("Запрошено разрешение брандмауэра только для частной локальной сети.");
        return Task.CompletedTask;
    }
    private async Task RecoverAsync()
    {
        if (receiver is null) await RestartAsync();
        using var dialog = new FolderBrowserDialog { Description = "Выберите папку записи с файлом take.json", SelectedPath = options.OutputDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var video = MessageBox.Show(this, "Восстановить только видео? Да — без звука; Нет — со звуком из microphone.wav.", "Восстановление", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (video == DialogResult.Cancel) return;
        var file = await receiver!.RecoverAsync(dialog.SelectedPath, video == DialogResult.Yes);
        AppendLog("Восстановлено: " + file);
    }
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (closing || smoke) return;
        if (receiver?.Busy == true)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Сначала нажмите ОСТАНОВИТЬ и дождитесь сохранения MP4. Закрытие сейчас может оборвать звук или передачу.", "AirTake", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        e.Cancel = true; closing = true; timer.Stop();
        try { if (receiver is not null) await receiver.DisposeAsync(); audio.Dispose(); }
        catch (Exception exception) { Storage.Log(exception.ToString()); }
        finally { identity.Certificate.Dispose(); Close(); }
    }
}
