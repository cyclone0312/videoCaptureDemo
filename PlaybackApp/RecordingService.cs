// RecordingService.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FFMpegCore;

namespace PlaybackApp
{
    /// <summary>
    /// 专门负责处理录像文件的查找、解析和剪辑。
    /// 这个类不依赖任何UI元素或播放器。
    /// </summary>
    public class RecordingService
    {
        private readonly string _videoDirectory;

        public RecordingService(string videoDirectory)
        {
            _videoDirectory = videoDirectory;
        }

        /// <summary>
        /// 查找最新的实时文件。
        /// (改进!) 使用文件名中的时间戳排序，而不是 LastWriteTime
        /// </summary>
        public string? FindLatestLiveFile()
        {
            if (!Directory.Exists(_videoDirectory))
            {
                return null;
            }

            var directory = new DirectoryInfo(_videoDirectory);
            var files = directory.GetFiles("CAM_USB-*.mp4")
                                 .OrderByDescending(f => f.Name) // (关键!) 按文件名排序，文件名包含时间戳
                                 .ToList();

            // (调试) 打印所有文件
            Console.WriteLine("=== 查找最新文件 (按文件名排序) ===");
            foreach (var file in files.Take(3)) // 只显示前3个
            {
                Console.WriteLine($"  {file.Name}");
                Console.WriteLine($"    创建时间: {file.CreationTime}");
                Console.WriteLine($"    修改时间: {file.LastWriteTime}");
                Console.WriteLine($"    文件大小: {file.Length / 1024 / 1024} MB");
            }

            var latestFile = files.FirstOrDefault();
            if (latestFile != null)
            {
                Console.WriteLine($"✓ 选中: {latestFile.Name}");
            }

            return latestFile?.FullName;
        }

        /// <summary>
        /// 核心函数：根据时间范围生成一个可播放的剪辑文件
        /// </summary>
        /// <param name="progress">用于向调用者(UI)报告进度的机制</param>
        /// <returns>临时剪辑文件的路径，如果失败则返回 null</returns>
        public async Task<string?> GeneratePlaybackClipAsync(DateTime startTime, DateTime endTime, IProgress<string> progress)
        {
            // 1. 找到 "开始时间" 和 "结束时间" 对应的文件
            string? sourceFile = FindFileForTime(startTime);
            string? endFile = FindFileForTime(endTime);

            if (sourceFile == null || endFile == null)
            {
                progress.Report("错误：未能在目录中找到对应的视频文件。");
                return null;
            }

            // --- 案例 A: 简单情况 (开始和结束在同一个文件) ---
            if (sourceFile == endFile)
            {
                progress.Report("正在剪辑单个文件...");

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
                    progress.Report("剪辑完成！");
                    return tempClipPath;
                }
            }

            // --- 案例 B: 高级情况 (跨越多个文件) ---
            else
            {
                progress.Report("检测到跨文件，正在合并多个片段...");

                // 1. 创建一个临时的"工作目录"来存放所有剪辑片段
                string workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(workDir);

                try
                {
                    // 2. 获取所有文件，并按名称排序 (确保时间顺序)
                    var allFiles = Directory.GetFiles(_videoDirectory, "CAM_USB-*.mp4")
                                            .OrderBy(f => f)
                                            .ToList();

                    int startIndex = allFiles.IndexOf(sourceFile);
                    int endIndex = allFiles.IndexOf(endFile);

                    if (startIndex == -1 || endIndex == -1)
                    {
                        progress.Report("错误：无法在文件列表中定位索引。");
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

                        progress.Report($"正在处理片段 {i + 1}/{filesToProcess.Count}...");

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
                    progress.Report("正在合并所有片段...");
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
                        progress.Report("剪辑完成！");
                        return finalClipPath;
                    }
                }
                catch (Exception ex)
                {
                    progress.Report($"多文件合并失败: {ex.Message}");
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
        /// 辅助函数：查找指定时间点所在的录像文件
        /// </summary>
        public string? FindFileForTime(DateTime requestedTime)
        {
            if (!Directory.Exists(_videoDirectory)) return null;

            var files = Directory.GetFiles(_videoDirectory, "CAM_USB-*.mp4");

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
    }
}
