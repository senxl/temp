using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Events.EventArgs;
using Lagrange.Core.Message;

namespace QqBridge;

/// <summary>
/// Lagrange.Core V2 的极简收发通道：
/// 终端扫码登录 -> keystore 落地复用 -> 好友私聊消息写入 data/inbox.jsonl
/// -> 本地 HTTP API 供外部脚本收发（POST /send, GET /messages）。
/// </summary>
public sealed class Bridge
{
    private const string DataDir = "data";

    private readonly object _inboxLock = new();
    private readonly List<InboxItem> _inbox = new();

    private BotContext? _bot;
    private long _selfUin;
    private bool _online;
    private string? _captchaUrl;
    private string? _qrUrl;
    private string? _deviceVerifyUrl;
    private DateTime _qrUpdatedAt = DateTime.MinValue;

    public static async Task Main(string[] args)
    {
        int port = 8090;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--port=")) port = int.Parse(arg["--port=".Length..]);
        }

        await new Bridge().RunAsync(port);
    }

    private async Task RunAsync(int port)
    {
        Directory.CreateDirectory(DataDir);

        var config = new BotConfig
        {
            Protocol = Protocols.Linux,
            LogLevel = LogLevel.Information,
            AutoReconnect = true,
            AutoReLogin = true,
            GetOptimumServer = true,
            SignProvider = null, // 走内置默认签名服务
        };

        var keystore = LoadKeystore();
        if (keystore.Uin > 0) _selfUin = keystore.Uin;

        var bot = BotFactory.Create(config, keystore);
        _bot = bot;

        // 注意：该框架的事件委托必须是实例方法（其内部基于 handler.Target 生成 DynamicMethod）
        bot.EventInvoker.RegisterEvent<BotQrCodeEvent>(OnQrCode);
        bot.EventInvoker.RegisterEvent<BotQrCodeQueryEvent>(OnQrCodeQuery);
        bot.EventInvoker.RegisterEvent<BotCaptchaEvent>(OnCaptcha);
        bot.EventInvoker.RegisterEvent<BotNewDeviceVerifyEvent>(OnNewDeviceVerify);
        bot.EventInvoker.RegisterEvent<BotRefreshKeystoreEvent>(OnRefreshKeystore);
        bot.EventInvoker.RegisterEvent<BotOnlineEvent>(OnOnline);
        bot.EventInvoker.RegisterEvent<BotOfflineEvent>(OnOffline);
        bot.EventInvoker.RegisterEvent<BotMessageEvent>(OnMessage);

        StartHttpApi(port);

        Console.WriteLine("[bridge] 正在登录（首次需要扫码）...");
        bool ok = await bot.Login(CancellationToken.None);
        if (!ok)
        {
            Console.WriteLine("[bridge] 登录失败，进程退出");
            return;
        }

        _online = true;
        _selfUin = bot.Keystore.Uin;
        Console.WriteLine($"[bridge] 已上线，账号 {_selfUin}，配置已生效");
        await Task.Delay(Timeout.Infinite);
    }

    // ---------------- 事件处理 ----------------

    private void OnQrCode(BotContext ctx, BotQrCodeEvent e)
    {
        File.WriteAllBytes(Path.Combine(DataDir, "qrcode.png"), e.Image);
        File.WriteAllText(Path.Combine(DataDir, "qrcode.url"), e.Url);
        _qrUrl = e.Url;
        _qrUpdatedAt = DateTime.Now;
        Console.WriteLine($"[qr] 二维码已刷新，图片 data/qrcode.png（约 2 分钟内有效）");
    }

    private void OnQrCodeQuery(BotContext ctx, BotQrCodeQueryEvent e)
        => Console.WriteLine($"[qr] 状态: {e.State}");

    private void OnCaptcha(BotContext ctx, BotCaptchaEvent e)
    {
        _captchaUrl = e.CaptchaUrl;
        Console.WriteLine($"[captcha] 需要验证码: {e.CaptchaUrl}");
    }

    private void OnNewDeviceVerify(BotContext ctx, BotNewDeviceVerifyEvent e)
    {
        _deviceVerifyUrl = e.Url;
        Console.WriteLine($"[device] 新设备验证: {e.Url}");
    }

    private void OnRefreshKeystore(BotContext ctx, BotRefreshKeystoreEvent e)
    {
        string path = Path.Combine(DataDir, $"{e.Keystore.Uin}.ks");
        File.WriteAllText(path, JsonSerializer.Serialize(e.Keystore));
        _selfUin = e.Keystore.Uin;
        Console.WriteLine($"[keystore] 已保存，下次免扫码");
    }

    private void OnOnline(BotContext ctx, BotOnlineEvent e)
    {
        _online = true;
        Console.WriteLine("[bridge] BotOnline");
    }

    private void OnOffline(BotContext ctx, BotOfflineEvent e)
    {
        _online = false;
        Console.WriteLine($"[bridge] BotOffline: {e.Reason} {e.Tips?.Tag}");
    }

    private void OnMessage(BotContext ctx, BotMessageEvent e)
    {
        BotMessage msg = e.Message;
        if (msg.Contact is not BotFriend friend) return; // 只处理好友私聊

        string text = msg.Entities
            .OfType<Lagrange.Core.Message.Entities.TextEntity>()
            .Select(t => t.Text)
            .Aggregate(string.Empty, (a, b) => a + b);

        var item = new InboxItem
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            FromUin = friend.Uin,
            FromName = friend.Nickname,
            Text = text,
            MsgId = msg.ClientSequence.ToString(),
        };

        lock (_inboxLock)
        {
            _inbox.Add(item);
        }

        AppendJsonLine(Path.Combine(DataDir, "inbox.jsonl"), item);
        Console.WriteLine($"[in] {friend.Uin}({friend.Nickname}): {text}");
    }

    // ---------------- keystore ----------------

    private static BotKeystore LoadKeystore()
    {
        if (!Directory.Exists(DataDir)) return BotKeystore.CreateEmpty();
        foreach (string file in Directory.GetFiles(DataDir, "*.ks"))
        {
            try
            {
                var ks = JsonSerializer.Deserialize<BotKeystore>(File.ReadAllText(file));
                if (ks is { Uin: > 0 }) return ks;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[keystore] 读取失败 {ex.Message}");
            }
        }
        return BotKeystore.CreateEmpty();
    }

    private static void AppendJsonLine(string path, object payload)
    {
        try
        {
            File.AppendAllText(path, JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[inbox] 写文件失败: {ex.Message}");
        }
    }

    // ---------------- HTTP API ----------------

    private void StartHttpApi(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine($"[api] 已监听 http://127.0.0.1:{port}/");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
        });
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod.ToUpperInvariant();

            switch (path)
            {
                case "/status":
                    await WriteJson(context, new
                    {
                        online = _online,
                        uin = _selfUin,
                        inboxCount = _inbox.Count,
                        qrUrl = _qrUrl,
                        qrUpdatedAt = _qrUrl == null ? null : _qrUpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        captchaUrl = _captchaUrl,
                        deviceVerifyUrl = _deviceVerifyUrl,
                    });
                    return;

                case "/qrcode.png":
                    await WriteFile(context, Path.Combine(DataDir, "qrcode.png"), "image/png");
                    return;

                case "/messages":
                    int since = 0;
                    string? sinceParam = context.Request.QueryString["since"];
                    if (sinceParam != null) since = int.Parse(sinceParam);
                    int total;
                    List<InboxItem> items;
                    lock (_inboxLock)
                    {
                        total = _inbox.Count;
                        items = _inbox.Skip(since).ToList();
                    }
                    await WriteJson(context, new { total, since, items });
                    return;

                case "/send" when method == "POST":
                    await HandleSend(context);
                    return;

                case "/captcha" when method == "POST":
                    await HandleCaptcha(context);
                    return;

                default:
                    context.Response.StatusCode = 404;
                    await WriteJson(context, new { error = "not found", path });
                    return;
            }
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJson(context, new { error = ex.Message });
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleSend(HttpListenerContext context)
    {
        string body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
        var req = JsonSerializer.Deserialize<SendRequest>(body);
        if (req == null || req.Uin <= 0 || string.IsNullOrEmpty(req.Text))
        {
            context.Response.StatusCode = 400;
            await WriteJson(context, new { error = "body 需要 {\"uin\":123456,\"text\":\"...\"}" });
            return;
        }
        if (_bot == null || !_online)
        {
            context.Response.StatusCode = 409;
            await WriteJson(context, new { error = "尚未登录/在线" });
            return;
        }

        var chain = new MessageBuilder().Text(req.Text).Build();
        var sent = await _bot.SendFriendMessage(req.Uin, chain);
        Console.WriteLine($"[out] -> {req.Uin}: {req.Text}");
        await WriteJson(context, new
        {
            ok = true,
            to = req.Uin,
            clientSequence = sent.ClientSequence.ToString(),
        });
    }

    private async Task HandleCaptcha(HttpListenerContext context)
    {
        string body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
        var req = JsonSerializer.Deserialize<CaptchaRequest>(body);
        if (_bot == null || req?.Ticket == null || req.RandStr == null)
        {
            context.Response.StatusCode = 400;
            await WriteJson(context, new { error = "body 需要 {\"ticket\":\"...\",\"randstr\":\"...\"}" });
            return;
        }
        _bot.SubmitCaptcha(req.Ticket, req.RandStr);
        _captchaUrl = null;
        await WriteJson(context, new { ok = true });
    }

    private static async Task WriteJson(HttpListenerContext context, object payload)
    {
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data);
    }

    private static async Task WriteFile(HttpListenerContext context, string path, string contentType)
    {
        if (!File.Exists(path))
        {
            context.Response.StatusCode = 404;
            await WriteJson(context, new { error = "还没有二维码，请稍等" });
            return;
        }
        byte[] data = await File.ReadAllBytesAsync(path);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data);
    }

    // ---------------- DTO ----------------

    private sealed class InboxItem
    {
        public string Time { get; set; } = "";
        public long FromUin { get; set; }
        public string FromName { get; set; } = "";
        public string Text { get; set; } = "";
        public string MsgId { get; set; } = "";
    }

    private sealed class SendRequest
    {
        [JsonPropertyName("uin")] public long Uin { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class CaptchaRequest
    {
        [JsonPropertyName("ticket")] public string? Ticket { get; set; }
        [JsonPropertyName("randstr")] public string? RandStr { get; set; }
    }
}
