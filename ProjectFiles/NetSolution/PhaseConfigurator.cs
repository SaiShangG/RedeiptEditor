#region Using directives
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.ODBCStore;
#endregion

public class PhaseConfigurator : BaseNetLogic
{
    private const string LogCategory = nameof(PhaseConfigurator);
    private const int JsonLogPreviewLength = 2000;
    private const string HttpPrefix = "http://127.0.0.1:8099/";
    private const string SelectedPhaseConfigJsonPath = "Model/UIData/PhaseConfigurator/SelectedPhaseConfigJson";
    private const string SelectedPhaseConfigFileNamePath = "Model/UIData/PhaseConfigurator/SelectedPhaseConfigFileName";
    private const string PhaseConfigSaveRequestJsonPath = "Model/UIData/PhaseConfigurator/PhaseConfigSaveRequestJson";
    private const string PhaseConfigSaveRequestFileNamePath = "Model/UIData/PhaseConfigurator/PhaseConfigSaveRequestFileName";
    private const string PhaseConfigSaveResultPath = "Model/UIData/PhaseConfigurator/PhaseConfigSaveResult";
    private const string PhaseConfigLoadVersionPath = "Model/UIData/PhaseConfigurator/PhaseConfigLoadVersion";
    private const string GetJsonEndpoint = "/api/phase-config/get-json";
    private const string SaveEndpoint = "/api/phase-config/save";

    private HttpListener _httpListener;
    private CancellationTokenSource _httpCancellation;
    private Task _httpServerTask;
    private ulong _httpSenderId;

    public override void Start()
    {
        RefreshPhaseConfigFileNames();
        StartHttpServer();
    }

    public override void Stop()
    {
        StopHttpServer();
    }

    [ExportMethod]
    public void RefreshPhaseConfigFileNames()
    {
        try
        {
            var targetFolder = Project.Current?.GetObject("Model/UIData/PhaseConfigurator/PhaseConfigFileListJson") as IUAObject;
            if (targetFolder == null)
            {
                Log.Error(LogCategory, "Target folder not found: Model/UIData/PhaseConfigurator/PhaseConfigFileListJson");
                return;
            }

            string phaseConfigDir = ResolvePhaseConfigDirectory();
            if (string.IsNullOrEmpty(phaseConfigDir))
            {
                Log.Error("PhaseConfigurator", "PhaseConfigure directory not found.");
                return;
            }

            foreach (var child in targetFolder.Children.ToList())
                child.Delete();

            var fileNames = Directory
                .GetFiles(phaseConfigDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

            foreach (var fileName in fileNames)
            {
                var variable = InformationModel.MakeVariable(fileName, OpcUa.DataTypes.String);
                variable.Value = fileName;
                targetFolder.Add(variable);
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"RefreshPhaseConfigFileNames failed: {ex.Message}");
        }
    }

    private void StartHttpServer()
    {
        try
        {
            _httpSenderId = LogicObject?.Context?.AssignSenderId() ?? 0;
        }
        catch
        {
            _httpSenderId = 0;
        }

        try
        {
            StopHttpServer();

            _httpCancellation = new CancellationTokenSource();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(HttpPrefix);
            _httpListener.Start();
            _httpServerTask = Task.Run(() => RunHttpServerAsync(_httpCancellation.Token));

            Log.Info(LogCategory, $"Phase config HTTP server started at {HttpPrefix}");
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"Failed to start phase config HTTP server: {ex.Message}");
        }
    }

    private void StopHttpServer()
    {
        try
        {
            _httpCancellation?.Cancel();
        }
        catch
        {
        }

        try
        {
            if (_httpListener?.IsListening == true)
                _httpListener.Stop();
        }
        catch
        {
        }

        try
        {
            _httpListener?.Close();
        }
        catch
        {
        }

        _httpListener = null;
        _httpCancellation = null;
        _httpServerTask = null;
    }

    private async Task RunHttpServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _httpListener != null)
        {
            HttpListenerContext context;
            try
            {
                context = await _httpListener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            try
            {
                await HandleHttpRequestAsync(context);
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteJsonAsync(context.Response, 500, new
                    {
                        ok = false,
                        error = ex.Message
                    });
                }
                catch
                {
                }
            }
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        AddCorsHeaders(response);

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }

        string path = request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(response, 405, new
            {
                ok = false,
                error = "Method not allowed"
            });
            return;
        }

        if (string.Equals(path, GetJsonEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            await HandleGetJsonRequestAsync(request, response);
            return;
        }

        if (string.Equals(path, SaveEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            await HandleSaveRequestAsync(request, response);
            return;
        }

        await WriteJsonAsync(response, 404, new
        {
            ok = false,
            error = "Endpoint not found"
        });
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task HandleGetJsonRequestAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body = await ReadBodyAsync(request);
        var payload = DeserializePayload<GetJsonRequestPayload>(body);
        string fileName = ValidatePhaseConfigFileName(payload?.FileName);
        string filePath = ResolvePhaseConfigFilePath(fileName);
        string json = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8);

        JsonDocument.Parse(json).Dispose();

        WriteStringVariable(SelectedPhaseConfigFileNamePath, fileName);
        WriteStringVariable(SelectedPhaseConfigJsonPath, json);
        int nextVersion = IncrementLoadVersion();

        await WriteJsonAsync(response, 200, new
        {
            ok = true,
            fileName,
            json,
            version = nextVersion
        });
    }

    private async Task HandleSaveRequestAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body = await ReadBodyAsync(request);
        var payload = DeserializePayload<SavePhaseConfigPayload>(body);
        string fileName = ValidatePhaseConfigFileName(payload?.FileName);
        string json = payload?.Json ?? string.Empty;

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("json is required.");

        JsonDocument.Parse(json).Dispose();

        string filePath = ResolvePhaseConfigFilePath(fileName);
        Log.Info(LogCategory,
            $"Save request received. fileName={fileName}, filePath={filePath}, jsonLength={json.Length}, jsonPreview={BuildJsonPreview(json)}");

        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8);

        WriteStringVariable(SelectedPhaseConfigFileNamePath, fileName);
        WriteStringVariable(SelectedPhaseConfigJsonPath, json);
        WriteStringVariable(PhaseConfigSaveRequestFileNamePath, fileName);
        WriteStringVariable(PhaseConfigSaveRequestJsonPath, json);

        string resultMessage = $"Saved {fileName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        WriteStringVariable(PhaseConfigSaveResultPath, resultMessage);
        RefreshPhaseConfigFileNames();
        Log.Info(LogCategory, $"Save request completed. fileName={fileName}, filePath={filePath}");

        await WriteJsonAsync(response, 200, new
        {
            ok = true,
            fileName,
            message = resultMessage
        });
    }

    private static T DeserializePayload<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Request body is required.");

        try
        {
            return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Request body is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Request body is not valid JSON: {ex.Message}");
        }
    }

    private static string ValidatePhaseConfigFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("fileName is required.");

        string normalizedFileName = Path.GetFileName(fileName.Trim());
        if (!string.Equals(normalizedFileName, fileName.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("fileName must not contain path segments.");

        return normalizedFileName;
    }

    private static string BuildJsonPreview(string json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;

        string normalized = json.Replace("\r", " ").Replace("\n", " ");
        if (normalized.Length <= JsonLogPreviewLength)
            return normalized;

        return normalized.Substring(0, JsonLogPreviewLength) + "...";
    }

    private static string ResolvePhaseConfigFilePath(string fileName)
    {
        string phaseConfigDir = ResolvePhaseConfigDirectory();
        if (string.IsNullOrEmpty(phaseConfigDir))
            throw new DirectoryNotFoundException("PhaseConfigure directory not found.");

        string filePath = Path.Combine(phaseConfigDir, fileName);
        string fullDirectoryPath = Path.GetFullPath(phaseConfigDir);
        string fullFilePath = Path.GetFullPath(filePath);

        if (!fullFilePath.StartsWith(fullDirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved file path is outside the PhaseConfigure directory.");

        return fullFilePath;
    }

    private int IncrementLoadVersion()
    {
        try
        {
            var variable = Project.Current?.GetVariable(PhaseConfigLoadVersionPath);
            if (variable == null)
                return 0;

            int currentValue = 0;
            object rawValue = variable.Value.Value;
            if (rawValue is int typedValue)
                currentValue = typedValue;
            else if (rawValue != null)
                int.TryParse(rawValue.ToString(), out currentValue);

            int nextValue = currentValue + 1;
            WriteVariableValue(variable, nextValue);
            return nextValue;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"IncrementLoadVersion failed: {ex.Message}");
            return 0;
        }
    }

    private void WriteStringVariable(string variablePath, string value)
    {
        var variable = Project.Current?.GetVariable(variablePath)
            ?? throw new InvalidOperationException($"Variable not found: {variablePath}");

        WriteVariableValue(variable, value ?? string.Empty);
    }

    private void WriteVariableValue(IUAVariable variable, object value)
    {
        UAValue uaValue = value switch
        {
            int intValue => new UAValue(intValue),
            string stringValue => new UAValue(stringValue),
            _ => new UAValue(value)
        };

        if (_httpSenderId != 0)
        {
            using (LogicObject.Context.SetCurrentThreadSenderId(_httpSenderId))
                variable.Value = uaValue;
            return;
        }

        variable.Value = uaValue;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = System.Text.Encoding.UTF8;
        response.ContentLength64 = bytes.Length;

        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.Close();
    }

    private static string ResolvePhaseConfigDirectory()
    {
        try
        {
            string projectDir = Environment.GetEnvironmentVariable("PROJECTDIR");
            if (!string.IsNullOrEmpty(projectDir))
            {
                string candidate = Path.Combine(projectDir, "PhaseConfigure");
                if (Directory.Exists(candidate))
                    return candidate;

                candidate = Path.Combine(projectDir, "ProjectFiles", "PhaseConfigure");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            string asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            for (var dir = string.IsNullOrEmpty(asmDir) ? null : new DirectoryInfo(asmDir); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "ProjectFiles", "PhaseConfigure");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
        }

        return null;
    }

    private sealed class GetJsonRequestPayload
    {
        public string FileName { get; set; }
    }

    private sealed class SavePhaseConfigPayload
    {
        public string FileName { get; set; }
        public string Json { get; set; }
    }
}
