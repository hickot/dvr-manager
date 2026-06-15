using System.Collections.Concurrent;
using System.Diagnostics;
using DvrManager.Models;
using Microsoft.Extensions.Options;

namespace DvrManager.Services;

public sealed class RecordingManager : IDisposable
{
    private sealed record Session(Process Process, DateOnly Date, DateTimeOffset StartedAt, string Folder);

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _errors = new();
    private readonly ConcurrentDictionary<Guid, string> _cameraStoragePaths = new();
    private readonly DvrOptions _options;
    private readonly string _storagePath;
    private readonly string _ffmpegPath;
    private readonly ILogger<RecordingManager> _logger;

    public RecordingManager(IOptions<DvrOptions> options, IHostEnvironment environment, ILogger<RecordingManager> logger)
    {
        _options = options.Value;
        _storagePath = Path.GetFullPath(_options.StoragePath, environment.ContentRootPath);
        _ffmpegPath = ResolveFfmpegPath(_options.FfmpegPath);
        _logger = logger;
        Directory.CreateDirectory(_storagePath);
    }

    public async Task StartAsync(Camera camera)
    {
        await StopAsync(camera.Id);
        _errors.TryRemove(camera.Id, out _);

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var cameraFolder = SanitizeFileName($"{camera.Name}_{camera.Id:N}");
            var cameraStoragePath = string.IsNullOrWhiteSpace(camera.StoragePath)
                ? Path.Combine(_storagePath, cameraFolder)
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(camera.StoragePath));
            if (string.IsNullOrWhiteSpace(camera.StoragePath))
                _cameraStoragePaths.TryRemove(camera.Id, out _);
            else
                _cameraStoragePaths[camera.Id] = cameraStoragePath;
            var folder = Path.Combine(cameraStoragePath, today.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(folder);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("warning");
            startInfo.ArgumentList.Add("-fflags");
            startInfo.ArgumentList.Add("+genpts");
            startInfo.ArgumentList.Add("-use_wallclock_as_timestamps");
            startInfo.ArgumentList.Add("1");
            if (camera.RtspTransport is "tcp" or "udp")
            {
                startInfo.ArgumentList.Add("-rtsp_transport");
                startInfo.ArgumentList.Add(camera.RtspTransport);
            }
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(BuildStreamUrl(camera));
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0?");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0?");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("64k");
            startInfo.ArgumentList.Add("-avoid_negative_ts");
            startInfo.ArgumentList.Add("make_zero");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("segment");
            startInfo.ArgumentList.Add("-segment_time");
            startInfo.ArgumentList.Add((camera.SegmentMinutes * 60).ToString());
            startInfo.ArgumentList.Add("-reset_timestamps");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-strftime");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-segment_format_options");
            startInfo.ArgumentList.Add("movflags=+frag_keyframe+empty_moov+default_base_moof");
            startInfo.ArgumentList.Add(Path.Combine(folder, "%H-%M-%S.mp4"));

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => HandleExit(camera.Id, process);

            if (!process.Start()) throw new InvalidOperationException("O FFmpeg não pôde ser iniciado.");
            _sessions[camera.Id] = new Session(process, today, DateTimeOffset.Now, folder);
            _ = DrainOutputAsync(process);
        }
        catch (Exception exception)
        {
            _errors[camera.Id] = exception.Message;
            _logger.LogError(exception, "Falha ao iniciar gravação da câmera {CameraId}", camera.Id);
        }
    }

    public async Task StopAsync(Guid cameraId)
    {
        if (!_sessions.TryRemove(cameraId, out var session)) return;

        try
        {
            if (!session.Process.HasExited)
            {
                await session.Process.StandardInput.WriteLineAsync("q");
                await session.Process.StandardInput.FlushAsync();

                try
                {
                    await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    session.Process.Kill(entireProcessTree: true);
                    await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Falha ao encerrar gravação da câmera {CameraId}", cameraId);
        }
        finally { session.Process.Dispose(); }
    }

    public RecordingStatus GetStatus(Guid cameraId)
    {
        if (_sessions.TryGetValue(cameraId, out var session) && !session.Process.HasExited)
            return new RecordingStatus("recording", session.StartedAt, session.Folder);

        return _errors.TryGetValue(cameraId, out var error)
            ? new RecordingStatus("error", Error: error)
            : new RecordingStatus("stopped");
    }

    public SystemStatus GetSystemStatus()
    {
        var bytes = EnumerateVideoFiles().Sum(file => file.Length);
        return new SystemStatus(
            _storagePath,
            Math.Round(bytes / 1024d / 1024d / 1024d, 3),
            _options.MaxStorageGb,
            _sessions.Count(pair => !pair.Value.Process.HasExited),
            IsFfmpegAvailable());
    }

    public IReadOnlyList<Guid> GetSessionsFromPreviousDays() => _sessions
        .Where(pair => pair.Value.Date != DateOnly.FromDateTime(DateTime.Now))
        .Select(pair => pair.Key)
        .ToList();

    public void RegisterStoragePaths(IEnumerable<Camera> cameras)
    {
        foreach (var camera in cameras)
        {
            var cameraFolder = SanitizeFileName($"{camera.Name}_{camera.Id:N}");
            var path = string.IsNullOrWhiteSpace(camera.StoragePath)
                ? Path.Combine(_storagePath, cameraFolder)
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(camera.StoragePath));
            if (string.IsNullOrWhiteSpace(camera.StoragePath))
                _cameraStoragePaths.TryRemove(camera.Id, out _);
            else
                _cameraStoragePaths[camera.Id] = path;
        }
    }

    public void EnforceStorageLimit()
    {
        if (_options.MaxStorageGb <= 0) return;
        var maxBytes = _options.MaxStorageGb * 1024d * 1024d * 1024d;
        var files = EnumerateVideoFiles().OrderBy(file => file.CreationTimeUtc).ToList();
        var totalBytes = files.Sum(file => file.Length);

        foreach (var file in files)
        {
            if (totalBytes <= maxBytes) break;
            try
            {
                var length = file.Length;
                file.Delete();
                totalBytes -= length;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Não foi possível remover o vídeo antigo {File}", file.FullName);
            }
        }
    }

    private IEnumerable<FileInfo> EnumerateVideoFiles()
    {
        var paths = _cameraStoragePaths.Values.Append(_storagePath).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*.mp4", SearchOption.AllDirectories); }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Não foi possível ler o diretório de vídeos {Path}", path);
                continue;
            }

            foreach (var file in files)
                yield return new FileInfo(file);
        }
    }

    private void HandleExit(Guid cameraId, Process process)
    {
        if (_sessions.TryGetValue(cameraId, out var current) && ReferenceEquals(current.Process, process))
        {
            _sessions.TryRemove(cameraId, out _);
            if (process.ExitCode != 0 && !_errors.ContainsKey(cameraId))
                _errors[cameraId] = $"FFmpeg encerrado com código {process.ExitCode}.";
        }
    }

    private async Task DrainOutputAsync(Process process)
    {
        try
        {
            var error = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(error))
                _logger.LogWarning("FFmpeg: {Output}", error.Trim());
        }
        catch (Exception exception) { _logger.LogDebug(exception, "Saída do FFmpeg encerrada."); }
    }

    private bool IsFfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            return process is not null && process.WaitForExit(2000) && process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string BuildStreamUrl(Camera camera)
    {
        if (string.IsNullOrEmpty(camera.Username)) return camera.RtspUrl;
        var uri = new UriBuilder(camera.RtspUrl) { UserName = camera.Username, Password = camera.Password };
        return uri.Uri.AbsoluteUri;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            name = name.Replace(invalidCharacter, '_');
        return name;
    }

    private static string ResolveFfmpegPath(string configuredPath)
    {
        if (!string.Equals(configuredPath, "ffmpeg", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
            return configuredPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesFolder = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packagesFolder)) return configuredPath;

        try
        {
            return Directory.EnumerateFiles(packagesFolder, "ffmpeg.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? configuredPath;
        }
        catch
        {
            return configuredPath;
        }
    }

    public void Dispose()
    {
        foreach (var cameraId in _sessions.Keys) StopAsync(cameraId).GetAwaiter().GetResult();
    }
}
