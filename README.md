# VideoCaptureDemo

这是一个基于 C# 开发的摄像头视频捕获、存储和播放系统。系统分为两个主要组件：捕获服务（CaptureService）和播放应用（PlaybackApp），支持实时视频录制、历史视频查询播放以及实时流播放。

## 功能特性

### CaptureService (捕获服务)

- **自动设备发现**：启动时自动检测并优先选择 USB 摄像头和音频设备
- **实时录制**：使用 FFmpeg 进行高质量视频录制，支持音视频同步
- **时间戳水印**：在视频上实时烧录时间戳水印
- **自动分段**：每 10 分钟自动分段保存视频文件，便于管理和播放
- **容错重启**：当设备断开或异常时自动重启录制
- **文件格式**：生成 Fragmented MP4 格式，支持实时播放

### PlaybackApp (播放应用)

- **历史视频查询**：通过时间范围查询历史录像，支持跨文件剪辑
- **实时流播放**：播放最新的实时录制文件，自动切换到最新内容
- **快捷键控制**：支持空格暂停/播放、方向键快进/快退
- **进度控制**：拖拽进度条跳转播放位置
- **多文件合并**：自动处理跨多个文件的查询请求

## 系统要求

- **操作系统**：Windows 10/11
- **运行时**：.NET 8.0 或更高版本
- **硬件**：USB 摄像头（推荐支持 1080P）
- **存储空间**：足够的磁盘空间用于视频存储（建议至少 100GB）
- **依赖工具**：
  - FFmpeg (用于视频处理)
  - VLC (用于视频播放，自动随 PlaybackApp 安装)

## 安装和设置

### 1. 克隆项目

```bash
git clone https://github.com/cyclone0312/videoCaptureDemo.git
cd videoCaptureDemo
```

### 2. 安装依赖

- 确保安装了 .NET 8.0 SDK
- 下载 FFmpeg：
  1. 访问 https://ffmpeg.org/download.html
  2. 下载 Windows 版本的静态构建
  3. 解压后将 `ffmpeg.exe`、`ffplay.exe`、`ffprobe.exe` 复制到 `CaptureService/` 目录下

### 3. 配置存储路径

默认视频存储路径为 `F:\Screenshot\videostore`。如需修改：

- 在 `CaptureService/Program.cs` 中修改 `outputDirectory` 变量
- 在 `PlaybackApp/MainWindow.xaml.cs` 中修改 `VideoDirectory` 常量
- 在 `PlaybackApp/RecordingService.cs` 中修改构造函数参数

## 使用指南

### 工作流程

1. **启动捕获服务**：

   ```bash
   cd CaptureService
   dotnet run
   ```

   服务将自动检测设备并开始录制，视频文件保存在配置的目录中。

2. **启动播放应用**：
   ```bash
   cd PlaybackApp
   dotnet run
   ```
   打开 WPF 界面进行视频播放。

### 播放应用操作

#### 查询历史视频

1. 设置开始时间和结束时间
2. 点击"查询并播放"按钮
3. 系统将自动剪辑相应时间段的视频并播放

#### 播放实时流

1. 点击"播放实时"按钮
2. 系统将播放最新的录制文件，并自动切换到新生成的文件

#### 播放控制

- **空格键**：暂停/播放
- **右方向键**：3 倍速快进
- **左方向键**：快退
- **进度条**：拖拽跳转到指定位置

## 项目结构

```
videoCaptureDemo/
├── CaptureService/          # 视频捕获控制台应用
│   ├── Program.cs          # 主程序，设备发现和录制逻辑
│   ├── ffmpeg.exe          # FFmpeg 可执行文件
│   └── ...                 # 其他配置文件
├── PlaybackApp/            # 视频播放WPF应用
│   ├── MainWindow.xaml     # 主界面
│   ├── MainWindow.xaml.cs  # 主窗口逻辑
│   ├── RecordingService.cs # 录像文件处理服务
│   └── ...                 # 其他UI和配置文件
├── global.json             # .NET SDK 版本配置
├── ffmpegDemo.sln          # Visual Studio 解决方案文件
└── README.md               # 项目文档
```

## 构建和运行

### 构建整个解决方案

```bash
dotnet build
```

### 单独运行组件

```bash
# 捕获服务
cd CaptureService
dotnet run

# 播放应用
cd PlaybackApp
dotnet run
```

## 故障排除

### 常见问题

1. **设备未找到**

   - 确保 USB 摄像头正确连接
   - 检查设备是否被其他程序占用
   - 运行 `ffmpeg -list_devices` 检查设备列表

2. **录制失败**

   - 确认 FFmpeg 文件存在且可执行
   - 检查存储路径权限
   - 查看控制台错误信息

3. **播放问题**

   - 确保 VLC 运行时已安装
   - 检查视频文件是否存在且完整
   - 尝试重新启动播放应用

4. **时间戳解析错误**
   - 确保文件名格式正确（CAM_USB-YYYYMMDD-HHMMSS.mp4）
   - 检查系统时区设置

### 日志查看

- CaptureService 的日志输出到控制台
- PlaybackApp 的错误信息显示在界面状态栏

## 技术细节

### 视频文件命名规则

- 格式：`CAM_USB-YYYYMMDD-HHMMSS.mp4`
- 示例：`CAM_USB-20251106-143025.mp4`
- 分段时长：600 秒（10 分钟）

### FFmpeg 参数说明

- 视频编码：H.264 (libx264)
- 音频编码：AAC
- 分段格式：Fragmented MP4 (支持实时播放)

### 播放器集成

- 使用 LibVLCSharp 进行视频播放
- 支持多种播放模式：历史查询、实时流

## 贡献

欢迎提交 Issue 和 Pull Request！

### 开发环境设置

1. 安装 Visual Studio 2022 或 VS Code
2. 安装 .NET 8.0 SDK
3. 克隆项目并安装依赖

## 许可证

本项目采用 MIT 许可证。
