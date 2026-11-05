using AirPlay.Models.Configs;
using AirPlay.Services;
using AirPlay.Services.Implementations;
using AirPlay.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlay
{
    public class AirPlayService : IHostedService, IDisposable
    {
        private readonly IAirPlayReceiver _airPlayReceiver;
        private readonly DumpConfig _dConfig;

        private AudioOutputService _audioOutput;
        private VideoOutputService _videoOutput;
        private List<byte> _audiobuf;
        private readonly object _audioOutputLock = new object();
        private readonly object _videoOutputLock = new object();

        public AirPlayService(IAirPlayReceiver airPlayReceiver, IOptions<DumpConfig> dConfig)
        {
            _airPlayReceiver = airPlayReceiver ?? throw new ArgumentNullException(nameof(airPlayReceiver));
            _dConfig = dConfig?.Value ?? throw new ArgumentNullException(nameof(dConfig));
        }

        private async Task HandleFfplayExitedAsync()
        {
            if (_airPlayReceiver is not AirPlayReceiver receiver)
            {
                return;
            }

            await SessionManager.Current.ReleaseAllMirroringAsync(
                receiver.OnMirroringStopped,
                "ffplay-exited").ConfigureAwait(false);
        }

        private void RecreateAudioOutput()
        {
            lock (_audioOutputLock)
            {
                Console.WriteLine("Recreating audio output after unexpected stop...");

                try
                {
                    _audioOutput?.Dispose();
                    Thread.Sleep(200);

                    _audioOutput = new AudioOutputService();
                    _audioOutput.Initialize();
                    _audioOutput.PlaybackStoppedUnexpectedly += (s, e) => RecreateAudioOutput();

                    Console.WriteLine("Audio output recreated successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to recreate audio output: {ex.Message}");
                }
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // #region agent log
            DebugSessionLog.Write("H3", "AirPlayService.cs:StartAsync", "StartAsync begin", null);
            // #endregion
            try
            {
#if DUMP
            var bPath = _dConfig.Path;
            var fPath = Path.Combine(bPath, "frames/");
            var oPath = Path.Combine(bPath, "out/");
            var pPath = Path.Combine(bPath, "pcm/");

            if (!Directory.Exists(bPath))
            {
                Directory.CreateDirectory(bPath);
            }
            if (!Directory.Exists(fPath))
            {
                Directory.CreateDirectory(fPath);
            }
            if (!Directory.Exists(oPath))
            {
                Directory.CreateDirectory(oPath);
            }
            if (!Directory.Exists(pPath))
            {
                Directory.CreateDirectory(pPath);
            }
#endif

            // Initialize audio output on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    _audioOutput = new AudioOutputService();
                    _audioOutput.Initialize();
                    _audioOutput.PlaybackStoppedUnexpectedly += (s, e) => RecreateAudioOutput();
                    Console.WriteLine("Audio output initialized successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to initialize audio output: {ex.Message}");
                    Console.WriteLine("Continuing without audio output...");
                }
            }

            // Initialize video output service (will be started/stopped per mirroring session)
            _videoOutput = new VideoOutputService();
            _videoOutput.FfplayExited += (_, _) => _ = HandleFfplayExitedAsync();
            Console.WriteLine("Video output service ready (will auto-launch ffplay when mirroring starts)");

            await _airPlayReceiver.StartListeners(cancellationToken);
            // #region agent log
            DebugSessionLog.Write("H3", "AirPlayService.cs:StartAsync", "StartListeners completed", null);
            // #endregion
                    await _airPlayReceiver.StartMdnsAsync().ConfigureAwait(false);
                    // #region agent log
                    DebugSessionLog.Write("H3", "AirPlayService.cs:StartAsync", "StartMdnsAsync completed", null);
                    // #endregion
                    // #region agent log
#if DEBUG
                    _ = Task.Run(async () =>
                    {
                        var proc = Process.GetCurrentProcess();
                        var lastCpu = proc.TotalProcessorTime;
                        var lastWall = DateTime.UtcNow;
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }

                            proc.Refresh();
                            var nowCpu = proc.TotalProcessorTime;
                            var nowWall = DateTime.UtcNow;
                            var wallMs = (nowWall - lastWall).TotalMilliseconds;
                            var cpuPct = wallMs > 0 ? (nowCpu - lastCpu).TotalMilliseconds / wallMs * 100.0 : 0;
                            lastCpu = nowCpu;
                            lastWall = nowWall;
                            DebugSessionLog.Write("H1", "AirPlayService.heartbeat", "process sample", new
                            {
                                cpuPct = Math.Round(cpuPct, 1),
                                wsMb = proc.WorkingSet64 / (1024 * 1024),
                                threads = proc.Threads.Count,
                                cZero = DebugSessionLog.CZeroReads,
                                cErr = DebugSessionLog.CErrors,
                                cPkt = DebugSessionLog.CPackets,
                                dZero = DebugSessionLog.DZeroReads,
                                dErr = DebugSessionLog.DErrors,
                                dPkt = DebugSessionLog.DPackets,
                                rtspDepth = DebugSessionLog.RtspDepth,
                                nicEvents = DebugSessionLog.NicEvents,
                            });
                        }
                    }, cancellationToken);
#endif
                    // #endregion

#if USE_WINDOWS_TRAY
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TrayStartupNotifier.ShowReady();
            }
#endif

            _airPlayReceiver.OnSetVolumeReceived += (s, e) =>
            {
                lock (_audioOutputLock)
                {
                    _audioOutput?.SetVolume(e);
                }
            };

            _airPlayReceiver.OnAudioFlushReceived += (s, e) =>
            {
                Console.WriteLine("Audio flush received - restarting audio output for new track");
                lock (_audioOutputLock)
                {
                    _audioOutput?.HandleFlush();
                }
            };

            _airPlayReceiver.OnMirroringStartedReceived += (s, e) =>
            {
                Console.WriteLine("Mirroring started - launching video player...");
                lock (_videoOutputLock)
                {
                    _videoOutput?.StartMirroring();
                }
            };

            _airPlayReceiver.OnMirroringStoppedReceived += (s, e) =>
            {
                Console.WriteLine("Mirroring stopped - closing video player...");
                lock (_videoOutputLock)
                {
                    _videoOutput?.StopMirroring();
                }
            };

            // H264 VIDEO OUTPUT
            _airPlayReceiver.OnH264DataReceived += (s, e) =>
            {
                // Send H264 data to video output pipe
                lock (_videoOutputLock)
                {
                    _videoOutput?.WriteFrame(e);
                }

#if DUMP
                using (FileStream writer = new FileStream($"{bPath}dump.h264", FileMode.Append))
                {
                    writer.Write(e.Data, 0, e.Length);
                }
#endif
            };

            _audiobuf = new List<byte>();
            int pcmReceivedCount = 0;
            _airPlayReceiver.OnPCMDataReceived += (s, e) =>
            {
                pcmReceivedCount++;
                if (pcmReceivedCount <= 5 || pcmReceivedCount % 500 == 0)
                {
                    bool allZeros = true;
                    int checkLen = Math.Min(e.Length, 100);
                    for (int i = 0; i < checkLen; i++)
                    {
                        if (e.Data[i] != 0) { allZeros = false; break; }
                    }
                    DebugSessionLog.WriteLine($"[DEBUG-PCM] OnPCMDataReceived #{pcmReceivedCount}: len={e.Length}, allZeros={allZeros}");
                }

                // Play audio through speakers
                lock (_audioOutputLock)
                {
                    _audioOutput?.AddSamples(e.Data, 0, e.Length);
                }

#if DUMP
                _audiobuf.AddRange(e.Data);
#endif
            };
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog.Write("H3", "AirPlayService.cs:StartAsync", "StartAsync failed", new { type = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });
                // #endregion
#if USE_WINDOWS_TRAY
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    TrayStartupNotifier.ShowError($"启动失败：{ex.Message}");
                }
#endif
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // #region agent log
            DebugSessionLog.Write("H6", "AirPlayService.StopAsync", "enter", new { thread = Environment.CurrentManagedThreadId });
            // #endregion
            return StopAsyncCore();
        }

        private async Task StopAsyncCore()
        {
            try
            {
                await _airPlayReceiver.StopListenersAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog.Write("H6", "AirPlayService.StopAsync", "StopListenersAsync failed", new { type = ex.GetType().Name, message = ex.Message });
                // #endregion
            }

            _audioOutput?.Dispose();
            _audioOutput = null;

            _videoOutput?.Dispose();
            _videoOutput = null;

#if DUMP
            // DUMP WAV AUDIO
            var bPath = _dConfig.Path;
            using (var wr = new FileStream($"{bPath}dequeued.wav", FileMode.Create))
            {
                var header = Utilities.WriteWavHeader(2, 44100, 16, (uint)_audiobuf.Count);
                wr.Write(header, 0, header.Length);
            }

            using (FileStream writer = new FileStream($"{bPath}dequeued.wav", FileMode.Append))
            {
                writer.Write(_audiobuf.ToArray(), 0, _audiobuf.Count);
            }
#endif
            // #region agent log
            DebugSessionLog.Write("H6", "AirPlayService.StopAsync", "exit", null);
            // #endregion
        }

        public void Dispose()
        {
            _audioOutput?.Dispose();
            _audioOutput = null;

            _videoOutput?.Dispose();
            _videoOutput = null;

            if (_airPlayReceiver is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
