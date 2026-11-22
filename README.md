# ALambda TTS

Edge TTS 的 Windows 桌面客户端：写一段文字，选个音色，点一下就能听到微软 Edge 的神经网络朗读。
C# / .NET 10 / WPF，直角简约风，深浅双色主题自动跟随系统。

作者：Anming_cat (使用 AI 开发)
仓库：<https://github.com/Anming1379/ALambdaTTS>

---

## 下载

安装包在 [Releases](https://github.com/Anming1379/ALambdaTTS/releases)：
`ALambdaTTS-Setup-1.0.1.exe`（2.5 MB），双击安装；缺少 .NET 10 桌面运行时会引导安装。
重装 / 升级会沿用你上次选的安装目录，安装完成后启动程序用的是当前用户身份，
不会把设置写进管理员账户。

---

## 功能

- 音色：自动获取全部 318 个音色（含中文 14 个），可按语言、性别、关键词筛选，
  列表里直接显示该音色自动获得的类别（News / Novel / Cartoon / Dialect ...）
- 筛选会记住：上次选了「女声 + 中文」，下次打开面板还是女声 + 中文（存在设置里）
- 语言筛选合并成大类：中国大陆、中国香港、中国台湾、辽宁话……都归到「中文」，
  英语、英式英语归到「英文」，下拉项后面带数量，面板从工具栏下方展开，点外面或 Esc 收起
- 语速、语调、音量各一个自绘旋钮：鼠标上划顺时针拧，下划逆时针，滚轮微调，双击复位
  （语调 -50 ~ +50 Hz → `pitch='+20Hz'`；音量 0 ~ 100 是**软件全局音量**，
  直接作用在播放器上，正在播的那段也会立刻跟着变，不影响合成出来的文件）
- 历史卡片：每次请求一张卡片，右侧播放 / 下载 / 删除（失败时多一个红色重试图标），
  删除只清掉这条记录和它的临时缓存，**下载文件夹里的 mp3 一个都不动**，
  底部强调色小字写音色、语速、时长与片段数
- 生成完立刻自动播放；同一时刻只播一条，切换卡片自动停前一条
- 长文本自动按句子切段，逐段合成，各段落成独立 mp3，播放时顺序连播，下载时全部保存
- 输入框获得焦点自动向上展开，Enter 发送、Shift+Enter 换行、Ctrl+L 随时聚焦，
  支持粘贴、拖入 `.txt`（多个文件按空行拼接）
- 状态栏显示当前请求状态与下载文件夹（点击直达）
- 设置：主题（跟随系统 / 浅色 / 深色）、下载文件夹（默认 `音乐\ATTS`）
- 默认下载目录是「现算」的：设置里填的路径如果等于默认值，一律存成空，
  以后你把系统的「音乐」文件夹挪到别的盘，程序跟着走，不会被焊死在老路径上
- 局域网模式：两台电脑配对后，一边发消息，两边同时朗读（详见下面「局域网模式」）
- 单实例运行，重复启动会激活已有窗口
- 合成失败自动重试 3 次；仍然失败时卡片显示人话提示（如"网络连接被中断"）并给一个重试按钮，
  详细异常写进 `%LOCALAPPDATA%\ALambdaTTS\error.log`
- 重复保存同一条卡片会覆盖上次的文件，不再堆一堆副本

### 关于缓存

卡片里的语音只存在于 `%LOCALAPPDATA%\ALambdaTTS\cache`，**关闭程序即清空**。
想留下音频必须点卡片上的下载按钮（保存到设置里的下载文件夹）。

### 快捷键

```text
Enter          生成语音
Shift + Enter  在输入框里换行
Ctrl + L       聚焦输入框（音色面板开着会先收起）
Esc            取消正在进行的合成 / 收起音色面板
滚轮 / 上下拖   调节旋钮
双击旋钮        恢复默认值
```

---

## 局域网模式（两台电脑互发朗读）

同一个程序在两台电脑上跑起来，配对之后就能互发消息，各自用本机音色朗读。
走的是普通 TCP/IP，所以 **Radmin VPN / ZeroTier / 手机热点** 这类虚拟局域网同样可用。

### 配对步骤

```text
1. 两台电脑都打开工具栏最右边那个「局域网」按钮
2. 都勾上「启用局域网」，填一个 6 位配对码（两边必须一样），点完成
3. A 点「发现」→ 列表里出现 B → **点一下那条设备**就直接配对（整行都是按钮）
   · 广播被拦或者用 Radmin VPN 时不方便广播，也可以在 A 上直接填 B 的 IP
     （就是 Radmin 里显示的那串 26.x.x.x），端口默认 45790 → 点「直接连接」
4. 配对成功：状态变成「已连接 · 电脑 B」，工具栏图标右上角出现强调色小圆点
```

### 配对之后

```text
在输入框敲「你好，电脑 B」按 Enter：
  · 本机：立刻合成朗读，历史卡片标注「由 <本机名> 发送」
  · 对端：历史卡片标注「来自 <本机名>」，并立即用自己的 Edge TTS 合成播放
```

对端收到消息会带着发送方的音色 / 语速 / 语调，两边听起来是一致的声音；
如果对端没有那个音色（音色表版本不同），自动退回对端当前音色。
对端正在合成别的请求时会排队，不会两个声音打架。

**音量不跟着消息走**：局域网只传文本 / 音色 / 语速 / 语调，不传音量。
音量是本软件自己的全局音量（作用在播放器上），本机拧到 0 就是真静音，
下载保存的 mp3 也永远是原始音量；不想被打扰还能把面板里的「静音对方」打开，
收到的消息只生成到历史记录里，点播放才出声。

### 记住连接

```text
配对成功后两边都会记住对端（名字 + IP + 端口），写进 settings.json：
  · 下次打开程序，只要开关还开着，会立刻尝试连回去，之后每 5 秒重试一次
  · 面板里那一行会显示「上次连接：电脑 B（26.12.34.56:45790）」，
    旁边有「连接 电脑 B」和「忘记」两个按钮
  · 「自动连接」那个小开关可以关掉它：关了就只在你点「连接 电脑 B」时连
```

### 断开与重连

```text
面板里的「断开」立即断开，但记住的对端不会被清掉（点「忘记」才清）；
对方关掉程序、拔网线、休眠，本机 20 秒内会感知到（心跳），自动回到「等待配对」；
只要两边都开着局域网模式并且「自动连接」开着，本机会每 5 秒自动重连上次的对端。
```

### 端口与防火墙

```text
TCP 45790   配对、消息（面板里可以改本机端口，对方连接时填同一个）
UDP 45789   广播发现（只用来"发现"，不影响已经配对好的连接）
```

Windows 防火墙第一次会弹提示，允许「专用网络」即可；只允许两台机器配对，
不提供多人房间模式。

---

## 构建

环境：Windows 10/11 x64、.NET 10 SDK（本机为 10.0.404）。

```bash
# 直接运行
dotnet run --project ALambdaTTS.csproj

# 框架依赖单文件（约 1.6 MB）
dotnet publish -c Release -r win-x64 --self-contained false \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/app

# 打包安装程序（publish + Inno Setup，自动检测 .NET 10 桌面运行时）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
```

安装包输出在 `installer\Output\ALambdaTTS-Setup-<版本>.exe`，约 2.5 MB。
安装时如果没有 .NET 10 桌面运行时，会调用 `install-dotnet.cmd` 自动下载并安装。

---

## 自检

两套零依赖的控制台 harness（本机截图不可靠，所以用进程内断言 + 渲染核对）：

```bash
# 合成管线：分段器、参数格式、DRM 令牌向量、真实合成、取消、音色目录
dotnet run --project tests/TtsProbe -c Release

# 界面：布局、旋钮范围、展开动画、真实合成 + 自动播放、下载、主题切换、音色面板、设置窗口
dotnet run --project tests/UiHarness -c Release
```

UiHarness 会把窗口渲染成 PNG 放到 `%TEMP%\alambda-tts-ui`，方便人工核对视觉。

---

## 目录结构

```text
ALambdaTTS.csproj        net10.0-windows / WinExe / 内嵌字体与图标
App.xaml(.cs)            主题字典装载、单实例、启动主窗口
MainWindow.xaml(.cs)     四段式主界面：工具栏 / 历史 / 输入 / 状态栏
Themes/
  Tokens.Light.xaml      浅色色板（强调色 #20808D 的多级 alpha）
  Tokens.Dark.xaml       深色色板
  Controls.xaml          直角控件模板：按钮 / 下拉 / 列表 / 滚动条 / 进度条 / 输入框
  IconGlyphs.cs          由 tools/make-icon-glyphs.py 从字体生成的图标码位
Controls/
  Knob.cs                自绘旋钮（270 度量程，可设双极中心点）
Models/
  TtsItem.cs             一张历史卡片
  VoiceEntry.cs          音色条目（显示名、语言名、类别）
  AppSettings.cs         设置模型
Services/
  EdgeTts/SecMsGec.cs    Sec-MS-GEC v2 令牌（从 edge-tts 的 python 实现移植）
  EdgeTts/EdgeTtsClient.cs  WebSocket 合成客户端
  EdgeTts/RawWebSocket.cs   极简 WebSocket 传输（TLS + 帧读写）
  TtsService.cs          分段 / 进度 / 取消
  TextChunker.cs         长文本按句切段
  AudioPlayer.cs         MediaPlayer 顺序播放一组片段
  VoiceCatalog.cs        音色表（在线拉取 + 本地 json 缓存）
  ThemeManager.cs        深浅主题
  NativeTheme.cs         DWM 标题栏配色
  AppPaths.cs            目录与文件名工具（含退出清缓存）
  SingleInstance.cs      单实例激活
Views/
  VoicePickerView.xaml   音色面板
  SettingsWindow.xaml    设置窗口
tools/
  make-icon.py           生成 app.ico（零依赖）
  make-icon-glyphs.py    从字体生成图标码位表
tests/
  TtsProbe/              合成管线 harness
  UiHarness/             界面 harness（进程内断言 + 截图）
installer/
  ALambdaTTS.iss         Inno Setup 脚本
assets/
  MaterialSymbolsSharp.ttf / app.ico / install-dotnet.*
```

---

## 技术说明

### 为什么自己实现合成传输

PRD 指定后端为 `EdgeTTS.DotNet`，但该库 0.4.0 的 DRM 令牌还是 v1 格式，
微软服务端现在只认 **v2**（`Sec-MS-GEC` 形如 `16位CS64-64位SHA256`，
`Sec-MS-GEC-Version=2-143.0.3650.75`），握手阶段直接返回 403。

因此：

- 音色列表仍然用 `EdgeTTS.DotNet.Voices.ListVoicesAsync()`（HTTP 接口未变，工作正常）
- 合成改为 `Services/EdgeTts` 里的自实现：
  - `SecMsGec.cs` 按 python `edge-tts 7.3.1` 的 `drm.py` 逐行移植 v2 令牌，
    并用 7 组测试向量校验（见 TtsProbe 的"令牌向量"用例）
  - `EdgeTtsClient.cs` 按 edge-tts 的协议发送 `speech.config` + SSML，收二进制音频帧
  - `RawWebSocket.cs` 是自己写的 TLS WebSocket（`ClientWebSocket` 发出的帧会被服务端
    以 `Bad request` 拒绝，换自实现后与 python 客户端字节一致）

服务端只接受完整音色名，`zh-CN-XiaoxiaoNeural` 会被翻译成
`Microsoft Server Speech Text to Speech Voice (zh-CN, XiaoxiaoNeural)` 再发出去。

### 图标

Material Symbols Sharp 这款字体依赖 `rlig` 连字，WPF 不会应用它
（写 `Text="play_arrow"` 只会渲染成字母）。所以图标一律用字体里的私用区码位，
由 `tools/make-icon-glyphs.py` 从 ttf 反查生成 `Themes/IconGlyphs.cs`。
需要新图标时把名字加进脚本的 `NAMES` 再跑一次。

### 缓存与文件

```text
%LOCALAPPDATA%\ALambdaTTS\settings.json   设置
%LOCALAPPDATA%\ALambdaTTS\voices.json     音色表缓存
%LOCALAPPDATA%\ALambdaTTS\cache\          本次运行生成的 mp3（启动和退出各清一次）
音乐\ATTS\                                 默认下载目录（点下载才写入）
```

下载文件名取文本前 28 个字符并清洗非法字符，多片段时追加 `-01`、`-02` 后缀。

---

## 已知限制

- 依赖微软的公开朗读接口，服务端策略变化（例如又升级 DRM 版本）需要再跟进
- 超长文本按 ~1200 字符切段，段与段之间会有一个短暂的换气停顿
- 不做托盘图标，关闭窗口即退出并清空缓存

---

## 第三方

- [EdgeTTS.DotNet](https://github.com/twn39/EdgeTTS.DotNet)（MIT）：音色列表
- [Material Symbols](https://fonts.google.com/icons)（Apache 2.0）：界面图标
- .NET 10 / WPF（MIT）

详见 `THIRD-PARTY-NOTICES.txt`。
