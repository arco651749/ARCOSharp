import sys
import socket
import argparse

sys.stdout.reconfigure(encoding="utf-8")

def send_response(host, port, message):
    # 確保結尾為 Windows 標準 CRLF (\r\n)，符合 LabVIEW TCP Read 行結尾規範
    message = message.rstrip("\r\n") + "\r\n"
    
    data = message.encode("utf-8")
    
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(5.0) # 5 秒連線超時
        s.connect((host, port))
        s.sendall(data)
        s.close()
        print(f"📤 [已送達 MVCon:{port}] {message.strip()}", flush=True)
        return True
    except ConnectionRefusedError:
        print(f"❌ [連線被拒絕] 無法連線至 {host}:{port}，請確認 MVCon 是否已開啟 Port {port} 監聽！", file=sys.stderr)
        return False
    except socket.timeout:
        print(f"⏱️ [連線超時] 連線至 {host}:{port} 超時！", file=sys.stderr)
        return False
    except Exception as e:
        print(f"❌ [傳送失敗] {e}", file=sys.stderr)
        return False

def main():
    parser = argparse.ArgumentParser(description="MVCon AIAgent 訊息與決策回傳工具 (發送至 Port 2901)")
    parser.add_argument("-H", "--host", default="127.0.0.1", help="目標主機 IP (預設 127.0.0.1)")
    parser.add_argument("-p", "--port", type=int, default=2901, help="目標 Port (預設 2901)")
    parser.add_argument("message", nargs="?", default=None, help="欲發送的訊息字串")

    args = parser.parse_args()

    # 支援 pipeline 輸入或命令列引數
    msg = args.message
    if msg is None:
        if not sys.stdin.isatty():
            msg = sys.stdin.read().strip()
        else:
            parser.print_help()
            sys.exit(1)

    success = send_response(args.host, args.port, msg)
    sys.exit(0 if success else 1)

if __name__ == "__main__":
    main()
