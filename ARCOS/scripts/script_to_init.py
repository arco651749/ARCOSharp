import struct
import os
import sys
import argparse

from arcos_parse import split_commands, split_cmd_param

# 強制 UTF-8 輸出
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


def parse_script_text(script_text):
    """
    將 ARCOS 腳本文字解析為 [(指令, 參數), ...]。
    只在中括號外的 `;` 切分（保護陣列寫法），並保留 Python@Script 內部的 0x0A 換行。
    """
    return [split_cmd_param(c) for c in split_commands(script_text)]


def create_init_file(filepath, commands, encoding='utf-8'):
    """
    將 ARCOS 指令陣列封裝為二進制 .init 檔案。

    :param filepath: 輸出的檔案路徑 (副檔名建議為 .init)。
                     【存放路徑規範】：
                     - 一般子程序與 UI 綁定腳本：請存放在機台專案的「Script/」資料夾（如 Script/MyTask.init）。
                     - 系統開機啟動腳本：請存放在「ARCOS 系統根目錄」。
    :param commands: 格式為 [(指令1, 參數1), (指令2, 參數2), ...] 的串列
    :param encoding: 文字編碼，預設為 'utf-8'
    """
    row_count = len(commands)
    col_count = 2

    # 確保父資料夾存在
    out_dir = os.path.dirname(os.path.abspath(filepath))
    if out_dir and not os.path.exists(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    with open(filepath, 'wb') as f:
        # 1. 寫入標頭 (Header): N x 2，INT32 Big-endian
        f.write(struct.pack('>I', row_count))
        f.write(struct.pack('>I', col_count))

        # 2. 寫入資料區塊 (Data Blocks)：長度標頭 + 文字內容
        for cmd, param in commands:
            cmd_bytes = cmd.encode(encoding) if cmd else b''
            f.write(struct.pack('>I', len(cmd_bytes)))
            f.write(cmd_bytes)

            param_bytes = param.encode(encoding) if param else b''
            f.write(struct.pack('>I', len(param_bytes)))
            f.write(param_bytes)


def main():
    parser = argparse.ArgumentParser(
        description="ARCOS 二進制 .init 檔案生成與編譯工具",
        add_help=True
    )
    parser.add_argument(
        '-o', '--output',
        type=str,
        required=True,
        help="輸出的 .init 檔案路徑（規範：一般子程序/UI腳本請輸出至「Script/」目錄；開機啟動腳本輸出至「系統根目錄」）"
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('-s', '--script', type=str, help="欲編譯之 ARCOS 腳本字串 (如 'IO@SetPort::P0.0;System@Delay::0.5')")
    group.add_argument('-f', '--file', type=str, help="包含 ARCOS 腳本的文字檔案路徑（含 Python@Script 等多行指令時請用此方式）")
    parser.add_argument('--encoding', type=str, default='utf-8', help="字串編碼 (預設 utf-8)")

    args = parser.parse_args()

    if args.file:
        if not os.path.exists(args.file):
            print(f"? Error: 找不到輸入檔案 {args.file}")
            sys.exit(1)
        with open(args.file, 'r', encoding='utf-8', errors='replace') as sf:
            script_content = sf.read()
    else:
        script_content = args.script

    try:
        commands = parse_script_text(script_content)
    except ValueError as e:
        print(f"? Script error: {e}")
        sys.exit(2)

    if not commands:
        print("? Warning: 解析後無有效指令！")
        sys.exit(1)

    create_init_file(args.output, commands, encoding=args.encoding)
    print(f"# Success: 已成功生成 ARCOS .init 檔案 -> {args.output} (共 {len(commands)} 行指令)")


if __name__ == "__main__":
    main()
