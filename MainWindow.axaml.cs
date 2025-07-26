using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.ComponentModel;

namespace ScreenStreamer
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private CancellationTokenSource? _cts;
        private IHost? _webHost;
        private byte[] _latestFrame = Array.Empty<byte>();
        private readonly object _frameLock = new();

        private Bitmap? _latestBitmap = null;
        public Bitmap? LatestBitmap
        {
            get => _latestBitmap;
            private set
            {
                if (_latestBitmap != null)
                    _latestBitmap.Dispose();

                _latestBitmap = value;
                OnPropertyChanged(nameof(LatestBitmap));
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this; // Bind to self for XAML
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            Dispatcher.UIThread.Post(() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));

        private async void OnStartStreaming(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Starting...";

            _cts = new CancellationTokenSource();

            // Start FFmpeg capture asynchronously (fire-and-forget)
            _ = CaptureScreenAsync(_cts.Token);

            // Start web server
            await StartWebServer(_cts.Token);

            StatusText.Text = "Streaming on http://<your-lan-ip>:5000";
        }

        private async void OnStopStreaming(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            if (_webHost != null)
            {
                await _webHost.StopAsync();
                _webHost.Dispose();
                _webHost = null;
            }

            StatusText.Text = "Stopped";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            LatestBitmap = null;
        }

        private async Task StartWebServer(CancellationToken token)
        {
            _webHost = new HostBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseUrls("http://0.0.0.0:5000");
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();

                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/", async context =>
                            {
                                await context.Response.WriteAsync("<html><body><img src='/stream'/></body></html>");
                            });

                            endpoints.MapGet("/stream", async context =>
                            {
                                context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                                while (!token.IsCancellationRequested)
                                {
                                    byte[] frame;
                                    lock (_frameLock) frame = _latestFrame;

                                    if (frame.Length > 0)
                                    {
                                        await context.Response.WriteAsync("--frame\r\n");
                                        await context.Response.WriteAsync("Content-Type: image/jpeg\r\n\r\n");
                                        await context.Response.Body.WriteAsync(frame, 0, frame.Length);
                                        await context.Response.WriteAsync("\r\n");
                                        await context.Response.Body.FlushAsync();
                                    }

                                    await Task.Delay(33, token); // ~30 FPS
                                }
                            });
                        });
                    });
                })
                .Build();

            await _webHost.StartAsync(token);
        }

        private async Task CaptureScreenAsync(CancellationToken token)
        {
            var ffmpegArgs = OperatingSystem.IsWindows()
                ? "-f gdigrab -i desktop -vf fps=15 -q:v 5 -f mjpeg -"
                : "-f x11grab -i :0.0 -vf fps=15 -q:v 5 -f mjpeg -";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            // Capture ffmpeg errors for debugging (optional)
            _ = Task.Run(async () =>
            {
                var reader = process.StandardError;
                while (!reader.EndOfStream)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line != null)
                    {
                        // Optionally log or show ffmpeg stderr output
                        Debug.WriteLine($"FFmpeg: {line}");
                    }
                }
            });

            process.Start();

            var outputStream = process.StandardOutput.BaseStream;
            var buffer = new byte[4096];
            var frameBuffer = new MemoryStream();

            try
            {
                while (!token.IsCancellationRequested && !process.HasExited)
                {
                    int bytesRead = await outputStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead == 0) break;

                    frameBuffer.Write(buffer, 0, bytesRead);

                    var data = frameBuffer.GetBuffer();
                    int length = (int)frameBuffer.Length;

                    int lastIndex = 0;
                    for (int i = 1; i < length; i++)
                    {
                        if (data[i - 1] == 0xFF && data[i] == 0xD9) // JPEG EOI
                        {
                            int frameLength = i + 1 - lastIndex;
                            byte[] frame = new byte[frameLength];
                            Array.Copy(data, lastIndex, frame, 0, frameLength);

                            lastIndex = i + 1;

                            lock (_frameLock)
                            {
                                _latestFrame = frame;
                            }

                            // Decode JPEG frame to Avalonia Bitmap on background thread
                            using var ms = new MemoryStream(frame);
                            var bitmap = await Task.Run(() => Bitmap.DecodeToWidth(ms, 800));

                            Dispatcher.UIThread.Post(() =>
                            {
                                LatestBitmap = bitmap;
                            });
                        }
                    }

                    if (lastIndex > 0)
                    {
                        var remaining = length - lastIndex;
                        var remainingData = new byte[remaining];
                        Array.Copy(data, lastIndex, remainingData, 0, remaining);
                        frameBuffer.SetLength(0);
                        frameBuffer.Write(remainingData, 0, remaining);
                        // Reset position for next write
                        frameBuffer.Position = frameBuffer.Length;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        // Attempt graceful exit by sending "q"
                        await process.StandardInput.WriteAsync("q");
                        await process.StandardInput.FlushAsync();
                    }
                    catch { /* ignore */ }

                    // Wait a short time for process to exit gracefully
                    if (!process.WaitForExit(2000))
                    {
                        process.Kill();
                    }
                }
            }
        }
    }
}