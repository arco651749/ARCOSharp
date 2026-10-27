"""
MVCon 腳本文字切分與靜態語法檢查工具 (Parser & Linter)。

功能：
1. split_commands: 切分多指令（保護中括號、保留 Python@Script 0x0A 換行）。
2. split_cmd_param: 分離指令與參數。
3. lint_script: 離線靜態檢查（依據 Pre-flight Checklist 8 點與系統規則進行防呆驗證）。
4. CLI 介面：提供 Agent 於終端機執行本機秒速自檢（支援 -c/--check 與 -f/--file）。
"""

import sys
import re

# 強制 UTF-8 輸出
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


def _normalize_newlines(text):
    return text.replace('\r\n', '\n').replace('\r', '\n')


def strip_comment_lines(text):
    """移除整行以 // 開頭的說明行（行內的 //，例如 Python 的整數除法，不受影響）。"""
    lines = _normalize_newlines(text).split('\n')
    return '\n'.join(line for line in lines if not line.strip().startswith('//'))


def split_commands(text):
    """將 MVCon 腳本文字切分為指令字串串列。中括號未配對時拋出 ValueError。"""
    text = strip_comment_lines(text)
    commands = []
    buf = []
    depth = 0
    for ch in text:
        if ch == '[':
            depth += 1
        elif ch == ']':
            if depth == 0:
                raise ValueError("中括號未配對：出現多餘的 ']'")
            depth -= 1

        if ch == ';' and depth == 0:
            commands.append(''.join(buf))
            buf = []
        else:
            buf.append(ch)
    commands.append(''.join(buf))

    if depth != 0:
        raise ValueError("中括號未配對：缺少 ']'")

    return [c.strip() for c in commands if c.strip()]


def split_cmd_param(command):
    """將單一指令拆成 (指令, 參數)。無 `::` 者參數為空字串。"""
    if '::' in command:
        cmd, param = command.split('::', 1)
        return cmd.strip(), param.strip()
    return command.strip(), ''


def lint_script(script_text):
    """
    對 MVCon 腳本進行靜態語法檢查 (Pre-flight Linter)。
    檢查項目涵蓋 Checklist 8 點與關鍵邊界約束。
    回傳: (is_valid: bool, errors: list[str], warnings: list[str])
    """
    errors = []
    warnings = []

    # 1. 括號與分號切分檢查
    try:
        commands = split_commands(script_text)
    except ValueError as e:
        return False, [f"括號語法錯誤: {e}"], []

    if not commands:
        return True, [], ["警告: 腳本無任何可執行的指令。"]

    subthread_stack = []
    if_depth = 0

    for idx, raw_cmd in enumerate(commands):
        line_num = idx + 1

        # 2. 必須保留 '::' 規範 (即使無參數)
        if '::' not in raw_cmd:
            errors.append(f"[第 {line_num} 行] 無參數指令格式錯誤：必須保留 '::' (如 '{raw_cmd}::') -> '{raw_cmd}'")
            cmd, param = raw_cmd.strip(), ''
        else:
            cmd, param = split_cmd_param(raw_cmd)

        # 3. 模組前綴 '@' 檢查
        if '@' not in cmd:
            errors.append(f"[第 {line_num} 行] 缺少模組前綴 '@' -> '{raw_cmd}'")
            continue

        mod_name, action_name = cmd.split('@', 1)

        # 4. 邏輯表達式 ($logic) 嚴禁 '$' 前綴檢查
        # 觸發條件指令包含 Thread@If, Thread@ElseIf, Thread@ForeachBreak, Thread@ForeachContinue
        if cmd in ['Thread@If', 'Thread@ElseIf', 'Thread@ForeachBreak', 'Thread@ForeachContinue']:
            # 檢查 logic 是否誤加 $變數
            dollar_vars = re.findall(r'\$([a-zA-Z0-9_]+)', param)
            if dollar_vars:
                errors.append(f"[第 {line_num} 行] 邏輯表達式約束：$logic 運算式內之變數嚴禁添加 '$' 前綴 (發現: {', '.join(['$' + v for v in dollar_vars])}) -> '{raw_cmd}'")

        # 5. Output IO 誤用 '?' 標記檢查
        if cmd in ['IO@SetPort', 'IO@ClrPort']:
            port = param.split(',', 1)[0].strip()
            if port.endswith('?'):
                errors.append(f"[第 {line_num} 行] IO 命名規範錯誤：'?' 僅限作為 Input IO 尾綴，Output IO 嚴禁使用 '?' -> '{raw_cmd}'")

        # 6. Call 模組預設 Runtime 模式建議 (& 或 &&)
        if cmd.startswith('Call@'):
            vi_name = cmd.split('@', 1)[1]
            if not (vi_name.endswith('&') or vi_name.endswith('&&')):
                warnings.append(f"[第 {line_num} 行] Call VI 模式建議：正式腳本建議加上 '&' (Runtime 模式，不開前面板) -> '{cmd}'")

        # 7. SubThread 區塊配對檢查
        if cmd == 'Thread@SubThreadStart':
            subthread_stack.append((line_num, param))
        elif cmd == 'Thread@SubThreadEnd':
            if not subthread_stack:
                errors.append(f"[第 {line_num} 行] 配對錯誤：出現多餘的 Thread@SubThreadEnd -> '{raw_cmd}'")
            else:
                last_line, last_name = subthread_stack.pop()
                if param != last_name:
                    errors.append(f"[第 {line_num} 行] SubThread 名稱不一致：Start 為 '{last_name}' (第 {last_line} 行)，但 End 為 '{param}'")

        # 8. If / EndIf 巢狀配對檢查
        if cmd == 'Thread@If':
            if_depth += 1
        elif cmd == 'Thread@EndIf':
            if if_depth == 0:
                errors.append(f"[第 {line_num} 行] 配對錯誤：出現未配對的 Thread@EndIf -> '{raw_cmd}'")
            else:
                if_depth -= 1

        # 9. ErrorHandler 安全阻斷檢查 (上方必須有 Stop 或跳躍指令)
        if cmd == 'Thread@ErrorHandler':
            if idx == 0:
                errors.append(f"[第 {line_num} 行] 流程安全嚴重錯誤：腳本開頭即為 ErrorHandler")
            else:
                prev_cmd, _ = split_cmd_param(commands[idx - 1])
                safe_blockers = ['Thread@Stop', 'Thread@Jump', 'Thread@Repeat', 'Thread@Return', 'Thread@Suspend']
                if prev_cmd not in safe_blockers:
                    errors.append(f"[第 {line_num} 行] 流程安全阻斷錯誤：Thread@ErrorHandler 上方必須以 Thread@Stop:: 或跳躍中斷指令阻隔，嚴禁正常流程墜入錯誤處理區段 (前一行指令為: {commands[idx - 1]})")

    # 收尾檢查未閉合之區塊
    if subthread_stack:
        for st_line, st_name in subthread_stack:
            errors.append(f"[第 {st_line} 行] 未閉合的 SubThread：'{st_name}' 缺少對應的 Thread@SubThreadEnd")

    if if_depth > 0:
        errors.append(f"未閉合的條件判斷：尚有 {if_depth} 個 Thread@If 缺少對應的 Thread@EndIf")

    is_valid = len(errors) == 0
    return is_valid, errors, warnings


def main():
    import argparse
    import os

    parser = argparse.ArgumentParser(
        description="MVCon 語法剖析與靜態檢查工具 (Syntax Linter)",
        add_help=True
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('-c', '--check', type=str, help="欲靜態檢查的 MVCon 腳本字串 (請使用單引號包覆)")
    group.add_argument('-f', '--file', type=str, help="欲靜態檢查的 MVCon 腳本檔案路徑")

    args = parser.parse_args()

    if args.file:
        if not os.path.exists(args.file):
            print(f"? Error: 找不到指定檔案 {args.file}")
            sys.exit(1)
        with open(args.file, 'r', encoding='utf-8', errors='replace') as f:
            script_text = f.read()
    else:
        script_text = args.check

    is_valid, errors, warnings = lint_script(script_text)

    if warnings:
        for w in warnings:
            print(f"! Warning: {w}")

    if not is_valid:
        print(f"? Lint Failed: 發現 {len(errors)} 個語法/安全性錯誤：")
        for e in errors:
            print(f"  - {e}")
        sys.exit(1)
    else:
        print("# Lint Passed: 語法與安全阻斷檢查通過！")
        sys.exit(0)


if __name__ == '__main__':
    main()
