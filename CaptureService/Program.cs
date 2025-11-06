// ============================================================
// 引入必需的命名空间
// ============================================================
using System.Diagnostics;      // 用于启动和管理 FFmpeg 进程
using System.IO;                // 用于文件和目录操作
using System.Threading.Tasks;   // 用于异步编程支持
using System.Collections.Generic; // 用于支持 List<string>
using System.Text.RegularExpressions; // 用于更复杂的解析
using System.Linq;              // 用于 LINQ 查询方法（如 FirstOrDefault）

/// <summary>
/// USB摄像头视频采集服务 (V2 - 带自动设备发现)
/// 核心原理：
/// 1. 启动时，调用 "ffmpeg -list_devices" 来自动查找可用的音视频设备。
/// 2. 自动选择第一个找到的视频和音频设备。
/// 3. 使用 FFmpeg 进行实时录制，并自动按时间分段保存视频文件。
/// </summary>
class Program
{
    /// <summary>
    /// 程序入口点 - 异步主方法
    /// 为什么用 async：允许我们使用 await 关键字，避免阻塞主线程
    /// </summary>
    static async Task Main(string[] args)
    {
        Console.WriteLine("Capture Service starting...");

        string videoDeviceName;
        string audioDeviceName;

        // ============================================================
        // 步骤 1: (新!) 自动发现设备 (V3 - 带优先级选择)
        // ============================================================
        try
        {
            Console.WriteLine("Discovering devices...");

            // 1. 自动查找视频设备
            List<string> videoDevices = GetDirectShowDevices("video");
            if (videoDevices.Count == 0)
            {
                Console.WriteLine("Error: No DirectShow video devices found.");
                return;
            }

            // (--- 新的智能选择逻辑 ---)
            // 优先选择名字中包含 "USB" 的设备
            string? preferredVideoDevice = videoDevices.FirstOrDefault(name => name.Contains("USB", StringComparison.OrdinalIgnoreCase));

            if (preferredVideoDevice != null)
            {
                videoDeviceName = preferredVideoDevice;
                Console.WriteLine($"✓ Prioritized External (USB) Video Device: {videoDeviceName}");
            }
            else
            {
                // 回退：如果没有 "USB" 设备，则选择列表中的第一个
                videoDeviceName = videoDevices[0];
                Console.WriteLine($"⚠ No USB device found. Defaulting to first Video Device: {videoDeviceName}");
            }
            // (--- 智能选择逻辑结束 ---)


            // 2. 自动查找音频设备
            List<string> audioDevices = GetDirectShowDevices("audio");
            if (audioDevices.Count == 0)
            {
                Console.WriteLine("Error: No DirectShow audio devices found.");
                return;
            }

            // (--- 新的智能选择逻辑 ---)
            // 优先选择名字中包含 "USB" 的设备 (通常是摄像头自带的麦克风)
            string? preferredAudioDevice = audioDevices.FirstOrDefault(name => name.Contains("USB", StringComparison.OrdinalIgnoreCase));

            if (preferredAudioDevice != null)
            {
                audioDeviceName = preferredAudioDevice;
                Console.WriteLine($"✓ Prioritized External (USB) Audio Device: {audioDeviceName}");
            }
            else
            {
                // 回退：如果没有 "USB" 音频，则选择列表中的第一个 (可能是板载麦克风)
                audioDeviceName = audioDevices[0];
                Console.WriteLine($"⚠ No USB device found. Defaulting to first Audio Device: {audioDeviceName}");
            }
            // (--- 智能选择逻辑结束 ---)
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to discover devices: {ex.Message}");
            Console.WriteLine("Please ensure ffmpeg.exe is in your PATH or application directory.");
            return;
        }

        // ============================================================
        // 步骤 2: 配置视频文件存储路径
        // ============================================================
        string outputDirectory = @"F:\Screenshot\videostore";

        // 确保输出目录存在，如不存在则自动创建
        // 这样可以避免后续写入文件时出现 "目录不存在" 错误
        Directory.CreateDirectory(outputDirectory);

        // ============================================================
        // 步骤 3: 无限循环 - 服务持续运行的核心机制
        // ============================================================
        // 为什么需要无限循环：
        // 1. 确保服务永不停止，类似 Windows 服务的行为
        // 2. 当 FFmpeg 意外退出时自动重启
        // 3. 处理摄像头断开、USB故障等异常情况
        while (true)
        {
            try
            {
                // ============================================================
                // 步骤 4: 生成带时间戳的文件名模板
                // ============================================================
                // 文件名格式说明：
                // %Y - 4位年份 (2025)
                // %m - 2位月份 (01-12)
                // %d - 2位日期 (01-31)
                // %H - 2位小时 (00-23)
                // %M - 2位分钟 (00-59)
                // %S - 2位秒数 (00-59)
                // 示例输出:CAM_USB-20251104-143025.mp4
                string outputTemplate = Path.Combine(outputDirectory, "CAM_USB-%Y%m%d-%H%M%S.mp4");

                Console.WriteLine($"Starting FFmpeg capture...");
                Console.WriteLine($"  Video: {videoDeviceName}");
                Console.WriteLine($"  Audio: {audioDeviceName}");

                // ============================================================
                // 步骤 5: (还原!) 构建包含音视频和时间戳水印的 FFmpeg 参数
                // (重要!) 不再使用 tee muxer，只负责录制文件
                // ============================================================
                // (新增!) 构建 drawtext 滤镜
                //
                // (正确的转义方案!)
                // 关键: 不使用 @ 字符串,手动双重转义
                // C# 字符串: \\  -> 编译后: \  -> FFmpeg 收到: \
                string drawtextFilter =
                    "drawtext=" +
                    "fontfile=Arial:" +
                    "expansion=strftime:" +
                    "text=%Y-%m-%d\\\\ %H\\\\:%M\\\\:%S:" +  // 四个反斜杠在 C# 中变成两个,FFmpeg 收到 \\ 和 \:
                    "fontsize=28:" +
                    "fontcolor=white@0.9:" +
                    "box=1:" +
                    "boxcolor=black@0.6:" +
                    "boxborderw=8:" +
                    "x=w-tw-15:" +
                    "y=15";

                // (新!) 定义 RTSP 推流地址
                string rtspStreamUrl = "rtsp://localhost:8554/live_stream";

                // (!!! 重要 !!!) 对文件路径中的反斜杠进行转义
                // 在 tee muxer 内部，反斜杠需要双重转义：
                // C# 中: \\\\  ->  编译后: \\  ->  FFmpeg 的 tee 解析后: \
                string escapedOutputTemplate = outputTemplate.Replace("\\", "\\\\\\\\");

                // ============================================================
                // (关键修改!) 构建最终的 Tee Muxer FFmpeg 参数
                // ============================================================
                // 我们将文件录制和RTSP推流合并到一个进程中：
                // 1. (输入) 使用 -re -f dshow... 以实时速率读取
                // 2. (编码) 使用统一的编码参数
                // 3. (关键!) 使用 -map 指定要输出的流
                // 4. (输出) 使用 -f tee 同时输出到文件和RTSP
                //
                // tee muxer 格式：
                // -map 0 -f tee "[options1]output1|[options2]output2"
                // 目标1: segment格式的分片MP4文件（使用普通MP4，不用fMP4）
                // 目标2: RTSP推流
                //
                // (!!! 关键修复 !!!)
                // 从 movflags=frag_keyframe+empty_moov (fMP4 - 流式友好但不兼容tee)
                // 改为 movflags=faststart (普通MP4 - tee兼容但需要完整写入后才能播放)
                string ffmpegArgs = $"-re -f dshow -i video=\"{videoDeviceName}\":audio=\"{audioDeviceName}\" " +
                    $"-vf \"{drawtextFilter}\" " +
                    // 统一的、对 RTSP 和 fMP4 都友好的编码参数
                    $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 50 " +
                    $"-c:a aac -ar 44100 -b:a 128k " +
                    // (!!! 关键修复 !!!) 添加 -map 0 将所有输入流映射到输出
                    $"-map 0 " +
                    // 指定 "tee" Muxer
                    $"-f tee " +
                    // 目标1 (录制) 和 目标2 (推流)
                    // 注意：使用转义后的路径，使用 faststart 代替 frag_keyframe+empty_moov
                    $"\"[f=segment:segment_time=600:strftime=1:segment_format_options=movflags=faststart]{escapedOutputTemplate}|[f=rtsp:rtsp_transport=tcp]{rtspStreamUrl}\"";

                Console.WriteLine($"FFmpeg arguments: {ffmpegArgs}");

                // ============================================================
                // 步骤 6: 配置并启动 FFmpeg 进程
                // ============================================================
                // 为什么不用 FFMpegCore：
                // FFMpegCore 对 DirectShow 支持有限，无法正确传递 -f dshow 参数
                // 直接使用 Process 类可以完全控制命令行参数，更加灵活
                var processInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",              // 可执行文件名 (需要 ffmpeg 在 PATH 中或同目录)
                    Arguments = ffmpegArgs,           // 上面构建的命令行参数
                    UseShellExecute = false,          // 不使用系统 Shell，直接启动进程
                    RedirectStandardOutput = true,    // 重定向标准输出，以便读取 FFmpeg 输出
                    RedirectStandardError = true,     // 重定向错误输出，FFmpeg 的日志都在这里
                    CreateNoWindow = false            // 显示控制台窗口，方便调试 (生产环境可设为 true)
                };

                using (var process = Process.Start(processInfo))
                {
                    // 进程启动失败检查
                    if (process == null)
                    {
                        throw new Exception("Failed to start FFmpeg process");
                    }

                    // ============================================================
                    // 步骤 7: 异步读取 FFmpeg 的日志输出
                    // ============================================================
                    // 为什么需要单独的 Task：
                    // 1. FFmpeg 的日志信息输出到 stderr (标准错误流)
                    // 2. 如果不持续读取，缓冲区会满，导致 FFmpeg 进程挂起
                    // 3. 使用异步任务避免阻塞主线程
                    var errorTask = Task.Run(() =>
                    {
                        while (!process.StandardError.EndOfStream)
                        {
                            string? line = process.StandardError.ReadLine();
                            if (!string.IsNullOrEmpty(line))
                            {
                                // 将 FFmpeg 的输出打印到控制台，便于监控录制状态
                                Console.WriteLine($"FFmpeg: {line}");
                            }
                        }
                    });

                    // ============================================================
                    // 步骤 8: 等待 FFmpeg 进程结束
                    // ============================================================
                    // 正常情况下 FFmpeg 会一直运行 (持续录制)
                    // 只有在以下情况会退出：
                    // 1. 摄像头断开
                    // 2. 磁盘空间不足
                    // 3. 用户手动停止 (Ctrl+C)
                    await process.WaitForExitAsync();
                    await errorTask;  // 等待日志读取任务也完成

                    // 检查退出代码，非 0 表示异常退出
                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"FFmpeg exited with code {process.ExitCode}");
                    }
                }

                // 如果 FFmpeg 正常退出 (理论上不应该发生)，等待后重启
                Console.WriteLine("FFmpeg process exited. Restarting in 10 seconds...");
                await Task.Delay(10000);
            }
            catch (Exception ex)
            {
                // ============================================================
                // 步骤 9: 异常处理与自动重启机制
                // ============================================================
                // 可能的异常场景：
                // 1. 摄像头设备未找到或设备名错误
                // 2. USB 连接中断或摄像头被拔出
                // 3. FFmpeg 进程启动失败 (ffmpeg.exe 不在 PATH 中)
                // 4. 磁盘写入失败 (空间不足、权限问题)
                // 5. 编码器不可用或参数错误
                //
                // 处理策略：
                // 打印错误信息到控制台，等待 10 秒后自动重试
                // 这种"容错重试"机制确保临时故障不会导致服务停止
                Console.WriteLine($"Error occurred: {ex.Message}");
                Console.WriteLine("Capture failed. Retrying in 10 seconds...");

                // 等待 10 秒后，while(true) 循环会再次执行，尝试重启 FFmpeg
                // 可以根据需要调整重试间隔时间
                await Task.Delay(10000);
            }
        }
    }

    // ============================================================
    // (新功能) 辅助函数：调用 FFmpeg 并解析设备列表
    // ============================================================
    /// <summary>
    /// 调用 "ffmpeg -list_devices" 并解析出指定类型的设备列表
    /// </summary>
    /// <param name="deviceType"> "video" 或 "audio" </param>
    /// <returns>一个包含设备名称的字符串列表</returns>
    private static List<string> GetDirectShowDevices(string deviceType)
    {
        var devices = new List<string>();
        var processInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-list_devices true -f dshow -i dummy", // FFmpeg 的设备列表命令
            RedirectStandardOutput = true,
            RedirectStandardError = true,  // FFmpeg 将列表输出到 StandardError
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(processInfo))
        {
            if (process == null)
            {
                throw new Exception("Failed to start ffmpeg for device listing.");
            }

            // 读取 FFmpeg 的全部输出
            string output = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // 开始解析输出
            using (var reader = new StringReader(output))
            {
                string? line;
                string deviceTypeMarker = $"({deviceType})"; // 例如: "(video)" 或 "(audio)"

                while ((line = reader.ReadLine()) != null)
                {
                    // 检查这一行是否包含我们要找的设备类型
                    // 例如: [dshow @ ...] "1080P USB Camera" (video)
                    if (line.Contains("[dshow") && line.Contains(deviceTypeMarker))
                    {
                        // 解析设备名称
                        string? deviceName = ParseDeviceName(line);
                        if (deviceName != null)
                        {
                            devices.Add(deviceName);
                        }
                    }
                }
            }
        }
        return devices;
    }

    /// <summary>
    /// (新功能) 辅助函数：从 FFmpeg 输出行中提取设备名称
    /// 例如: 从 "[dshow]  \"1080P USB Camera\"" 中提取 "1080P USB Camera"
    /// </summary>
    private static string? ParseDeviceName(string line)
    {
        int firstQuote = line.IndexOf('"');
        if (firstQuote == -1) return null;

        int secondQuote = line.IndexOf('"', firstQuote + 1);
        if (secondQuote == -1) return null;

        return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }
}