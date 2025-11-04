// ============================================================
// 引入必需的命名空间
// ============================================================
using System.Diagnostics;      // 用于启动和管理 FFmpeg 进程
using System.IO;                // 用于文件和目录操作
using System.Threading.Tasks;   // 用于异步编程支持

/// <summary>
/// USB摄像头视频采集服务
/// 核心原理：通过 DirectShow (Windows 专用的多媒体框架) 访问 USB 摄像头，
/// 使用 FFmpeg 进行实时录制，并自动按时间分段保存视频文件。
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

        // ============================================================
        // 步骤 1: 配置摄像头设备名称
        // ============================================================
        // 如何获取设备名称：
        // 在命令行运行: ffmpeg -list_devices true -f dshow -i dummy
        // 从输出的 "DirectShow video devices" 列表中复制完整的设备名称
        string videoDeviceName = "Integrated Camera"; // <-- 替换为您的实际设备名称

        // 音频设备（本示例不录制音频，如需音频可取消注释并配置）
        // 获取方式同上，在 "DirectShow audio devices" 列表中查找
        // string audioDeviceName = "Microphone Array (Realtek Audio)";

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
                // 示例输出：CAM_USB-20251104-143025.mp4
                string outputTemplate = Path.Combine(outputDirectory, "CAM_USB-%Y%m%d-%H%M%S.mp4");

                Console.WriteLine($"Starting FFmpeg capture from device: {videoDeviceName}");

                // ============================================================
                // 步骤 5: 构建 FFmpeg 命令行参数
                // ============================================================
                // 完整命令相当于在命令行执行：
                // ffmpeg -f dshow -i video="Integrated Camera" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -an -f segment -segment_time 600 -strftime 1 "输出路径"
                //
                // 各参数详解：
                // -f dshow                      指定使用 DirectShow 输入格式 (Windows 系统专用)
                // -i video="设备名"             指定视频输入源为特定的摄像头设备
                // -c:v libx264                  使用 H.264 编码器压缩视频 (必须编码，不能用 copy)
                // -preset ultrafast             编码速度预设为"超快"，降低CPU占用，但文件会稍大
                // -pix_fmt yuv420p              设置像素格式为 YUV 4:2:0，确保最广泛的播放器兼容性
                // -an                           禁用音频流 (audio none)
                // -f segment                    使用分段输出格式，自动将长视频切割成多个文件
                // -segment_time 600             每个分段的时长 (秒)，600秒 = 10分钟
                // -strftime 1                   启用时间戳文件名功能，支持 %Y %m %d 等变量
                string ffmpegArgs = $"-f dshow -i video=\"{videoDeviceName}\" " +
                    $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p " +
                    $"-an " +
                    $"-f segment -segment_time 600 -strftime 1 " +
                    $"\"{outputTemplate}\"";

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
}