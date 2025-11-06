# 🎥 RTSP 实时流播放系统使用指南

## 📋 系统架构

本系统现在支持**两种播放模式**：

### 1. 🟠 伪实时播放（文件轮询）

- **延迟**: ~30 秒
- **原理**: 轮询最新的 fMP4 文件
- **优点**: 不需要额外组件，简单可靠
- **缺点**: 延迟较高，需要磁盘 I/O

### 2. 🟢 真实时播放（RTSP 流）

- **延迟**: <1 秒（亚秒级）
- **原理**: 通过 RTSP 协议直接传输视频流
- **优点**: 延迟极低，真正的实时
- **缺点**: 需要 MediaMTX 服务器运行

---

## 🚀 快速启动指南

### 步骤 1: 启动 RTSP 服务器

双击运行项目根目录下的：

```
start_rtsp_server.bat
```

**预期输出**：

```
========================================
  启动 MediaMTX RTSP 流媒体服务器
========================================

[RTSP] listener opened on :8554 (TCP), :8000 (UDP/RTP), :8001 (UDP/RTCP)
[RTMP] listener opened on :1935
[HLS] listener opened on :8888
...
```

**注意**：保持这个窗口运行！不要关闭它。

---

### 步骤 2: 启动视频采集服务

打开新的终端窗口，进入 `CaptureService` 目录并运行：

```powershell
cd CaptureService
dotnet run
```

**预期输出**：

```
Capture Service starting...
Discovering devices...
✓ Prioritized External (USB) Video Device: 1080P USB Camera
✓ Prioritized External (USB) Audio Device: USB Microphone
Starting FFmpeg capture...
  Video: 1080P USB Camera
  Audio: USB Microphone
  RTSP Stream: rtsp://localhost:8554/live_stream
FFmpeg: Output #0, tee, to '[f=segment...]|[f=rtsp...]':
...
```

**关键指标**：

- ✅ 看到 `RTSP Stream: rtsp://localhost:8554/live_stream` - 表示 RTSP 推流已配置
- ✅ 看到 FFmpeg 输出且没有错误 - 表示推流成功

---

### 步骤 3: 启动播放应用

打开另一个终端窗口，进入 `PlaybackApp` 目录并运行：

```powershell
cd PlaybackApp
dotnet run
```

应用启动后，您会看到**三个播放按钮**：

1. **▶ 查询并播放** - 播放历史录像（选择时间范围）
2. **🟠 伪实时（文件轮询）** - 基于文件的伪实时播放（~30 秒延迟）
3. **🟢 真实时（RTSP 流）** - ⭐ 新功能！基于 RTSP 的真实时播放（<1 秒延迟）

---

## 🎬 使用 RTSP 真实时播放

1. 确保所有三个组件都在运行：

   - ✅ MediaMTX 服务器（`start_rtsp_server.bat`）
   - ✅ CaptureService（`dotnet run`）
   - ✅ PlaybackApp（`dotnet run`）

2. 点击绿色按钮 **🟢 真实时（RTSP 流）**

3. 观察状态栏：

   - "正在连接 RTSP 实时流..." → 正在建立连接
   - "🟢 RTSP 实时流已连接（亚秒级延迟）" → 连接成功！

4. 现在您应该能看到**几乎零延迟**的实时画面！

---

## 🔧 故障排查

### 问题 1: 点击 RTSP 按钮后提示连接错误

**可能原因**：

- MediaMTX 服务器未运行
- CaptureService 未正在推流
- 防火墙阻止端口 8554

**解决方案**：

```powershell
# 1. 检查 MediaMTX 是否运行
# 应该能看到 start_rtsp_server.bat 窗口

# 2. 检查 CaptureService 是否运行
# 应该能看到 FFmpeg 输出日志

# 3. 检查防火墙（Windows PowerShell 管理员模式）
New-NetFirewallRule -DisplayName "MediaMTX RTSP" -Direction Inbound -Protocol TCP -LocalPort 8554 -Action Allow
```

---

### 问题 2: CaptureService 启动失败

**错误信息**：

```
Failed to start FFmpeg process
```

**解决方案**：
确保 `ffmpeg.exe` 在系统 PATH 中：

```powershell
# 测试 ffmpeg 是否可用
ffmpeg -version
```

---

### 问题 3: 画面有延迟或卡顿

**调整方法**：

1. **降低网络缓冲**（在 `MainWindow.xaml.cs` 的 `BtnPlayRtsp_Click` 中）：

```csharp
media.AddOption(":network-caching=100");  // 从 300 降到 100
```

2. **使用 UDP 传输**（在 `Program.cs` 中）：

```csharp
// 将 rtsp_transport=tcp 改为 udp
$"[f=rtsp:rtsp_transport=udp]{rtspStreamUrl}"
```

---

## 📊 性能对比

| 特性     | 伪实时（文件轮询） | 真实时（RTSP） |
| -------- | ------------------ | -------------- |
| 延迟     | ~30 秒             | <1 秒          |
| CPU 占用 | 中等               | 低             |
| 磁盘 I/O | 高                 | 无（直播时）   |
| 可靠性   | 非常高             | 高             |
| 历史回放 | ✅ 支持            | ❌ 不支持      |
| 实时监控 | ⚠️ 延迟高          | ✅ 完美        |

---

## 🌐 远程访问配置

如果您想在**其他电脑**上观看 RTSP 流：

### 1. 修改 CaptureService 的推流地址

在 `Program.cs` 中：

```csharp
// 将 localhost 改为服务器的实际 IP 地址
string rtspStreamUrl = "rtsp://192.168.1.100:8554/live_stream";
```

### 2. 修改 PlaybackApp 的播放地址

在 `MainWindow.xaml.cs` 的 `BtnPlayRtsp_Click` 中：

```csharp
// 将 localhost 改为服务器的 IP 地址
string rtspUrl = "rtsp://192.168.1.100:8554/live_stream";
```

### 3. 配置防火墙

确保端口 8554 对外开放。

---

## 🎯 最佳实践

### 推荐配置（低延迟优先）

```csharp
// CaptureService - Program.cs
[f=rtsp:rtsp_transport=tcp]  // TCP 更稳定

// PlaybackApp - MainWindow.xaml.cs
media.AddOption(":network-caching=100");   // 低延迟
media.AddOption(":rtsp-tcp");              // 匹配 TCP
```

### 推荐配置（稳定性优先）

```csharp
// CaptureService - Program.cs
[f=rtsp:rtsp_transport=tcp]

// PlaybackApp - MainWindow.xaml.cs
media.AddOption(":network-caching=500");   // 更大缓冲
media.AddOption(":rtsp-tcp");
```

---

## 📝 技术细节

### FFmpeg tee Muxer 工作原理

```bash
ffmpeg -i 输入 \
  -f tee \
  "目的地1|目的地2"
```

**我们的配置**：

- **目的地 1**: 本地文件录制（fMP4 分段，10 分钟一个文件）
- **目的地 2**: RTSP 实时推流

这样做的好处：

- ✅ 同时满足实时监控和历史回放需求
- ✅ 一次编码，多路输出，CPU 效率高
- ✅ 系统架构清晰，职责分明

---

## 💡 提示

- **实时播放**适合监控当前画面
- **历史回放**适合查看过去的录像
- 两种模式可以随时切换，互不干扰
- RTSP 流不会保存到磁盘，只用于实时观看
- 历史录像仍然正常保存在 `F:\Screenshot\videostore`

---

## ❓ 常见问题

**Q: 为什么还要保留"伪实时"模式？**  
A: 作为备份方案。如果 RTSP 服务器故障，仍然可以通过文件轮询实现基本的实时功能。

**Q: RTSP 流会占用额外的存储空间吗？**  
A: 不会。RTSP 流只在内存中传输，不写入磁盘。磁盘上的录像文件和以前一样。

**Q: 可以同时使用两种模式吗？**  
A: 可以！它们完全独立。但通常只需要使用 RTSP 模式即可。

**Q: 延迟能降到多低？**  
A: 理论上可以达到 0.3-0.5 秒。实际延迟取决于：

- 网络状况
- 编码器延迟
- 播放器缓冲设置

---

## 🎉 享受实时视频流！

如有问题，请检查：

1. MediaMTX 服务器日志
2. CaptureService 控制台输出
3. PlaybackApp 状态栏提示

祝您使用愉快！ 🚀
