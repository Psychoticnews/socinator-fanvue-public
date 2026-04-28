using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FanvueDominatorUI.Controls.Setup
{
    /// <summary>
    /// Debug control for diagnosing OAuth callback issues.
    /// </summary>
    public partial class FanvueDebugControl : UserControl
    {
        private const int CallbackPort = 19876;
        private HttpListener _testListener;
        private bool _isTestServerRunning;

        public FanvueDebugControl()
        {
            InitializeComponent();
        }

        #region Port Status

        private void BtnCheckPort_Click(object sender, RoutedEventArgs e)
        {
            CheckPortStatus();
        }

        private void CheckPortStatus()
        {
            try
            {
                var ipProperties = IPGlobalProperties.GetIPGlobalProperties();
                var tcpListeners = ipProperties.GetActiveTcpListeners();
                var tcpConnections = ipProperties.GetActiveTcpConnections();

                var isPortInUse = tcpListeners.Any(ep => ep.Port == CallbackPort) ||
                                  tcpConnections.Any(c => c.LocalEndPoint.Port == CallbackPort);

                if (isPortInUse)
                {
                    // Check if it's our test server
                    if (_isTestServerRunning)
                    {
                        TxtPortStatus.Text = string.Format("Port {0}: IN USE (by test server)", CallbackPort);
                        PortStatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                    }
                    else
                    {
                        TxtPortStatus.Text = string.Format("Port {0}: IN USE (by another process)", CallbackPort);
                        PortStatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFC107"));

                        // Try to find what's using it
                        FindProcessUsingPort(CallbackPort);
                    }
                }
                else
                {
                    TxtPortStatus.Text = string.Format("Port {0}: AVAILABLE", CallbackPort);
                    PortStatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C757D"));
                }
            }
            catch (Exception ex)
            {
                TxtPortStatus.Text = string.Format("Error checking port: {0}", ex.Message);
                PortStatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4948"));
            }
        }

        private void FindProcessUsingPort(int port)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var line in lines)
                    {
                        if (line.Contains(":" + port.ToString()) && (line.Contains("LISTENING") || line.Contains("ESTABLISHED")))
                        {
                            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                var pid = parts[parts.Length - 1];
                                int processId;
                                if (int.TryParse(pid, out processId))
                                {
                                    try
                                    {
                                        var proc = Process.GetProcessById(processId);
                                        TxtPortStatus.Text += string.Format(" - Process: {0} (PID: {1})", proc.ProcessName, processId);
                                    }
                                    catch { }
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Test Server

        private void BtnStartTestServer_Click(object sender, RoutedEventArgs e)
        {
            StartTestServer();
        }

        private void BtnStopTestServer_Click(object sender, RoutedEventArgs e)
        {
            StopTestServer();
        }

        private void StartTestServer()
        {
            try
            {
                if (_isTestServerRunning)
                {
                    TxtTestServerStatus.Text = "Server already running";
                    return;
                }

                _testListener = new HttpListener();
                _testListener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", CallbackPort));
                _testListener.Start();

                _isTestServerRunning = true;
                BtnStartTestServer.IsEnabled = false;
                BtnStopTestServer.IsEnabled = true;

                TxtTestServerStatus.Text = "Server RUNNING - listening on port " + CallbackPort;
                TxtTestServerStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));

                // Start listening for requests
                Task.Run(() => ListenForTestRequests());

                CheckPortStatus();
            }
            catch (HttpListenerException ex)
            {
                TxtTestServerStatus.Text = string.Format("Failed to start: {0}", ex.Message);
                TxtTestServerStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4948"));

                if (ex.ErrorCode == 5) // Access denied
                {
                    TxtTestServerStatus.Text += "\nTry running as Administrator or add URL reservation.";
                }
            }
            catch (Exception ex)
            {
                TxtTestServerStatus.Text = string.Format("Error: {0}", ex.Message);
                TxtTestServerStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4948"));
            }
        }

        private async Task ListenForTestRequests()
        {
            while (_isTestServerRunning && _testListener != null && _testListener.IsListening)
            {
                try
                {
                    var context = await _testListener.GetContextAsync();
                    var request = context.Request;
                    var response = context.Response;

                    // Log the request
                    var requestInfo = string.Format(
                        "REQUEST RECEIVED!\n" +
                        "Time: {0}\n" +
                        "Method: {1}\n" +
                        "URL: {2}\n" +
                        "RawUrl: {3}\n" +
                        "Query: {4}\n" +
                        "User-Agent: {5}",
                        DateTime.Now.ToString("HH:mm:ss.fff"),
                        request.HttpMethod,
                        request.Url,
                        request.RawUrl,
                        request.Url.Query,
                        request.UserAgent ?? "(none)"
                    );

                    Dispatcher.Invoke(() =>
                    {
                        TxtTestServerStatus.Text = requestInfo;
                        TxtTestServerStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                    });

                    // Send response
                    var html = GetTestResponseHtml(request);
                    var buffer = Encoding.UTF8.GetBytes(html);
                    response.ContentLength64 = buffer.Length;
                    response.ContentType = "text/html";
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                }
                catch (HttpListenerException)
                {
                    break; // Listener was stopped
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtTestServerStatus.Text = "Error: " + ex.Message;
                    });
                }
            }
        }

        private string GetTestResponseHtml(HttpListenerRequest request)
        {
            var queryParams = new StringBuilder();
            foreach (string key in request.QueryString.AllKeys)
            {
                queryParams.AppendFormat("<li><strong>{0}:</strong> {1}</li>",
                    WebUtility.HtmlEncode(key ?? "(null)"),
                    WebUtility.HtmlEncode(request.QueryString[key] ?? "(null)"));
            }

            return string.Format(@"<!DOCTYPE html>
<html>
<head>
    <title>Socinator Debug - Request Received</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
               padding: 40px; background: #1a1a2e; color: #eee; }}
        .container {{ max-width: 600px; margin: 0 auto; }}
        h1 {{ color: #28a745; }}
        .info {{ background: #16213e; padding: 20px; border-radius: 8px; margin: 20px 0; }}
        code {{ background: #0f0f23; padding: 2px 6px; border-radius: 4px; color: #00ff00; }}
        ul {{ list-style: none; padding: 0; }}
        li {{ margin: 8px 0; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>&#10004; Test Server Working!</h1>
        <p>The callback server received your request successfully.</p>
        <div class='info'>
            <h3>Request Details:</h3>
            <ul>
                <li><strong>Method:</strong> {0}</li>
                <li><strong>Path:</strong> <code>{1}</code></li>
                <li><strong>Full URL:</strong> <code>{2}</code></li>
                <li><strong>Time:</strong> {3}</li>
            </ul>
        </div>
        <div class='info'>
            <h3>Query Parameters:</h3>
            <ul>{4}</ul>
            {5}
        </div>
        <p>You can close this window and return to Socinator.</p>
    </div>
</body>
</html>",
                request.HttpMethod,
                WebUtility.HtmlEncode(request.RawUrl),
                WebUtility.HtmlEncode(request.Url.ToString()),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                queryParams.Length > 0 ? queryParams.ToString() : "<li>(no parameters)</li>",
                request.QueryString["code"] != null ? "<p style='color:#28a745;font-weight:bold;'>OAuth code parameter detected!</p>" : "");
        }

        private void StopTestServer()
        {
            try
            {
                _isTestServerRunning = false;

                if (_testListener != null)
                {
                    if (_testListener.IsListening)
                    {
                        _testListener.Stop();
                    }
                    _testListener.Close();
                    _testListener = null;
                }

                BtnStartTestServer.IsEnabled = true;
                BtnStopTestServer.IsEnabled = false;

                TxtTestServerStatus.Text = "Server stopped";
                TxtTestServerStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C757D"));

                CheckPortStatus();
            }
            catch (Exception ex)
            {
                TxtTestServerStatus.Text = string.Format("Error stopping: {0}", ex.Message);
            }
        }

        private void BtnOpenTestUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = string.Format("http://127.0.0.1:{0}/test", CallbackPort),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open browser: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSimulateCallback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Simulate what Fanvue's redirect would look like
                var fakeCode = "test_auth_code_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                var fakeState = "test_state_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                var callbackUrl = string.Format("http://127.0.0.1:{0}/callback?code={1}&state={2}", CallbackPort, fakeCode, fakeState);

                Process.Start(new ProcessStartInfo
                {
                    FileName = callbackUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open browser: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Firewall

        private void BtnCheckFirewall_Click(object sender, RoutedEventArgs e)
        {
            CheckFirewall();
        }

        private void CheckFirewall()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall show rule name=all",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var output = process.StandardOutput.ReadToEnd();

                    // Look for any rules blocking our port or Socinator
                    var hasSocinatorRule = output.IndexOf("Socinator", StringComparison.OrdinalIgnoreCase) >= 0;
                    var hasPortRule = output.Contains(":54321") || output.Contains(" 54321");

                    var result = new StringBuilder();
                    result.AppendLine("Firewall Check Results:");
                    result.AppendLine();
                    result.AppendFormat("Socinator rules found: {0}\n", hasSocinatorRule ? "Yes" : "No");
                    result.AppendFormat("Port 54321 rules found: {0}\n", hasPortRule ? "Yes" : "No");
                    result.AppendLine();
                    result.AppendLine("If you're having issues, try adding a firewall exception.");

                    TxtFirewallStatus.Text = result.ToString();
                }
            }
            catch (Exception ex)
            {
                TxtFirewallStatus.Text = "Error checking firewall: " + ex.Message;
            }
        }

        private void BtnAddFirewallRule_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will add a Windows Firewall exception for Socinator OAuth on port 54321.\n\n" +
                "This requires administrator privileges. Continue?",
                "Add Firewall Rule",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                AddFirewallRule();
            }
        }

        private void AddFirewallRule()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall add rule name=\"Socinator OAuth Callback\" dir=in action=allow protocol=tcp localport=54321",
                    UseShellExecute = true,
                    Verb = "runas" // Run as administrator
                };

                Process.Start(startInfo);

                TxtFirewallStatus.Text = "Firewall rule command executed. Check if it was added successfully.";
            }
            catch (Exception ex)
            {
                TxtFirewallStatus.Text = "Error adding rule: " + ex.Message;

                if (ex.Message.Contains("canceled"))
                {
                    TxtFirewallStatus.Text = "Operation cancelled by user.";
                }
            }
        }

        #endregion

        #region Logs

        private void BtnOpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Socinator1.0",
                    "logs"
                );

                if (Directory.Exists(logsPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = logsPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show("Logs folder not found at:\n" + logsPath, "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRefreshLogs_Click(object sender, RoutedEventArgs e)
        {
            RefreshLogs();
        }

        private void RefreshLogs()
        {
            try
            {
                var logsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Socinator1.0",
                    "logs"
                );

                if (!Directory.Exists(logsPath))
                {
                    TxtLogViewer.Text = "Logs folder not found.";
                    return;
                }

                // Get today's log file
                var today = DateTime.Now.ToString("yyyyMMdd");
                var logFiles = Directory.GetFiles(logsPath, "*" + today + "*.log")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (logFiles.Count == 0)
                {
                    // Try getting any recent log file
                    logFiles = Directory.GetFiles(logsPath, "*.log")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .Take(1)
                        .ToList();
                }

                if (logFiles.Count == 0)
                {
                    TxtLogViewer.Text = "No log files found.";
                    return;
                }

                var logContent = new StringBuilder();
                logContent.AppendLine("=== FanvueAuth Logs ===");
                logContent.AppendLine();

                foreach (var logFile in logFiles.Take(2))
                {
                    try
                    {
                        // Read last 100 lines
                        var lines = File.ReadAllLines(logFile);
                        var allFanvueLines = lines
                            .Where(l => l.Contains("[FanvueAuth]") || l.Contains("FanvueAuth"))
                            .ToList();
                        // Take last 50 (TakeLast doesn't exist in .NET 4.8)
                        var fanvueLines = allFanvueLines.Skip(Math.Max(0, allFanvueLines.Count - 50)).ToList();

                        if (fanvueLines.Count > 0)
                        {
                            logContent.AppendFormat("--- {0} ---\n", Path.GetFileName(logFile));
                            foreach (var line in fanvueLines)
                            {
                                logContent.AppendLine(line);
                            }
                            logContent.AppendLine();
                        }
                    }
                    catch { }
                }

                if (logContent.Length < 50)
                {
                    logContent.AppendLine("No FanvueAuth entries found in recent logs.");
                    logContent.AppendLine("Try the OAuth flow first, then refresh.");
                }

                TxtLogViewer.Text = logContent.ToString();
            }
            catch (Exception ex)
            {
                TxtLogViewer.Text = "Error reading logs: " + ex.Message;
            }
        }

        #endregion

        #region Network Test

        private async void BtnTestNetwork_Click(object sender, RoutedEventArgs e)
        {
            BtnTestNetwork.IsEnabled = false;
            TxtNetworkStatus.Text = "Testing...";

            try
            {
                var results = new StringBuilder();

                await Task.Run(() =>
                {
                    // Test auth.fanvue.com
                    results.Append("auth.fanvue.com: ");
                    try
                    {
                        var request = WebRequest.CreateHttp("https://auth.fanvue.com/.well-known/openid-configuration");
                        request.Timeout = 10000;
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            results.AppendFormat("{0} ({1})\n", response.StatusCode, (int)response.StatusCode);
                        }
                    }
                    catch (WebException ex)
                    {
                        if (ex.Response is HttpWebResponse httpResp)
                        {
                            results.AppendFormat("{0} ({1})\n", httpResp.StatusCode, (int)httpResp.StatusCode);
                        }
                        else
                        {
                            results.AppendFormat("FAILED - {0}\n", ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        results.AppendFormat("FAILED - {0}\n", ex.Message);
                    }

                    // Test api.fanvue.com
                    results.Append("api.fanvue.com: ");
                    try
                    {
                        var request = WebRequest.CreateHttp("https://api.fanvue.com/health");
                        request.Timeout = 10000;
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            results.AppendFormat("{0} ({1})\n", response.StatusCode, (int)response.StatusCode);
                        }
                    }
                    catch (WebException ex)
                    {
                        if (ex.Response is HttpWebResponse httpResp)
                        {
                            results.AppendFormat("{0} ({1})\n", httpResp.StatusCode, (int)httpResp.StatusCode);
                        }
                        else
                        {
                            results.AppendFormat("FAILED - {0}\n", ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        results.AppendFormat("FAILED - {0}\n", ex.Message);
                    }

                    // Test www.fanvue.com
                    results.Append("www.fanvue.com: ");
                    try
                    {
                        var request = WebRequest.CreateHttp("https://www.fanvue.com");
                        request.Timeout = 10000;
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            results.AppendFormat("{0} ({1})\n", response.StatusCode, (int)response.StatusCode);
                        }
                    }
                    catch (WebException ex)
                    {
                        if (ex.Response is HttpWebResponse httpResp)
                        {
                            results.AppendFormat("{0} ({1})\n", httpResp.StatusCode, (int)httpResp.StatusCode);
                        }
                        else
                        {
                            results.AppendFormat("FAILED - {0}\n", ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        results.AppendFormat("FAILED - {0}\n", ex.Message);
                    }
                });

                TxtNetworkStatus.Text = results.ToString();
            }
            catch (Exception ex)
            {
                TxtNetworkStatus.Text = "Error: " + ex.Message;
            }
            finally
            {
                BtnTestNetwork.IsEnabled = true;
            }
        }

        #endregion
    }
}
