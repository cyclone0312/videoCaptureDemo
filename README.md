# 视频捕获与回放系统 (Video Capture and Playback System)

## 项目概述

这是一个基于 C# 的视频捕获与回放系统，利用 FFmpeg 进行实时视频捕获和录制，使用 LibVLC 进行视频播放。系统支持 USB 摄像头捕获、RTSP 实时流推送、历史视频查询播放等多种功能。

## 功能特性

### 🎥 视频捕获服务 (CaptureService)

- **自动设备发现**: 智能检测并优先选择 USB 摄像头设备
- **实时录制**: 自动按 10 分钟分段录制 MP4 文件
- **RTSP 流推送**: 同时推送实时流到 MediaMTX 服务器
- **时间戳水印**: 在视频上实时叠加时间戳
- **高性能编码**: 使用 H.264/AAC 编码，优化 CPU 使用

### 🎬 视频播放应用 (PlaybackApp)

- **时间范围查询**: 根据开始和结束时间剪辑并播放历史视频
- **实时播放**: 支持基于文件轮询的伪实时播放（因为tee无法与fmp4格式适配输出，而RTSP流的延迟明显更低，决定将该功能阉割，暂时只是作为一个播放已经录制好的最新视频流的按钮。还有一种方法是可以让RTSP服务器来负责用fmp4格式存储视频流，这样这个功能就能正常使用了，但考虑到一些原因没有采用这个方法）
- **RTSP 实时流**: 亚秒级延迟的真实实时播放
- **进度控制**: 拖拽进度条、快进快退等控制功能
- **现代化 UI**: WPF 界面，支持键盘快捷键

### 🔧 核心技术栈

- **C# .NET 8.0**: 跨平台开发框架
- **FFmpeg**: 强大的音视频处理工具
- **LibVLC**: VLC 媒体播放器库
- **MediaMTX**: 高性能 RTSP 服务器
- **WPF**: Windows 桌面应用界面

### 软件要求

- .NET 8.0 SDK
- FFmpeg (自动下载或手动安装)
- MediaMTX RTSP 服务器 (已包含)

## 安装与设置

### 1. 克隆项目

```bash
git clone https://github.com/cyclone0312/videoCaptureDemo.git
cd ffmpegDemo
```

### 2. 构建项目

```bash
# 构建整个解决方案
dotnet build

# 或者分别构建各个项目
dotnet build CaptureService/CaptureService.csproj
dotnet build PlaybackApp/PlaybackApp.csproj
```

### 3. 配置存储路径

在 `CaptureService/Program.cs` 中修改输出目录：

```csharp
string outputDirectory = @"F:\Screenshot\videostore";  // 修改为您想要的路径
```

### 4. 启动 RTSP 服务器

运行提供的批处理文件：

```bash
start_rtsp_server.bat
```

或者手动启动：

```bash
cd mediamtx_v1.15.3_windows_amd64
./mediamtx.exe
```

## 使用说明

### 启动捕获服务

```bash
cd CaptureService
dotnet run
```

服务将自动：

- 检测 USB 摄像头设备
- 开始录制视频文件
- 推送 RTSP 流到 `rtsp://localhost:8554/live_stream`

### 启动播放应用

```bash
cd PlaybackApp
dotnet run
```

### 播放功能说明

#### 1. 查询并播放历史视频

1. 选择开始时间和结束时间
2. 点击"▶ 查询并播放"按钮
3. 系统将自动剪辑指定时间范围的视频并播放

#### 2. 伪实时播放 (文件轮询)

- 点击"🔄 伪实时 (文件轮询)"按钮
- 系统每 5 秒检查新录制的文件并自动切换
- 延迟约 30 秒

#### 3. 真实时 RTSP 播放

- 点击"🟢 真实时 (RTSP 流)"按钮
- 直接连接 RTSP 流，亚秒级延迟

### 播放控制

- **空格键**: 播放/暂停
- **长按→ 右箭头**:3倍数播放 
- **长按← 左箭头**:快速回退 
- **鼠标拖拽**: 进度条跳转

## 项目架构

```
ffmpegDemo/
├── CaptureService/          # 视频捕获服务
│   ├── Program.cs          # 主程序，FFmpeg 集成
│   └── CaptureService.csproj
├── PlaybackApp/            # 视频播放应用
│   ├── MainWindow.xaml     # WPF 主界面
│   ├── MainWindow.xaml.cs  # 界面逻辑
│   ├── RecordingService.cs # 录像文件处理服务
│   └── PlaybackApp.csproj
├── mediamtx_v1.15.3_windows_amd64/  # RTSP 服务器
├── RTSP_SETUP.md           # RTSP 配置说明
├── start_rtsp_server.bat  # 服务器启动脚本
└── ffmpegDemo.sln         # Visual Studio 解决方案
```

### 数据流说明

1. **捕获阶段**: USB 摄像头 → FFmpeg → 文件录制 + RTSP 推送
2. **存储阶段**: 视频文件按时间戳命名存储在指定目录
3. **播放阶段**: 用户查询 → RecordingService 剪辑 → LibVLC 播放

## 依赖项

### NuGet 包

- `FFMpegCore` (6.0.0): FFmpeg .NET 封装
- `LibVLCSharp` (3.8.2): VLC 播放器库
- `LibVLCSharp.WPF` (3.8.2): WPF 控件
- `Xceed.Wpf.Toolkit` (4.6.1): WPF 扩展控件

### 外部依赖

- FFmpeg: 自动从 PATH 或项目目录查找
- MediaMTX: 已包含在项目中

## 配置说明

### RTSP 服务器配置

MediaMTX 配置文件位于 `mediamtx_v1.15.3_windows_amd64/mediamtx.yml`

主要配置项：

```yaml
rtspAddress: :8554 # RTSP 监听端口
rtpAddress: :8000 # RTP 端口
rtcpAddress: :8001 # RTCP 端口
```

### 视频编码参数

在 `CaptureService/Program.cs` 中可调整：

- 分辨率: 默认跟随摄像头
- 码率: 可通过 `-b:v` 参数调整
- 分段时间: `segment_time=600` (10 分钟)

## 故障排除

### 常见问题

#### 1. 找不到摄像头设备

- 确保摄像头已正确连接
- 检查设备管理器中的摄像头状态
- 尝试重新插拔 USB 设备

#### 2. FFmpeg 启动失败

- 确保 FFmpeg 已安装并在 PATH 中
- 或者将 ffmpeg.exe 放在项目根目录

#### 3. RTSP 播放失败

- 确认 MediaMTX 服务器正在运行
- 检查防火墙设置
- 验证 RTSP URL: `rtsp://localhost:8554/live_stream`

#### 4. 播放应用崩溃

- 确保所有 NuGet 包已正确安装
- 检查 .NET 8.0 运行时是否安装
- 查看输出窗口的错误信息

### 日志查看

- 捕获服务日志: 控制台输出
- 播放应用日志: VS 输出窗口或控制台

## 开发说明

### 项目结构

- **CaptureService**: 控制台应用，专注于视频捕获
- **PlaybackApp**: WPF 应用，专注于视频播放
- **RecordingService**: 业务逻辑层，处理录像文件操作

### 扩展开发

- 添加更多摄像头支持
- 实现云存储集成
- 添加视频分析功能 (AI 检测等)
- 支持更多输出格式

### 开发环境设置

1. 安装 Visual Studio 2022 或 VS Code
2. 安装 .NET 8.0 SDK
3. 克隆项目并构建
4. 运行测试确保功能正常

## 更新日志

### v1.0.0 (2025-11-06)

- 初始版本发布
- 支持 USB 摄像头捕获
- 实现 RTSP 实时流推送
- 完成历史视频查询播放
- 添加现代化 WPF 界面

## 联系方式

如有问题或建议，请通过以下方式联系：

- GitHub Issues: [提交问题](https://github.com/cyclone0312/videoCaptureDemo/issues)
- Email: cyclone0312@example.com

---

**注意**: 本项目仅用于学习和研究目的，请遵守当地法律法规。
