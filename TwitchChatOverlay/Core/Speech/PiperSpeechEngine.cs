using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Speech;

/// <summary>
/// Runs piper.exe as a child process: text in on stdin, WAV out into a temporary file.
/// Local synthesis means message text never leaves the machine, which is why this is the
/// default engine.
///
/// The audio deliberately does NOT come back over stdout. piper.exe leaves its stdout in
/// Windows text mode, so the CRT inserts a 0x0D before every 0x0A byte of the PCM stream —
/// measured on ru_RU-denis-medium: 621 CR-LF pairs in the piped stream versus 3 in the same
/// audio written to a file. The result still plays, which is the trap: it sounds like noise
/// with the rhythm of speech rather than failing outright. Writing to a file bypasses the
/// translation entirely.
/// </summary>
public sealed class PiperSpeechEngine : ISpeechEngine
{
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<PiperSpeechEngine> _logger;

    public PiperSpeechEngine(ILogger<PiperSpeechEngine> logger)
    {
        _logger = logger;
    }

    public SpeechEngineType EngineType => SpeechEngineType.Piper;

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "Resources", "piper", "piper.exe");

    public bool IsAvailable => File.Exists(ExecutablePath) && HasAnyModel();

    public string? UnavailableReason
    {
        get
        {
            if (!File.Exists(ExecutablePath))
            {
                return $"Не найден piper.exe в папке {Path.GetDirectoryName(ExecutablePath)}";
            }

            return HasAnyModel() ? null : $"Не найдены модели голосов (.onnx) в папке {VoiceCatalog.VoicesDirectory}";
        }
    }

    private static bool HasAnyModel() =>
        Directory.Exists(VoiceCatalog.VoicesDirectory) &&
        Directory.EnumerateFiles(VoiceCatalog.VoicesDirectory, "*.onnx", SearchOption.AllDirectories).Any();

    public async Task<byte[]> SynthesizeAsync(string text, VoiceProfile profile, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(UnavailableReason ?? "Piper недоступен");
        }

        // Piper expresses speed as length_scale, the inverse of a rate multiplier:
        // rate 2.0 (twice as fast) → length_scale 0.5.
        var lengthScale = (1.0 / Math.Clamp(profile.Rate, 0.5, 2.0)).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var outputPath = Path.Combine(Path.GetTempPath(), $"tco-piper-{Guid.NewGuid():N}.wav");

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            // piper resolves espeak-ng-data relative to its working directory.
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(profile.Voice.Id);
        startInfo.ArgumentList.Add("--length_scale");
        startInfo.ArgumentList.Add(lengthScale);
        startInfo.ArgumentList.Add("--output_file");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SynthesisTimeout);
        var token = timeoutCts.Token;

        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить процесс piper.exe");
        }

        try
        {
            // Both pipes still have to be drained or a chatty run can fill the buffer and
            // deadlock the child, even though neither carries the audio any more.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            await process.StandardInput.WriteLineAsync(text.AsMemory(), token);
            process.StandardInput.Close();

            await stdoutTask;
            var stderr = await stderrTask;
            await process.WaitForExitAsync(token);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"piper.exe завершился с кодом {process.ExitCode}: {stderr}");
            }

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException($"piper.exe не создал файл с аудио. {stderr}");
            }

            var wav = await File.ReadAllBytesAsync(outputPath, ct);
            if (wav.Length == 0)
            {
                throw new InvalidOperationException("piper.exe вернул пустой файл");
            }

            return wav;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new TimeoutException($"Синтез Piper превысил {SynthesisTimeout.TotalSeconds:0} с");
        }
        catch
        {
            KillQuietly(process);
            throw;
        }
        finally
        {
            DeleteQuietly(outputPath);
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось удалить временный файл {Path}", path);
        }
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось завершить процесс piper.exe");
        }
    }
}
