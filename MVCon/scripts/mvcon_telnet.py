import socket
import sys
import argparse
import json
import re

from mvcon_parse import split_commands

# 強制 UTF-8 輸出，避免 Windows 終端機亂碼
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

parser = argparse.ArgumentParser(
    description="MVCon Telnet 通訊與遠端指令調度工具",
    add_help=False
)
# 將 --help 保留，-h 讓位給 --host
parser.add_argument('--help', action='help', help="顯示此說明訊息")
parser.add_argument('-h', '--host', type=str, default="127.0.0.1", help="目標 MVCon 伺服器 IP (預設 127.0.0.1)")
parser.add_argument('-p', '--port', type=int, required=True, help="通訊埠號碼 (2800~2812)")
parser.add_argument('--dry-run', action='store_true', help="只印出實際要傳送的指令內容，不連線")
parser.add_argument('--json', action='store_true', help="將 $Get* 回傳結果額外以 JSON 格式印出")
parser.add_argument('text', type=str, help="欲傳送之核心指令字串 (如 '$Execute,...')")

args = parser.parse_args()
HOST = args.host
PORT = args.port

raw_text = args.text.strip()
prefix = raw_text.split(',', 1)[0].strip()


def build_payload(text, prefix):
    """組出單行的 Telnet 指令。Telnet 以 \\r\\n 作為一筆指令的結尾，因此內容不可含換行。"""
    if prefix.upper() == '$EXECUTE':
        body = text.split(',', 1)[1] if ',' in text else ''
        try:
            commands = split_commands(body)
        except ValueError as e:
            print(f"? Script error: {e}")
            sys.exit(2)
        multiline = [c for c in commands if '\n' in c]
        if multiline:
            first = multiline[0].split('\n', 1)[0]
            print("? Multiline command not supported via Telnet: "
                  f"「{first} ...」內含換行（例如 Python@Script）。"
                  "Telnet $Execute 為單行協議，請改用 Python@File 或編譯成 .init 執行。")
            sys.exit(2)
        return '$Execute,' + ';'.join(commands)

    if '\n' in text or '\r' in text:
        print(f"? {prefix} 指令不可包含換行。")
        sys.exit(2)
    return text


def parse_get_response(prefix, line):
    """將 $Get* 的第二行回傳字串解析為結構化字典（支援多點位陣列與大小寫）。"""
    prefix_upper = prefix.upper()
    if prefix_upper == '$GETIO':
        # 格式: IO: {P0.0: 1}, IO: {P0.1: 0}
        result = {}
        for name, val in re.findall(r'IO:\s*\{([^:]+):\s*([0-9]+)\}', line):
            result[name.strip()] = int(val)
        return result

    elif prefix_upper == '$GETVARIABLE':
        # 格式: Variable: {Var1: abc}, Variable: {Var2: cde}
        result = {}
        for name, val in re.findall(r'Variable:\s*\{([^:]+):\s*([^}]+)\}', line):
            result[name.strip()] = val.strip()
        return result

    elif prefix_upper == '$GETPOINT':
        # 格式 (單點): Point: {Pick: [...]}, PointIndex: {Pick: 0}
        # 格式 (多點): Point: {PT: [...]}, Point: {PT: [...]}, PointIndex: {PT: 0}
        # 或找不到點位: PointIndex: {NotExist: -1}
        result = {}
        for name, coords_str in re.findall(r'Point:\s*\{([^:]+):\s*\[([^\]]+)\]\}', line):
            name = name.strip()
            coords = [float(x.strip()) for x in coords_str.split(',') if x.strip()]
            if name not in result:
                result[name] = {"points": []}
            result[name]["points"].append(coords)

        for name, idx_str in re.findall(r'PointIndex:\s*\{([^:]+):\s*(-?[0-9]+)\}', line):
            name = name.strip()
            if name not in result:
                result[name] = {"points": []}
            result[name]["index"] = int(idx_str)

        # 整理點位資訊：提供 points 陣列、目前 index、總點數 count 與目前 coords
        for name, data in result.items():
            pts = data.get("points", [])
            idx = data.get("index", 0)
            data["count"] = len(pts)
            if len(pts) == 1:
                data["coords"] = pts[0]
            elif len(pts) > 1:
                if 0 <= idx < len(pts):
                    data["coords"] = pts[idx]
                else:
                    data["coords"] = pts[0]
        return result

    elif prefix_upper == '$GETMOTIONSTATE':
        # 格式: MotionState: { test: { State: Idle, ErrorCode: }
        result = {}
        match = re.search(r'MotionState:\s*\{\s*([^:]+):\s*\{\s*State:\s*([^,]+),\s*ErrorCode:\s*([^}]*)', line)
        if match:
            name = match.group(1).strip()
            state = match.group(2).strip()
            err = match.group(3).strip()
            result[name] = {
                "State": state,
                "ErrorCode": err
            }
        return result

    return None


payload = build_payload(raw_text, prefix)

if args.dry_run:
    print(payload)
    sys.exit(0)

CMD = payload + "\r\n"

try:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(5.0)
    s.connect((HOST, PORT))
    f = s.makefile('r', encoding='utf-8', errors='replace')
except ConnectionRefusedError:
    print(f"? Connection refused: 無法連線至 {HOST}:{PORT}，請確認 MVCon 系統是否啟動且通訊埠已開放。")
    sys.exit(1)
except socket.timeout:
    print(f"? Connection timeout: 連線至 {HOST}:{PORT} 超時。")
    sys.exit(1)
except Exception as e:
    print(f"? Socket error: {e}")
    sys.exit(1)

exit_code = 0

try:
    prefix_upper = prefix.upper()
    if prefix_upper == '$EXECUTE':
        s.send(CMD.encode('utf-8'))
        resp1 = f.readline().strip()
        print(resp1)
        if resp1.startswith('?'):
            exit_code = 2
        else:
            s.settimeout(120.0)
            resp2 = f.readline().strip()
            print(resp2)
            # 解析第二行 JSON 酬載
            payload_str = resp2.lstrip('#').strip()
            if payload_str:
                try:
                    data = json.loads(payload_str)
                    if isinstance(data, dict) and data.get("ErrorMessage"):
                        err = data["ErrorMessage"]
                        print(f"? Machine Execution Exception: {err}")
                        exit_code = 3
                except json.JSONDecodeError:
                    pass
    elif prefix_upper in ['$SETIO', '$SETPOINT', '$SETVARIABLE']:
        s.send(CMD.encode('utf-8'))
        resp = f.readline().strip()
        print(resp)
        if resp.startswith('?'):
            exit_code = 2
    elif prefix_upper in ['$GETIO', '$GETPOINT', '$GETVARIABLE', '$GETMOTIONSTATE']:
        s.send(CMD.encode('utf-8'))
        resp = f.readline().strip()
        print(resp)
        if resp.startswith('?'):
            exit_code = 2
        else:
            resp2 = f.readline().strip()
            print(resp2)
            parsed = parse_get_response(prefix, resp2)
            if args.json and parsed is not None:
                print(json.dumps(parsed, ensure_ascii=False, indent=2))

            # 檢查運動狀態異常碼
            if prefix_upper == '$GETMOTIONSTATE' and parsed:
                for axis, info in parsed.items():
                    if info.get("ErrorCode"):
                        print(f"? Machine Execution Exception: {axis} 異常碼 '{info['ErrorCode']}'")
                        exit_code = 3
            # 檢查點位是否存在 (PointIndex == -1 代表點位未定義)
            elif prefix_upper == '$GETPOINT' and parsed:
                missing = [name for name, data in parsed.items() if data.get("index") == -1]
                if missing:
                    print(f"? Point Not Found: 點位不存在: {', '.join(missing)}")
                    exit_code = 2
    else:
        print('? Command not support!')
        exit_code = 2
except socket.timeout:
    print("? Execution timeout: 等待 MVCon 伺服器回應超時。")
    exit_code = 1
except Exception as e:
    print(f"? Execution error: {e}")
    exit_code = 1
finally:
    try:
        f.close()
        s.close()
    except Exception:
        pass

if exit_code != 0:
    sys.exit(exit_code)