using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clocky.Config;
using Clocky.UI;

#if DEBUG
namespace Clocky.Core;

public class ClockyTestServer : IDisposable
{
    private readonly TcpListener? _listener;
    private readonly MainWindow _mainWindow;
    private readonly AppConfig _config;
    private bool _running;
    private const int Port = 19842;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public ClockyTestServer(MainWindow mainWindow, AppConfig config)
    {
        _mainWindow = mainWindow;
        _config = config;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.ExclusiveAddressUse = false;
            _listener.Start();
            _running = true;
            Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server_err.log"), $"[Server Bind Error] {ex}\n");
            }
            catch { }
        }
    }

    private async Task ListenLoop()
    {
        if (_listener == null) return;
        while (_running)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch
            {
                if (!_running) break;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        using (var writer = new BinaryWriter(stream))
        {
            try
            {
                string? reqLine = reader.ReadLine();
                if (string.IsNullOrEmpty(reqLine)) return;

                var parts = reqLine.Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string rawUrl = parts[1];
                string path = rawUrl;
                string query = "";
                int qIdx = rawUrl.IndexOf('?');
                if (qIdx >= 0)
                {
                    path = rawUrl.Substring(0, qIdx).ToLowerInvariant();
                    query = rawUrl.Substring(qIdx + 1);
                }
                else
                {
                    path = rawUrl.ToLowerInvariant();
                }

                var queryDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(query))
                {
                    foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = pair.Split('=', 2);
                        if (kv.Length == 2) queryDict[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                        else if (kv.Length == 1) queryDict[Uri.UnescapeDataString(kv[0])] = "";
                    }
                }

                int contentLength = 0;
                string? origin = null;
                string? referer = null;
                string? header;
                while (!string.IsNullOrEmpty(header = reader.ReadLine()))
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(header.Substring("Content-Length:".Length).Trim(), out contentLength);
                    }
                    else if (header.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
                    {
                        origin = header.Substring("Origin:".Length).Trim();
                    }
                    else if (header.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase))
                    {
                        referer = header.Substring("Referer:".Length).Trim();
                    }
                }

                // Security Check: Block browser-based cross-origin requests with strict host verification
                if (!IsLocalOrigin(origin) || !IsLocalOrigin(referer))
                {
                    SendHttp(writer, 403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("Forbidden: Cross-origin browser requests rejected."));
                    return;
                }

                string body = "";
                if (contentLength > 0)
                {
                    char[] buffer = new char[contentLength];
                    int read = reader.ReadBlock(buffer, 0, contentLength);
                    body = new string(buffer, 0, read);
                }

                if (path == "/api/status" && method == "GET")
                {
                    string json = "";
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        var status = new
                        {
                            ActiveTab = _mainWindow.CurrentTab,
                            Theme = _config.ThemePreference,
                            AlwaysOnTop = _mainWindow.Topmost,
                            Pid = Environment.ProcessId,
                            ActiveSensors = _mainWindow.ActiveSensorsCount,
                            CpuText = _mainWindow.HdrCpuText,
                            GpuText = _mainWindow.HdrGpuText,
                            PowerText = _mainWindow.HdrPowerText,
                            RamText = _mainWindow.HdrRamText
                        };
                        json = JsonSerializer.Serialize(status, JsonOpts);
                    });

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path == "/api/snapshot" && method == "GET")
                {
                    string json = "";
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        var snap = _mainWindow.LatestSnapshot;
                        json = JsonSerializer.Serialize(snap, JsonOpts);
                    });

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path == "/api/tab" && method == "POST")
                {
                    using var doc = JsonDocument.Parse(body);
                    int tab = doc.RootElement.GetProperty("tab").GetInt32();

                    _mainWindow.Dispatcher.Invoke(() => _mainWindow.SelectTab(tab));

                    byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path.StartsWith("/api/table/columns") && method == "GET")
                {
                    string table = queryDict.GetValueOrDefault("table", "processes");
                    if (path.EndsWith("/sensors")) table = "sensors";
                    else if (path.EndsWith("/processes")) table = "processes";

                    string json = "";
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        var info = _mainWindow.GetTableColumnsInfo(table);
                        json = JsonSerializer.Serialize(info, JsonOpts);
                    });

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path.StartsWith("/api/table/resize") && (method == "POST" || method == "GET"))
                {
                    string table = queryDict.GetValueOrDefault("table", "processes");
                    string col = queryDict.GetValueOrDefault("column", "1");
                    double width = 150.0;
                    if (queryDict.TryGetValue("width", out string? wStr)) double.TryParse(wStr, out width);

                    if (!string.IsNullOrEmpty(body) && method == "POST")
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("table", out var tProp)) table = tProp.GetString() ?? table;
                            if (doc.RootElement.TryGetProperty("column", out var cProp)) col = cProp.GetString() ?? col;
                            if (doc.RootElement.TryGetProperty("width", out var wProp)) width = wProp.GetDouble();
                        }
                        catch { }
                    }

                    string json = "";
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        var res = _mainWindow.SetTableColumnWidth(table, col, width);
                        json = JsonSerializer.Serialize(res, JsonOpts);
                    });

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path.StartsWith("/api/element") && method == "GET")
                {
                    string elName = path.Substring("/api/element".Length).TrimStart('/');
                    if (string.IsNullOrEmpty(elName)) elName = "CanvasCpuLoad";

                    string json = "";
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        var r = _mainWindow.GetElementScreenRect(elName);
                        var obj = new
                        {
                            Name = elName,
                            X = r.IsEmpty || double.IsInfinity(r.X) || double.IsNaN(r.X) ? 0.0 : r.X,
                            Y = r.IsEmpty || double.IsInfinity(r.Y) || double.IsNaN(r.Y) ? 0.0 : r.Y,
                            Width = r.IsEmpty || double.IsInfinity(r.Width) || double.IsNaN(r.Width) ? 0.0 : r.Width,
                            Height = r.IsEmpty || double.IsInfinity(r.Height) || double.IsNaN(r.Height) ? 0.0 : r.Height,
                            IsEmpty = r.IsEmpty
                        };
                        json = JsonSerializer.Serialize(obj, JsonOpts);
                    });

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path == "/api/theme" && method == "POST")
                {
                    using var doc = JsonDocument.Parse(body);
                    string theme = doc.RootElement.GetProperty("theme").GetString() ?? "System";

                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        _config.ThemePreference = theme;
                        _mainWindow.ApplyTheme(theme);
                        _config.Save();
                    });

                    byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path == "/api/exit" && method == "POST")
                {
                    byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"exiting\"}");
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                    Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        Environment.Exit(0);
                    });
                }
                else if (path == "/api/test_crash" && method == "POST")
                {
                    byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"triggering_exception\"}");
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                    _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        throw new InvalidOperationException("Simulated test diagnostic exception triggered by /api/test_crash.");
                    }));
                }
                else if (path == "/api/test_update" && method == "POST")
                {
                    using var doc = JsonDocument.Parse(body);
                    string feed = doc.RootElement.GetProperty("feedUrl").GetString() ?? "";
                    
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrEmpty(feed))
                        {
                            _config.UpdateFeedUrl = feed;
                            _config.Save();
                        }
                    });

                    byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"feed_configured\"}");
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else if (path == "/api/screenshot" && method == "GET")
                {
                    byte[]? pngBytes = null;
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            if (_mainWindow.WindowState == WindowState.Minimized || _mainWindow.Visibility != Visibility.Visible)
                            {
                                _mainWindow.Show();
                                _mainWindow.WindowState = WindowState.Normal;
                                _mainWindow.Activate();
                            }

                            int width = (int)_mainWindow.ActualWidth;
                            int height = (int)_mainWindow.ActualHeight;
                            if (width <= 0 || height <= 0)
                            {
                                width = 1420;
                                height = 890;
                            }

                            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                            rtb.Render(_mainWindow);

                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(rtb));

                            using var ms = new MemoryStream();
                            encoder.Save(ms);
                            pngBytes = ms.ToArray();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Screenshot error: {ex.Message}");
                        }
                    });

                    if (pngBytes != null)
                    {
                        SendHttp(writer, 200, "OK", "image/png", pngBytes);
                    }
                    else
                    {
                        SendHttp(writer, 500, "Internal Error", "text/plain", Encoding.UTF8.GetBytes("Screenshot failed"));
                    }
                }
                else if (path == "/api/exit" && (method == "POST" || method == "GET"))
                {
                    byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"shutting_down\"}");
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                    _mainWindow.Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(200);
                        System.Windows.Application.Current.Shutdown();
                    });
                }
                else if (path == "/api/toggle" && (method == "POST" || method == "GET"))
                {
                    _mainWindow.Dispatcher.Invoke(() =>
                    {
                        if (_mainWindow.IsVisible) _mainWindow.Hide();
                        else
                        {
                            _mainWindow.Show();
                            _mainWindow.WindowState = WindowState.Normal;
                            _mainWindow.Activate();
                        }
                    });
                    byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    SendHttp(writer, 200, "OK", "application/json", bodyBytes);
                }
                else
                {
                    SendHttp(writer, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("Not Found"));
                }
            }
            catch (Exception ex)
            {
                try
                {
                    SendHttp(writer, 500, "Internal Server Error", "text/plain", Encoding.UTF8.GetBytes(ex.ToString()));
                }
                catch { }
            }
        }
    }

    private static void SendHttp(BinaryWriter writer, int statusCode, string statusText, string contentType, byte[] body)
    {
        string header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        $"Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        writer.Write(headerBytes);
        writer.Write(body);
        writer.Flush();
    }

    private static bool IsLocalOrigin(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true; // Direct non-browser clients omit Origin/Referer
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }
}
#endif
