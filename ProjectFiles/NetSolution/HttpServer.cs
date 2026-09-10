#region Using directives
using System;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Web;
using System.Xml.XPath;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using FTOptix.WebUI;
using FTOptix.ODBCStore;
#endregion

public class HttpServer : BaseNetLogic
{
    private static HttpListener _listener;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public override void Start()
    {
        // ���HttpListener�Ƿ�֧��
        if (!HttpListener.IsSupported)
        {
            Log.Info(" HttpListener class is not supported.");
            return;
        }
        else
        {
            Log.Info(" HttpListener class is supported.");
        }

        // ����HTTP������ 
        StartServer();
    }

    void StartServer()
    {
        // ��ʼ��HttpListener
        var _linkport = LogicObject.GetVariable("httpserver").Value;
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://" + _linkport + '/'); // ���Ӽ���ǰ׺
        _listener.Start();
        Log.Info("Listening for connections on " + _linkport);

        // ��ʼ�첽��������
        _listener.BeginGetContext(new AsyncCallback(ListenerCallback), _listener);
    }

    private void ListenerCallback(IAsyncResult result)
    {
        // ��ȡ���������������
        HttpListener listener = (HttpListener)result.AsyncState;
        HttpListenerContext context = listener.EndGetContext(result);

        // ��ȡ�������Ӧ����
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        response.Headers.Add("Access-Control-Allow-Origin", "*");
        string filename = context.Request.Url.AbsolutePath.Trim('/');


        // ������Ӧ���� 
        if (filename != "" && filename.Contains('.'))
        {
            try
            {
                // ��ȡ�ļ���չ��������file.jpg�������õ��ľ���jpg
                string[] ext_list = filename.Split('.');
                string ext = ext_list.Length > 1 ? ext_list[ext_list.Length - 1] : "";

                // ���ݽ�ϱ�����ԴĿ¼����ַ�е��ļ���ַ���õ�Ҫ���ʵ��ļ��ľ���·��
                var localFilePath = ResourceUri.FromProjectRelativePath("Echarts/").Uri.ToString();
                string absPath = Path.Combine(localFilePath, filename);

                // ������Ӧ״̬��������ҳ��Ӧ�롣ok == 200
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                string expires = DateTime.Now.AddYears(10).ToString("r");
                LOG(absPath);

                switch (ext)
                {
                    case "html":
                    case "htm":
                        context.Response.ContentType = "text/html";
                        break;
                    case "js":
                        context.Response.ContentType = "application/x-javascript";
                        context.Response.AddHeader("cache-control", "max-age=315360000, immutable");
                        context.Response.AddHeader("expires", expires);
                        break;
                    case "css":
                        context.Response.ContentType = "text/css";
                        context.Response.AddHeader("cache-control", "max-age=315360000, immutable");
                        context.Response.AddHeader("expires", expires);
                        break;
                    case "jpg":
                    case "jpeg":
                    case "jpe":
                        context.Response.ContentType = "image/jpeg";
                        context.Response.AddHeader("cache-control", "max-age=315360000, immutable");
                        context.Response.AddHeader("expires", expires);
                        break;
                    case "png":
                        context.Response.ContentType = "image/png";
                        context.Response.AddHeader("cache-control", "max-age=315360000, immutable");
                        context.Response.AddHeader("expires", expires);
                        break;
                    case "gif":
                        context.Response.ContentType = "image/gif";
                        context.Response.AddHeader("cache-control", "max-age=315360000, immutable");
                        context.Response.AddHeader("expires", expires);
                        break;
                    case "ico":
                        context.Response.ContentType = "application/x-ico";
                        context.Response.AddHeader("cache-control", "max-age=315360000, immutable");
                        context.Response.AddHeader("expires", expires);
                        break;
                    case "txt":
                        context.Response.ContentType = "text/plain";
                        break;
                    case "do":
                        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                        context.Response.ContentType = "text/plain;charset=utf-8";
                        break;
                    default:
                        context.Response.ContentType = "";
                        context.Response.AddHeader("cache-control", "max-age=315360000, immutable");
                        context.Response.AddHeader("expires", expires);
                        break;
                }

                // ��֯������
                byte[] msg = new byte[0];
                if (msg.Length == 0)
                {
                    // ���Ŀ���ļ������ھ���ʾ����ҳ��
                    if (!File.Exists(absPath))
                    {
                        context.Response.ContentType = "text/html";
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        if (File.Exists(localFilePath + "error.html"))
                        {
                            msg = File.ReadAllBytes(localFilePath + "error.html");
                        }
                        else
                        {
                            msg = Encoding.Default.GetBytes("404");
                        }
                        Log.Info("File not exist");
                    }
                    // ������ھͽ��ļ�תΪbyte��
                    else
                    {
                        msg = File.ReadAllBytes(absPath);

                        // ������Ӧ���ݳ���
                        response.ContentLength64 = msg.Length;

                        // д����Ӧ���ݵ������
                        using (Stream output = response.OutputStream)
                        {
                            output.Write(msg, 0, msg.Length);
                        }
                        Log.Info(">> send process done ");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("error exist ");
                var responseString = JsonSerializer.Serialize(new
                {
                    error = ex.Message,
                    details = ex.ToString()
                }, JsonOptions); // ����Ĭ��404��Ӧ���� 
                response.StatusCode = (int)HttpStatusCode.InternalServerError; // ������Ӧ״̬��Ϊ404 Not Found  
                byte[] b = Encoding.UTF8.GetBytes(responseString); // ����Ӧ����ת��Ϊ�ֽ����� 
                response.OutputStream.Write(b, 0, b.Length); // ����Ӧ����д������� 
                response.OutputStream.Close(); // �ر������ 
                LOG(ex);

            }
        }
        else
        {
            string query = request.Url.Query;
            NameValueCollection queryParameters = HttpUtility.ParseQueryString(query);

            string pValue = queryParameters["p"];
            if (!string.IsNullOrEmpty(pValue))
            {
                var projectName = Project.Current.BrowseName;
                var p = pValue;
                var splits = p.Split('/');
                var found = false;
                var pathItem = new List<string>();
                for (var i = 0; i < splits.Length; i++)
                {
                    var item = splits[i];
                    if (found)
                    {
                        pathItem.Add(item);
                    }
                    if (item == projectName)
                    {
                        found = true;
                    }
                }
                var nodePath = string.Join('/', pathItem);
                var values = Project.Current.Get(nodePath).ChildrenRemoteRead();
                Dictionary<string, object> chartData = new Dictionary<string, object>
                                {
                                    { "data", values }
                                };
                var dataStr = JsonSerializer.Serialize(chartData, JsonOptions);
                var responseString = dataStr;
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.StatusCode = (int)HttpStatusCode.OK;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            else
            {


                byte[] buffer = Encoding.UTF8.GetBytes("Not Found Path");
                response.ContentLength64 = buffer.Length;
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
        }

        // �����첽�ȴ���һ������
        listener.BeginGetContext(new AsyncCallback(ListenerCallback), listener);
    }

    void LOG(object obj)
    {
        Log.Info(JsonSerializer.Serialize(obj, JsonOptions));
    }

}
