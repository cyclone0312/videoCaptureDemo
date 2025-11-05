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
        private bool _isSliderDragging = false; // 防止拖动滑块时与播放器更新冲突
        private bool _isSeeking = false; // (新!) 防止跳转时进度条被更新

        // (新!) 添加一个定时器用于快退
        private System.Windows.Threading.DispatcherTimer? _rewindTimer;
        // (新!) 添加一个标志,防止 KeyDown 的 IsRepeat 属性干扰
        private bool _isRewinding = false;

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

            // 订阅播放器事件
            if (_mediaPlayer != null)
            {
                _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
                _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            }

            // (新!) 初始化快退定时器
            _rewindTimer = new System.Windows.Threading.DispatcherTimer();
            _rewindTimer.Interval = TimeSpan.FromMilliseconds(250); // 每 250 毫秒跳转一次
            _rewindTimer.Tick += RewindTimer_Tick;
        }

        // (新!) 定时器触发的事件
        private void RewindTimer_Tick(object? sender, EventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.IsSeekable)
            {
                // (改动!) 使用 Position 而不是 Time 来避免花屏
                // 计算向后跳转的百分比 (约1秒)
                float currentPosition = _mediaPlayer.Position;
                long totalDuration = _mediaPlayer.Length;

                if (totalDuration > 0)
                {
                    // 计算1秒对应的百分比
                    float oneSecondPercent = 1000f / totalDuration;
                    float newPosition = Math.Max(0, currentPosition - oneSecondPercent);

                    // 使用 Position 跳转,安全且不会花屏
                    _mediaPlayer.Position = newPosition;

                    // 更新状态文本
                    StatusText.Text = $"快退中... {FormatTime((long)(newPosition * totalDuration))}";
                }
            }
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


        /// <summary>
        /// "播放实时" 按钮的点击事件
        /// </summary>
        private void BtnPlayLive_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "正在连接实时流...";
            BtnPlayLive.IsEnabled = false; // 禁用实时按钮
            BtnPlay.IsEnabled = false;     // 禁用查询按钮

            try
            {
                // 1. 检查录像目录是否存在
                if (!Directory.Exists(VideoDirectory))
                {
                    StatusText.Text = "错误：录像目录未找到。";
                    return;
                }

                // 2. 查找"最新"的录像文件
                //    我们使用 FileInfo 来获取准确的 "LastWriteTime"
                var directory = new DirectoryInfo(VideoDirectory);
                var latestFile = directory.GetFiles("CAM_USB-*.mp4") // 匹配 Program.cs 中的文件名前缀
                                          .OrderByDescending(f => f.LastWriteTime)
                                          .FirstOrDefault(); // 获取最新的那一个

                if (latestFile == null)
                {
                    StatusText.Text = "错误：未找到任何录像文件。";
                    return;
                }

                // 3. 检查播放器是否已初始化
                if (_libVLC == null || _mediaPlayer == null)
                {
                    StatusText.Text = "错误：播放器未初始化";
                    return;
                }

                // 4. (关键) 播放这个文件，并添加低延迟选项
                StatusText.Text = $"正在播放实时画面 (来自: {latestFile.Name})...";

                // 创建 Media 对象并添加低延迟选项
                // ":live-caching=300" 告诉 VLC 仅缓冲 300 毫秒的数据
                // 这强制它播放"实时边缘"，而不是从文件开头播放
                var media = new Media(_libVLC, latestFile.FullName);
                media.AddOption(":live-caching=300");

                _mediaPlayer.Play(media);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"播放实时失败: {ex.Message}";
            }
            finally
            {
                BtnPlayLive.IsEnabled = true; // 恢复实时按钮
                BtnPlay.IsEnabled = true;     // 恢复查询按钮
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

        // ============================================================
        // 进度条 和 播放器 事件处理
        // ============================================================

        /// <summary>
        /// 当用户开始点击/拖动滑块时触发
        /// </summary>
        private void PlaybackSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isSliderDragging = true;
        }

        /// <summary>
        /// 当用户释放滑块时触发
        /// </summary>
        private void PlaybackSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isSliderDragging = false;

            if (_mediaPlayer != null && _mediaPlayer.IsSeekable)
            {
                // (新!) 标记正在跳转,防止 TimeChanged 事件更新进度条
                _isSeeking = true;

                // (关键改动!) 使用 Position (百分比 0.0-1.0) 而不是 Time (毫秒)
                // VLC 会自动寻找最近的关键帧，避免花屏问题
                _mediaPlayer.Position = (float)PlaybackSlider.Value;

                // (新!) 使用定时器在短暂延迟后解除 Seeking 标志
                // 这给 VLC 足够时间完成跳转
                var seekTimer = new System.Windows.Threading.DispatcherTimer();
                seekTimer.Interval = TimeSpan.FromMilliseconds(300); // 300ms 延迟
                seekTimer.Tick += (s, args) =>
                {
                    _isSeeking = false;
                    seekTimer.Stop();
                };
                seekTimer.Start();
            }
        }

        /// <summary>
        /// (播放器事件) 当视频的总时长确定时 (即加载新视频时) 触发
        /// </summary>
        private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            // 在 UI 线程上更新
            Dispatcher.Invoke(() =>
            {
                // (改动!) 设置滑块的最大值为 1.0 (代表 100%)
                // 使用 Position (百分比) 而不是 Time (毫秒) 来避免花屏
                PlaybackSlider.Maximum = 1.0; // 1.0 代表 100%
                PlaybackSlider.IsEnabled = true;
            });
        }

        /// <summary>
        /// (播放器事件) 当播放时间改变时 (每秒触发多次) 触发
        /// </summary>
        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            // 只有当用户 *没有* 正在拖动滑块 *且* 没有正在跳转时，才更新滑块的位置
            if (!_isSliderDragging && !_isSeeking)
            {
                // 在 UI 线程上更新
                Dispatcher.Invoke(() =>
                {
                    // (改动!) 更新滑块位置使用 Position (0.0-1.0) 而不是 Time (毫秒)
                    // 这样可以避免精确跳转导致的花屏问题
                    if (_mediaPlayer != null)
                    {
                        PlaybackSlider.Value = _mediaPlayer.Position;
                    }

                    // (保持不变) 时间文本仍然使用 Time 和 Length 来显示精确时间
                    if (_mediaPlayer != null)
                    {
                        TimeText.Text = $"{FormatTime(e.Time)} / {FormatTime(_mediaPlayer.Length)}";
                    }
                });
            }
        }

        /// <summary>
        /// 辅助函数：将毫秒转换为 "mm:ss" 格式的字符串
        /// </summary>
        private string FormatTime(long milliseconds)
        {
            TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
            return time.ToString(@"mm\:ss");
        }

        // ============================================================
        // 快捷键控制
        // ============================================================

        /// <summary>
        /// 当按键被按下时触发
        /// </summary>
        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // 如果播放器不存在，则不执行任何操作
            if (_mediaPlayer == null) return;

            switch (e.Key)
            {
                // 功能1: 按空格键 暂停/播放
                case System.Windows.Input.Key.Space:
                    if (_mediaPlayer.CanPause)
                    {
                        _mediaPlayer.Pause(); // Pause() 方法在 LibVLC 中是"切换"功能
                    }
                    e.Handled = true; // 标记为已处理，防止按钮等控件也响应
                    break;

                // 功能2: 按住右键 3倍快进
                case System.Windows.Input.Key.Right:
                    // 仅当 1) 正在播放 2) 可变速 3) 且 *不是* 重复按键时才设置
                    if (_mediaPlayer.IsPlaying && _mediaPlayer.IsSeekable && !e.IsRepeat)
                    {
                        _mediaPlayer.SetRate(3.0f);
                    }
                    e.Handled = true;
                    break;

                // (新!) 功能3: 左键快退 (使用定时器)
                case System.Windows.Input.Key.Left:
                    // 仅在 *第一次* 按下时启动定时器
                    // 移除 IsPlaying 检查，允许在暂停状态下也能快退
                    if (_mediaPlayer.IsSeekable && !_isRewinding && !e.IsRepeat)
                    {
                        _isRewinding = true;
                        _rewindTimer?.Start();
                        // 立即执行一次，获得即时反馈
                        RewindTimer_Tick(null, EventArgs.Empty);
                    }
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// 当按键被松开时触发
        /// </summary>
        private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_mediaPlayer == null) return;

            // 当"快进"的右键被松开时，恢复 1.0x 正常速度
            if (e.Key == System.Windows.Input.Key.Right)
            {
                _mediaPlayer.SetRate(1.0f);
                e.Handled = true;
            }

            // (新!) 当"快退"的左键被松开时，停止定时器
            if (e.Key == System.Windows.Input.Key.Left)
            {
                _rewindTimer?.Stop();
                _isRewinding = false;
                e.Handled = true;
            }
        }
    }
}