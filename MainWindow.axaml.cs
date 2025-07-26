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

        private readonly SemaphoreSlim _decodeSemaphore = new(1, 1);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this; 
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
                                var filePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
                                context.Response.ContentType = "text/html";
                                await context.Response.SendFileAsync(filePath);
                            });


                            endpoints.MapGet("/stream", async context =>
                            {
                                context.Response.ContentType = "multipart/x-mixed-replace;boundary=frame";
                                context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                                context.Response.Headers["Pragma"] = "no-cache";
                                context.Response.Headers["Expires"] = "0";

                                var boundaryBytes = System.Text.Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n\r\n");
                                var endBytes = System.Text.Encoding.ASCII.GetBytes("\r\n");

                                DateTime lastSent = DateTime.MinValue;
                                TimeSpan minInterval = TimeSpan.FromMilliseconds(66); 

                                try
                                {
                                    while (!token.IsCancellationRequested)
                                    {
                                        var now = DateTime.UtcNow;
                                        if ((now - lastSent) < minInterval)
                                        {
                                            await Task.Delay(10, token);
                                            continue;
                                        }

                                        byte[] frame;
                                        lock (_frameLock) frame = _latestFrame;

                                        if (frame.Length == 0)
                                        {
                                            await Task.Delay(50, token);
                                            continue;
                                        }

                                        await context.Response.Body.WriteAsync(boundaryBytes, 0, boundaryBytes.Length, token);
                                        await context.Response.Body.WriteAsync(frame, 0, frame.Length, token);
                                        await context.Response.Body.WriteAsync(endBytes, 0, endBytes.Length, token);
                                        await context.Response.Body.FlushAsync(token);

                                        lastSent = now;
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Stream error: {ex.Message}");
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
                ? "-f gdigrab -framerate 15 -i desktop -q:v 10 -vf scale=640:-1 -f mjpeg -"
                : "-f x11grab -framerate 15 -i :0.0 -q:v 10 -vf scale=640:-1 -f mjpeg -";

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

            // Log ffmpeg errors for debugging
            _ = Task.Run(async () =>
            {
                var reader = process.StandardError;
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null)
                        Debug.WriteLine($"FFmpeg: {line}");
                }
            });

            process.Start();

            var outputStream = process.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
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
                        if (data[i - 1] == 0xFF && data[i] == 0xD9)
                        {
                            int frameLength = i + 1 - lastIndex;
                            var frame = new byte[frameLength];
                            Array.Copy(data, lastIndex, frame, 0, frameLength);
                            lastIndex = i + 1;

                            lock (_frameLock)
                            {
                                _latestFrame = frame;
                            }

                            if (_decodeSemaphore.Wait(0))
                            {
                                _ = Task.Run(() =>
                                {
                                    try
                                    {
                                        using var ms = new MemoryStream(frame);
                                        var bitmap = Bitmap.DecodeToWidth(ms, 800);

                                        Dispatcher.UIThread.Post(() =>
                                        {
                                            LatestBitmap = bitmap;
                                        });
                                    }
                                    finally
                                    {
                                        _decodeSemaphore.Release();
                                    }
                                });
                            }
                        }
                    }

                    if (lastIndex > 0)
                    {
                        var remaining = length - lastIndex;
                        var remainingData = new byte[remaining];
                        Array.Copy(data, lastIndex, remainingData, 0, remaining);
                        frameBuffer.SetLength(0);
                        frameBuffer.Write(remainingData, 0, remaining);
                        frameBuffer.Position = frameBuffer.Length;
                    }
                    else if (frameBuffer.Length > 10 * 1024 * 1024)
                    {
                        frameBuffer.SetLength(0);
                        frameBuffer.Position = 0;
                    }

                    await Task.Delay(15, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteAsync("q");
                        await process.StandardInput.FlushAsync();
                    }
                    catch { }
                    if (!process.WaitForExit(2000))
                    {
                        process.Kill();
                    }
                }
            }
        }
    }
}