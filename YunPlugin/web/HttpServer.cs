using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TS3AudioBot;

namespace YunPlugin.web
{
    public class HttpServer : IDisposable
    {
        private HttpListener _listener;
        private YunService _yunService;
        private int _port;
        private bool _isRunning;
        private string _htmlContent;

        public HttpServer(YunService service, int port)
        {
            _yunService = service;
            _port = port;
            
            LoadResources();

            _listener = new HttpListener();
            try
            {
                _listener.Prefixes.Add($"http://*:{_port}/");
            }
            catch (Exception)
            {
                _listener.Prefixes.Add($"http://localhost:{_port}/");
            }
        }

        private void LoadResources()
        {
            try 
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "YunPlugin.web.index.html"; 

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) 
                    {
                         global::YunPlugin.YunPlugin.GetLogger("Web").Warn($"Resource {resourceName} not found! Using fallback.");
                         _htmlContent = "<html><body><h1>Error: Resource not found</h1></body></html>";
                         return;
                    }
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        _htmlContent = reader.ReadToEnd();
                    }
                }
            }
            catch(Exception ex)
            {
                global::YunPlugin.YunPlugin.GetLogger("Web").Error(ex, "Error loading resources");
                _htmlContent = "<html><body><h1>Error loading resources</h1></body></html>";
            }
        }

        public void Start()
        {
            if (_isRunning) return;
            try
            {
                _listener.Start();
                _isRunning = true;
                Task.Run(() => ListenLoop());
                global::YunPlugin.YunPlugin.GetLogger("Web").Info($"Web server started on port {_port}");
            }
            catch (Exception ex)
            {
                global::YunPlugin.YunPlugin.GetLogger("Web").Error(ex, "Failed to start web server.");
            }
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_listener.IsListening) { }
                catch (Exception ex)
                {
                    global::YunPlugin.YunPlugin.GetLogger("Web").Error(ex, "Error accepting connection");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            res.AppendHeader("Access-Control-Allow-Origin", "*");
            res.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

            if (req.HttpMethod == "OPTIONS")
            {
                res.Close();
                return;
            }

            try
            {
                string path = req.Url.AbsolutePath.ToLower();

                if (req.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
                {
                    ServeHtml(res);
                    return;
                }

                // Authentication Check
                string password = global::YunPlugin.YunPlugin.config.WebPassword;
                if (!string.IsNullOrEmpty(password))
                {
                    string token = req.Headers["X-Yun-Token"];
                    if (string.IsNullOrEmpty(token)) token = req.QueryString["token"];

                    if (token != password)
                    {
                        res.StatusCode = 401;
                        res.ContentType = "application/json";
                        byte[] err = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { error = "Unauthorized" }));
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                        return;
                    }
                }

                if (path.StartsWith("/api/"))
                {
                    await DispatchApi(context);
                }
                else
                {
                    res.StatusCode = 404;
                    res.Close();
                }
            }
            catch (Exception ex)
            {
                global::YunPlugin.YunPlugin.GetLogger("Web").Error(ex, "Error handling request");
                res.StatusCode = 500;
                res.Close();
            }
        }

        private void ServeHtml(HttpListenerResponse res)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(_htmlContent);
            res.ContentType = "text/html";
            res.ContentLength64 = buffer.Length;
            res.OutputStream.Write(buffer, 0, buffer.Length);
            res.Close();
        }

        private async Task DispatchApi(HttpListenerContext context)
        {
            var req = context.Request;
            var path = req.Url.AbsolutePath.ToLower();
            object response = null;

            try 
            {
                switch (path)
                {
                    case "/api/status":
                        response = _yunService.GetPlaybackStatus();
                        break;
                    case "/api/system":
                        response = await _yunService.GetPluginStatus();
                        break;
                    case "/api/control":
                        if (req.HttpMethod == "POST") response = await HandleControl(req);
                        break;
                    case "/api/setmode":
                        if (req.HttpMethod == "POST") response = await HandleSetMode(req);
                        break;
                    case "/api/cmd":
                         if (req.HttpMethod == "POST") response = await HandleCmd(req);
                         break;
                    case "/api/login":
                         if (req.HttpMethod == "POST") response = await HandleLogin(req);
                         break;
                    case "/api/search":
                         if (req.HttpMethod == "GET") response = await HandleSearch(req);
                         break;
                    default:
                        context.Response.StatusCode = 404;
                        response = new { error = "Endpoint not found" };
                        break;
                }
            } 
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                response = new { error = ex.Message };
            }

            if (response != null)
            {
                SendJson(context.Response, response);
            }
            else
            {
                context.Response.Close();
            }
        }

        private void SendJson(HttpListenerResponse res, object data)
        {
            string json = JsonConvert.SerializeObject(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json";
            res.ContentEncoding = Encoding.UTF8;
            res.ContentLength64 = buffer.Length;
            res.OutputStream.Write(buffer, 0, buffer.Length);
            res.Close();
        }

        private async Task<object> HandleControl(HttpListenerRequest req)
        {
            string action = req.QueryString["action"];
            switch (action)
            {
                case "next":
                    await _yunService.PlayNextMusic();
                    break;
                case "stop":
                    await _yunService.Stop();
                    break;
                case "pause":
                    _yunService.SetPaused(true);
                    break;
                case "resume":
                    _yunService.SetPaused(false);
                    break;
                case "clear":
                    _yunService.Clear();
                    break;
                default:
                    throw new Exception("Unknown action");
            }
            return new { success = true };
        }

        private async Task<object> HandleSetMode(HttpListenerRequest req)
        {
            string m = req.QueryString["mode"];
            if (int.TryParse(m, out int modeVal))
            {
                string result = await _yunService.SetMode(modeVal);
                if (result.Contains("正确的播放模式"))
                    return new { success = false, error = result };
                return new { success = true, msg = result };
            }
            return new { success = false, error = "Invalid mode" };
        }

        private async Task<object> HandleCmd(HttpListenerRequest req)
        {
            string type = req.QueryString["type"];
            string query = req.QueryString["query"];

            global::YunPlugin.YunPlugin.GetLogger("Web").Info($"Cmd received: type={type}, query={query}");

            if (type == "reload")
            {
                return new { success = true, msg = _yunService.ReloadConfig() };
            }
            
            if (string.IsNullOrWhiteSpace(query))
            {
                return new { success = false, error = "请输入内容" };
            }

            if (type.StartsWith("play") || type.StartsWith("add"))
            {
                try 
                {
                    // Define subType early so it can be used in the manual parsing block
                    string subType = type.Split('_').Length > 1 ? type.Split('_')[1] : "auto";

                    // Manual parsing to avoid "Limit" ambiguity in ProcessArgs
                    var argsParts = query.Split(new[] { ' ' }, 2);
                    var input = new CommandArgs();
                    
                    if (argsParts.Length == 2)
                    {
                        var apiName = argsParts[0];
                        var data = argsParts[1];
                        var apiContainer = _yunService.GetApiConfig(apiName); // Need to expose GetApiConfig in YunService or access Config directly

                        if (apiContainer != null && apiContainer.Enable)
                        {
                            input.Api = _yunService.GetApiInterface(apiContainer.Type); // Need to expose GetApiInterface
                            input.Data = data;
                            input.InputData = input.Api.GetInputData(data);

                            // Force correct type for explicit commands from WebUI
                            if (subType == "album") 
                            {
                                input.InputData.Type = global::YunPlugin.api.MusicUrlType.Album;
                                input.InputData.Id = data;
                            }
                            else if (subType == "list") 
                            {
                                input.InputData.Type = global::YunPlugin.api.MusicUrlType.PlayList;
                                input.InputData.Id = data;
                            }
                            else if (subType == "song")
                            {
                                // Force Music type for songs to avoid issues with QQ Music IDs being misidentified
                                input.InputData.Type = global::YunPlugin.api.MusicUrlType.Music;
                                input.InputData.Id = data;
                            }
                        }
                        else
                        {
                            // Fallback to ProcessArgs if API not found (e.g. pure search query)
                            input = _yunService.ProcessArgs(query, 2);
                        }
                    }
                    else
                    {
                         input = _yunService.ProcessArgs(query, 2);
                    }

                    string err = null;
                    bool isAdd = type.StartsWith("add");

                    // Logging for debug
                    global::YunPlugin.YunPlugin.GetLogger("Web").Info($"Executing {type}: Api={input.Api?.Name}, Data={input.Data}, Type={input.InputData?.Type}");

                    if (subType == "song" || subType == "podcast" || subType == "auto") 
                    {
                        err = isAdd ? await _yunService.Add(input) : await _yunService.Play(input);
                    }
                    else if (subType == "list")
                    {
                        err = await _yunService.PlayPlaylist(input, isAdd);
                    }
                    else if (subType == "album")
                    {
                        err = await _yunService.PlayAlbum(input, isAdd);
                    }

                    if (err == null || (!err.Contains("失败") && !err.Contains("未找到") && !err.Contains("错误")))
                        return new { success = true, msg = err ?? "操作成功" };
                    return new { success = false, error = err };
                }
                catch(Exception ex)
                {
                     return new { success = false, error = ex.Message };
                }
            }
            else if (type == "add")
            {
                try
                {
                    var input = _yunService.ProcessArgs(query, 2);
                    string msg = await _yunService.Add(input);
                    return new { success = true, msg = msg };
                }
                catch(Exception ex)
                {
                    return new { success = false, error = ex.Message };
                }
            }
            
            return new { success = false, error = "Unknown command type" };
        }

        private async Task<object> HandleLogin(HttpListenerRequest req)
        {
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            {
                string body = await reader.ReadToEndAsync();
                dynamic data = JsonConvert.DeserializeObject(body);
                string platform = data.platform;
                string argsStr = data.args;
                string[] args = Utils.ProcessArgs(argsStr); 

                try 
                {
                    string result = await _yunService.Login(platform, args);
                    return new { success = true, msg = result };
                }
                catch(Exception ex)
                {
                    return new { success = false, error = ex.Message };
                }
            }
        }

        private async Task<object> HandleSearch(HttpListenerRequest req)
        {
            string keyword = req.QueryString["q"];
            string type = req.QueryString["type"] ?? "song";
            string limitStr = req.QueryString["limit"] ?? "10";
            if (!int.TryParse(limitStr, out int limit)) limit = 10;
            if (limit <= 0) limit = 10;
            if (limit > 50) limit = 50;

            if (string.IsNullOrWhiteSpace(keyword))
            {
                return new { success = false, error = "请输入搜索关键词" };
            }

            try
            {
                var results = await _yunService.SearchAll(keyword, type, limit);
                return new { success = true, data = results };
            }
            catch(Exception ex)
            {
                return new { success = false, error = ex.Message };
            }
        }

        public void Dispose()
        {
            _isRunning = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}