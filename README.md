# lagrange-qq-bridge

在开源框架 [Lagrange.Core V2](https://github.com/LagrangeDev/Lagrange.Core)（纯 C# 实现的 NTQQ 协议）之上做的一个最小 QQ 收发通道：手机扫码登录一个 QQ 号，然后把「收消息 / 发消息」暴露成本地 HTTP 接口，其他脚本只负责调接口。

不用装 QQ 客户端，不依赖 Docker，登录凭据落盘后重启免扫码。

## 结构

```
.
├── qq-bridge/            # 桥接程序（.NET 10）
│   ├── Program.cs        # 登录、事件订阅、本地 HTTP API
│   ├── channel.py        # 命令行客户端（状态/二维码/发消息/读消息/监听）
│   ├── start.sh          # 编译并启动
│   └── README.md         # 接口与用法详解
└── Lagrange.Core/        # 上游框架（git submodule）
```

`qq-bridge.csproj` 通过相对路径 `../Lagrange.Core/Lagrange.Core/Lagrange.Core.csproj` 引用框架，所以克隆时必须带上 submodule。

## 快速开始

```bash
# 1. 装 .NET 10 SDK（示例装到项目内，不污染系统）
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0 --install-dir "$PWD/.toolchain/dotnet" --no-path

# 2. 克隆（含 submodule）
git clone --recurse-submodules https://github.com/senxl/lagrange-qq-bridge.git
cd lagrange-qq-bridge

# 3. 启动，首次需要手机 QQ 扫码
cd qq-bridge && ./start.sh

# 4. 另开终端，把当前二维码渲染成能扫的大图
python3 channel.py qr --out qrcode_big.png

# 5. 登录后收发消息
python3 channel.py send 3889687544 "在吗"
python3 channel.py messages --since 0
```

## 接口

默认只监听 `127.0.0.1:8090`：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/status` | 在线状态、账号、二维码 URL、验证码/新设备验证链接 |
| GET | `/qrcode.png` | 当前二维码原图 |
| GET | `/messages?since=N` | 收件箱第 N 条之后的消息 |
| POST | `/send` | `{"uin": 3889687544, "text": "你好"}` |
| POST | `/captcha` | `{"ticket":"...","randstr":"..."}` |

细节和最小自动化示例见 [qq-bridge/README.md](qq-bridge/README.md)。

## 已知边界

- 目前只处理**好友私聊文本**，群消息、图片、文件未接。
- 收件箱在内存里，进程重启后 `/messages` 从 0 开始，历史以 `data/inbox.jsonl` 为准。
- 非官方协议存在风控概率：仅建议用自己名下的小号，别拿主号跑，别做群发/批量加好友。
- 登录凭据 `data/*.ks` 已加入 `.gitignore`，**不要提交**。

## 免责声明

本项目仅用于学习协议与消息通道实现。使用非官方客户端可能违反腾讯用户协议，风险与后果由使用者自行承担。
