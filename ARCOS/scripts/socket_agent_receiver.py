import sys
import socket
from datetime import datetime

sys.stdout.reconfigure(encoding="utf-8")

HOST = '0.0.0.0'
PORT = 2900

print(f"🔌 [TCP Socket 啟動] 正在監聽 0.0.0.0:{PORT} ...", flush=True)

server_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server_sock.bind((HOST, PORT))
server_sock.listen(1)

# 等待連線
conn, addr = server_sock.accept()
now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

# 接收資料（緩衝區 64KB）
raw_data = b""
while True:
    chunk = conn.recv(4096)
    if not chunk:
        break
    raw_data += chunk
    # 若接收到換行符號或大括號結尾，視為傳送完畢
    if b'\n' in chunk or raw_data.strip().endswith(b'}'):
        break

msg = raw_data.decode('utf-8', errors='ignore').strip()

# 回覆 LabVIEW ACK，讓 LabVIEW TCP Read 收到確認 (Windows CRLF)
conn.sendall(b"ACK\r\n")
conn.close()
server_sock.close()


print(f"\n========================================", flush=True)
print(f"🎯 [TCP Socket 攔截成功] 來自: {addr[0]}:{addr[1]}", flush=True)
print(f"時間: {now_str}", flush=True)
print(f"資料長度: {len(msg)} bytes", flush=True)
print(f"JSON 內容:\n{msg}", flush=True)
print(f"========================================\n", flush=True)

print("✅ Socket 資料接收完畢，Python 進程結束，交接給 AI Agent！", flush=True)
sys.exit(0)
