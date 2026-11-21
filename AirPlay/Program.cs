using AirPlay.Models.Configs;
using AirPlay.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlay
{
    public class Program
    {
        [STAThread]
        public static async Task Main(string[] args)
        {
#if USE_WINDOWS_TRAY
            if (OperatingSystem.IsWindows())
            {
                // 高 DPI 感知必须在外层任意窗口创建前设置：
                // ffplay/SDL 是 144 DPI(150%) 感知进程，若本进程不感知 DPI，
                // 嵌入的 ffplay 子窗口会被 DPI 虚拟化放大，画面只显示一部分。
                try
                {
                    System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                }
                catch
                {
                    // 已经设置过时忽略。
                }

                AppFileLog.Install();
                Console.WriteLine($"High DPI mode: {System.Windows.Forms.Application.HighDpiMode}");
            }
#endif

            // DUMP WAV AUDIO
            //var dir = Directory.GetFiles("dump/pcm");
            //var _audiobuf = dir.SelectMany(d => File.ReadAllBytes(d)).ToList();
            //using (var wr = new FileStream("dump/dequeued.wav", FileMode.Create))
            //{
            //    var header = Utilities.WriteWavHeader(2, 44100, 16, (uint)_audiobuf.Count);
            //    wr.Write(header, 0, header.Length);
            //}

            //using (FileStream _writer = new FileStream("dump/dequeued.wav", FileMode.Append))
            //{
            //    _writer.Write(_audiobuf.ToArray(), 0, _audiobuf.Count);
            //}

            var builder = CreateHostBuilder(args);

            // #region agent log
            DebugSessionLog.Write("H4", "Program.cs:Main", "Process starting", new { cwd = Directory.GetCurrentDirectory(), args = args ?? Array.Empty<string>() });
            // #endregion

#if USE_WINDOWS_TRAY
            if (OperatingSystem.IsWindows())
            {
                await WindowsTrayHost.RunAsync(async cancellationToken =>
                {
                    using var host = builder.Build();
                    // #region agent log
                    DebugSessionLog.Write("H6", "Program.cs:host", "RunAsync begin", new { cancelled = cancellationToken.IsCancellationRequested });
                    // #endregion
                    try
                    {
                        await host.StartAsync(cancellationToken).ConfigureAwait(false);
                        await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
                        // #region agent log
                        DebugSessionLog.Write("H6", "Program.cs:host", "WaitForShutdownAsync returned", null, runId: "post-fix");
                        // #endregion
                    }
                    catch (OperationCanceledException)
                    {
                        // #region agent log
                        DebugSessionLog.Write("H6", "Program.cs:host", "host cancelled", null, runId: "post-fix");
                        // #endregion
                    }
                    catch (Exception ex)
                    {
                        // #region agent log
                        DebugSessionLog.Write("H6", "Program.cs:host", "host faulted", new { type = ex.GetType().Name, message = ex.Message });
                        // #endregion
                    }
                    finally
                    {
                        try
                        {
                            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                            // #region agent log
                            DebugSessionLog.Write("H6", "Program.cs:host", "StopAsync completed", null, runId: "post-fix");
                            // #endregion
                        }
                        catch (Exception ex)
                        {
                            // #region agent log
                            DebugSessionLog.Write("H6", "Program.cs:host", "StopAsync failed", new { type = ex.GetType().Name, message = ex.Message });
                            // #endregion
                        }
                    }
                }).ConfigureAwait(false);
                // #region agent log
                DebugSessionLog.Write("H6", "Program.cs:Main", "tray host finished, exiting process", null, runId: "post-fix");
                // #endregion
                return;
            }
#endif

            await builder.RunConsoleAsync().ConfigureAwait(false);
        }

        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            return new HostBuilder()
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.SetBasePath(Directory.GetCurrentDirectory());

                    var os = "win";
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        os = "osx";
                    }
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        os = "linux";
                    }

                    config.AddJsonFile($"appsettings_{os}.json", optional: false, reloadOnChange: false);
                    config.AddEnvironmentVariables();
                    if (args != null)
                    {
                        config.AddCommandLine(args);
                    }
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddOptions();

                    services.Configure<AirPlayReceiverConfig>(hostContext.Configuration.GetSection("AirPlayReceiver"));
                    services.Configure<DumpConfig>(hostContext.Configuration.GetSection("Dump"));

                    services.AddSingleton<IAirPlayReceiver, AirPlayReceiver>();

                    services.AddHostedService<AirPlayService>();
                })
                .ConfigureLogging((hostContext, logging) =>
                {
                    logging.AddConfiguration(hostContext.Configuration.GetSection("Logging"));
                    logging.AddConsole();
                });
        }
    }
}
