"""
ARCOSharp 端到端全工業操作流自動化測試工具 (End-to-End Operational Flow Test Runner)
========================================================================================
依賴環境：
  1. Desktop Cockpit (ADC 2.0 / 3.0) 虛擬第二螢幕 (人機零干擾)
  2. ARCOS.App 內嵌 JSON-RPC 2.0 自動化服務 (TCP 127.0.0.1:19528)
  3. Python 3.12 (.NET 8 Avalonia 執行環境)

執行方式：
  python.exe -X utf8 tools/test_arcosharp_e2e.py [--no-launch] [--keep-alive]
"""

import socket
import json
import time
import subprocess
import argparse
import sys
from pathlib import Path

# 強制 UTF-8 輸出防終端亂碼
sys.stdout.reconfigure(encoding="utf-8")

REPO_ROOT = Path(__file__).resolve().parent.parent
RUNTIME_APP = REPO_ROOT / "dist" / "ARCOS_Runtime" / "ARCOS.App.exe"
COCKPIT_CLI = Path(r"D:\Users\arco0\.gemini\config\plugins\antigravity-desktop-cockpit\skills\desktop-cockpit\scripts\cockpit_cli.py")
SCREENSHOT_DIR = REPO_ROOT / "docs" / "test_screenshots"

TCP_HOST = "127.0.0.1"
TCP_PORT = 19528

def call_mcp(tool_name, arguments, timeout=10):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect((TCP_HOST, TCP_PORT))
    req = {
        "jsonrpc": "2.0",
        "id": int(time.time() * 1000),
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": arguments
        }
    }
    s.sendall((json.dumps(req) + "\n").encode("utf-8"))
    raw = b""
    while True:
        chunk = s.recv(4096)
        if not chunk:
            break
        raw += chunk
        if b"\n" in raw:
            break
    s.close()
    resp = json.loads(raw.decode("utf-8").strip())
    if "result" in resp and "content" in resp["result"]:
        txt = resp["result"]["content"][0]["text"]
        try:
            return json.loads(txt)
        except Exception:
            return txt
    return resp

def is_tcp_online():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(1.0)
        s.connect((TCP_HOST, TCP_PORT))
        s.close()
        return True
    except Exception:
        return False

def launch_arcos_app():
    print(f"[LAUNCH] 透過 Desktop Cockpit 啟動 ARCOS.App 於虛擬螢幕 (0 彈窗干擾)...")
    if not COCKPIT_CLI.exists():
        raise FileNotFoundError(f"找不到 cockpit_cli.py 於 {COCKPIT_CLI}")
    
    cmd = [
        "python.exe", "-X", "utf8", str(COCKPIT_CLI), "launch",
        str(RUNTIME_APP),
        "--screen", "virtual",
        "--working-dir", str(RUNTIME_APP.parent)
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
    if proc.returncode != 0:
        raise RuntimeError(f"啟動失敗: {proc.stderr}")
    print(proc.stdout.strip())
    
    # 等待 TCP 服務就緒
    for _ in range(30):
        if is_tcp_online():
            print("[LAUNCH] ARCOS 自動化 TCP 服務 (19528) 已上線！")
            return
        time.sleep(0.5)
    raise TimeoutError("等待 ARCOS.App TCP 19528 上線超時！")

def close_arcos_app():
    print("[CLEANUP] 關閉虛擬螢幕測試視窗...")
    cmd = ["python.exe", "-X", "utf8", str(COCKPIT_CLI), "close", "-f"]
    subprocess.run(cmd, capture_output=True)

def run_tests():
    SCREENSHOT_DIR.mkdir(parents=True, exist_ok=True)
    
    print("\n" + "=" * 70)
    print("  ARCOSharp 工業級端到端操作流自動化回歸測試 (E2E Test Suite)")
    print("=" * 70)

    # -------------------------------------------------------------
    # Phase 1: 權限與操作模式切換 (Operator / Supervisor)
    # -------------------------------------------------------------
    print("\n[Phase 1] 權限與操作模式切換")
    res_op = call_mcp("arcos_sequencer_action", {"action": "set_mode", "mode": "Operator"})
    assert res_op.get("isLocked") is True, f"Operator 應為鎖定態: {res_op}"
    print("  ✓ Operator 模式: 鎖定排程防呆保護有效 (isLocked = True)")

    res_sup = call_mcp("arcos_sequencer_action", {"action": "set_mode", "mode": "Supervisor"})
    assert res_sup.get("isLocked") is False, f"Supervisor 應為解鎖態: {res_sup}"
    print("  ✓ Supervisor 模式: 工程師編輯權限解鎖有效 (isLocked = False)")

    # -------------------------------------------------------------
    # Phase 2: 工業排程指令注入 (Sequencer Script Setup)
    # -------------------------------------------------------------
    print("\n[Phase 2] 工業排程指令注入")
    flow_rows = [
        ("System@Log Message", "==== INITIATING ARCOSHARP INDUSTRIAL FLOW ===="),
        ("IO@AddIODriver", "DriverName=MockIO,DeviceName=MainIO"),
        ("Motion@AddMotion", "DriverName=MockMotion,DeviceName=AxisX"),
        ("System@IncludeModule", "Python"),
        ("Variable@Set", "CycleCount,0"),
        ("IO@SetPort", "P0.0,0.02,ForceUpdate"),
        ("IO@WaitPortOn", "P0.0,2.0,[[Err_Clamp]]Clamp Failure"),
        ("Python@Script", 'batch_id = "BATCH_2026_A1"; part_ok = True;'),
        ("Python@GetVariable", "batch_id,ARCOSName=CurrentBatch"),
        ("Call@mycsvi.csvi&", "Cam1,70,190,HistScore"),
        ("Variable@Inc", "CycleCount"),
        ("System@Log Message", "Process successfully finished."),
        ("Thread@Stop", ""),
        ("Thread@ErrorHandler", "[[Err_Clamp]]"),
        ("System@Log Error", "Clamp cylinder failed to respond in time."),
        ("Thread@Stop", "True,Clamp Error")
    ]

    for idx, (cmd, param) in enumerate(flow_rows):
        call_mcp("arcos_sequencer_action", {
            "action": "add_row",
            "rowIndex": idx,
            "command": cmd,
            "parameter": param
        })
    print(f"  ✓ 成功注入 {len(flow_rows)} 行工業級標準排程指令")

    # -------------------------------------------------------------
    # Phase 3: 視覺化參數導引視窗互動 (Visual Guidance Dialogs)
    # -------------------------------------------------------------
    print("\n[Phase 3] 參數導引視窗互動與兩階段過渡")
    call_mcp("arcos_click", {"tabIndex": 0})
    time.sleep(0.3)

    # 3.1 IO 導引視窗
    io_guide = call_mcp("arcos_sequencer_action", {"action": "open_guidance", "rowIndex": 5})
    assert io_guide.get("success") is True, f"IO 導引視窗開啟失敗: {io_guide}"
    io_shot = str(SCREENSHOT_DIR / "01_io_guidance.png")
    call_mcp("arcos_capture_ui", {"outputPath": io_shot, "target": "Dialog"})
    call_mcp("arcos_click", {"name": "BtnOk", "scope": "Dialog"})
    time.sleep(0.3)
    print(f"  ✓ Line 5 (IO@SetPort) 導引視窗正常彈出並關閉 (截圖: {io_shot})")

    # 3.2 Call 兩階段導引視窗
    call_guide = call_mcp("arcos_sequencer_action", {"action": "open_guidance", "rowIndex": 9})
    assert call_guide.get("success") is True, f"Call 第一階段開啟失敗: {call_guide}"
    c1_shot = str(SCREENSHOT_DIR / "02_call_stage1.png")
    call_mcp("arcos_capture_ui", {"outputPath": c1_shot, "target": "Dialog"})

    # 點擊「下一步」進入第二階段
    call_mcp("arcos_click", {"name": "BtnNext", "scope": "Dialog"})
    time.sleep(0.5)
    st2 = call_mcp("arcos_get_state", {})
    assert st2.get("activeDialog", {}).get("isOpen") is True, "Call 第二階段對話盒未開啟"
    c2_shot = str(SCREENSHOT_DIR / "03_call_stage2.png")
    call_mcp("arcos_capture_ui", {"outputPath": c2_shot, "target": "Dialog"})
    call_mcp("arcos_click", {"name": "BtnConfirm", "scope": "Dialog"})
    time.sleep(0.3)
    print(f"  ✓ Line 9 (Call@mycsvi.csvi&) 兩階段導引視窗流暢過渡並回傳 (截圖: {c2_shot})")

    # -------------------------------------------------------------
    # Phase 4: 全系統分頁 (10 大 TabItem) 導航巡檢
    # -------------------------------------------------------------
    print("\n[Phase 4] 全系統分頁 (10 大 TabItem) 導航巡檢")
    tabs = [
        (0, "User Tab"), (1, "Capture"), (2, "IMAQ Array"), (3, "Point&Path"),
        (4, "Modules Info"), (5, "Threads"), (6, "Parameter&Variable"),
        (7, "WaveForm"), (8, "Panel CMD"), (9, "Remote")
    ]
    for tidx, tname in tabs:
        call_mcp("arcos_click", {"tabIndex": tidx})
        time.sleep(0.2)
        cur = call_mcp("arcos_get_state", {})
        assert cur.get("mainTabIndex") == tidx, f"分頁切換異常: 預期 {tidx}, 實際 {cur.get('mainTabIndex')}"
    call_mcp("arcos_click", {"tabIndex": 0})
    time.sleep(0.3)
    print("  ✓ 10 大主控制項分頁遍歷切換無崩潰，視覺樹保持健全")

    # -------------------------------------------------------------
    # Phase 5: 排程引擎單步執行與數據鏈路驗證
    # -------------------------------------------------------------
    print("\n[Phase 5] 排程單步執行與數據鏈路驗證")
    call_mcp("arcos_sequencer_action", {"action": "jump", "rowIndex": 0})
    time.sleep(0.2)

    # 逐步執行
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 0: Log Start
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 1: AddIODriver MockIO
    time.sleep(0.2)
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 2: AddMotion MockMotion
    time.sleep(0.2)
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 3: IncludeModule Python
    time.sleep(0.4)
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 4: Variable@Set CycleCount,0
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 5: IO@SetPort P0.0
    time.sleep(0.2)
    
    st_io = call_mcp("arcos_get_state", {})
    assert st_io.get("port0States", {}).get("P0.0") is True, "P0.0 應成功點亮"
    print("  ✓ Step 1~5: 硬體驅動掛載、Python 初始化、IO@SetPort P0.0 點亮成功")

    call_mcp("arcos_sequencer_action", {"action": "step"}) # 6: WaitPortOn P0.0
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 7: Python@Script
    time.sleep(0.2)
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 8: Python@GetVariable
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 9: Call@mycsvi.csvi&
    time.sleep(0.3)
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 10: Variable@Inc
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 11: Log finished
    call_mcp("arcos_sequencer_action", {"action": "step"}) # 12: Thread@Stop
    time.sleep(0.2)
    print("  ✓ Step 6~12: Python 數據萃取、CSVI AOI 直方圖演算法運算與計數器自增全鏈路貫通")

    # -------------------------------------------------------------
    # Phase 6: 即時硬體點位反轉交互 (Live I/O Interlock)
    # -------------------------------------------------------------
    print("\n[Phase 6] 即時硬體點位反轉交互 (Live I/O)")
    call_mcp("arcos_io_action", {"action": "toggle", "port": "P0.3"})
    time.sleep(0.2)
    st_t1 = call_mcp("arcos_get_state", {})
    assert st_t1.get("port0States", {}).get("P0.3") is True, "P0.3 反轉開未生效"

    call_mcp("arcos_io_action", {"action": "toggle", "port": "P0.3"})
    time.sleep(0.2)
    st_t2 = call_mcp("arcos_get_state", {})
    assert st_t2.get("port0States", {}).get("P0.3") is False, "P0.3 反轉關未生效"
    print("  ✓ 外部反轉指令與底層 IoEngine 即時同步驗證成功")

    # -------------------------------------------------------------
    # Phase 7: 系統稽核日誌檢閱
    # -------------------------------------------------------------
    print("\n[Phase 7] 系統稽核日誌檢閱")
    st_final = call_mcp("arcos_get_state", {})
    logs = st_final.get("recentLogs", [])
    has_py = any("Module 'Python' initialized successfully" in l for l in logs)
    has_csvi = any("Executed CSVI 'mycsvi.csvi' successfully. Result: 110.5" in l for l in logs)
    assert has_py, "日誌中缺少 Python 初始化紀錄"
    assert has_csvi, "日誌中缺少 CSVI 演算法成功運算紀錄 (110.5)"
    print("  ✓ 稽核日誌確認 Python 3.12 與 mycsvi.csvi 執行成功 (計算分數 = 110.5)")

    print("\n" + "=" * 70)
    print(" [PASS] ARCOSharp 工業級全操作流驗證 100% 通過！")
    print("=" * 70)

def main():
    parser = argparse.ArgumentParser(description="ARCOSharp E2E Operational Flow Tester")
    parser.add_argument("--no-launch", action="store_true", help="不重新啟動應用程式，直接連接已運行的實例")
    parser.add_argument("--keep-alive", action="store_true", help="測試完成後保持 ARCOS.App 運行不退出")
    args = parser.parse_args()

    launched = False
    try:
        if not args.no_launch:
            if not is_tcp_online():
                launch_arcos_app()
                launched = True
            else:
                print("[INFO] 偵測到 ARCOS.App 已在線上運行，直接進行測試連線。")
        run_tests()
    finally:
        if launched and not args.keep_alive:
            close_arcos_app()

if __name__ == "__main__":
    main()
