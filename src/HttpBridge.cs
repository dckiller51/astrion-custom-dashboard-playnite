using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AstrionPlaynite
{
    public class HttpRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class HttpResponse
    {
        public int Status;
        public string ContentType;
        public byte[] Body;
        public string Location;
        public int CacheSeconds;

        public HttpResponse(int status, string contentType, byte[] body)
        {
            Status = status;
            ContentType = contentType;
            Body = body ?? new byte[0];
        }

        public static HttpResponse Json(string json)
        {
            return new HttpResponse(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        public static HttpResponse Error(int status, string message)
        {
            var j = new JsonWriter();
            j.Begin();
            j.Prop("error", message);
            j.End();
            return new HttpResponse(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(j.ToString()));
        }

        public static HttpResponse Redirect(string url)
        {
            return new HttpResponse(302, "text/plain", new byte[0]) { Location = url };
        }
    }

    /// <summary>
    /// A minimal HTTP/1.1 server on a plain socket (HttpListener needs an admin URL reservation to
    /// listen beyond localhost). One request per connection; requests are small.
    /// </summary>
    public class HttpBridge
    {
        private readonly int port;
        private readonly Func<HttpRequest, HttpResponse> handler;
        private readonly ILogger log;
        private TcpListener listener;
        private volatile bool running;

        public HttpBridge(int port, Func<HttpRequest, HttpResponse> handler, ILogger log)
        {
            this.port = port;
            this.handler = handler;
            this.log = log;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            running = true;
            new Thread(Loop) { IsBackground = true, Name = "Astrion-http" }.Start();
        }

        public void Stop()
        {
            running = false;
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
            }
        }

        private void Loop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!running) return;
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 10000;
                    var stream = client.GetStream();
                    var req = Read(stream);
                    if (req == null) return;
                    HttpResponse resp;
                    try
                    {
                        resp = handler(req);
                    }
                    catch (Exception e)
                    {
                        log.Error(e, "Astrion Custom Dashboard: " + req.Method + " " + req.Path + " failed");
                        resp = HttpResponse.Error(500, e.Message);
                    }
                    Write(stream, resp);
                }
                catch (Exception e)
                {
                    log.Warn("Astrion Custom Dashboard: a connection failed: " + e.Message);
                }
            }
        }

        private static HttpRequest Read(Stream stream)
        {
            var head = new StringBuilder();
            var buf = new byte[1];
            // Byte by byte up to the blank line: no body is read (none is needed).
            while (head.Length < 16384)
            {
                var n = stream.Read(buf, 0, 1);
                if (n <= 0) return null;
                head.Append((char)buf[0]);
                if (head.Length >= 4 && head[head.Length - 1] == '\n' && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break;
            }
            var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new HttpRequest { Method = first[0].ToUpperInvariant() };
            var target = first[1];
            var q = target.IndexOf('?');
            req.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            for (var i = 1; i < lines.Length; i++)
            {
                var c = lines[i].IndexOf(':');
                if (c > 0) req.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            return req;
        }

        private static void Write(Stream stream, HttpResponse resp)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(resp.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(resp.Status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(resp.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(resp.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (resp.Location != null) sb.Append("Location: ").Append(resp.Location).Append("\r\n");
            sb.Append(resp.CacheSeconds > 0 ? "Cache-Control: max-age=" + resp.CacheSeconds + "\r\n" : "Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(resp.Body, 0, resp.Body.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 302: return "Found";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 404: return "Not Found";
                default: return "Error";
            }
        }
    }

    /// <summary>Just enough JSON writing for the bridge's answers.</summary>
    public class JsonWriter
    {
        private readonly StringBuilder sb = new StringBuilder();
        private bool needComma;

        public void Begin()
        {
            Comma();
            sb.Append('{');
            needComma = false;
        }

        public void End()
        {
            sb.Append('}');
            needComma = true;
        }

        public void BeginArray()
        {
            Comma();
            sb.Append('[');
            needComma = false;
        }

        public void EndArray()
        {
            sb.Append(']');
            needComma = true;
        }

        public void Key(string name)
        {
            Comma();
            Str(name);
            sb.Append(':');
            needComma = false;
        }

        public void Prop(string name, string value)
        {
            Key(name);
            Value(value);
        }

        public void Prop(string name, long value)
        {
            Key(name);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            needComma = true;
        }

        public void Prop(string name, bool value)
        {
            Key(name);
            sb.Append(value ? "true" : "false");
            needComma = true;
        }

        public void Value(string value)
        {
            Comma();
            Str(value);
            needComma = true;
        }

        public void Null()
        {
            Comma();
            sb.Append("null");
            needComma = true;
        }

        private void Comma()
        {
            if (needComma) sb.Append(',');
        }

        private void Str(string s)
        {
            sb.Append('"');
            foreach (var ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        public override string ToString()
        {
            return sb.ToString();
        }
    }
}
