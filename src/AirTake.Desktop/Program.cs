using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirTake.Core;
using Microsoft.Win32;
using QRCoder;

namespace AirTake.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--integration-test"))
        {
            try { IntegrationSmoke.Run(args.Last()).GetAwaiter().GetResult(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args.Last(), "integration-error.txt"), ex.ToString()); Environment.Exit(1); }
            return;
        }
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Directory.CreateDirectory(Preferences.DataFolder);
            File.AppendAllText(Path.Combine(Preferences.DataFolder, "crash.log"), DateTimeOffset.UtcNow + " " + e.Exception + "\n");
            MessageBox.Show(e.Exception.Message, "AirTake — ошибка", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true;
        };
        app.Run(new MainWindow(args.Contains("--smoke-test"), args.LastOrDefault()));
    }
}

public sealed class MainWindow : Window
{
    private readonly Preferences prefs;
    private Receiver? receiver;
    private MicrophoneRecorder? microphone;
    private readonly SemaphoreSlim audioGate = new(1);
    private Guid? currentTake;
    private bool recording, waiting, shuttingDown;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock status = Text("Приёмник выключен", 14);
    private readonly TextBlock metrics = Text("FPS —   ·   Буфер —   ·   Кадры —", 15);
    private readonly TextBlock disk = Text("", 14);
    private readonly TextBlock mode = Text("4K / 120 FPS", 24, true);
    private readonly TextBlock previewHint = Text("Подключите AirTake Camera на iPhone", 20, true);
    private readonly TextBlock microphoneStatus = Text("Fifine: выберите устройство в настройках", 13);
    private readonly TextBlock url = Text("", 14);
    private readonly TextBlock warning = Text("4K/120 проверяется на устройстве. Автоматического перехода на 60 FPS нет.", 12);
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly Image qr = new() { Width = 216, Height = 216, Margin = new(0, 14, 0, 14) };
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, Foreground = Brush("A8B8CF"), BorderThickness = new(0), FontFamily = new("Consolas"), FontSize = 12 };
    private readonly Button recordButton;
    private readonly Button stopButton;
    private readonly Button releaseButton;
    private readonly ComboBox resolution = Combo(["3840 × 2160", "1920 × 1080"]);
    private readonly ComboBox fps = Combo(["120", "60", "30"]);
    private readonly ComboBox address = Combo(Preferences.Addresses());
    private readonly ComboBox microphones = new() { MinWidth = 250, Foreground = Brushes.Black, Margin = new(0, 5, 0, 12), Padding = new(7) };
    private readonly TextBox bitrate = Input(), buffer = Input(), port = Input(), reserve = Input(), offset = Input(), output = Input();
    private readonly CheckBox strict = Check("Останавливать дубль при потере кадра");
    private readonly CheckBox audio = Check("Записывать выбранный микрофон на ПК");
    private readonly StackPanel settings = new() { Margin = new(22) };
    private readonly ListBox takes = new() { Background = Brush("121923"), Foreground = Brushes.White, BorderThickness = new(0), MinHeight = 260, FontSize = 14 };
    private string? lastError;
    private readonly bool smoke;
    private readonly string? smokePath;
    private bool closeAllowed;

    public MainWindow(bool smoke, string? smokePath)
    {
        this.smoke = smoke; this.smokePath = smokePath;
        prefs = smoke ? new Preferences { OutputFolder = Path.Combine(Path.GetTempPath(), "AirTake-smoke") } : Preferences.Load();
        Title = "AirTake · Wireless Recording"; Width = 1240; Height = 860; MinWidth = 1050; MinHeight = 730;
        Background = Brush("0D121B"); Foreground = Brushes.White; FontFamily = new("Segoe UI"); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        recordButton = Button("●   Начать запись", BeginTake, true);
        stopButton = Button("■   Стоп", StopTake); stopButton.IsEnabled = false;
        releaseButton = Button("Завершить ожидание", ReleaseTake); releaseButton.IsEnabled = false;
        var root = new Grid { Margin = new(28, 20, 28, 20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new(0, 0, 0, 20) };
        var brand = new StackPanel(); brand.Children.Add(Text("AirTake", 32, true)); brand.Children.Add(Text("iPhone → Wi-Fi → Windows · без облака", 12));
        header.Children.Add(brand);
        status.HorizontalAlignment = HorizontalAlignment.Right; status.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(status); root.Children.Add(header);
        var tabs = new TabControl { Background = Brush("0D121B"), BorderThickness = new(0), Foreground = Brushes.White, Padding = new(0, 14, 0, 0) };
        tabs.Items.Add(Tab("  ЗАПИСЬ  ", RecordingView()));
        tabs.Items.Add(Tab("  НАСТРОЙКИ  ", SettingsView()));
        tabs.Items.Add(Tab("  ДУБЛИ  ", TakesView()));
        tabs.Items.Add(Tab("  ПОДКЛЮЧЕНИЕ  ", HelpView()));
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var footer = Text("AirTake 0.1.0 · HEVC · оригиналы сохраняются · 4K/120 требует проверки на реальном iPhone", 11); footer.Margin = new(0, 14, 0, 0);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        Populate();
        Loaded += async (_, _) =>
        {
            if (smoke) { await Task.Delay(700); SaveScreenshot(); closeAllowed = true; Close(); return; }
            try { await StartReceiver(); } catch (Exception ex) { AddLog(ex.Message); status.Text = "Не удалось запустить приёмник"; }
            timer.Tick += async (_, _) => await Refresh(); timer.Start();
        };
        Closing += async (_, e) =>
        {
            if (closeAllowed) return;
            e.Cancel = true; if (shuttingDown) return;
            if ((recording || waiting) && MessageBox.Show("Незавершённый дубль останется на диске. Закрыть AirTake?", "AirTake", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            shuttingDown = true; timer.Stop();
            try { if (currentTake is not null) await StopTake(); if (receiver is not null) await receiver.DisposeAsync(); }
            catch (Exception ex) { AddLog(ex.Message); }
            closeAllowed = true; Close();
        };
    }
    private UIElement RecordingView()
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new(290) });
        var left = new Grid { Margin = new(0, 0, 18, 0) }; left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new(115) });
        var title = new StackPanel { Margin = new(18, 16, 18, 12) }; title.Children.Add(mode); title.Children.Add(Text("HEVC · задняя основная камера 1× · изображение без перекодирования", 12)); left.Children.Add(title);
        var imageArea = new Grid { Background = Brushes.Black, Margin = new(0, 0, 0, 14), MinHeight = 260 }; imageArea.Children.Add(preview);
        previewHint.HorizontalAlignment = HorizontalAlignment.Center; previewHint.VerticalAlignment = VerticalAlignment.Center; imageArea.Children.Add(previewHint);
        Grid.SetRow(imageArea, 1); left.Children.Add(imageArea);
        var controls = new StackPanel(); metrics.Margin = new(0, 0, 0, 10); controls.Children.Add(metrics);
        var row = new WrapPanel(); row.Children.Add(recordButton); row.Children.Add(stopButton); row.Children.Add(releaseButton); controls.Children.Add(row);
        controls.Children.Add(microphoneStatus); disk.Margin = new(0, 6, 0, 8); controls.Children.Add(disk); controls.Children.Add(warning);
        Grid.SetRow(controls, 2); left.Children.Add(controls);
        var logs = Card(log); logs.Margin = new(0, 14, 0, 0); Grid.SetRow(logs, 3); left.Children.Add(logs); grid.Children.Add(left);
        var pair = new StackPanel { Margin = new(18) }; pair.Children.Add(Text("ПОДКЛЮЧЕНИЕ IPHONE", 12, true)); pair.Children.Add(qr); pair.Children.Add(url);
        pair.Children.Add(Button("Скопировать подключение", () => { if (receiver?.Pairing is not null) Clipboard.SetText(JsonSerializer.Serialize(receiver.Pairing, Json.Options)); return Task.CompletedTask; }));
        pair.Children.Add(Text("Откройте AirTake Camera на iPhone и отсканируйте QR. Телефон и ПК должны быть в одной локальной сети.", 13));
        pair.Children.Add(Button("Открыть папку записей", () => { Directory.CreateDirectory(prefs.OutputFolder); Open(prefs.OutputFolder); return Task.CompletedTask; }));
        pair.Children.Add(Text("TLS + привязка сертификата\nSHA-256 каждого фрагмента\nПовтор передачи после обрыва\nFifine записывается на ПК", 12));
        var right = Card(pair); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
    }
    private UIElement SettingsView()
    {
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new());
        var camera = new StackPanel { Margin = new(0, 0, 30, 0) }; camera.Children.Add(Text("Камера и передача", 23, true));
        Field(camera, "Разрешение", resolution); Field(camera, "Частота кадров", fps); Field(camera, "Битрейт HEVC, Мбит/с (20–200)", bitrate); Field(camera, "Буфер на iPhone, МиБ (128–4096)", buffer); camera.Children.Add(strict);
        camera.Children.Add(Text("Буфер временно использует память накопителя iPhone. Подтверждённые фрагменты удаляются автоматически. При заполнении запись останавливается, данные не перезаписываются.", 12));
        var computer = new StackPanel(); computer.Children.Add(Text("Компьютер и Fifine", 23, true));
        Field(computer, "Микрофон", microphones); computer.Children.Add(audio); Field(computer, "Поправка звука, мс (плюс — задержать звук)", offset);
        Field(computer, "Адрес сетевого адаптера ПК", address); Field(computer, "Порт приёмника", port); Field(computer, "Резерв свободного диска, ГиБ", reserve);
        columns.Children.Add(camera); Grid.SetColumn(computer, 1); columns.Children.Add(computer); settings.Children.Add(columns);
        Field(settings, "Папка записей", output);
        var buttons = new WrapPanel(); buttons.Children.Add(Button("Выбрать папку", () => { var dialog = new OpenFolderDialog { Title = "Папка записей AirTake" }; if (dialog.ShowDialog() == true) output.Text = dialog.FolderName; return Task.CompletedTask; }));
        buttons.Children.Add(Button("Сохранить и перезапустить приёмник", SaveSettings, true));
        buttons.Children.Add(Button("Обновить микрофоны", () => { PopulateMicrophones(); return Task.CompletedTask; }));
        buttons.Children.Add(Button("Разрешить частную сеть", Firewall)); settings.Children.Add(buttons);
        settings.Children.Add(Text("Для общего файла MP4: видео копируется без потери качества, WAV Fifine кодируется в AAC 48 кГц. Превью имеет пониженное разрешение и не отражает FPS файла.", 12));
        return new ScrollViewer { Content = Card(settings), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private UIElement TakesView()
    {
        var panel = new StackPanel { Margin = new(22) }; panel.Children.Add(Text("Записанные дубли", 24, true));
        panel.Children.Add(Text("В папке дубля: итоговый AirTake.mp4, исходное video.mp4, microphone.wav, фрагменты, take.json и журнал экспорта.", 13)); panel.Children.Add(takes);
        var row = new WrapPanel();
        row.Children.Add(Button("Обновить", () => { RefreshTakes(); return Task.CompletedTask; }));
        row.Children.Add(Button("Открыть папку дубля", () => { if (takes.SelectedItem is TakeRow take && receiver is not null) Open(receiver.Store.Folder(take.Manifest.Id)); return Task.CompletedTask; }));
        row.Children.Add(Button("Повторить экспорт", async () => { if (takes.SelectedItem is TakeRow take && receiver is not null) { await Exporter.Export(receiver.Store, take.Manifest.Id); RefreshTakes(); AddLog("Экспорт завершён."); } }));
        panel.Children.Add(row); panel.Children.Add(Text("Не удаляйте фрагменты до проверки результата. При хранении оригиналов и экспорте резервируйте до трёх объёмов видео плюс WAV.", 12)); return Card(panel);
    }
    private UIElement HelpView()
    {
        var panel = new StackPanel { Margin = new(24) }; panel.Children.Add(Text("Первый запуск", 26, true));
        panel.Children.Add(Text("1. Распакуйте весь Windows ZIP и запустите AirTake.exe.\n\n2. Установите подписанное AirTake Camera на iPhone. EXE не заменяет iOS-приложение. В репозитории есть проект Xcode и сборка IPA без подписи. Для установки нужна подпись вашим Apple ID.\n\n3. Подключите iPhone к Wi-Fi 5/6 ГГц той же локальной сети. ПК желательно подключить к роутеру кабелем.\n\n4. В настройках выберите сетевой адаптер, Fifine, папку и режим съёмки. Разрешите входящий порт только для частной сети.\n\n5. Отсканируйте QR в AirTake Camera. Удерживайте iPhone горизонтально, основной задней камерой к себе.\n\n6. Нажмите «Начать запись» на ПК. После «Стоп» дождитесь передачи буфера и экспорта. Не закрывайте приложение на телефоне раньше.", 15));
        panel.Children.Add(Text("Проверка перед важной записью", 22, true));
        panel.Children.Add(Text("Сначала запишите 30–60 секунд и проверьте FPS, звук и пропуски. Затем проверьте длительный дубль и краткий обрыв Wi-Fi. 120 FPS на этой сборке не прошли аппаратную проверку. Системные ограничения камеры, нагрев и недостаток света не устраняются настройками приложения.\n\nСинхронизация по сетевым часам приблизительная; USB-микрофон имеет собственную задержку. Сделайте хлопок и выставьте поправку звука при необходимости. Номинальная частота WAV определяется устройством; экспорт — 48 кГц.\n\nНе пересылайте QR: он содержит ключ подключения. Не открывайте порт в интернет. Не используйте гостевую Wi-Fi-сеть с изоляцией клиентов.", 14));
        panel.Children.Add(Button("Репозиторий и инструкция iPhone", () => { Open("https://github.com/natural0101/AirTake"); return Task.CompletedTask; }));
        return new ScrollViewer { Content = Card(panel), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private void Populate()
    {
        resolution.SelectedIndex = prefs.Capture.Width == 3840 ? 0 : 1; fps.SelectedItem = prefs.Capture.Fps.ToString();
        bitrate.Text = prefs.Capture.BitrateMbps.ToString(); buffer.Text = prefs.Capture.BufferMiB.ToString(); port.Text = prefs.Port.ToString(); reserve.Text = prefs.ReserveGiB.ToString();
        offset.Text = prefs.AudioOffsetMs.ToString(); output.Text = prefs.OutputFolder; strict.IsChecked = prefs.Capture.StopOnDroppedFrame; audio.IsChecked = prefs.RecordAudio;
        address.ItemsSource = Preferences.Addresses().Append(prefs.Address).Distinct().ToArray(); address.SelectedItem = prefs.Address;
        PopulateMicrophones();
    }
    private void PopulateMicrophones()
    {
        try
        {
            var devices = MicrophoneRecorder.Devices(); microphones.ItemsSource = devices;
            microphones.SelectedItem = devices.FirstOrDefault(d => d.Id == prefs.MicrophoneId) ?? devices.FirstOrDefault(d => d.Name.Contains("fifine", StringComparison.OrdinalIgnoreCase));
            if (microphones.SelectedItem is Microphone mic) { prefs.MicrophoneId = mic.Id; microphoneStatus.Text = "Микрофон: " + mic.Name; }
            else microphoneStatus.Text = "Микрофон не выбран — выберите Fifine в настройках";
        }
        catch (Exception ex) { AddLog("Микрофоны: " + ex.Message); }
    }
    private async Task StartReceiver()
    {
        receiver = new Receiver(prefs); receiver.Log = AddLog;
        receiver.OnCompleted = id => Dispatcher.InvokeAsync(() => FinishTake(id)).Task.Unwrap();
        await receiver.Start(); prefs.Save(); url.Text = receiver.Pairing!.Url;
        using var code = QRCodeGenerator.GenerateQrCode(JsonSerializer.Serialize(receiver.Pairing, Json.Options), QRCodeGenerator.ECCLevel.L);
        using var png = new PngByteQRCode(code); qr.Source = Bitmap(png.GetGraphic(6));
        mode.Text = $"{(prefs.Capture.Width == 3840 ? "4K" : "1080p")} / {prefs.Capture.Fps} FPS"; RefreshTakes();
    }
    private async Task SaveSettings()
    {
        if (recording || waiting) throw new InvalidOperationException("Сначала завершите дубль.");
        var width = resolution.SelectedIndex == 0 ? 3840 : 1920;
        var capture = new CaptureSettings(width, width == 3840 ? 2160 : 1080, int.Parse((string)fps.SelectedItem), int.Parse(bitrate.Text), int.Parse(buffer.Text), strict.IsChecked == true); capture.Validate();
        int p = int.Parse(port.Text), r = int.Parse(reserve.Text), o = int.Parse(offset.Text);
        if (p is < 1024 or > 65535 || r is < 1 or > 1000 || o is < -5000 or > 5000) throw new ArgumentException("Проверьте порт, резерв диска и поправку звука (±5000 мс).");
        var folder = Path.GetFullPath(output.Text); Directory.CreateDirectory(folder);
        if (receiver is not null) await receiver.DisposeAsync();
        prefs.Capture = capture; prefs.OutputFolder = folder; prefs.Port = p; prefs.ReserveGiB = r; prefs.AudioOffsetMs = o;
        prefs.Address = (string)address.SelectedItem; prefs.RecordAudio = audio.IsChecked == true; prefs.MicrophoneId = (microphones.SelectedItem as Microphone)?.Id;
        await StartReceiver(); AddLog("Настройки сохранены. При смене адреса заново отсканируйте QR.");
    }
    private async Task BeginTake()
    {
        if (recording || waiting) return;
        if (receiver is null || !receiver.PhoneOnline) throw new InvalidOperationException("Сначала подключите iPhone через QR.");
        if (prefs.RecordAudio && string.IsNullOrWhiteSpace(prefs.MicrophoneId)) throw new InvalidOperationException("Выберите Fifine в настройках.");
        var manifest = receiver.Store.Create(prefs.Capture, prefs.AudioOffsetMs); currentTake = manifest.Id;
        try
        {
            if (prefs.RecordAudio)
            {
                microphone = new MicrophoneRecorder(); microphone.Start(prefs.MicrophoneId!, Path.Combine(receiver.Store.Folder(manifest.Id), "microphone.wav"));
                await receiver.Store.Update(manifest.Id, m => { m.AudioSampleRate = microphone.SampleRate; m.AudioChannels = microphone.Channels; });
            }
            recording = true; waiting = false; lastError = null; settings.IsEnabled = false;
            receiver.SetCommand(currentTake, true); AddLog("Запрошен новый дубль. Ожидание первых кадров iPhone…");
        }
        catch (Exception ex)
        {
            await receiver.Store.Update(manifest.Id, m => { m.State = "failed-to-start"; m.Error = ex.Message; });
            if (microphone is not null) { try { await microphone.Stop(); } catch { } microphone = null; }
            currentTake = null; throw;
        }
    }
    private async Task FinishAudio(Guid id)
    {
        await audioGate.WaitAsync();
        try
        {
            if (currentTake != id || microphone is null || receiver is null) return;
            var mic = microphone; await mic.Stop();
            await receiver.Store.Update(id, m => { m.FirstAudioTimeMs = mic.FirstSampleTimeMs; if (mic.Error is not null) m.Error = mic.Error.Message; });
            microphone = null;
        }
        finally { audioGate.Release(); }
    }
    private async Task StopTake()
    {
        if (currentTake is not Guid id || receiver is null) return;
        receiver.SetCommand(id, false); recording = false; waiting = true;
        await FinishAudio(id); AddLog("Остановка. Ожидаю подтверждённую передачу всех фрагментов.");
    }
    private async Task FinishTake(Guid id)
    {
        if (receiver is null) return;
        if (currentTake == id) { receiver.SetCommand(null, false); recording = false; waiting = true; await FinishAudio(id); }
        AddLog("Все фрагменты получены. Экспорт MP4 без перекодирования видео…");
        try { var path = await Exporter.Export(receiver.Store, id); AddLog("Сохранено: " + path); }
        catch (Exception ex) { await receiver.Store.Update(id, m => { m.State = "export-failed"; m.Error = ex.Message; }); AddLog(ex.Message); }
        if (currentTake == id) { currentTake = null; waiting = false; settings.IsEnabled = true; }
        RefreshTakes();
    }
    private async Task ReleaseTake()
    {
        if (currentTake is not Guid id || receiver is null) return;
        if (MessageBox.Show("Прекратить ожидание этого дубля? Полученные файлы останутся на диске; недостающие фрагменты — в буфере iPhone.", "AirTake", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        receiver.SetCommand(null, false); await FinishAudio(id);
        await receiver.Store.Update(id, m => { if (m.Completion is null) m.State = "interrupted"; });
        currentTake = null; recording = waiting = false; settings.IsEnabled = true; RefreshTakes();
    }
    private async Task Refresh()
    {
        if (receiver is null) return;
        recordButton.IsEnabled = !recording && !waiting && receiver.PhoneOnline;
        stopButton.IsEnabled = recording; releaseButton.IsEnabled = waiting;
        status.Text = recording ? "● ЗАПИСЬ / ОЖИДАНИЕ КАМЕРЫ" : waiting ? "ПЕРЕДАЧА / ЭКСПОРТ" : receiver.PhoneOnline ? "IPHONE ПОДКЛЮЧЁН" : "ОЖИДАНИЕ IPHONE";
        var phone = receiver.Phone;
        if (phone is not null)
        {
            metrics.Text = $"FPS: {phone.Fps:F1}   ·   Буфер: {phone.QueueBytes / 1048576.0:F1} МиБ   ·   Кадры: {phone.Encoded}   ·   Пропуски: {phone.Dropped}";
            warning.Text = $"iPhone: {phone.Name} · Температура: {phone.Thermal} · HEVC {prefs.Capture.BitrateMbps} Мбит/с";
            if (phone.PreviewJpeg is { Length: > 0 and < 300_000 })
            { try { preview.Source = Bitmap(Convert.FromBase64String(phone.PreviewJpeg)); previewHint.Visibility = Visibility.Collapsed; } catch (FormatException) { } catch (NotSupportedException) { } }
            if (!string.IsNullOrWhiteSpace(phone.Error) && phone.Error != lastError) { lastError = phone.Error; AddLog("iPhone: " + phone.Error); if (recording) await StopTake(); }
        }
        if (microphone?.Error is Exception error && recording) { AddLog("Микрофон: " + error.Message); await StopTake(); }
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(prefs.OutputFolder)!);
            disk.Text = $"Свободно: {drive.AvailableFreeSpace / 1073741824.0:F1} ГиБ   ·   Видео ≈ {prefs.Capture.BitrateMbps * .45:F1} ГБ/час (без оригиналов и WAV)";
            if (recording && drive.AvailableFreeSpace < (long)prefs.ReserveGiB * 1073741824) await StopTake();
        }
        catch (IOException ex) { AddLog(ex.Message); }
    }
    private Task Firewall()
    {
        if (recording || waiting) throw new InvalidOperationException("Сначала завершите дубль.");
        string exe = Environment.ProcessPath ?? throw new IOException("Путь EXE не найден");
        var process = new ProcessStartInfo("netsh.exe") { UseShellExecute = true, Verb = "runas", Arguments = $"advfirewall firewall add rule name=\"AirTake local receiver\" dir=in action=allow program=\"{exe}\" protocol=TCP localport={prefs.Port} profile=private remoteip=localsubnet" };
        Process.Start(process); AddLog("Запрошено правило брандмауэра только для частной локальной сети."); return Task.CompletedTask;
    }
    private void RefreshTakes()
    {
        if (receiver is not null) takes.ItemsSource = receiver.Store.List().OrderByDescending(t => t.CreatedUtc).Select(t => new TakeRow(t)).ToArray();
    }
    private sealed record TakeRow(TakeManifest Manifest)
    { public override string ToString() => $"{Manifest.CreatedUtc.ToLocalTime():dd.MM HH:mm:ss}   {Manifest.Settings.Width}×{Manifest.Settings.Height} / {Manifest.Settings.Fps}   {Manifest.State}"; }
    private void SaveScreenshot()
    {
        UpdateLayout(); var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var path = smokePath?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true ? smokePath : "AirTake-ui.png";
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private void AddLog(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLog(text)); return; }
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n"); if (log.Text.Length > 24000) log.Text = log.Text[^16000..]; log.ScrollToEnd();
    }
    private Button Button(string title, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = title, Padding = new(14, 10, 14, 10), Margin = new(0, 8, 10, 8), Background = Brush(primary ? "79D8CE" : "29374B"), Foreground = primary ? Brush("091B23") : Brushes.White, BorderThickness = new(0), FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand };
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { AddLog(ex.Message); MessageBox.Show(ex.Message, "AirTake", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button;
    }
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private static TabItem Tab(string title, object content) => new() { Header = title, Content = content, Padding = new(14, 9, 14, 9), Foreground = Brushes.Black };
    private static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFrom("#" + color)!;
    private static TextBlock Text(string text, double size = 14, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(bold ? "F2F6FC" : "A7B5C8"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 5) };
    private static TextBox Input() => new() { Margin = new(0, 5, 0, 12), Padding = new(9), FontSize = 14, Background = Brush("101823"), Foreground = Brushes.White, BorderBrush = Brush("354760"), BorderThickness = new(1) };
    private static ComboBox Combo(string[] items) => new() { ItemsSource = items, SelectedIndex = 0, Margin = new(0, 5, 0, 12), Padding = new(7), Foreground = Brushes.Black, FontSize = 14 };
    private static CheckBox Check(string text) => new() { Content = text, Foreground = Brushes.White, Margin = new(0, 7, 0, 12), FontSize = 13 };
    private static Border Card(UIElement content) => new() { Child = content, Background = Brush("18212E"), CornerRadius = new(14), Padding = new(10) };
    private static void Field(Panel panel, string label, UIElement control) { panel.Children.Add(Text(label, 12)); panel.Children.Add(control); }
    private static BitmapImage Bitmap(byte[] data)
    {
        var image = new BitmapImage(); using var stream = new MemoryStream(data); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
