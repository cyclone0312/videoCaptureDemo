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

        // (新!) 引入服务
        private readonly RecordingService _recordingService;

        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private bool _isSliderDragging = false; // 防止拖动滑块时与播放器更新冲突
        private bool _isSeeking = false; // (新!) 防止跳转时进度条被更新
        private float _targetSeekPosition = 0f; // (新!) 记录目标跳转位置
        private bool _isFastForwarding = false; // (新!) 标志：是否正在快进

        // (新!) 添加节流变量：记录上次更新进度条的时间
        private DateTime _lastProgressUpdate = DateTime.MinValue;
        private const int ProgressUpdateIntervalMs = 100; // 进度条更新间隔（毫秒）

        // (新!) 添加一个定时器用于快退
        private System.Windows.Threading.DispatcherTimer? _rewindTimer;
        // (新!) 添加一个标志,防止 KeyDown 的 IsRepeat 属性干扰
        private bool _isRewinding = false;

        // (新!) 标志：是否正在播放"实时流"（需要跳转到末尾）
        private bool _isLivePlayback = false;

        // (新!) 记录当前正在播放的实时文件路径
        private string? _currentLiveFilePath = null;

        // (新!) 添加一个定时器，用于定期检查是否有新的录像文件
        private System.Windows.Threading.DispatcherTimer? _liveMonitorTimer;

        public MainWindow()
        {
            InitializeComponent();

            // (新!) 初始化服务
            _recordingService = new RecordingService(VideoDirectory);

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
                _mediaPlayer.Playing += MediaPlayer_Playing; // (新!) 订阅播放开始事件
                _mediaPlayer.EndReached += MediaPlayer_EndReached; // (新!) 订阅播放结束事件
            }

            // (新!) 初始化快退定时器
            _rewindTimer = new System.Windows.Threading.DispatcherTimer();
            _rewindTimer.Interval = TimeSpan.FromMilliseconds(250); // 每 250 毫秒跳转一次
            _rewindTimer.Tick += RewindTimer_Tick;

            // (新!) 初始化实时监控定时器
            _liveMonitorTimer = new System.Windows.Threading.DispatcherTimer();
            _liveMonitorTimer.Interval = TimeSpan.FromSeconds(5); // 每 5 秒检查一次
            _liveMonitorTimer.Tick += LiveMonitorTimer_Tick;
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

        // (新!) 实时监控定时器触发的事件
        // 每 5 秒检查一次是否有新的录像文件生成
        private void LiveMonitorTimer_Tick(object? sender, EventArgs e)
        {
            // 只有在实时播放模式下才检查
            if (_currentLiveFilePath == null) return;

            try
            {
                Console.WriteLine("=== 定时器检查新文件 ===");

                // 查找最新的文件
                string? latestFile = _recordingService.FindLatestLiveFile();

                if (latestFile == null)
                {
                    Console.WriteLine("⚠ 未找到任何文件");
                    return;
                }

                Console.WriteLine($"当前播放: {Path.GetFileName(_currentLiveFilePath)}");
                Console.WriteLine($"最新文件: {Path.GetFileName(latestFile)}");

                // 如果发现了新文件（文件路径不同）
                if (latestFile != _currentLiveFilePath)
                {
                    Console.WriteLine("✓ 发现新文件，立即切换！");
                    StatusText.Text = $"定时器发现新文件，正在切换: {Path.GetFileName(latestFile)}...";

                    // 更新当前文件路径
                    _currentLiveFilePath = latestFile;

                    // 设置标志，让播放器跳转到新文件的末尾
                    _isLivePlayback = true;

                    // 播放新文件
                    if (_libVLC != null && _mediaPlayer != null)
                    {
                        var media = new Media(_libVLC, latestFile);
                        media.AddOption(":live-caching=300");
                        _mediaPlayer.Play(media);
                    }
                }
                else
                {
                    Console.WriteLine("→ 仍是同一文件，继续等待...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ 监控文件失败: {ex.Message}");
                StatusText.Text = $"监控文件失败: {ex.Message}";
            }
        }

        // 4. (关键改动) 在窗口关闭时释放资源
        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }

        /// <summary>
        /// "查询并播放" 按钮的点击事件 (已重构)
        /// </summary>
        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            // 1. 获取并验证 UI 输入
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
                // (重要!) 清除实时播放标志
                _isLivePlayback = false;

                // 2. (新!) 创建一个 Progress 对象，它会自动在UI线程上更新 StatusText
                var progress = new Progress<string>(message =>
                {
                    StatusText.Text = message;
                });

                // 3. (新!) 将繁重的工作委托给服务
                //    我们传入 progress 对象，以便服务可以报告状态
                string? clipPath = await _recordingService.GeneratePlaybackClipAsync(startTime, endTime, progress);

                if (clipPath == null)
                {
                    // StatusText 已经被服务更新过了 (例如: "错误：未找到...")
                    return;
                }

                // 3. (不变) 播放剪辑好的文件 (这是 MainWindow 的职责)
                StatusText.Text = "正在播放...";

                // (新!) 清除实时播放相关标志
                _currentLiveFilePath = null;
                _isLivePlayback = false;

                // (新!) 停止实时监控定时器
                _liveMonitorTimer?.Stop();

                if (_libVLC != null && _mediaPlayer != null)
                {
                    // (修复!) 不要使用 using，让 Media 对象在播放期间保持存活
                    var media = new Media(_libVLC, new Uri(clipPath));

                    // (新!) 禁用循环播放
                    media.AddOption(":no-loop");
                    media.AddOption(":no-repeat");

                    // 开始播放
                    _mediaPlayer.Play(media);
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
        /// "播放实时" 按钮的点击事件 (已重构)
        /// </summary>
        private void BtnPlayLive_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "正在连接实时流...";
            BtnPlayLive.IsEnabled = false; // 禁用实时按钮
            BtnPlay.IsEnabled = false;     // 禁用查询按钮

            try
            {
                // 1. (新!) 从服务中获取文件
                string? latestFile = _recordingService.FindLatestLiveFile();

                if (latestFile == null)
                {
                    StatusText.Text = "错误：未找到任何录像文件。";
                    return;
                }

                // 2. 检查播放器是否已初始化
                if (_libVLC == null || _mediaPlayer == null)
                {
                    StatusText.Text = "错误：播放器未初始化";
                    return;
                }

                // 3. (不变) 播放文件 (这是 MainWindow 的职责)
                StatusText.Text = "正在加载实时画面，准备跳转...";

                // (新!) 记录当前正在播放的文件路径
                _currentLiveFilePath = latestFile;

                // (重要!) 设置实时播放标志，告诉 Playing 事件处理器需要跳转到末尾
                _isLivePlayback = true;

                // (新!) 启动定时器，定期检查是否有新文件
                _liveMonitorTimer?.Start();

                // 创建 Media 对象并添加低延迟选项
                var media = new Media(_libVLC, latestFile);
                media.AddOption(":live-caching=300");

                // 直接播放，跳转逻辑在 MediaPlayer_Playing 事件中处理
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
                // 1. 标记我们正在等待跳转
                _isSeeking = true;

                // 2. 记录目标位置
                _targetSeekPosition = (float)PlaybackSlider.Value;

                // 3. 执行跳转
                _mediaPlayer.Position = _targetSeekPosition;
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
        /// (新!) (播放器事件) 当播放开始时触发
        /// 用于实时播放时自动跳转到视频末尾
        /// </summary>
        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            // 只有在"实时播放"模式下才跳转到末尾
            if (_isLivePlayback && _mediaPlayer != null && _mediaPlayer.IsSeekable)
            {
                // (美化方案!) 跳转期间隐藏视频，避免看到绿屏
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                    {
                        // 1. 隐藏视频控件
                        VideoView.Visibility = Visibility.Hidden;
                        StatusText.Text = "正在跳转到实时位置，请稍候...";

                        // 2. 计算目标位置（最后30秒）
                        long thirtySecondsInMs = 30 * 1000;
                        long targetTime = Math.Max(0, _mediaPlayer.Length - thirtySecondsInMs);

                        // 3. 使用 Time 跳转
                        _mediaPlayer.Time = targetTime;

                        // 4. 等待4.5秒让跳转完成，然后显示视频
                        Task.Delay(4500).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                VideoView.Visibility = Visibility.Visible;
                                StatusText.Text = "正在播放实时画面...";
                            });
                        });
                    }
                }));

                // 清除标志，避免重复跳转
                _isLivePlayback = false;
            }
            else
            {
                // (修复!) 确保非实时播放模式下视频是可见的
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    VideoView.Visibility = Visibility.Visible;
                }));
            }
        }

        /// <summary>
        /// (新!) (播放器事件) 当视频播放到末尾时触发
        /// 实时播放模式下，等待5秒后重新查找并播放最新文件
        /// </summary>
        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            // 只有在实时播放模式下才处理
            if (_currentLiveFilePath != null)
            {
                // (重要!) 使用 BeginInvoke 而不是 Invoke，避免死锁
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        Console.WriteLine("=== 播放到文件末尾，等待10秒后查找新文件 ===");
                        StatusText.Text = "已到达文件末尾，等待新文件生成（10秒后重试）...";

                        // 等待10秒，给录制服务时间创建新文件
                        await Task.Delay(10000);

                        Console.WriteLine("=== 10秒已过，现在查找最新文件 ===");

                        // 查找最新的文件
                        string? latestFile = _recordingService.FindLatestLiveFile();

                        if (latestFile == null)
                        {
                            Console.WriteLine("✗ 未找到任何录像文件");
                            StatusText.Text = "实时播放已结束：未找到任何录像文件。";
                            _currentLiveFilePath = null;
                            _liveMonitorTimer?.Stop();
                            return;
                        }

                        Console.WriteLine($"当前文件: {Path.GetFileName(_currentLiveFilePath)}");
                        Console.WriteLine($"最新文件: {Path.GetFileName(latestFile)}");

                        // 无论是不是新文件，都重新播放最新文件
                        // 这样即使文件名相同，也能继续播放（文件在持续增长）
                        if (latestFile != _currentLiveFilePath)
                        {
                            Console.WriteLine("✓ 发现新文件！切换播放");
                            StatusText.Text = $"检测到新文件，正在切换: {Path.GetFileName(latestFile)}...";
                        }
                        else
                        {
                            Console.WriteLine("→ 仍是同一文件，但重新播放以获取最新内容");
                            StatusText.Text = "重新加载当前文件的最新内容...";
                        }

                        _currentLiveFilePath = latestFile;
                        _isLivePlayback = true; // 设置标志，跳转到新文件末尾

                        // 播放（或重新播放）最新文件
                        if (_libVLC != null && _mediaPlayer != null)
                        {
                            var media = new Media(_libVLC, latestFile);
                            media.AddOption(":live-caching=300");
                            _mediaPlayer.Play(media);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"✗ 切换文件失败: {ex.Message}");
                        StatusText.Text = $"切换文件失败: {ex.Message}";
                    }
                }));
            }
        }        /// <summary>
                 /// (播放器事件) 当播放时间改变时 (每秒触发多次) 触发
                 /// </summary>
        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            // 如果用户正在拖动滑块，立即停止，不更新任何内容
            if (_isSliderDragging)
            {
                return;
            }

            // (新!) 如果我们正在等待跳转...
            if (_isSeeking)
            {
                // 检查播放器报告的当前位置是否已经 "接近" 我们的目标位置
                // (使用 0.02 (2%) 作为误差范围)
                if (_mediaPlayer != null && Math.Abs(_mediaPlayer.Position - _targetSeekPosition) < 0.02)
                {
                    // 看起来跳转已经完成！解除标志
                    _isSeeking = false;
                }
                else
                {
                    // 跳转尚未完成 (播放器仍在报告旧时间)，
                    // 立即停止，不要更新滑块的 UI
                    return;
                }
            }

            // (不变) 只有当不在拖动且不在等待跳转时，才更新 UI
            // (_isSeeking 标志在上面刚刚被我们解除)

            // (新!) 节流机制：限制更新频率，避免快进时进度条更新过快
            var now = DateTime.Now;
            if ((now - _lastProgressUpdate).TotalMilliseconds < ProgressUpdateIntervalMs)
            {
                // 距离上次更新时间太短，跳过本次更新
                return;
            }
            _lastProgressUpdate = now;

            Dispatcher.Invoke(() =>
            {
                if (_mediaPlayer != null)
                {
                    PlaybackSlider.Value = _mediaPlayer.Position;
                }

                if (_mediaPlayer != null)
                {
                    // (改进!) 在实时播放模式下，显示实际时间而不是视频时间
                    if (_currentLiveFilePath != null)
                    {
                        // 实时模式：显示当前系统时间
                        string currentTime = DateTime.Now.ToString("HH:mm:ss");
                        TimeText.Text = $"实时播放 - {currentTime}";
                    }
                    else
                    {
                        // 普通模式：显示视频播放时间
                        string timeInfo = $"{FormatTime(e.Time)} / {FormatTime(_mediaPlayer.Length)}";

                        // (新!) 如果正在快进，显示倍速提示
                        if (_isFastForwarding)
                        {
                            TimeText.Text = $"⏩ {timeInfo} (3x)";
                        }
                        else
                        {
                            TimeText.Text = timeInfo;
                        }
                    }
                }
            });
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
                    if (_mediaPlayer.IsPlaying && _mediaPlayer.IsSeekable && !e.IsRepeat && !_isFastForwarding)
                    {
                        _isFastForwarding = true;
                        _mediaPlayer.SetRate(3.0f);
                        StatusText.Text = "⏩ 快进中 (3倍速)...";
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
                if (_isFastForwarding)
                {
                    _mediaPlayer.SetRate(1.0f);
                    _isFastForwarding = false;

                    // 恢复状态文本
                    if (_currentLiveFilePath != null)
                    {
                        StatusText.Text = "正在播放实时画面...";
                    }
                    else
                    {
                        StatusText.Text = "正在播放...";
                    }
                }
                e.Handled = true;
            }

            // (新!) 当"快退"的左键被松开时，停止定时器
            if (e.Key == System.Windows.Input.Key.Left)
            {
                _rewindTimer?.Stop();
                _isRewinding = false;

                // (修复!) 恢复状态文本
                if (_currentLiveFilePath != null)
                {
                    StatusText.Text = "正在播放实时画面...";
                }
                else
                {
                    StatusText.Text = "正在播放...";
                }

                e.Handled = true;
            }
        }
    }
}