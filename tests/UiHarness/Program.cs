using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ALambdaTTS;
using ALambdaTTS.Controls;
using ALambdaTTS.Models;
using ALambdaTTS.Services;
using ALambdaTTS.Services.Lan;

namespace UiHarness;

internal static class Program
{
    private static int _failures;
    private static string _outputDir = string.Empty;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _outputDir = Path.Combine(Path.GetTempPath(), "alambda-tts-ui");
        Directory.CreateDirectory(_outputDir);

        // 与真实 App 一致：界面线程要有 DispatcherSynchronizationContext，
        // 否则 await 之后会跑到线程池，WPF 对象就没法碰了
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ALambdaTTS;component/Themes/Tokens.Light.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ALambdaTTS;component/Themes/Controls.xaml"),
        });

        AppPaths.EnsureDirectories();
        ThemeManager.Apply(AppThemeMode.Light);

        var downloadDir = Path.Combine(_outputDir, "downloads");
        Directory.CreateDirectory(downloadDir);
        foreach (var old in Directory.GetFiles(downloadDir)) File.Delete(old);

        var settings = new AppSettings
        {
            ThemeMode = AppThemeMode.Light,
            WindowWidth = 560,
            WindowHeight = 780,
            Volume = 100,
            Rate = 0,
            DownloadFolder = downloadDir,
        };

        var window = new MainWindow(settings);
        window.Show();
        Pump(1200);

        Console.WriteLine("== 布局与默认状态 ==");
        Check("窗口宽度 560", Math.Abs(window.ActualWidth - 560) < 40, window.ActualWidth.ToString("F1"));
        Check("窗口高度 780", Math.Abs(window.ActualHeight - 780) < 60, window.ActualHeight.ToString("F1"));

        var rate = Find<Knob>(window, "RateKnob");
        var pitch = Find<Knob>(window, "PitchKnob");
        var volume = Find<Knob>(window, "VolumeKnob");
        Check("工具栏只剩语速与语调两个旋钮", rate is not null && pitch is not null);
        Check("音量旋钮已移除", volume is null);
        Check("语调旋钮默认 0", pitch is not null && Math.Abs(pitch.Value) < 0.001, pitch?.Value.ToString());
        Check("语速旋钮默认 0", rate is not null && Math.Abs(rate.Value) < 0.001, rate?.Value.ToString());
        Check("语速范围 -50~100", rate is not null && rate.Minimum == -50 && rate.Maximum == 100);
        Check("语调范围 -50~50", pitch is not null && pitch.Minimum == -50 && pitch.Maximum == 50);

        var send = Find<Button>(window, "SendButton");
        var input = Find<TextBox>(window, "InputBox");
        var composer = Find<Border>(window, "ComposerPanel");
        var empty = Find<StackPanel>(window, "EmptyState");
        var history = Find<ListBox>(window, "HistoryList");
        var placeholder = Find<TextBlock>(window, "InputPlaceholder");
        var status = Find<TextBlock>(window, "StatusText");
        var folderText = Find<TextBlock>(window, "DownloadFolderText");

        Check("发送按钮初始禁用", send is not null && !send.IsEnabled);
        Check("空状态可见", empty is not null && empty.Visibility == Visibility.Visible);
        Check("占位提示可见", placeholder is not null && placeholder.Visibility == Visibility.Visible);
        Check("历史列表为空", history is not null && history.Items.Count == 0);
        Check("状态栏有文字", status is not null && !string.IsNullOrWhiteSpace(status.Text), status?.Text);
        Check("状态栏显示下载文件夹", folderText is not null && folderText.Text.Length > 3, folderText?.Text);
        Check("输入框初始高度接近折叠值", composer is not null && Math.Abs(composer.ActualHeight - 98) < 3, composer?.ActualHeight.ToString("F1"));

        Save(window, "01-empty-light.png");

        Console.WriteLine("== 输入框展开动画 ==");
        input!.Focus();
        Pump(400);
        Check("获得焦点后展开", composer!.ActualHeight > 150, composer.ActualHeight.ToString("F1"));
        Save(window, "02-focused-light.png");
        SaveZoom(window, "02-toolbar-zoom.png", 2.4);
        SaveElement(rate, "02-knob-rate.png", 10);

        input.Text = "你好，这是 ALambda TTS 的界面自检。";
        Pump(200);
        Check("有文字后发送可用", send!.IsEnabled);
        Check("有文字后占位提示隐藏", placeholder.Visibility == Visibility.Collapsed);

        Console.WriteLine("== 旋钮交互 ==");
        rate!.Value = 25;
        pitch!.Value = 20;
        Pump(50);
        Check("旋钮值可写",
            Math.Abs(rate.Value - 25) < 0.001 && Math.Abs(pitch.Value - 20) < 0.001);
        var rateX1 = rate.TransformToAncestor(window).Transform(new Point(0, 0)).X;
        var pitchX1 = pitch.TransformToAncestor(window).Transform(new Point(0, 0)).X;
        rate.Value = 100;
        pitch.Value = -50;
        Pump(200);
        var rateX2 = rate.TransformToAncestor(window).Transform(new Point(0, 0)).X;
        var pitchX2 = pitch.TransformToAncestor(window).Transform(new Point(0, 0)).X;
        Check("数值变化时旋钮不位移",
            Math.Abs(rateX1 - rateX2) < 0.6 && Math.Abs(pitchX1 - pitchX2) < 0.6,
            $"rate {rateX1:F1}->{rateX2:F1}, pitch {pitchX1:F1}->{pitchX2:F1}");
        Check("语调显示带 Hz",
            new ALambdaTTS.Utils.KnobValueConverter()
                .Convert(20d, typeof(string), "pitch", System.Globalization.CultureInfo.InvariantCulture) as string == "+20Hz",
            new ALambdaTTS.Utils.KnobValueConverter()
                .Convert(20d, typeof(string), "pitch", System.Globalization.CultureInfo.InvariantCulture) as string);

        rate.Value = 0;
        pitch.Value = 0;

        Console.WriteLine("== 真实合成 + 自动播放 ==");
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var ready = PumpUntil(
            () => history!.Items.Count > 0
                  && history.Items[0] is TtsItem { State: TtsState.Ready },
            60000);

        if (!ready)
        {
            var item = history!.Items.Count > 0 ? history.Items[0] as TtsItem : null;
            Check("合成在 60 秒内完成", false, item is null ? "没有卡片" : item.StatusLabel);
            Console.WriteLine("     状态栏: " + status!.Text);
        }
        else
        {
            var item = (TtsItem)history.Items[0]!;
            Check("卡片已就绪", item.State == TtsState.Ready);
            Check("生成了 mp3 片段", item.Files.Count >= 1 && item.Files.All(File.Exists));
            Check("卡片显示音色小字", item.MetaText.Contains(item.Voice.DisplayName), item.MetaText);
            Check("输入框已清空", string.IsNullOrEmpty(input.Text));
            Pump(1200);
            Check("自动播放中", item.IsPlaying, "IsPlaying=false");
            Save(window, "03-ready-light.png");

            var downloadButton = FindByToolTip(history, "保存到下载文件夹");
            Check("卡片上有下载按钮", downloadButton is not null);
            downloadButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(600);
            var saved = Directory.GetFiles(downloadDir, "*.mp3");
            Check("下载把 mp3 写进下载文件夹", saved.Length >= 1, string.Join(", ", saved.Select(Path.GetFileName)));
            Check("下载用的是卡片文本做文件名",
                saved.Length > 0 && Path.GetFileName(saved[0]).StartsWith("你好", StringComparison.Ordinal),
                saved.Length > 0 ? Path.GetFileName(saved[0]) : "无");

            // 先把下载出来的文件改坏，再点一次：内容应该被源文件覆盖回来
            byte[] junk = { 1, 2, 3 };
            if (saved.Length > 0)
            {
                File.WriteAllBytes(saved[0], junk);
            }

            Pump(200);
            downloadButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(500);
            var savedAgain = Directory.GetFiles(downloadDir, "*.mp3");
            Check("重复下载覆盖原文件（不产生副本）", savedAgain.Length == saved.Length,
                $"{saved.Length} -> {savedAgain.Length}");
            Check("重复下载确实重写了文件",
                savedAgain.Length > 0 && new FileInfo(savedAgain[0]).Length > junk.Length,
                savedAgain.Length > 0 ? new FileInfo(savedAgain[0]).Length + " bytes" : "无文件");
            Check("状态栏提示已覆盖", status!.Text.Contains("覆盖", StringComparison.Ordinal), status.Text);
        var card = FindInTree<ContentPresenter>(history, "CardRoot");
        SaveElement(history, "03-card-zoom.png", 2.2);
        }

        Console.WriteLine("== 多片段连续播放 ==");
        var firstTask = TtsService.SynthesizeAsync(
            "第一段。", "zh-CN-XiaoxiaoNeural", 0, 0, 100, AppPaths.CacheDir, null, CancellationToken.None);
        var secondTask = TtsService.SynthesizeAsync(
            "这是第二段，用来验证顺序连播。", "zh-CN-XiaoxiaoNeural", 0, 10, 100, AppPaths.CacheDir, null, CancellationToken.None);
        var bothDone = PumpUntil(() => firstTask.IsCompleted && secondTask.IsCompleted, 90000);
        Check("两次短合成返回", bothDone);

        if (bothDone)
        {
            var files = firstTask.GetAwaiter().GetResult().Files
                .Concat(secondTask.GetAwaiter().GetResult().Files)
                .ToList();
            Check("准备好两个片段", files.Count == 2, files.Count.ToString());

            using var player = new AudioPlayer();
            var durations = new List<TimeSpan>();
            var finished = false;
            player.DurationResolved += (_, duration) => durations.Add(duration);
            player.Finished += (_, _) => finished = true;
            player.Play(files);

            var played = PumpUntil(() => finished, 60000);
            Check("多片段能顺序播完", played, finished ? null : "超时");
            Check("每段都解析出时长", durations.Count == 2, durations.Count + "/2");
        }

        Console.WriteLine("== 失败卡片重试 ==");
        var itemsSource = (System.Collections.ObjectModel.ObservableCollection<TtsItem>)history!.ItemsSource;
        var failedItem = new TtsItem(
            "失败重试用例。",
            new VoiceEntry("zh-CN-XiaoxiaoNeural", "Xiaoxiao", "Female", "zh-CN",
                "中文（中国大陆）", new[] { "News" }, Array.Empty<string>()),
            0,
            0,
            100)
        {
            State = TtsState.Failed,
            ErrorMessage = "网络连接被中断（已重试 3 次）",
        };
        itemsSource.Add(failedItem);
        Pump(800);
        var retryButton = FindByToolTip(history, "重新合成这一次请求");
        Check("失败卡片显示重试按钮", retryButton is not null && retryButton.IsVisible);

        if (retryButton is not null)
        {
            retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var retried = PumpUntil(() => failedItem.State is TtsState.Ready or TtsState.Failed or TtsState.Canceled, 60000);
            Check("重试后卡片恢复就绪", retried && failedItem.State == TtsState.Ready, failedItem.StatusLabel);
            Check("重试生成了音频", failedItem.Files.Count > 0 && failedItem.Files.All(File.Exists));
        }

        itemsSource.Remove(failedItem);
        Pump(200);

        Console.WriteLine("== 深色主题 ==");
        ThemeManager.Apply(AppThemeMode.Dark);
        Pump(900);
        Check("切主题后卡片仍在", history.Items.Count > 0, history.Items.Count.ToString());
        var background = (SolidColorBrush)app.Resources["Background"];
        Check("背景切换到深色", background.Color == (Color)ColorConverter.ConvertFromString("#15191A"),
            background.Color.ToString());
        Save(window, "04-dark.png");
        SaveZoom(window, "04-dark-zoom.png", 1.6);

        ThemeManager.Apply(AppThemeMode.Light);
        Pump(200);

        Console.WriteLine("== 音色面板 ==");
        var voiceButton = Find<Button>(window, "VoiceButton");
        voiceButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(600);
        var picker = Find<FrameworkElement>(window, "VoicePicker");
        var voiceList = picker?.FindName("VoiceList") as ListBox;
        Check("音色面板已打开", picker is not null && picker.IsVisible);
        Check("音色列表有内容", voiceList is not null && voiceList.Items.Count > 0,
            voiceList?.Items.Count.ToString());

        var voiceName = Find<TextBlock>(window, "VoiceNameText");
        Check("工具栏显示当前音色", voiceName is not null && voiceName.Text.Length > 0, voiceName?.Text);

        var languageCombo = picker?.FindName("LanguageCombo") as ComboBox;
        var languages = (languageCombo?.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
        Check("语言筛选项已合并成大类", languages.Count is > 20 and < 90, languages.Count.ToString());
        var chineseOption = languages.FirstOrDefault(o => o?.ToString()?.Contains("中文", StringComparison.Ordinal) == true
            || o?.GetType().GetProperty("Label")?.GetValue(o)?.ToString()?.StartsWith("中文", StringComparison.Ordinal) == true);
        Check("中文大类存在", chineseOption is not null);

        if (languageCombo is not null && chineseOption is not null)
        {
            languageCombo.SelectedItem = chineseOption;
            Pump(300);
            Check("选中中文后只剩 14 个音色", voiceList!.Items.Count == 14, voiceList.Items.Count.ToString());
            languageCombo.SelectedIndex = 0;
            Pump(300);
            Check("切回全部语言", voiceList!.Items.Count > 20, voiceList.Items.Count.ToString());
        }

        // 真实点击第二个音色（走 PreviewMouseLeftButtonUp），工具栏要跟着变
        if (voiceList is not null && voiceList.Items.Count > 2)
        {
            var targetEntry = (ALambdaTTS.Models.VoiceEntry)voiceList.Items[2];
            var container = (ListBoxItem)voiceList.ItemContainerGenerator.ContainerFromIndex(2);
            container.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseUpEvent,
            });
            Pump(300);
            Check("点击音色会切换工具栏显示",
                voiceName!.Text == targetEntry.DisplayName, voiceName.Text + " vs " + targetEntry.DisplayName);
            var overlay = window.FindName("VoiceOverlay") as Grid;
            Check("选完音色面板自动关闭", overlay is not null && overlay.Visibility != Visibility.Visible);

            window.Activate();
            Pump(200);
            voiceButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(400);
            Check("音色面板可以再次打开", overlay!.Visibility == Visibility.Visible);

            var pickerElement = (FrameworkElement)picker!;
            var searchBox = pickerElement.FindName("SearchBox") as TextBox;
            Pump(200);
            Check("面板打开时搜索框自动获得焦点", searchBox?.IsKeyboardFocusWithin == true,
                "FocusedElement=" + Keyboard.FocusedElement?.GetType().Name);

            // 点面板外面的遮罩收起
            var catcher = VisualTreeHelper.GetChild(overlay, 0) as Border;
            catcher!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseDownEvent,
            });
            Pump(250);
            Check("点面板外面收起面板", overlay.Visibility != Visibility.Visible);

            voiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(300);
            Check("再点音色按钮会打开面板", overlay.Visibility == Visibility.Visible);
            voiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(250);
            Check("面板打开着再点按钮会收起", overlay.Visibility != Visibility.Visible);
        }

        voiceButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(500);
        Save(window, "05-voice-picker.png");
        if (picker is not null)
        {
            SaveElement(picker, "05-voice-picker-zoom.png", 1.5);
        }

        voiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(200);

        Console.WriteLine("== 筛选记忆 ==");
        voiceButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(500);

        var genderFemale = Find<System.Windows.Controls.Primitives.ToggleButton>(picker!, "GenderFemale");
        genderFemale!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(300);

        var languageBox = Find<ComboBox>(picker!, "LanguageCombo")!;
        var languageOptions = ((System.Collections.IEnumerable)languageBox.ItemsSource).Cast<object>().ToList();
        var chineseIndex = languageOptions.FindIndex(o => o.ToString()!.Contains("中文", StringComparison.Ordinal));
        languageBox.SelectedIndex = chineseIndex;
        Pump(350);

        var filteredCount = voiceList!.Items.Count;
        Check("性别筛选写进设置", settings.VoiceFilterGender == "female", settings.VoiceFilterGender);
        Check("语言筛选写进设置", settings.VoiceFilterLanguage == "zh", settings.VoiceFilterLanguage);

        voiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(250);
        Check("关面板后筛选仍在设置里", settings.VoiceFilterLanguage == "zh" && settings.VoiceFilterGender == "female");

        voiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(500);
        var genderFemale2 = Find<System.Windows.Controls.Primitives.ToggleButton>(picker!, "GenderFemale")!;
        var languageBox2 = Find<ComboBox>(picker!, "LanguageCombo")!;
        Check("重新打开后性别还是女声", genderFemale2.IsChecked == true);
        Check("重新打开后语言还是中文", languageBox2.SelectedIndex == chineseIndex,
            languageBox2.SelectedIndex + " vs " + chineseIndex);
        Check("重新打开后筛选结果一致", voiceList.Items.Count == filteredCount,
            voiceList.Items.Count + " vs " + filteredCount);

        // 恢复默认筛选，别影响后面的用例
        Find<System.Windows.Controls.Primitives.ToggleButton>(picker!, "GenderAll")!
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(200);
        languageBox2.SelectedIndex = 0;
        Pump(250);
        voiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(200);

        Console.WriteLine("== 删除记录 ==");
        var deleteTarget = (TtsItem)history!.Items[0];
        var downloadedBefore = Directory.GetFiles(downloadDir).ToList();
        var cacheBefore = deleteTarget.Files.Where(File.Exists).ToList();
        var countBeforeDelete = history.Items.Count;

        var deleteButton = FindByToolTip(history, "删除这条记录（已下载的文件保留）");
        Check("卡片上有删除按钮", deleteButton is not null && deleteButton.IsVisible);

        if (deleteButton is not null)
        {
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(350);
            Check("删除后卡片消失", !history.Items.Contains(deleteTarget), history.Items.Count + " 张");
            Check("卡片数减一", history.Items.Count == countBeforeDelete - 1);
            Check("已下载的文件不受影响", downloadedBefore.All(File.Exists));
            Check("缓存片段被清掉", cacheBefore.All(f => !File.Exists(f)), cacheBefore.Count + " 个");
            Check("状态栏说明文件保留", status!.Text.Contains("保留", StringComparison.Ordinal), status.Text);
        }

        Console.WriteLine("== 局域网面板 ==");
        var lanButton = Find<Button>(window, "LanButton");
        Check("工具栏有局域网按钮", lanButton is not null && lanButton.IsVisible);

        lanButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(400);
        var lanOverlay = (Grid)window.FindName("LanOverlay")!;
        var lanPanel = (FrameworkElement)window.FindName("LanPanel")!;
        Check("局域网面板能打开", lanOverlay.Visibility == Visibility.Visible);

        var nameBox = lanPanel.FindName("NameBox") as TextBox;
        var codeBox = lanPanel.FindName("CodeBox") as TextBox;
        Check("面板填好了本机名称", !string.IsNullOrWhiteSpace(nameBox?.Text), nameBox?.Text);
        Check("面板生成了 6 位配对码", codeBox?.Text.Length == 6, codeBox?.Text);
        Check("配对码写进设置", settings.LanPairCode == codeBox?.Text, settings.LanPairCode);

        var enableToggle = (System.Windows.Controls.Primitives.ToggleButton)lanPanel.FindName("EnableToggle")!;
        enableToggle.IsChecked = true;
        enableToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(600);
        Check("打开开关后设置里记下了", settings.LanEnabled);
        Check("开始监听端口", window.IsLanListening);
        Check("状态栏提示配对码", status!.Text.Contains("局域网已开启", StringComparison.Ordinal), status.Text);
        Save(window, "08-lan-panel.png");

        // 真起一个对端服务，连上窗口自己的监听端口，走完整 TCP 链路
        Console.WriteLine("== 局域网真实连接 ==");
        var peer = new LanService();
        peer.Start("电脑 B", 45802, codeBox!.Text);
        Pump(300);

        var pairTask = peer.PairAsync("127.0.0.1", settings.LanPort, codeBox.Text);
        var pairedByPeer = PumpUntil(() => pairTask.IsCompleted, 12000) && pairTask.Result;
        Check("对端服务能配对上本窗口", pairedByPeer);
        Check("窗口自己也认为已连接", PumpUntil(() => window.IsLanConnected, 4000), "未连接");
        Check("对端记下了本机名字", peer.Peer?.Name.Length > 0, peer.Peer?.Name);

        var countBeforePeer = history!.Items.Count;
        var sendTask = peer.SendAsync(new LanChatMessage(
            "peer-1",
            "电脑 B",
            "这条是对端通过 TCP 发过来的。",
            "zh-CN-XiaoxiaoNeural",
            0,
            -5,
            100,
            DateTimeOffset.Now));
        PumpUntil(() => sendTask.IsCompleted, 5000);
        Check("对端消息发送成功", sendTask.IsCompleted && sendTask.Result);

        var peerReady = PumpUntil(
            () => history.Items.Count > countBeforePeer
                  && history.Items[history.Items.Count - 1] is TtsItem { State: TtsState.Ready },
            60000);
        var peerItem = history.Items.Count > countBeforePeer
            ? history.Items[history.Items.Count - 1] as TtsItem
            : null;
        Check("TCP 消息进了历史并完成合成", peerReady, peerItem?.StatusLabel);
        Check("TCP 消息标注来自 电脑 B",
            peerItem?.MetaText.Contains("来自 电脑 B", StringComparison.Ordinal) == true,
            peerItem?.MetaText);
        Check("TCP 消息带上了语调", peerItem is { Pitch: -5 }, "Pitch=" + peerItem?.Pitch);

        // 反向：本机发一条，看对端能不能收到（这就是配对后最常用的路径）
        var peerInbox = new List<LanChatMessage>();
        peer.MessageReceived += (_, message) =>
        {
            lock (peerInbox)
            {
                peerInbox.Add(message);
            }
        };

        var countBeforeSend = history.Items.Count;
        input!.Text = "你好，电脑 B";
        Pump(200);
        send!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var localReady = PumpUntil(
            () => history.Items.Count > countBeforeSend
                  && history.Items[history.Items.Count - 1] is TtsItem { State: TtsState.Ready },
            60000);
        var localItem = history.Items.Count > countBeforeSend
            ? history.Items[history.Items.Count - 1] as TtsItem
            : null;

        Check("本机发送的卡片标注发送方",
            localItem?.MetaText.Contains("由 " + window.LocalDeviceName + " 发送", StringComparison.Ordinal) == true,
            localItem?.MetaText);
        Check("本机卡片同样完成合成", localReady, localItem?.StatusLabel);
        Check("对端收到了本机发的消息",
            PumpUntil(() => { lock (peerInbox) { return peerInbox.Count >= 1; } }, 5000),
            "没收到");
        lock (peerInbox)
        {
            Check("对端收到的正文一致", peerInbox.Count >= 1 && peerInbox[0].Text == "你好，电脑 B",
                peerInbox.Count >= 1 ? peerInbox[0].Text : "空");
            Check("对端知道是本机发的", peerInbox.Count >= 1 && peerInbox[0].FromName == window.LocalDeviceName,
                peerInbox.Count >= 1 ? peerInbox[0].FromName : "空");
        }

        peer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Pump(300);
        Check("对端关闭后本窗口感知断开", PumpUntil(() => !window.IsLanConnected, 5000));

        var countBeforeLan = history!.Items.Count;
        window.OnLanMessage(new LanChatMessage(
            "probe-1",
            "电脑 B",
            "这是对端发来的朗读请求。",
            "zh-CN-XiaoxiaoNeural",
            0,
            10,
            100,
            DateTimeOffset.Now));

        var remoteReady = PumpUntil(
            () => history.Items.Count > countBeforeLan
                  && history.Items[history.Items.Count - 1] is TtsItem { State: TtsState.Ready },
            60000);
        var remoteItem = history.Items.Count > countBeforeLan
            ? history.Items[history.Items.Count - 1] as TtsItem
            : null;

        Check("远端消息生成了卡片", remoteItem is not null);
        Check("卡片标注来自 电脑 B",
            remoteItem?.MetaText.Contains("来自 电脑 B", StringComparison.Ordinal) == true,
            remoteItem?.MetaText);
        Check("远端消息完成本机合成", remoteReady, remoteItem?.StatusLabel);
        Check("远端消息带着对端的语调", remoteItem is { Pitch: 10 }, "Pitch=" + remoteItem?.Pitch);
        Check("远端消息开始播放", remoteItem?.IsPlaying == true);

        enableToggle.IsChecked = false;
        enableToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(500);
        Check("关掉开关后不再监听", !window.IsLanListening);

        lanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(300);
        Check("再点按钮收起局域网面板", lanOverlay.Visibility != Visibility.Visible);

        Console.WriteLine("== 快捷键 ==");
        var composerBox = Find<TextBox>(window, "InputBox")!;
        var beforeShortcut = history!.Items.Count;

        composerBox.Text = "快捷键测试。";
        Pump(150);
        Check("Shift+Enter 不拦截（交给输入框换行）",
            !window.HandleComposerKeyAsync(Key.Enter, ModifierKeys.Shift).GetAwaiter().GetResult());

        Find<Button>(window, "SendButton")!.Focus();
        Pump(200);
        var hadFocusBefore = composerBox.IsKeyboardFocusWithin;
        var ctrlL = window.HandleShortcut(Key.L, ModifierKeys.Control);
        Pump(250);
        Check("Ctrl+L 被识别为快捷键", ctrlL);
        Check("Ctrl+L 把光标送回输入框", composerBox.IsKeyboardFocusWithin && !hadFocusBefore,
            $"before={hadFocusBefore}, now={composerBox.IsKeyboardFocusWithin}");

        pitch!.Value = 25;
        composerBox.Text = "Enter 发送自检。";
        Pump(150);
        composerBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        var sent = PumpUntil(
            () => history.Items.Count > beforeShortcut
                  && history.Items[history.Items.Count - 1] is TtsItem { State: TtsState.Ready },
            60000);
        Check("Enter 直接发送并完成合成", sent, beforeShortcut + " -> " + history.Items.Count);
        Check("发送后输入框被清空（没有留下换行）", composerBox.Text.Length == 0, "[" + composerBox.Text + "]");
        var shortcutItem = history.Items.Count > 0 ? history.Items[history.Items.Count - 1] as TtsItem : null;
        Check("语调随请求一起提交", shortcutItem is { Pitch: 25 }, "Pitch=" + shortcutItem?.Pitch);
        pitch.Value = 0;

        Console.WriteLine("== 设置窗口 ==");
        var settingsWindow = new ALambdaTTS.Views.SettingsWindow(settings) { Owner = window };
        settingsWindow.Show();
        Pump(500);
        Check("设置窗口打开", settingsWindow.IsVisible);
        Check("主题三选项齐全",
            Find<System.Windows.Controls.Primitives.ToggleButton>(settingsWindow, "ThemeAuto") is not null
            && Find<System.Windows.Controls.Primitives.ToggleButton>(settingsWindow, "ThemeDark") is not null);
        var folderBox = Find<TextBox>(settingsWindow, "DownloadBox");
        Check("设置里的下载文件夹与当前一致", folderBox is not null && folderBox.Text == downloadDir, folderBox?.Text);

        Console.WriteLine("== 存储目录不被焊死 ==");
        var defaultFolder = ALambdaTTS.Services.AppSettings.DefaultDownloadFolder;
        Check("默认目录是音乐下的 ATTS", defaultFolder.EndsWith("ATTS", StringComparison.OrdinalIgnoreCase), defaultFolder);
        Check("能认出默认目录",
            ALambdaTTS.Services.AppSettings.IsDefaultDownloadFolder(defaultFolder)
            && !ALambdaTTS.Services.AppSettings.IsDefaultDownloadFolder(downloadDir));

        var typedDefault = folderBox!.Text;
        folderBox.Text = defaultFolder;
        Pump(150);
        Check("填默认目录时设置里存 null", settings.DownloadFolder is null, settings.DownloadFolder ?? "(null)");
        folderBox.Text = typedDefault;
        Pump(150);
        Check("填自定义目录时照常存路径", settings.DownloadFolder == downloadDir, settings.DownloadFolder);
        Save(settingsWindow, "06-settings-light.png");
        SaveZoom(settingsWindow, "06-settings-zoom.png", 1.5);

        var darkChip = Find<System.Windows.Controls.Primitives.ToggleButton>(settingsWindow, "ThemeDark");
        darkChip!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump(500);
        Save(settingsWindow, "07-settings-dark.png");
        ThemeManager.Apply(AppThemeMode.Light);
        Pump(200);
        settingsWindow.Close();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "UI 自检全部通过。" : $"UI 自检失败 {_failures} 项。");
        Console.WriteLine("截图目录: " + _outputDir);

        window.Close();
        Pump(300);
        AppPaths.ClearSessionCache();
        var cacheLeft = Directory.Exists(AppPaths.CacheDir)
            ? Directory.GetFiles(AppPaths.CacheDir).Length
            : 0;
        Check("退出时清空缓存目录", cacheLeft == 0, cacheLeft + " 个文件残留");

        app.Shutdown();
        return _failures == 0 ? 0 : 1;
    }

    private static T? Find<T>(FrameworkElement root, string name)
        where T : class
    {
        if (root.FindName(name) is T direct)
        {
            return direct;
        }

        return FindInTree<T>(root, name);
    }

    private static T? FindInTree<T>(DependencyObject root, string name)
        where T : class
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name && child is T match)
            {
                return match;
            }

            var found = FindInTree<T>(child, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static List<string> CollectToolTips(DependencyObject root)
    {
        var results = new List<string>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && button.ToolTip is string tip)
            {
                results.Add($"{tip}({button.IsVisible})");
            }

            results.AddRange(CollectToolTips(child));
        }

        return results;
    }

    private static Button? FindByToolTip(DependencyObject root, string toolTip)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && button.ToolTip as string == toolTip && button.IsVisible)
            {
                return button;
            }

            var found = FindByToolTip(child, toolTip);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            Console.WriteLine($"  [OK]   {name}");
            return;
        }

        _failures++;
        Console.WriteLine($"  [FAIL] {name}{(detail is null ? string.Empty : " -> " + detail)}");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };

        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static bool PumpUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Pump(120);
            if (condition())
            {
                return true;
            }
        }

        return condition();
    }

    private static void SaveZoom(Window window, string fileName, double scale)
    {
        try
        {
            window.UpdateLayout();
            var width = (int)Math.Round(window.ActualWidth * scale);
            var height = (int)Math.Round(window.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(_outputDir, fileName));
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [WARN] 缩放截图失败: " + ex.Message);
        }
    }

    private static void SaveElement(FrameworkElement element, string fileName, double scale)
    {
        try
        {
            element.UpdateLayout();
            var width = element.ActualWidth * scale;
            var height = element.ActualHeight * scale;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(
                    new VisualBrush(element),
                    null,
                    new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(width), (int)Math.Round(height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(_outputDir, fileName));
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [WARN] 元素截图失败: " + ex.Message);
        }
    }

    private static void Save(Window window, string fileName)
    {
        try
        {
            window.UpdateLayout();
            var width = (int)Math.Round(window.ActualWidth);
            var height = (int)Math.Round(window.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var path = Path.Combine(_outputDir, fileName);
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [WARN] 截图失败: " + ex.Message);
        }
    }
}
