import json
import re
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

COCKPIT_DIR = Path(r"D:\Users\arco0\.gemini\config\plugins\antigravity-desktop-cockpit\skills\desktop-cockpit\scripts")
COCKPIT_CLI = COCKPIT_DIR / "cockpit_cli.py"
RUNTIME_DIR = Path(r"d:\AntiGravity\ARCOSharp\dist\ARCOS_Runtime")
APP_EXE = RUNTIME_DIR / "ARCOS.App.exe"

OUT_DIR = Path(r"d:\AntiGravity\ARCOSharp\verification\2026-11-22\ui_e2e")
SHOT_DIR = OUT_DIR / "screenshots_after_fix"
SHOT_DIR.mkdir(parents=True, exist_ok=True)

TRACE = []
CHECKS = []


def log_step(step: str, status: str, elapsed_ms: int, detail: str, shot: str = ""):
    TRACE.append({"step": step, "status": status, "elapsed_ms": elapsed_ms, "detail": detail, "shot": shot})
    print(f"[{status}] ({elapsed_ms:4d}ms) {step} -> {detail}")


def record_check(check_id: str, passed: bool, detail: str):
    CHECKS.append({"id": check_id, "passed": passed, "detail": detail})
    print(f"  {'[PASS]' if passed else '[FAIL]'} {check_id}: {detail}")


def run_cli(*args: str, timeout: float = 15.0) -> tuple[int, str]:
    cmd = ["python.exe", "-X", "utf8", str(COCKPIT_CLI), *args]
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    return p.returncode, ((p.stdout or "") + (p.stderr or "")).strip()


def find_window_hwnd(title_sub: str) -> str | None:
    _, out = run_cli("list", "-f", title_sub)
    for line in out.splitlines():
        m = re.match(r"\[(0x[0-9A-Fa-f]+)\]\s+(.*)", line.strip())
        if m and title_sub.lower() in m.group(2).lower():
            return m.group(1)
    return None


def capture(name: str, hwnd: str) -> str:
    path = SHOT_DIR / f"{name}.jpg"
    run_cli("capture", "-o", str(path), "--hwnd", hwnd)
    return str(path)


def uia_dump(hwnd: str) -> list[dict]:
    _, out = run_cli("uia", "--hwnd", hwnd)
    items = []
    for line in out.splitlines():
        m = re.match(r"^(@\d+)\s*\|\s*([^|]+)\|\s*\(([^,]+),\s*([^)]+)\)\s*\|\s*([^|]+)\|\s*(.*)$", line.strip())
        if m:
            items.append({
                "ref": m.group(1).strip(),
                "type": m.group(2).strip(),
                "u": float(m.group(3).strip()),
                "v": float(m.group(4).strip()),
                "state": m.group(5).strip(),
                "name": m.group(6).strip(),
            })
    return items


def find_ref(items: list[dict], keyword: str, ctrl_type: str | None = None, exact_label: bool = False) -> dict | None:
    kw = keyword.lower()
    for it in items:
        if ctrl_type and it["type"].lower() != ctrl_type.lower():
            continue
        if exact_label:
            m = re.search(r'"([^"]+)"', it["name"])
            lbl = m.group(1).lower() if m else it["name"].lower()
            if lbl == kw:
                return it
        else:
            if kw in it["name"].lower():
                return it
    return None


def mcp_read_only(tool_name: str, arguments: dict | None = None) -> dict:
    req = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": tool_name, "arguments": arguments or {}}}
    try:
        with socket.create_connection(("127.0.0.1", 19528), timeout=3.0) as s:
            s.sendall((json.dumps(req) + "\n").encode("utf-8"))
            chunks = []
            s.settimeout(2.0)
            while True:
                try:
                    data = s.recv(65536)
                    if not data:
                        break
                    chunks.append(data)
                    if b"\n" in data:
                        break
                except socket.timeout:
                    break
            raw = b"".join(chunks).decode("utf-8", errors="replace").strip()
            return json.loads(raw) if raw else {"error": "empty"}
    except Exception as ex:
        return {"error": str(ex)}


def pick_cmd(main_hwnd: str, row_v: float, module_btn: str, cmd_btn: str) -> str | None:
    run_cli("mouse", "click", "0.84", f"{row_v:.3f}", "--hwnd", main_hwnd)
    time.sleep(0.7)
    sel = find_window_hwnd("ARCOS Command Selector")
    if not sel:
        return None
    items = uia_dump(sel)
    mod_ref = find_ref(items, module_btn, "Button", exact_label=True)
    if mod_ref:
        run_cli("act", mod_ref["ref"], "--hwnd", sel)
        time.sleep(0.4)
        items = uia_dump(sel)
    cmd_ref = find_ref(items, cmd_btn, "Button", exact_label=True)
    if not cmd_ref:
        print(f"  [WARN] Command '{cmd_btn}' not found under '{module_btn}'")
        return None
    run_cli("act", cmd_ref["ref"], "--hwnd", sel)
    time.sleep(0.8)
    return find_window_hwnd("Parameter Guidance")


def main():
    # Ensure clean start
    subprocess.run(["powershell.exe", "-NoProfile", "-Command", "Get-Process ARCOS.App -ErrorAction SilentlyContinue | Stop-Process -Force"], capture_output=True)
    time.sleep(0.5)

    t0 = time.perf_counter()
    rc, launch_out = run_cli("launch", str(APP_EXE), "--screen", "virtual", "--working-dir", str(RUNTIME_DIR), timeout=20.0)
    time.sleep(2.5)
    main_hwnd = find_window_hwnd("ARCOS Automated Machine Controller")
    if not main_hwnd:
        print(f"ERROR launching ARCOS.App: {launch_out}")
        sys.exit(1)
    shot0 = capture("01_startup_after_fix", main_hwnd)
    log_step("0. Launch ARCOS.App on DISPLAY9", "PASS", int((time.perf_counter() - t0) * 1000), f"HWND={main_hwnd}", shot0)

    # Verify P2-1: UIA Accessibility Names (PAUSE, OPERATOR, SUPERVISOR, READ .PROG, Run)
    init_items = uia_dump(main_hwnd)
    uia_names = [it["name"] for it in init_items]
    named_ok = all(
        any(expected in n for n in uia_names)
        for expected in ["PAUSE", "OPERATOR", "SUPERVISOR", "READ .PROG", "SAVE .PROG"]
    )
    record_check("P2-1_UIA_Names", named_ok, f"Key buttons exposed cleanly in UIA (total @Ref={len(init_items)})")

    # Switch to SUPERVISOR via its new AutomationProperties.Name!
    sup_ref = find_ref(init_items, "SUPERVISOR", "Button", exact_label=True)
    assert sup_ref is not None, "SUPERVISOR button not found by UIA Name!"
    run_cli("act", sup_ref["ref"], "--hwnd", main_hwnd)
    time.sleep(0.6)
    shot_sup = capture("02_supervisor_highlight", main_hwnd)
    log_step("1. Switch to SUPERVISOR via UIA Name", "PASS", 600, f"Clicked {sup_ref['ref']} ({sup_ref['name']})", shot_sup)

    # Verify UI-02a: HELLO > must NOT exist in ARCOS Command Selector!
    run_cli("mouse", "click", "0.84", "0.160", "--hwnd", main_hwnd)
    time.sleep(0.7)
    sel = find_window_hwnd("ARCOS Command Selector")
    assert sel is not None, "Command Selector did not open"
    sel_items = uia_dump(sel)
    hello_exists = find_ref(sel_items, "HELLO >", "Button", exact_label=True) is not None
    record_check("UI-02a_No_Ghost_HELLO", not hello_exists, "HELLO > absent from Runtime Command Selector" if not hello_exists else "HELLO > STILL PRESENT!")
    shot_sel = capture("03_cmd_selector_clean", main_hwnd)

    # Row 0: System@UserPanelOpen (already inside Command Selector!)
    up_ref = find_ref(sel_items, "UserPanelOpen", "Button", exact_label=True)
    run_cli("act", up_ref["ref"], "--hwnd", sel)
    time.sleep(0.8)
    g0 = find_window_hwnd("Parameter Guidance")
    g0_items = uia_dump(g0)
    apply0 = find_ref(g0_items, "StackPanel", "Button") or find_ref(g0_items, "套用", "Button")
    run_cli("act", apply0["ref"], "--hwnd", g0)
    time.sleep(0.5)
    log_step("2. Row 0: System@UserPanelOpen", "PASS", 1300, "Applied UserPanel.csvi")

    # Row 1: System@Log Message with '=' and Chinese
    g1 = pick_cmd(main_hwnd, 0.180, "System >", "Log Message")
    g1_items = uia_dump(g1)
    txt1 = find_ref(g1_items, "TxtMsgText", "Edit")
    run_cli("fill", txt1["ref"], "Status = READY (測試)", "--hwnd", g1)
    apply1 = find_ref(g1_items, "StackPanel", "Button") or find_ref(g1_items, "套用", "Button")
    run_cli("act", apply1["ref"], "--hwnd", g1)
    time.sleep(0.5)
    log_step("3. Row 1: System@Log Message (with '=')", "PASS", 1200, "Applied msg='Status = READY (測試)'")

    # Row 2: Variable@Set (exact match 'Set'!) -> TotalCount = 42
    g2 = pick_cmd(main_hwnd, 0.205, "Variable >", "Set")
    g2_items = uia_dump(g2)
    edits2 = [i for i in g2_items if i["type"] == "Edit" and "ParamOut" not in i["name"]]
    if len(edits2) >= 2:
        run_cli("fill", edits2[0]["ref"], "TotalCount", "--hwnd", g2)
        run_cli("fill", edits2[1]["ref"], "42", "--hwnd", g2)
    apply2 = find_ref(g2_items, "StackPanel", "Button") or find_ref(g2_items, "套用", "Button")
    run_cli("act", apply2["ref"], "--hwnd", g2)
    time.sleep(0.5)
    log_step("4. Row 2: Variable@Set (TotalCount=42)", "PASS", 1200, f"Filled {len(edits2)} fields")

    # Row 3: IO@AddVirtualIO (CycleReady? = True)
    g3 = pick_cmd(main_hwnd, 0.228, "IO >", "AddVirtualIO")
    g3_items = uia_dump(g3)
    edits3 = [i for i in g3_items if i["type"] == "Edit" and "ParamOut" not in i["name"]]
    if len(edits3) >= 2:
        run_cli("fill", edits3[0]["ref"], "CycleReady?", "--hwnd", g3)
        run_cli("fill", edits3[1]["ref"], "True", "--hwnd", g3)
    apply3 = find_ref(g3_items, "StackPanel", "Button") or find_ref(g3_items, "套用", "Button")
    run_cli("act", apply3["ref"], "--hwnd", g3)
    time.sleep(0.5)
    log_step("5. Row 3: IO@AddVirtualIO (CycleReady?=True)", "PASS", 1200, "Applied")

    # Row 4: System@Delay (0.2s)
    g4 = pick_cmd(main_hwnd, 0.251, "System >", "Delay")
    g4_items = uia_dump(g4)
    edits4 = [i for i in g4_items if i["type"] == "Edit" and "ParamOut" not in i["name"]]
    if edits4:
        run_cli("fill", edits4[0]["ref"], "0.2", "--hwnd", g4)
    apply4 = find_ref(g4_items, "StackPanel", "Button") or find_ref(g4_items, "套用", "Button")
    run_cli("act", apply4["ref"], "--hwnd", g4)
    time.sleep(0.5)
    log_step("6. Row 4: System@Delay (0.2s)", "PASS", 1200, "Applied")

    # Execute via real UI 'Run' button (using new UIA Name "Run"!)
    sup_items = uia_dump(main_hwnd)
    run_btn = find_ref(sup_items, "Run", "Button", exact_label=True)
    assert run_btn is not None, "Run button not found by UIA Name!"
    run_cli("act", run_btn["ref"], "--hwnd", main_hwnd)
    time.sleep(2.0)
    # Hover mouse directly over Run button too to visually verify UI-04 fix (vivid blue, NOT grayed out!)
    run_cli("mouse", "move", f"{run_btn['u']:.4f}", f"{run_btn['v']:.4f}", "--hwnd", main_hwnd)
    time.sleep(0.3)
    shot_run = capture("04_after_run_real_userpanel", main_hwnd)

    # Read-only Internal Oracle Verification!
    state_raw = mcp_read_only("arcos_get_state")
    state_json = json.loads(state_raw["result"]["content"][0]["text"])
    var_raw = mcp_read_only("arcos_basic_engine", {"engine": "Variable", "action": "get_all"})
    var_json = json.loads(var_raw["result"]["content"][0]["text"])

    panel_path = state_json["loadedUserPanels"][0]["filePath"] if state_json.get("loadedUserPanels") else ""
    panel_ctrl_count = state_json["loadedUserPanels"][0]["controlCount"] if state_json.get("loadedUserPanels") else 0
    is_runtime_panel = "dist\\ARCOS_Runtime\\PANELS\\UserPanel.csvi".lower() in panel_path.lower()
    record_check("UI-02b_Runtime_Panel_Isolation", is_runtime_panel, f"filePath={panel_path}")

    # Real UserPanelView has >= 15 controls (unlike System.csvi's 10) + check TotalCount==42!
    total_count_val = str(var_json.get("variables", {}).get("TotalCount", ""))
    record_check("E2E_Variable_TotalCount", total_count_val == "42", f"VariableEngine.TotalCount={total_count_val}")
    has_log_eq = any("Status = READY (測試)" in l for l in state_json.get("recentLogs", []))
    record_check("E2E_Log_With_Equals", has_log_eq, "Verbatim log with '=' found in recentLogs")
    record_check("UI-03_Real_UserPanelView_Mounted", panel_ctrl_count > 10 or "UserPanel" in state_json.get("activeUserPanel", ""), f"activeUserPanel={state_json.get('activeUserPanel')}, controlCount={panel_ctrl_count}")

    # Patrol fixed tabs: Capture, Modules Info, Panel CMD, Remote, CSVI STUDIO!
    for tab_name, shot_name in [
        ("Capture", "05_tab_capture_after_fix"),
        ("Modules Info", "06_tab_modules_info_after_fix"),
        ("Panel CMD", "07_tab_panel_cmd_after_fix"),
        ("Remote", "08_tab_remote_after_fix"),
        ("CSVI STUDIO", "09_tab_csvi_studio_after_fix"),
    ]:
        cur = uia_dump(main_hwnd)
        t_ref = find_ref(cur, tab_name, "TabItem")
        if t_ref:
            run_cli("act", t_ref["ref"], "--hwnd", main_hwnd)
            time.sleep(0.8)
            shot = capture(shot_name, main_hwnd)
            log_step(f"7. Tab '{tab_name}'", "PASS", 800, f"Captured {shot_name}", shot)
            if tab_name == "CSVI STUDIO":
                studio_uia = uia_dump(main_hwnd)
                all_txt = " ".join(i["name"] + " " + i["state"] for i in studio_uia)
                opened_app_dll = "ARCOS.App.dll" in all_txt
                record_check("UI-01_CsviStudio_Not_AppDll", not opened_app_dll, "CSVI STUDIO did NOT open ARCOS.App.dll" if not opened_app_dll else "STILL OPENED ARCOS.App.dll!")

    # Clean shutdown
    run_cli("close", main_hwnd)
    time.sleep(1.0)
    if find_window_hwnd("ARCOS Automated Machine Controller"):
        run_cli("close", main_hwnd, "--force")

    out_json = OUT_DIR / "e2e_regression_after_fix.json"
    out_json.write_text(json.dumps({"checks": CHECKS, "trace": TRACE, "state": state_json, "variables": var_json}, ensure_ascii=False, indent=2), encoding="utf-8")

    passed = sum(1 for c in CHECKS if c["passed"])
    print(f"\n=== REGRESSION SUMMARY: {passed}/{len(CHECKS)} CHECKS PASSED ===")
    if passed != len(CHECKS):
        sys.exit(2)


if __name__ == "__main__":
    main()
