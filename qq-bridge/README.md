# qq-bridge：基于 Lagrange.Core 的 QQ 收发通道

用开源框架 [Lagrange.Core V2](https://github.com/LagrangeDev/Lagrange.Core)（纯 C# 实现的 NTQQ 协议）搭的一个最小可用的收发通道：扫码登录一个 QQ 号，然后把「收消息 / 发消息」暴露成本地 HTTP 接口，其他脚本只管调接口。

## 目录

```
qq-bridge/
├── Program.cs        # 桥接程序：登录、事件订阅、本地 HTTP API
├── channel.py        # 命令行客户端（状态 / 二维码 / 发消息 / 读消息 / 监听）
├── start.sh          # 编译并启动
└── data/             # 运行时数据（自动生成，勿提交）
    ├── qrcode.png        # 框架输出的原始二维码（147px）
    ├── qrcode.url        # 二维码内容 URL
    ├── {uin}.ks          # 登录凭据，保留后下次免扫码
    └── inbox.jsonl       # 收到的好友消息流水
```

依赖的 .NET 10 SDK 装在项目根目录 `.toolchain/dotnet`（不污染系统）。

## 首次登录（需要你扫码）

```bash
./start.sh                     # 前台启动，看到 "[api] 已监听" 即就绪
python3 channel.py qr          # 另开一个终端，把当前二维码渲染成 600px 高清图
```

二维码约 2 分钟有效，过期后框架会自动换取新码；`channel.py qr` 每次都按最新 URL 重新生成，扫码前现取一张即可。扫完后 `data/{uin}.ks` 会落盘，之后重启直接免扫码上线。

## HTTP 接口（默认 127.0.0.1:8090，仅本机）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/status` | 在线状态、账号、待扫码二维码 URL、验证码/新设备验证链接 |
| GET | `/qrcode.png` | 当前二维码原图 |
| GET | `/messages?since=N` | 收件箱第 N 条之后的消息 |
| POST | `/send` | `{"uin": 3889687544, "text": "你好"}` 发送好友私聊 |
| POST | `/captcha` | `{"ticket":"...","randstr":"..."}` 提交登录验证码 |

## 命令行

```bash
python3 channel.py status
python3 channel.py send 3889687544 "在吗"
python3 channel.py messages --since 0
python3 channel.py watch            # 轮询打印新消息，可接自己的回复逻辑
```

## 接自己的逻辑（最小示例）

```python
import json, time, urllib.request

BASE = "http://127.0.0.1:8090"

def get(p):
    return json.loads(urllib.request.urlopen(BASE + p).read())

def send(uin, text):
    data = json.dumps({"uin": uin, "text": text}).encode()
    req = urllib.request.Request(BASE + "/send", data=data,
                                headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req).read())

seen = 0
while True:
    for m in get(f"/messages?since={seen}")["items"]:
        seen += 1
        if m["fromUin"] == 3889687544:
            send(m["fromUin"], f"收到：{m['text']}")   # 换成你的回复逻辑
    time.sleep(2)
```

## 已知边界

- **只做私聊**：当前只订阅并处理好友私聊文本；群消息、图片/文件收发需要再扩 `BotGroup` 分支和 entity 类型。
- **消息丢失窗口**：收件箱在内存里，进程重启后 `/messages` 从 0 开始，历史以 `data/inbox.jsonl` 为准。
- **登录风控**：QQ 对新设备登录、异地登录有验证码/新设备验证，事件会落到 `/status` 的 `captchaUrl`、`deviceVerifyUrl`，需要人工过。
- **协议风险**：非官方协议库存在被风控（掉线、限制登录）的可能，仅建议用自己名下的小号，别拿主号跑，也别做群发/加好友这类批量操作。
- **端口只监听 127.0.0.1**，不要暴露到公网。
