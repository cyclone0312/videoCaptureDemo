using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FFMpegCore; // (剪辑功能) FFMpegCore 保持不变，它很好用

// 1. (关键改动) 引入 LibVLCSharp
using LibVLCSharp.Shared;

namespace PlaybackApp
{
    public partial class MainWindow : Window
    {
        // !!! (重要) 确保这个路径和您 CaptureService 的路径一致
        private const string VideoDirectory = @"F:\Screenshot\videostore";

        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;

        public MainWindow()
        {
            InitializeComponent();

            // 为方便测试，设置默认时间
            DtpStartTime.Value = DateTime.Now.AddMinutes(-15);
            DtpEndTime.Value = DateTime.Now.AddMinutes(-13);
        }

        // 3. (关键改动) 在窗口加载时初始化 VLC
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 初始化 VLC 核心
            Core.Initialize();

            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            // 将 UI 控件 (VideoView) 和 播放器逻辑 (_mediaPlayer) 绑定
            VideoView.MediaPlayer = _mediaPlayer;
        }

        // 4. (关键改动) 在窗口关闭时释放资源
        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }

        /// <summary>
        /// "查询并播放" 按钮的点击事件
        /// </summary>
        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            // 1. 获取并验证 UI 输入 (这部分不变)
            DateTime startTime = DtpStartTime.Value ?? DateTime.MinValue;
            DateTime endTime = DtpEndTime.Value ?? DateTime.MinValue;

            if (endTime <= startTime)
            {
                StatusText.Text = "错误：结束时间必须大于开始时间";
                return;
            }

            StatusText.Text = "正在查找和剪辑视频...";
            BtnPlay.IsEnabled = false;

            try
            {
                // 2. 核心逻辑：生成剪辑 (这部分不变, FFMpegCore 负责)
                string? clipPath = await GeneratePlaybackClipAsync(startTime, endTime);

                if (clipPath == null)
                {
                    StatusText.Text = "错误：未找到该时间段的视频文件。";
                    return;
                }

                // 3. (关键改动) 播放剪辑好的文件 (使用 LibVLC)
                StatusText.Text = "正在播放...";

                // 创建一个 Media 对象
                if (_libVLC != null && _mediaPlayer != null)
                {
                    using (var media = new Media(_libVLC, new Uri(clipPath)))
                    {
                        // 开始播放
                        _mediaPlayer.Play(media);
                    }
                }
                else
                {
                    StatusText.Text = "错误：播放器未初始化";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"播放失败: {ex.Message}";
            }
            finally
            {
                BtnPlay.IsEnabled = true;
            }
        }


        // --- (好消息!) ---
        // --- 以下所有 FFMpegCore 的剪辑逻辑完全不变 ---
        // --- 它们与播放器无关 ---

        /// <summary>
        /// 核心函数：根据时间范围生成一个可播放的剪辑文件
        /// </summary>
        /// <returns>临时剪辑文件的路径，如果失败则返回 null</returns>
        private async Task<string?> GeneratePlaybackClipAsync(DateTime startTime, DateTime endTime)
        {
            // 1. 找到 "开始时间" 和 "结束时间" 对应的文件
            string? sourceFile = FindFileForTime(startTime);
            string? endFile = FindFileForTime(endTime);

            if (sourceFile == null || endFile == null)
            {
                StatusText.Text = "错误：未能在目录中找到对应的视频文件。";
                return null; // 未找到文件
            }

            // --- 案例 A: 简单情况 (开始和结束在同一个文件) ---
            if (sourceFile == endFile)
            {
                DateTime fileStartTime = ParseTimeFromFileName(sourceFile);
                TimeSpan clipStartTime = startTime - fileStartTime; // 这是 -ss 参数
                TimeSpan clipDuration = endTime - startTime;        // 这是 -t 参数

                string tempClipPath = Path.Combine(Path.GetTempPath(), $"playback_{Guid.NewGuid()}.mp4");

                await FFMpegArguments
                    .FromFileInput(sourceFile)
                    .OutputToFile(tempClipPath, true, options => options
                        .Seek(clipStartTime)
                        .WithDuration(clipDuration)
                        .WithVideoCodec("copy") // 使用 "copy" 模式，极快
                    )
                    .ProcessAsynchronously();

                if (File.Exists(tempClipPath))
                {
                    return tempClipPath;
                }
            }
            
            // --- 案例 B: 高级情况 (跨越多个文件) ---
            else
            {
                // 1. 创建一个临时的"工作目录"来存放所有剪辑片段
                string workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(workDir);

                try
                {
                    // 2. 获取所有文件，并按名称排序 (确保时间顺序)
                    var allFiles = Directory.GetFiles(VideoDirectory, "CAM_USB-*.mp4")
                                            .OrderBy(f => f)
                                            .ToList();

                    int startIndex = allFiles.IndexOf(sourceFile);
                    int endIndex = allFiles.IndexOf(endFile);

                    if (startIndex == -1 || endIndex == -1)
                    {
                        StatusText.Text = "错误：无法在文件列表中定位索引。";
                        return null;
                    }

                    var filesToProcess = allFiles.GetRange(startIndex, (endIndex - startIndex) + 1);
                    var clipFileNames = new List<string>(); // 用于 mylist.txt

                    // 3. 循环处理所有涉及的文件 (第一个、中间的、最后一个)
                    for (int i = 0; i < filesToProcess.Count; i++)
                    {
                        string currentFile = filesToProcess[i];
                        string clipName = $"{i}.mp4"; // 临时片段名 0.mp4, 1.mp4 ...
                        string clipOutputPath = Path.Combine(workDir, clipName);
                        
                        var clipArgs = FFMpegArguments.FromFileInput(currentFile);

                        if (i == 0) // 第一个文件: 从 startTime 剪到末尾
                        {
                            DateTime fileStartTime = ParseTimeFromFileName(currentFile);
                            TimeSpan clipStartTime = startTime - fileStartTime;

                            await clipArgs.OutputToFile(clipOutputPath, true, options => options
                                    .Seek(clipStartTime)
                                    .WithVideoCodec("copy")
                                ).ProcessAsynchronously();
                        }
                        else if (i == filesToProcess.Count - 1) // 最后一个文件: 从开头剪到 endTime
                        {
                            DateTime fileStartTime = ParseTimeFromFileName(currentFile);
                            TimeSpan clipDuration = endTime - fileStartTime;

                            await clipArgs.OutputToFile(clipOutputPath, true, options => options
                                    .WithDuration(clipDuration)
                                    .WithVideoCodec("copy")
                                ).ProcessAsynchronously();
                        }
                        else // 中间文件: 复制整个文件
                        {
                            await clipArgs.OutputToFile(clipOutputPath, true, options => options
                                    .WithVideoCodec("copy")
                                ).ProcessAsynchronously();
                        }

                        clipFileNames.Add(clipName); // 将 "0.mp4" 等添加到列表
                    }

                    // 4. 创建 FFmpeg concat 必需的 "mylist.txt"
                    string listPath = Path.Combine(workDir, "mylist.txt");
                    var fileContent = string.Join('\n', clipFileNames.Select(f => $"file '{f}'"));
                    await File.WriteAllTextAsync(listPath, fileContent);

                    // 5. 执行合并 (concat) 命令
                    string finalClipPath = Path.Combine(Path.GetTempPath(), $"playback_merged_{Guid.NewGuid()}.mp4");
                    
                    // FFMpegCore 需要这样来调用 concat demuxer:
                    // 注意：我们需要使用绝对路径或者在工作目录中执行
                    var previousDir = Directory.GetCurrentDirectory();
                    try
                    {
                        Directory.SetCurrentDirectory(workDir);
                        
                        await FFMpegArguments
                            // -f concat -safe 0 -i mylist.txt
                            .FromFileInput("mylist.txt", false, options => options 
                                .WithCustomArgument("-f concat")
                                .WithCustomArgument("-safe 0") // 允许使用相对路径
                            ) 
                            // -c copy output.mp4
                            .OutputToFile(finalClipPath, false, options => options
                                .WithVideoCodec("copy")
                            )
                            .ProcessAsynchronously();
                    }
                    finally
                    {
                        Directory.SetCurrentDirectory(previousDir);
                    }

                    if (File.Exists(finalClipPath))
                    {
                        return finalClipPath;
                    }
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"多文件合并失败: {ex.Message}";
                    return null;
                }
                finally
                {
                    // 6. (重要) 清理我们的临时工作目录
                    if (Directory.Exists(workDir))
                    {
                        Directory.Delete(workDir, true);
                    }
                }
            }
            
            return null; // 所有逻辑都失败了
        }

        /// <summary>
        /// 辅助函数：根据文件名解析出文件的开始时间
        /// </summary>
        private DateTime ParseTimeFromFileName(string filePath)
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string[] parts = fileName.Split('-');
            string dateTimeString = $"{parts[1]}{parts[2]}";

            return DateTime.ParseExact(dateTimeString, "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 辅助函数：查找指定时间点所在的录像文件
        /// </summary>
        private string? FindFileForTime(DateTime requestedTime)
        {
            if (!Directory.Exists(VideoDirectory)) return null;

            var files = Directory.GetFiles(VideoDirectory, "CAM_USB-*.mp4");

            foreach (var file in files)
            {
                try
                {
                    DateTime fileStartTime = ParseTimeFromFileName(file);
                    DateTime fileEndTime = fileStartTime.AddMinutes(10); // 匹配 600 秒分段

                    if (requestedTime >= fileStartTime && requestedTime < fileEndTime)
                    {
                        return file; // 找到了!
                    }
                }
                catch
                {
                    // 忽略无法解析的文件名
                }
            }

            return null; // 没找到
        }
    }
}