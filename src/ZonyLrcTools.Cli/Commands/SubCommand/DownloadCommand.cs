using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZonyLrcTools.Cli.Infrastructure.Tag;
using ZonyLrcTools.Common;
using ZonyLrcTools.Common.Album;
using ZonyLrcTools.Common.Configuration;
using ZonyLrcTools.Common.Infrastructure.Exceptions;
using ZonyLrcTools.Common.Infrastructure.Extensions;
using ZonyLrcTools.Common.Infrastructure.IO;
using ZonyLrcTools.Common.Infrastructure.Threading;
using ZonyLrcTools.Common.Lyrics;
using File = System.IO.File;

namespace ZonyLrcTools.Cli.Commands.SubCommand
{
    [Command("download", Description = "下载歌词文件或专辑图像。")]
    public class DownloadCommand : ToolCommandBase
    {
        private readonly ILogger<DownloadCommand> _logger;
        private readonly IFileScanner _fileScanner;
        private readonly ITagLoader _tagLoader;
        private readonly IEnumerable<ILyricsProvider> _lyricDownloaderList;
        private readonly IEnumerable<IAlbumDownloader> _albumDownloaderList;

        private readonly GlobalOptions _options;

        public DownloadCommand(ILogger<DownloadCommand> logger,
            IFileScanner fileScanner,
            IOptions<GlobalOptions> options,
            ITagLoader tagLoader,
            IEnumerable<ILyricsProvider> lyricDownloaderList,
            IEnumerable<IAlbumDownloader> albumDownloaderList)
        {
            _logger = logger;
            _fileScanner = fileScanner;
            _tagLoader = tagLoader;
            _lyricDownloaderList = lyricDownloaderList;
            _albumDownloaderList = albumDownloaderList;
            _options = options.Value;
        }

        #region > Options <

        [Option("-d|--dir", CommandOptionType.SingleValue, Description = "指定需要扫描的目录。")]
        [DirectoryExists]
        public string SongsDirectory { get; set; }

        [Option("-l|--lyric", CommandOptionType.NoValue, Description = "指定程序需要下载歌词文件。")]
        public bool DownloadLyric { get; set; }

        [Option("-a|--album", CommandOptionType.NoValue, Description = "指定程序需要下载专辑图像。")]
        public bool DownloadAlbum { get; set; }

        [Option("-n|--number", CommandOptionType.SingleValue, Description = "指定下载时候的线程数量。(默认值 2)")]
        public int ParallelNumber { get; set; } = 2;

        [Option] public string ErrorMessage { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "error.log");

        #endregion

        protected override async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            if (DownloadLyric)
            {
                await DownloadLyricFilesAsync(
                    await LoadMusicInfoAsync(
                        RemoveExistLyricFiles(
                            await ScanMusicFilesAsync())));
            }

            if (DownloadAlbum)
            {
                await DownloadAlbumAsync(
                    await LoadMusicInfoAsync(
                        await ScanMusicFilesAsync()));
            }

            return 0;
        }

        private async Task<List<string>> ScanMusicFilesAsync()
        {
            var files = (await _fileScanner.ScanAsync(SongsDirectory, _options.SupportFileExtensions))
                .SelectMany(t => t.FilePaths)
                .ToList();

            if (files.Count == 0)
            {
                _logger.LogError("没有找到任何音乐文件。");
                throw new ErrorCodeException(ErrorCodes.NoFilesWereScanned);
            }

            _logger.LogInformation($"已经扫描到了 {files.Count} 个音乐文件。");

            return files;
        }

        private List<string> RemoveExistLyricFiles(List<string> filePaths)
        {
            if (!_options.Provider.Lyric.Config.IsSkipExistLyricFiles)
            {
                return filePaths;
            }

            return filePaths
                .Where(path =>
                {
                    if (!File.Exists(Path.ChangeExtension(path, ".lrc")))
                    {
                        return true;
                    }

                    _logger.LogWarning($"已经存在歌词文件 {path}，跳过。");
                    return false;
                })
                .ToList();
        }

        private async Task<ImmutableList<MusicInfo>> LoadMusicInfoAsync(IReadOnlyCollection<string> files)
        {
            _logger.LogInformation("开始加载音乐文件的标签信息...");

            var warpTask = new WarpTask(ParallelNumber);
            var warpTaskList = files.Select(file => warpTask.RunAsync(() => Task.Run(async () => await _tagLoader.LoadTagAsync(file))));
            var result = (await Task.WhenAll(warpTaskList))
                .Where(m => m != null)
                .Where(m => !string.IsNullOrEmpty(m.Name) || !string.IsNullOrEmpty(m.Artist))
                .ToList();

            // Load music total time info.
            // result.Foreach(m => { m.TotalTime = (long?)new AudioFileReader(m.FilePath).TotalTime.TotalMilliseconds; });

            _logger.LogInformation($"已成功加载 {files.Count} 个音乐文件的标签信息。");

            return result.ToImmutableList();
        }

        private IEnumerable<ILyricsProvider> GetLyricDownloaderList()
        {
            var downloader = _options.Provider.Lyric.Plugin
                .Where(op => op.Priority != -1)
                .OrderBy(op => op.Priority)
                .Join(_lyricDownloaderList,
                    op => op.Name,
                    loader => loader.DownloaderName,
                    (op, loader) => loader);

            return downloader;
        }

        #region > Lyric download logic <

        private async ValueTask DownloadLyricFilesAsync(ImmutableList<MusicInfo> musicInfos)
        {
            _logger.LogInformation("开始下载歌词文件数据...");

            var downloaderList = GetLyricDownloaderList();
            var warpTask = new WarpTask(ParallelNumber);
            var warpTaskList = musicInfos.Select(info =>
                warpTask.RunAsync(() => Task.Run(async () => await DownloadLyricTaskLogicAsync(downloaderList, info))));

            await Task.WhenAll(warpTaskList);

            _logger.LogInformation($"歌词数据下载完成，成功: {musicInfos.Count(m => m.IsSuccessful)} 失败{musicInfos.Count(m => m.IsSuccessful == false)}。");
        }

        private async Task DownloadLyricTaskLogicAsync(IEnumerable<ILyricsProvider> downloaderList, MusicInfo info)
        {
            async Task InternalDownloadLogicAsync(ILyricsProvider downloader)
            {
                try
                {
                    var lyric = await downloader.DownloadAsync(info.Name, info.Artist, info.TotalTime);
                    var lyricFilePath = Path.Combine(Path.GetDirectoryName(info.FilePath)!,
                        $"{Path.GetFileNameWithoutExtension(info.FilePath)}.lrc");

                    if (File.Exists(lyricFilePath))
                    {
                        File.Delete(lyricFilePath);
                    }

                    info.IsSuccessful = true;

                    if (lyric.IsPruneMusic)
                    {
                        return;
                    }

                    await using var stream = new FileStream(lyricFilePath, FileMode.Create);
                    await using var sw = new BinaryWriter(stream);

                    sw.Write(EncodingConvert(lyric));
                    await stream.FlushAsync();
                }
                catch (ErrorCodeException ex)
                {
                    info.IsSuccessful = ex.ErrorCode == ErrorCodes.NoMatchingSong;

                    _logger.LogWarningInfo(ex);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"下载歌词文件时发生错误：{ex.Message}，歌曲名: {info.Name}，歌手: {info.Artist}。");
                    info.IsSuccessful = false;
                }
            }

            foreach (var downloader in downloaderList)
            {
                await InternalDownloadLogicAsync(downloader);

                if (info.IsSuccessful)
                {
                    _logger.LogSuccessful(info);
                    return;
                }
            }
        }

        private byte[] EncodingConvert(LyricItemCollection lyric)
        {
            var supportEncodings = Encoding.GetEncodings();
            if (supportEncodings.All(x => x.Name != _options.Provider.Lyric.Config.FileEncoding))
            {
                throw new ErrorCodeException(ErrorCodes.NotSupportedFileEncoding);
            }

            return Encoding.Convert(Encoding.UTF8, Encoding.GetEncoding(_options.Provider.Lyric.Config.FileEncoding), lyric.GetUtf8Bytes());
        }

        #endregion

        #region > Ablum image download logic <

        private async ValueTask DownloadAlbumAsync(ImmutableList<MusicInfo> musicInfos)
        {
            _logger.LogInformation("开始下载专辑图像数据...");

            var downloader = _albumDownloaderList.FirstOrDefault(d => d.DownloaderName == InternalAlbumDownloaderNames.NetEase);
            var warpTask = new WarpTask(ParallelNumber);
            var warpTaskList = musicInfos.Select(info =>
                warpTask.RunAsync(() => Task.Run(async () => await DownloadAlbumTaskLogicAsync(downloader, info))));

            await Task.WhenAll(warpTaskList);

            _logger.LogInformation($"专辑数据下载完成，成功: {musicInfos.Count(m => m.IsSuccessful)} 失败{musicInfos.Count(m => m.IsSuccessful == false)}。");
        }

        private async Task DownloadAlbumTaskLogicAsync(IAlbumDownloader downloader, MusicInfo info)
        {
            _logger.LogSuccessful(info);

            try
            {
                var album = await downloader.DownloadAsync(info.Name, info.Artist);
                var filePath = Path.Combine(Path.GetDirectoryName(info.FilePath)!, $"{Path.GetFileNameWithoutExtension(info.FilePath)}.png");
                if (File.Exists(filePath) || album.Length <= 0)
                {
                    return;
                }

                await new FileStream(filePath, FileMode.Create).WriteBytesToFileAsync(album, 1024);
            }
            catch (ErrorCodeException ex)
            {
                _logger.LogWarningInfo(ex);
            }
        }

        #endregion
    }
}