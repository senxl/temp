#!/usr/bin/env python3
"""qq-bridge 的命令行客户端：查询状态、发送好友消息、读取收件箱。

用法示例：
    python3 channel.py status
    python3 channel.py qr --out qrcode_big.png
    python3 channel.py send 3889687544 "在吗"
    python3 channel.py messages --since 0
    python3 channel.py watch
"""
import argparse
import json
import sys
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:8090"


def _request(req, timeout):
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors="replace")
        try:
            payload = json.loads(body)
        except json.JSONDecodeError:
            payload = {"error": body}
        print(f"HTTP {e.code}: {json.dumps(payload, ensure_ascii=False)}", file=sys.stderr)
        sys.exit(1)
    except urllib.error.URLError as e:
        print(f"连不上 qq-bridge（{BASE}）：{e.reason}，先运行 ./start.sh", file=sys.stderr)
        sys.exit(1)


def _get(path):
    return _request(BASE + path, timeout=10)


def _post(path, payload):
    data = json.dumps(payload).encode()
    req = urllib.request.Request(
        BASE + path, data=data, headers={"Content-Type": "application/json"}
    )
    return _request(req, timeout=30)


def cmd_status(_args):
    print(json.dumps(_get("/status"), ensure_ascii=False, indent=2))


def cmd_send(args):
    result = _post("/send", {"uin": int(args.uin), "text": args.text})
    print(json.dumps(result, ensure_ascii=False))


def cmd_messages(args):
    data = _get(f"/messages?since={args.since}")
    for item in data["items"]:
        print(f"[{item['time']}] {item['fromUin']}({item['fromName']}): {item['text']}")
    print(f"-- 共 {data['total']} 条，本次返回 {len(data['items'])} 条")


def cmd_watch(args):
    seen = args.since
    print(f"开始监听新消息（从第 {seen} 条之后）... Ctrl+C 结束")
    while True:
        data = _get(f"/messages?since={seen}")
        for item in data["items"]:
            print(f"[{item['time']}] {item['fromUin']}({item['fromName']}): {item['text']}")
            seen += 1
        time.sleep(args.interval)


def cmd_qr(args):
    status = _get("/status")
    url = status.get("qrUrl")
    if not url:
        print("当前没有待扫码的二维码（可能已登录）", file=sys.stderr)
        return 1
    import qrcode
    img = qrcode.make(url, box_size=10, border=4)
    img.save(args.out)
    print(f"已生成高清二维码 {args.out}（{img.size[0]}x{img.size[1]}），"
          f"生成时间 {status.get('qrUpdatedAt')}")
    return 0


def main():
    parser = argparse.ArgumentParser(description="qq-bridge 客户端")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("status").set_defaults(func=cmd_status)

    p = sub.add_parser("send")
    p.add_argument("uin", help="目标 QQ 号")
    p.add_argument("text", help="消息内容")
    p.set_defaults(func=cmd_send)

    p = sub.add_parser("messages")
    p.add_argument("--since", type=int, default=0, help="从第几条之后开始读（0 表示全部）")
    p.set_defaults(func=cmd_messages)

    p = sub.add_parser("watch")
    p.add_argument("--since", type=int, default=0)
    p.add_argument("--interval", type=float, default=2.0)
    p.set_defaults(func=cmd_watch)

    p = sub.add_parser("qr")
    p.add_argument("--out", default="qrcode_big.png")
    p.set_defaults(func=cmd_qr)

    args = parser.parse_args()
    sys.exit(args.func(args) or 0)


if __name__ == "__main__":
    main()
