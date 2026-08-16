using System.IO;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TwitchChatOverlay.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string DisplayName)
{
    /// <summary>Sentinel for "Системное по умолчанию" — an empty id means "let NAudio pick".</summary>
    public static AudioDeviceInfo SystemDefault { get; } = new(string.Empty, "Системное по умолчанию");
}

public sealed class AudioPlaybackService
{
    private readonly ILogger<AudioPlaybackService> _logger;

    public AudioPlaybackService(ILogger<AudioPlaybackService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo> { AudioDeviceInfo.SystemDefault };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось перечислить устройства вывода звука");
        }

        return devices;
    }

    /// <summary>
    /// Plays a WAV buffer to completion on the selected device. The returned task finishes
    /// when playback actually ends, which is what the dispatcher uses to time how long the
    /// card stays on screen — no fixed timers involved.
    /// </summary>
    public async Task PlayAsync(byte[] wavData, string? deviceId, int volumePercent, int pitch, CancellationToken ct)
    {
        using var sourceStream = new MemoryStream(wavData);
        using var reader = new WaveFileReader(sourceStream);

        ISampleProvider sampleProvider = reader.ToSampleProvider();

        if (pitch != 0)
        {
            // −10…+10 mapped to roughly ±1 octave, which is as far as the shifter stays natural.
            var factor = (float)Math.Pow(2, Math.Clamp(pitch, -10, 10) / 12.0);
            sampleProvider = new SmbPitchShiftingSampleProvider(sampleProvider) { PitchFactor = factor };
        }

        var volumeProvider = new VolumeSampleProvider(sampleProvider)
        {
            Volume = Math.Clamp(volumePercent, 0, 100) / 100f
        };

        using var output = CreateOutputDevice(deviceId);
        var completion = new TaskCompletionSource();

        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                completion.TrySetException(args.Exception);
            }
            else
            {
                completion.TrySetResult();
            }
        };

        output.Init(volumeProvider);
        output.Play();

        await using var registration = ct.Register(() =>
        {
            try
            {
                output.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось остановить воспроизведение по отмене");
            }
        });

        await completion.Task;
    }

    private IWavePlayer CreateOutputDevice(string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                if (device is not null && device.State == DeviceState.Active)
                {
                    return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Устройство {DeviceId} недоступно, используется системное по умолчанию", deviceId);
            }
        }

        return new WaveOutEvent();
    }
}
