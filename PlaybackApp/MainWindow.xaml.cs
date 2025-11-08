using System;
using System.Collections.Generic;
using System.Diagnostics; // 添加 Debug 支持
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

        // (已移除!) _liveMonitorTimer 定时器已被移除，因为它与 EndReached 逻辑冲突
        // private System.Windows.Threading.DispatcherTimer? _liveMonitorTimer;

        // (新!) 添加一个专用的标志来区分RTSP流
        private bool _isRtspPlayback = false;

        // (新!) 记录RTSP播放的开始时间，用于计算已播放时长
        private DateTime _rtspPlaybackStartTime = DateTime.MinValue;

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

            // (关键修改!) 调用辅助函数来创建第一个播放器实例
            InitializeMediaPlayer();

            // 订阅播放器事件 (这部分逻辑已移至 InitializeMediaPlayer)

            // (保持不变) 初始化快退定时器
            _rewindTimer = new System.Windows.Threading.DispatcherTimer();
            _rewindTimer.Interval = TimeSpan.FromMilliseconds(250); // 每 250 毫秒跳转一次
            _rewindTimer.Tick += RewindTimer_Tick;

            // (已移除!) 不再需要初始化 _liveMonitorTimer
        }

        // (新!) 定时器触发的事件
        private void RewindTimer_Tick(object? sender, EventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.IsSeekable)
            {
                // (!!! 关键修复 !!!)
                // 每次快退时都强制清除跳转标志，防止被 TimeChanged 事件干扰
                _isSeeking = false;

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

        // (已移除!) LiveMonitorTimer_Tick 已被移除
        // 整个"伪实时"的切换逻辑现在完全由 MediaPlayer_EndReached 处理

        // 4. (关键改动) 在窗口关闭时释放资源
        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }

        // ============================================================
        // (!!! 关键修复 !!!) 
        // 添加一个新的辅助函数来彻底重建 MediaPlayer
        // ============================================================
        private void InitializeMediaPlayer()
        {
            // 1. (销毁) 如果旧的播放器存在，完全解除绑定并销毁它
            if (_mediaPlayer != null)
            {
                // 解除所有事件绑定，防止内存泄漏
                _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged;
                _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                _mediaPlayer.Playing -= MediaPlayer_Playing;
                _mediaPlayer.EndReached -= MediaPlayer_EndReached;
                _mediaPlayer.EncounteredError -= MediaPlayer_Error; // 解除新错误处理的绑定

                // 停止并销毁
                _mediaPlayer.Stop();
                _mediaPlayer.Dispose();
            }

            // 2. (新建) 创建一个 100% 干净的播放器实例
            _mediaPlayer = new MediaPlayer(_libVLC);

            // 3. (重新绑定) 将所有事件重新绑定到新实例上
            _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            _mediaPlayer.Playing += MediaPlayer_Playing;
            _mediaPlayer.EndReached += MediaPlayer_EndReached;
            _mediaPlayer.EncounteredError += MediaPlayer_Error; // 绑定新的全局错误处理

            // 4. (重新分配) 将新播放器分配给UI
            VideoView.MediaPlayer = _mediaPlayer;
        }

        /// <summary>
        /// (新!) 全局播放器错误处理
        /// </summary>
        private void MediaPlayer_Error(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "❌ 播放器内核遇到错误！";
            });
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
                _isRtspPlayback = false; // <== (新!) 明确清除RTSP标志
                _rtspPlaybackStartTime = DateTime.MinValue; // (!!! 新增 !!!) 重置RTSP开始时间

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

                // (已移除!) 不再需要停止 _liveMonitorTimer
                // _liveMonitorTimer?.Stop();

                if (_libVLC != null && _mediaPlayer != null)
                {
                    // (!!! 关键修复 !!!) 
                    // 用 "重建" 替换 "停止"
                    InitializeMediaPlayer();

                    // (!!! 关键修复 !!!) 
                    // 必须指定 UriKind.Absolute 才能正确加载本地文件
                    var media = new Media(_libVLC, new Uri(clipPath, UriKind.Absolute));
                    media.AddOption(":no-loop");
                    media.AddOption(":no-repeat");

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
        /// (!!! 重要 !!!) 
        /// 由于使用 faststart 格式，正在录制的文件无法播放。
        /// 此功能会播放"最新的已完成录制的文件"（通常是倒数第二个文件）。
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
                _isRtspPlayback = false; // <== (新!) 明确清除RTSP标志

                // (已移除!) 不再需要启动 _liveMonitorTimer
                // _liveMonitorTimer?.Start();

                // 创建 Media 对象并添加低延迟选项
                // (!!! 关键修复 !!!) 
                // 必须指定 UriKind.Absolute 才能正确加载本地文件
                var media = new Media(_libVLC, new Uri(latestFile, UriKind.Absolute));
                media.AddOption(":live-caching=300");

                // (!!! 关键修复 2 !!!) 
                // 用 "重建" 替换 "停止"
                InitializeMediaPlayer();

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

        /// <summary>
        /// "真实时 RTSP 播放" 按钮的点击事件
        /// 直接连接 RTSP 流，无需文件轮询，实现真正的实时播放
        /// </summary>
        private void BtnPlayRtsp_Click(object sender, RoutedEventArgs e)
        {
            // 检查播放器是否已初始化
            if (_libVLC == null || _mediaPlayer == null)
            {
                StatusText.Text = "错误：播放器未初始化";
                return;
            }

            StatusText.Text = "正在连接 RTSP 实时流...";

            // (已移除!) 停止所有基于文件的旧逻辑
            // _liveMonitorTimer?.Stop();
            _currentLiveFilePath = null;
            _isLivePlayback = false;
            _isRtspPlayback = true; // <== (新!) 明确设置RTSP标志

            try
            {
                // 定义 RTSP 流地址
                // 注意: 如果 WPF 客户端和 MediaMTX 服务器不在同一台电脑，
                // 请将 "localhost" 替换为服务器的 IP 地址
                string rtspUrl = "rtsp://localhost:8554/live_stream";

                StatusText.Text = $"正在连接: {rtspUrl}";

                // (移除这里的自定义错误事件监听)
                // 因为 InitializeMediaPlayer() 已经添加了一个全局的

                // 创建 Media 对象 - 使用更兼容的方式
                var media = new Media(_libVLC, new Uri(rtspUrl));

                // 为网络流添加特定的低延迟选项
                media.AddOption(":network-caching=1000");  // 增加到 1000ms 确保稳定
                media.AddOption(":rtsp-tcp");              // 强制使用 TCP 连接
                media.AddOption(":live-caching=1000");     // 实时流缓冲

                // (调试!) 添加详细日志
                media.AddOption("--verbose=2");

                // (!!! 关键修复 !!!) 
                // 用 "重建" 替换 "停止"
                InitializeMediaPlayer();

                // 直接播放 RTSP 流
                _mediaPlayer.Play(media);

                // (修复!) 不要立即显示"已连接"，等待 Playing 事件
                StatusText.Text = "⏳ 正在缓冲 RTSP 流...";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"RTSP 播放失败: {ex.Message}";
                MessageBox.Show(
                    $"无法连接到 RTSP 流。\n\n" +
                    $"错误信息: {ex.Message}\n\n" +
                    $"请确保：\n" +
                    $"1. MediaMTX 服务器正在运行 (运行 start_rtsp_server.bat)\n" +
                    $"2. CaptureService 正在推流\n" +
                    $"3. 防火墙允许端口 8554",
                    "连接错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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

                // (!!! 关键修复 !!!)
                // 4. 添加一个保险机制：500ms 后自动清除 _isSeeking 标志
                // 这样即使位置检查失败，也不会永久卡住
                Task.Delay(500).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_isSeeking)
                        {
                            _isSeeking = false;
                        }
                    });
                });
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
        /// (播放器事件) 当播放开始时触发
        /// (!!! 关键修改 !!!)
        /// 由于现在使用普通 MP4 格式（faststart），文件从头到尾都是完整可播放的。
        /// 不再需要跳转到末尾的操作，直接从头播放即可。
        /// </summary>
        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            // 模式 1: "伪实时" (文件轮询) - (!!! 简化 !!!)
            if (_isLivePlayback)
            {
                // (!!! 关键修改 !!!) 
                // 使用普通 MP4 后，不需要跳转，直接从头播放
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    VideoView.Visibility = Visibility.Visible;
                    StatusText.Text = "正在播放最新的已完成录像...";
                }));

                // 重置标志
                _isLivePlayback = false;
            }
            // 模式 2: "真实时" (RTSP流)
            else if (_isRtspPlayback)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    VideoView.Visibility = Visibility.Visible;
                    StatusText.Text = "🟢 RTSP 实时流播放中 (亚秒级延迟)";

                    // (!!! 新增 !!!) 记录RTSP播放开始时间
                    _rtspPlaybackStartTime = DateTime.Now;
                }));
            }
            // 模式 3: "历史回放" (剪辑文件)
            else
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    VideoView.Visibility = Visibility.Visible;
                    StatusText.Text = "正在播放剪辑文件...";
                }));
            }
        }

        // ===================================================================
        // (!!! 关键修改 !!!) 
        // 整个"伪实时"切换逻辑的核心
        // ===================================================================
        /// <summary>
        /// (播放器事件) 当视频播放到末尾时触发
        /// (已重构) 
        /// 1. 检查是否处于"伪实时"模式 (_currentLiveFilePath != null)
        /// 2. 如果是，则进入一个"等待循环"
        /// 3. 循环中每 5 秒检查一次是否有 *新* 的可播放文件
        /// 4. 如果没有新文件（即最新文件仍是我们刚播完的那个），则继续等待
        /// 5. 如果发现了 *新* 文件，则跳出循环，播放新文件
        /// </summary>
        private async void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            // 1. 检查是否处于"伪实时"模式
            if (_currentLiveFilePath == null)
            {
                // 不在伪实时模式（例如只是播放剪辑），什么都不做
                return;
            }

            // (重要!) 使用 BeginInvoke 避免UI线程死锁
            await Dispatcher.BeginInvoke(new Action(async () =>
            {
                string fileThatJustFinished = _currentLiveFilePath;
                string? newPlayableFile = null;

                try
                {
                    StatusText.Text = $"播放完毕: {Path.GetFileName(fileThatJustFinished)}。正在等待新文件...";
                    Console.WriteLine($"=== EndReached: {Path.GetFileName(fileThatJustFinished)} 播放完毕。进入等待循环... ===");

                    // 2. 进入"等待循环"
                    while (true)
                    {
                        // 3. 等待 5 秒
                        await Task.Delay(5000);

                        // 4. 检查最新的 *可播放* 文件
                        string? latestFile = _recordingService.FindLatestLiveFile();

                        if (latestFile == null)
                        {
                            // 极端情况：所有文件都被删除了？
                            Console.WriteLine("✗ EndReached: 找不到任何文件。停止实时循环。");
                            StatusText.Text = "实时播放已结束：未找到任何录像文件。";
                            _currentLiveFilePath = null; // 退出实时模式
                            return;
                        }

                        // 5. 检查这个最新文件是否 *不同于* 我们刚播完的那个
                        if (latestFile != fileThatJustFinished)
                        {
                            // 6. 找到了一个 *新* 文件！
                            Console.WriteLine($"✓ EndReached: 发现新文件！切换播放: {Path.GetFileName(latestFile)}");
                            newPlayableFile = latestFile;
                            break; // 跳出 while(true) 循环
                        }
                        else
                        {
                            // 7. 还是同一个文件，继续等待
                            Console.WriteLine($"→ EndReached: 最新文件仍是 {Path.GetFileName(latestFile)}。继续等待 5 秒...");
                            // 循环将继续
                        }
                    }

                    // 8. (只有在 break 后才会到这里) 播放新文件
                    if (newPlayableFile != null && _libVLC != null && _mediaPlayer != null)
                    {
                        StatusText.Text = $"检测到新文件，正在切换: {Path.GetFileName(newPlayableFile)}...";

                        _currentLiveFilePath = newPlayableFile;
                        _isLivePlayback = true; // 设置标志，以便 MediaPlayer_Playing 显示正确消息

                        var media = new Media(_libVLC, new Uri(newPlayableFile, UriKind.Absolute));
                        media.AddOption(":live-caching=300");

                        // (!!! 关键修复 !!!) 用 "重建" 替换 "停止"
                        InitializeMediaPlayer();

                        _mediaPlayer.Play(media);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ EndReached 切换文件失败: {ex.Message}");
                    StatusText.Text = $"切换文件失败: {ex.Message}";
                    _currentLiveFilePath = null; // 发生错误，退出实时模式
                }

            })); // 结束 BeginInvoke
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

            // (!!! 关键修复 !!!)
            // 如果正在快进或快退，完全跳过 _isSeeking 检查和节流机制
            // 因为快进/快退是"主动操作"，必须立即响应
            bool isActiveControl = _isFastForwarding || _isRewinding;

            // (新!) 如果我们正在等待跳转...
            if (_isSeeking && !isActiveControl)
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
            }            // (不变) 只有当不在拖动且不在等待跳转时，才更新 UI
            // (_isSeeking 标志在上面刚刚被我们解除)

            // (新!) 节流机制：限制更新频率，避免快进时进度条更新过快
            // (!!! 修复 !!!) 但在快进/快退时不要节流
            if (!isActiveControl)
            {
                var now = DateTime.Now;
                if ((now - _lastProgressUpdate).TotalMilliseconds < ProgressUpdateIntervalMs)
                {
                    // 距离上次更新时间太短，跳过本次更新
                    return;
                }
                _lastProgressUpdate = now;
            }

            Dispatcher.Invoke(() =>
            {
                if (_mediaPlayer != null)
                {
                    PlaybackSlider.Value = _mediaPlayer.Position;
                }

                if (_mediaPlayer != null)
                {
                    // (!!! 关键修改 !!!) 根据不同的播放模式显示不同的时间格式

                    // 模式 1: RTSP 实时播放 - 显示已播放时长
                    if (_isRtspPlayback && _rtspPlaybackStartTime != DateTime.MinValue)
                    {
                        TimeSpan elapsed = DateTime.Now - _rtspPlaybackStartTime;
                        TimeText.Text = $"已播放 {FormatTime((long)elapsed.TotalMilliseconds)}";
                    }
                    // 模式 2: 伪实时播放 - 显示当前时间/总时长
                    else if (_currentLiveFilePath != null)
                    {
                        string timeInfo = $"{FormatTime(e.Time)} / {FormatTime(_mediaPlayer.Length)}";

                        // 如果正在快进，显示倍速提示
                        if (_isFastForwarding)
                        {
                            TimeText.Text = $"⏩ {timeInfo} (3x)";
                        }
                        else
                        {
                            TimeText.Text = timeInfo;
                        }
                    }
                    // 模式 3: 普通回放 - 显示当前时间/总时长
                    else
                    {
                        string timeInfo = $"{FormatTime(e.Time)} / {FormatTime(_mediaPlayer.Length)}";

                        // 如果正在快进，显示倍速提示
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
            // (!!! 关键修复 !!!)
            // 检查焦点是否在可编辑的输入控件上 (例如 DateTimePicker, TextBox 等)
            // 如果是，则不处理快捷键，让控件自己处理
            if (IsInputFocused())
            {
                return; // 直接返回，不标记 e.Handled，让控件正常处理
            }

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
                        // (!!! 关键修复 !!!)
                        // 清除跳转标志，防止被 TimeChanged 阻塞
                        _isSeeking = false;

                        _isFastForwarding = true;
                        _mediaPlayer.SetRate(3.0f);
                        StatusText.Text = "⏩ 快进中 (3倍速)...";
                    }
                    e.Handled = true;
                    break;                // (新!) 功能3: 左键快退 (使用定时器)
                case System.Windows.Input.Key.Left:
                    // 仅在 *第一次* 按下时启动定时器
                    // 移除 IsPlaying 检查，允许在暂停状态下也能快退
                    if (_mediaPlayer.IsSeekable && !_isRewinding && !e.IsRepeat)
                    {
                        // (!!! 关键修复 !!!)
                        // 清除跳转标志，防止被 TimeChanged 阻塞
                        _isSeeking = false;

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
        /// (!!! 新增 !!!)
        /// 检查当前焦点是否在可编辑的输入控件上
        /// </summary>
        private bool IsInputFocused()
        {
            var focusedElement = System.Windows.Input.FocusManager.GetFocusedElement(this);

            // 检查是否是文本输入控件
            if (focusedElement is System.Windows.Controls.TextBox)
            {
                return true;
            }

            // 检查是否是 DateTimePicker 或其内部控件
            // DateTimePicker 内部包含 TextBox，需要向上遍历可视树查找
            var parent = focusedElement as DependencyObject;
            while (parent != null)
            {
                // 检查类型名称是否包含 DateTimePicker
                if (parent.GetType().Name.Contains("DateTimePicker"))
                {
                    return true;
                }
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }

            return false;
        }

        /// <summary>
        /// 当按键被松开时触发
        /// </summary>
        private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // (!!! 关键修复 !!!)
            // 同样需要检查焦点，如果在输入控件上则跳过处理
            if (IsInputFocused())
            {
                return;
            }

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