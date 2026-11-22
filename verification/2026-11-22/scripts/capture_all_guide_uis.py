import sys
sys.stdout.reconfigure(encoding="utf-8")

import subprocess
from pathlib import Path

STUDIO_EXE = Path(r"d:\AntiGravity\ARCOSharp\dist\ARCOS_Runtime\CSVI_Studio.exe")
MODULES_DIR = Path(r"d:\AntiGravity\ARCOSharp\dist\ARCOS_Runtime\MODULES")
SHOTS_ROOT = Path(r"d:\AntiGravity\ARCOSharp\verification\2026-11-22\screenshots")

MODULES = [
    "THREAD", "VARIABLE", "IO", "MOTION", "CAPTURE", "DAQ",
    "POINT&PATH", "USER", "PYTHON", "REDIS", "MULTISTATION", "AIAGENT", "CALL"
]

def capture_one(csvi_path: Path, target: str, out_png: Path) -> bool:
    if out_png.exists() and out_png.stat().st_size > 0:
        return True
    cmd = [str(STUDIO_EXE), str(csvi_path), "--capture-target", target, "--capture-out", str(out_png)]
    try:
        subprocess.run(cmd, capture_output=True, timeout=18)
        return out_png.exists() and out_png.stat().st_size > 0
    except Exception:
        return False

def main():
    total = 0
    ok_cnt = 0
    for mod in MODULES:
        mod_dir = MODULES_DIR / mod
        if not mod_dir.exists():
            print(f"[SKIP] {mod} not found")
            continue
        out_dir = SHOTS_ROOT / mod
        out_dir.mkdir(parents=True, exist_ok=True)
        csvis = sorted(mod_dir.glob("*.csvi"), key=lambda p: p.name.lower())
        mod_ok = 0
        for c in csvis:
            is_master = c.stem.lower() == mod.lower() or (mod == "POINT&PATH" and c.stem.lower() == "point&path")
            target = "Panel" if is_master else "Wizard"
            ok = capture_one(c, target, out_dir / f"{c.stem}.png")
            total += 1
            if ok:
                ok_cnt += 1
                mod_ok += 1
        print(f"[{mod:13s}] {mod_ok}/{len(csvis)} captured", flush=True)
    print(f"ALL DONE: {ok_cnt}/{total} screenshots captured.", flush=True)

if __name__ == "__main__":
    main()
