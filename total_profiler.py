#!/usr/bin/env python3
"""
G's Cyberpunk 2077 TOTAL Profiler v0.1.0 prototype

Unified controller for:
- GRSP 0.5.0 Public Preview (bundled exact GameRoot build)
- CET Runtime Profiler 3.0.0-alpha6b (bundled exact manager/payload)
- CapFrameX (external, user-linked; no version lock)
- GRSP Correlator 0.1.2 (bundled analysis engine)

The application does not bundle or redistribute CapFrameX.
"""
from __future__ import annotations

import csv
import datetime as dt
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import threading
import traceback
import webbrowser
import zipfile
from typing import Any, Optional

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

APP_NAME = "G's Cyberpunk 2077 TOTAL Profiler"
VERSION = "0.1.0"
GRSP_VERSION = "0.5.0"
CET_PROFILER_VERSION = "3.0.0-alpha6b"
CORRELATOR_VERSION = "0.1.2"
GRSP_DLL_SHA256 = "58b6caa3dccb03067d17a5d40ac049ba90cb74862b4302d7d2b5f91c5fca415d"
CAPTURE_KEY = "F11"
CET_EXPORT_KEY = "F12"


def app_dir() -> pathlib.Path:
    if getattr(sys, "frozen", False):
        return pathlib.Path(sys.executable).resolve().parent
    return pathlib.Path(__file__).resolve().parent


def resource_dir() -> pathlib.Path:
    if getattr(sys, "_MEIPASS", None):
        return pathlib.Path(sys._MEIPASS)
    return pathlib.Path(__file__).resolve().parent


def components_dir() -> pathlib.Path:
    return resource_dir() / "components"


def local_state_dir() -> pathlib.Path:
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("APPDATA")
    if base:
        p = pathlib.Path(base) / "GsCyberpunk2077_TOTAL_Profiler"
    else:
        p = app_dir() / ".total_profiler_state"
    p.mkdir(parents=True, exist_ok=True)
    return p


def config_path() -> pathlib.Path:
    return local_state_dir() / "config.json"


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().lower()


def safe_name(value: str) -> str:
    value = "".join(ch if ch.isalnum() or ch in "._-" else "_" for ch in value.strip())
    value = value.strip("._")
    return value or "CAPTURE"


def is_game_running() -> bool:
    if os.name != "nt":
        return False
    try:
        p = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq Cyberpunk2077.exe", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            timeout=5,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        return "Cyberpunk2077.exe" in (p.stdout or "")
    except Exception:
        return False


def validate_game_root(root: pathlib.Path) -> tuple[bool, str]:
    if not root.exists() or not root.is_dir():
        return False, "Folder does not exist."
    exe = root / "bin" / "x64" / "Cyberpunk2077.exe"
    plugins = root / "bin" / "x64" / "plugins"
    if not plugins.is_dir():
        return False, r"Missing bin\x64\plugins."
    if not exe.exists():
        # Some local layouts can still be valid for tool testing; keep this as a warning.
        return True, "Game plugins folder found; Cyberpunk2077.exe was not found at the normal path."
    return True, "Cyberpunk 2077 folder looks valid."


def copy_tree_merge(src: pathlib.Path, dst: pathlib.Path) -> None:
    for p in src.rglob("*"):
        rel = p.relative_to(src)
        target = dst / rel
        if p.is_dir():
            target.mkdir(parents=True, exist_ok=True)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, target)


def find_json_value(obj: Any, names: set[str]) -> Optional[Any]:
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k.lower() in names:
                return v
        for v in obj.values():
            found = find_json_value(v, names)
            if found is not None:
                return found
    elif isinstance(obj, list):
        for v in obj:
            found = find_json_value(v, names)
            if found is not None:
                return found
    return None


def set_json_value_recursive(obj: Any, names: set[str], value: Any) -> bool:
    changed = False
    if isinstance(obj, dict):
        for k in list(obj.keys()):
            if k.lower() in names:
                if obj[k] != value:
                    obj[k] = value
                    changed = True
            else:
                changed = set_json_value_recursive(obj[k], names, value) or changed
    elif isinstance(obj, list):
        for item in obj:
            changed = set_json_value_recursive(item, names, value) or changed
    return changed


def capframex_layout(exe_path: pathlib.Path) -> dict[str, Optional[pathlib.Path]]:
    """Best-effort, version-tolerant CapFrameX path discovery."""
    out: dict[str, Optional[pathlib.Path]] = {"captures": None, "config": None, "settings": None}
    if not exe_path.exists():
        return out
    base = exe_path.parent
    portable = base / "portable.json"
    if portable.exists():
        try:
            obj = json.loads(portable.read_text(encoding="utf-8-sig"))
            paths = obj.get("paths") or {}
            cap = paths.get("captures")
            cfg = paths.get("config")
            if isinstance(cap, str) and cap.strip():
                p = pathlib.Path(cap)
                out["captures"] = (base / p).resolve() if not p.is_absolute() else p.resolve()
            if isinstance(cfg, str) and cfg.strip():
                p = pathlib.Path(cfg)
                out["config"] = (base / p).resolve() if not p.is_absolute() else p.resolve()
        except Exception:
            pass

    if out["captures"] is None:
        out["captures"] = pathlib.Path.home() / "Documents" / "CapFrameX" / "Captures"
    if out["config"] is None:
        appdata = os.environ.get("APPDATA")
        if appdata:
            out["config"] = pathlib.Path(appdata) / "CapFrameX" / "Configuration"

    cfg = out["config"]
    if cfg:
        for name in ("AppSettings.json", "appsettings.json"):
            p = cfg / name
            if p.exists():
                out["settings"] = p
                break
    return out


def configure_capframex_f11(exe_path: pathlib.Path) -> tuple[bool, str]:
    """Set CaptureHotKey to F11 only when an existing supported property is found."""
    layout = capframex_layout(exe_path)
    settings = layout.get("settings")
    if not settings or not settings.exists():
        return False, "CapFrameX settings file not found yet. Set Capture Hotkey to F11 in CapFrameX."
    try:
        obj = json.loads(settings.read_text(encoding="utf-8-sig"))
    except Exception as e:
        return False, f"Could not read CapFrameX settings: {e}"
    names = {"capturehotkey", "capturehotkeystring"}
    current = find_json_value(obj, names)
    if current is None:
        return False, "CapFrameX CaptureHotKey setting was not found. Set Capture Hotkey to F11 in CapFrameX."
    if str(current).upper() == CAPTURE_KEY:
        return True, "CapFrameX capture hotkey is already F11."
    backup = settings.with_name(settings.name + ".TOTALProfiler.bak")
    try:
        if not backup.exists():
            shutil.copy2(settings, backup)
        changed = set_json_value_recursive(obj, names, CAPTURE_KEY)
        if not changed:
            return False, "CapFrameX capture hotkey could not be changed automatically."
        settings.write_text(json.dumps(obj, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        return True, f"CapFrameX capture hotkey set to F11. Backup: {backup.name}"
    except Exception as e:
        return False, f"Could not update CapFrameX hotkey: {e}"


def newest_files(root: pathlib.Path, pattern: str, limit: int = 30) -> list[pathlib.Path]:
    if not root.exists():
        return []
    files = [p for p in root.rglob(pattern) if p.is_file()]
    files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return files[:limit]


def capx_duration_ms(path: pathlib.Path) -> Optional[float]:
    try:
        obj = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(obj, dict) or "Runs" not in obj:
            return None
        best = None
        for run in obj.get("Runs") or []:
            cd = (run or {}).get("CaptureData") or {}
            ts = cd.get("TimeInSeconds") or []
            if ts:
                dur = float(ts[-1]) * 1000.0
                if best is None or dur > best:
                    best = dur
        return best
    except Exception:
        return None


def read_csv_first(path: pathlib.Path) -> dict[str, str]:
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as f:
            return next(csv.DictReader(f), {})
    except Exception:
        return {}


def grsp_latest_capture(game_root: pathlib.Path) -> Optional[pathlib.Path]:
    results = game_root / "red4ext" / "plugins" / "redscript_profiler_alpha" / "RESULTS"
    if not results.exists():
        return None
    latest = results / "LATEST.txt"
    if latest.exists():
        try:
            raw = latest.read_text(encoding="utf-8-sig", errors="replace").strip().strip('"')
            if raw:
                p = pathlib.Path(raw)
                if not p.is_absolute():
                    p = results / p
                if p.is_dir() and (p / "GRSP_Summary.csv").exists():
                    return p.resolve()
        except Exception:
            pass
    candidates = [p for p in results.iterdir() if p.is_dir() and p.name.startswith("Capture_") and (p / "GRSP_Summary.csv").exists()]
    if not candidates:
        return None
    candidates.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return candidates[0]


def grsp_capture_meta(capture: pathlib.Path) -> dict[str, Any]:
    row = read_csv_first(capture / "GRSP_Summary.csv")
    return {
        "start_unix_ms": float(row.get("start_unix_ms") or 0.0),
        "stop_unix_ms": float(row.get("stop_unix_ms") or 0.0),
        "duration_ms": float(row.get("duration_ms") or 0.0),
        "frame_quality": row.get("frame_quality", ""),
        "shard_merge_ok": row.get("shard_merge_ok", ""),
    }


def cet_capture_meta(cet_dir: pathlib.Path) -> dict[str, Any]:
    markers = cet_dir / "CET_Runtime_Profile_Markers.csv"
    rows = []
    try:
        with markers.open("r", encoding="utf-8-sig", newline="") as f:
            rows = list(csv.DictReader(f))
    except Exception:
        pass
    start = next((r for r in rows if r.get("Label") == "CAPTURE_START"), rows[0] if rows else {})
    cap_values = []
    for r in rows:
        try:
            cap_values.append(float(r.get("CaptureMs") or 0.0))
        except Exception:
            pass
    return {
        "start_unix_ms": float(start.get("UnixEpochMs") or 0.0) if start else 0.0,
        "start_capture_ms": float(start.get("CaptureMs") or 0.0) if start else 0.0,
        "duration_ms": max(cap_values, default=0.0),
    }


def choose_capx_capture(results_dir: pathlib.Path, target_duration_ms: float) -> tuple[Optional[pathlib.Path], Optional[float]]:
    candidates = newest_files(results_dir, "*.json", 40)
    scored = []
    for p in candidates:
        dur = capx_duration_ms(p)
        if dur is None or dur <= 0:
            continue
        score = abs(dur - target_duration_ms) if target_duration_ms > 0 else 0.0
        scored.append((score, -p.stat().st_mtime, p, dur))
    if not scored:
        return None, None
    scored.sort(key=lambda x: (x[0], x[1]))
    _, _, path, dur = scored[0]
    return path, dur


def recursive_zip(folder: pathlib.Path, out_zip: pathlib.Path) -> None:
    out_zip.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_zip.with_suffix(out_zip.suffix + ".tmp")
    if tmp.exists():
        tmp.unlink()
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, allowZip64=True) as z:
        for p in folder.rglob("*"):
            if p.is_file() and p.resolve() != out_zip.resolve() and p.resolve() != tmp.resolve():
                z.write(p, p.relative_to(folder.parent))
    tmp.replace(out_zip)


class TotalProfilerApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(f"{APP_NAME} v{VERSION}")
        self.geometry("1060x790")
        self.minsize(980, 720)
        self.option_add("*Font", ("Segoe UI", 9))
        self._busy = False
        self.cfg = self.load_config()

        self.game_var = tk.StringVar(value=self.cfg.get("game_dir", ""))
        self.capx_exe_var = tk.StringVar(value=self.cfg.get("capframex_exe", ""))
        self.capx_results_var = tk.StringVar(value=self.cfg.get("capframex_results", ""))
        default_results = str(app_dir() / "Results")
        self.results_var = tk.StringVar(value=self.cfg.get("results_dir", default_results))
        self.scenario_var = tk.StringVar(value=self.cfg.get("scenario", "WORLD"))
        self.core_only_var = tk.BooleanVar(value=bool(self.cfg.get("cet_core_only", False)))
        self.last_capture_var = tk.StringVar(value=self.cfg.get("last_capture", ""))

        self.status_vars = {
            "game": tk.StringVar(value="NOT CHECKED"),
            "grsp": tk.StringVar(value="NOT CHECKED"),
            "cet": tk.StringVar(value="NOT CHECKED"),
            "zero": tk.StringVar(value="NOT CHECKED"),
            "capx": tk.StringVar(value="NOT CHECKED"),
            "keys": tk.StringVar(value="F11 shared / F12 CET export"),
        }

        self._build_ui()
        self.after(250, self.refresh_status_async)

    def load_config(self) -> dict[str, Any]:
        try:
            return json.loads(config_path().read_text(encoding="utf-8"))
        except Exception:
            return {}

    def save_config(self) -> None:
        self.cfg.update({
            "game_dir": self.game_var.get().strip(),
            "capframex_exe": self.capx_exe_var.get().strip(),
            "capframex_results": self.capx_results_var.get().strip(),
            "results_dir": self.results_var.get().strip(),
            "scenario": self.scenario_var.get().strip(),
            "cet_core_only": bool(self.core_only_var.get()),
            "last_capture": self.last_capture_var.get().strip(),
        })
        config_path().write_text(json.dumps(self.cfg, indent=2) + "\n", encoding="utf-8")

    def snapshot(self) -> dict[str, Any]:
        """Read Tk-bound settings on the UI thread before background work."""
        return {
            "game_dir": self.game_var.get().strip(),
            "capframex_exe": self.capx_exe_var.get().strip(),
            "capframex_results": self.capx_results_var.get().strip(),
            "results_dir": self.results_var.get().strip(),
            "scenario": self.scenario_var.get().strip(),
            "cet_core_only": bool(self.core_only_var.get()),
            "last_capture": self.last_capture_var.get().strip(),
        }

    def _build_ui(self) -> None:
        style = ttk.Style(self)
        try:
            style.theme_use("vista")
        except Exception:
            pass

        outer = ttk.Frame(self, padding=12)
        outer.pack(fill="both", expand=True)

        title = ttk.Label(outer, text=APP_NAME, font=("Segoe UI Semibold", 18))
        title.pack(anchor="w")
        ttk.Label(
            outer,
            text=f"GRSP {GRSP_VERSION} + CET Runtime Profiler {CET_PROFILER_VERSION} + external CapFrameX + Correlator {CORRELATOR_VERSION}",
        ).pack(anchor="w", pady=(0, 10))

        setup = ttk.LabelFrame(outer, text="Setup", padding=10)
        setup.pack(fill="x")
        self._path_row(setup, 0, "Cyberpunk 2077 directory", self.game_var, self.browse_game)
        self._path_row(setup, 1, "CapFrameX.exe", self.capx_exe_var, self.browse_capx_exe, extra_button=("Launch", self.launch_capx))
        self._path_row(setup, 2, "CapFrameX results", self.capx_results_var, self.browse_capx_results)
        self._path_row(setup, 3, "TOTAL Profiler results", self.results_var, self.browse_results, extra_button=("Open", self.open_results_root))

        settings = ttk.Frame(setup)
        settings.grid(row=4, column=0, columnspan=4, sticky="ew", pady=(9, 0))
        ttk.Label(settings, text="Shared profiling key:").grid(row=0, column=0, sticky="w")
        ttk.Label(settings, text=CAPTURE_KEY, font=("Segoe UI Semibold", 10)).grid(row=0, column=1, sticky="w", padx=(5, 24))
        ttk.Label(settings, text="CET result export key:").grid(row=0, column=2, sticky="w")
        ttk.Label(settings, text=CET_EXPORT_KEY, font=("Segoe UI Semibold", 10)).grid(row=0, column=3, sticky="w", padx=(5, 24))
        ttk.Label(settings, text="Scenario:").grid(row=0, column=4, sticky="w")
        ttk.Entry(settings, textvariable=self.scenario_var, width=15).grid(row=0, column=5, sticky="w", padx=(5, 20))
        ttk.Checkbutton(
            settings,
            text="CET core profiler only — leave 0-Engine untouched",
            variable=self.core_only_var,
        ).grid(row=0, column=6, sticky="w")

        status = ttk.LabelFrame(outer, text="Profiler status", padding=10)
        status.pack(fill="x", pady=(10, 0))
        status.columnconfigure(1, weight=1)
        labels = [
            ("Game", "game"),
            ("GRSP", "grsp"),
            ("CET profiler", "cet"),
            ("0-Engine / Scheduler", "zero"),
            ("CapFrameX", "capx"),
            ("Capture keys", "keys"),
        ]
        for i, (name, key) in enumerate(labels):
            ttk.Label(status, text=name + ":", width=22).grid(row=i, column=0, sticky="nw", pady=2)
            ttk.Label(status, textvariable=self.status_vars[key]).grid(row=i, column=1, sticky="w", pady=2)
        ttk.Button(status, text="Refresh status", command=self.refresh_status_async).grid(row=0, column=2, rowspan=2, sticky="ne", padx=(8, 0))

        actions = ttk.LabelFrame(outer, text="Actions", padding=10)
        actions.pack(fill="x", pady=(10, 0))
        self.install_btn = ttk.Button(actions, text="INSTALL PROFILERS", command=self.install_profilers)
        self.install_btn.grid(row=0, column=0, padx=(0, 8), sticky="ew")
        self.collect_btn = ttk.Button(actions, text="COLLECT RESULTS", command=self.collect_results)
        self.collect_btn.grid(row=0, column=1, padx=8, sticky="ew")
        self.compare_btn = ttk.Button(actions, text="COMPARE RESULTS", command=self.compare_results)
        self.compare_btn.grid(row=0, column=2, padx=8, sticky="ew")
        ttk.Button(actions, text="Open latest", command=self.open_latest_capture).grid(row=0, column=3, padx=8, sticky="ew")
        ttk.Button(actions, text="Restore CET", command=self.restore_cet).grid(row=0, column=4, padx=8, sticky="ew")
        ttk.Button(actions, text="Restore GRSP DLL", command=self.restore_grsp).grid(row=0, column=5, padx=(8, 0), sticky="ew")
        for c in range(6):
            actions.columnconfigure(c, weight=1)

        workflow = ttk.LabelFrame(outer, text="Capture workflow", padding=10)
        workflow.pack(fill="x", pady=(10, 0))
        ttk.Label(
            workflow,
            text=(
                "1) Launch CapFrameX and Cyberpunk 2077.   2) F11 starts GRSP + CET + CapFrameX.   "
                "3) F11 stops all three.   4) F12 exports CET CSVs.   5) Close the game.   "
                "6) COLLECT RESULTS.   7) COMPARE RESULTS."
            ),
            wraplength=1000,
        ).pack(anchor="w")
        ttk.Label(
            workflow,
            text="CET note: CET itself does not allow a mod to declare default input keys. After first profiler install, bind 'Profiler: START / PAUSE / RESUME' to F11 and 'Profiler: CREATE CSV' to F12 in CET > Bindings.",
            wraplength=1000,
        ).pack(anchor="w", pady=(5, 0))

        log_frame = ttk.LabelFrame(outer, text="Log", padding=6)
        log_frame.pack(fill="both", expand=True, pady=(10, 0))
        self.log_text = tk.Text(log_frame, height=12, wrap="word", state="disabled", font=("Consolas", 9))
        scroll = ttk.Scrollbar(log_frame, command=self.log_text.yview)
        self.log_text.configure(yscrollcommand=scroll.set)
        self.log_text.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")

    def _path_row(self, parent, row: int, label: str, var: tk.StringVar, command, extra_button=None) -> None:
        ttk.Label(parent, text=label, width=25).grid(row=row, column=0, sticky="w", pady=3)
        e = ttk.Entry(parent, textvariable=var)
        e.grid(row=row, column=1, sticky="ew", pady=3, padx=(0, 6))
        ttk.Button(parent, text="Browse...", command=command, width=11).grid(row=row, column=2, pady=3)
        if extra_button:
            ttk.Button(parent, text=extra_button[0], command=extra_button[1], width=10).grid(row=row, column=3, pady=3, padx=(6, 0))
        parent.columnconfigure(1, weight=1)

    def log(self, msg: str) -> None:
        stamp = dt.datetime.now().strftime("%H:%M:%S")
        def add():
            self.log_text.configure(state="normal")
            self.log_text.insert("end", f"[{stamp}] {msg}\n")
            self.log_text.see("end")
            self.log_text.configure(state="disabled")
        self.after(0, add)

    def set_busy(self, busy: bool) -> None:
        self._busy = busy
        state = "disabled" if busy else "normal"
        for b in (self.install_btn, self.collect_btn, self.compare_btn):
            b.configure(state=state)

    def run_bg(self, fn, done=None) -> None:
        if self._busy:
            return
        self.set_busy(True)
        def worker():
            err = None
            result = None
            try:
                result = fn()
            except Exception as e:
                err = e
                self.log("ERROR: " + str(e))
                self.log(traceback.format_exc())
            def finish():
                self.set_busy(False)
                if err:
                    messagebox.showerror(APP_NAME, str(err))
                elif done:
                    done(result)
            self.after(0, finish)
        threading.Thread(target=worker, daemon=True).start()

    def browse_game(self) -> None:
        p = filedialog.askdirectory(title="Select Cyberpunk 2077 game directory", initialdir=self.game_var.get() or None)
        if p:
            self.game_var.set(p)
            self.save_config()
            self.refresh_status_async()

    def browse_capx_exe(self) -> None:
        p = filedialog.askopenfilename(title="Select CapFrameX.exe", filetypes=[("CapFrameX", "CapFrameX.exe"), ("Executable", "*.exe"), ("All files", "*.*")])
        if p:
            self.capx_exe_var.set(p)
            layout = capframex_layout(pathlib.Path(p))
            cap = layout.get("captures")
            if cap:
                self.capx_results_var.set(str(cap))
            self.save_config()
            self.refresh_status_async()

    def browse_capx_results(self) -> None:
        p = filedialog.askdirectory(title="Select CapFrameX capture/results folder", initialdir=self.capx_results_var.get() or None)
        if p:
            self.capx_results_var.set(p)
            self.save_config()
            self.refresh_status_async()

    def browse_results(self) -> None:
        p = filedialog.askdirectory(title="Select TOTAL Profiler results folder", initialdir=self.results_var.get() or None)
        if p:
            self.results_var.set(p)
            self.save_config()

    def launch_capx(self) -> None:
        p = pathlib.Path(self.capx_exe_var.get().strip())
        if not p.exists():
            messagebox.showerror(APP_NAME, "Select a valid CapFrameX.exe first.")
            return
        try:
            subprocess.Popen([str(p)], cwd=str(p.parent))
        except Exception as e:
            messagebox.showerror(APP_NAME, str(e))

    def open_path(self, p: pathlib.Path) -> None:
        p.mkdir(parents=True, exist_ok=True) if p.suffix == "" and not p.exists() else None
        try:
            if os.name == "nt":
                os.startfile(str(p))  # type: ignore[attr-defined]
            elif sys.platform == "darwin":
                subprocess.Popen(["open", str(p)])
            else:
                subprocess.Popen(["xdg-open", str(p)])
        except Exception as e:
            messagebox.showerror(APP_NAME, str(e))

    def open_results_root(self) -> None:
        p = pathlib.Path(self.results_var.get().strip() or (app_dir() / "Results"))
        p.mkdir(parents=True, exist_ok=True)
        self.open_path(p)

    def open_latest_capture(self) -> None:
        p = pathlib.Path(self.last_capture_var.get().strip())
        if not p.exists():
            messagebox.showinfo(APP_NAME, "No collected capture is recorded yet.")
            return
        self.open_path(p)

    def cet_script(self) -> pathlib.Path:
        return components_dir() / "cet" / "CET_Manager_Core.ps1"

    def call_cet(self, action: str, game_root: str, results_root: Optional[pathlib.Path] = None, core_only: bool = False) -> dict[str, Any]:
        if os.name != "nt":
            raise RuntimeError("CET installation/collection is Windows-only.")
        game = game_root
        cmd = [
            "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(self.cet_script()),
            "-Action", action,
            "-GameRoot", game,
        ]
        if results_root is not None:
            cmd += ["-ResultsRoot", str(results_root)]
        if core_only:
            cmd += ["-CoreProfilerOnly"]
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=120, creationflags=flags)
        if p.returncode != 0:
            err = (p.stderr or p.stdout or "CET manager failed").strip()
            # PowerShell prefixes can be noisy; preserve the useful tail.
            raise RuntimeError(err[-4000:])
        lines = [x.strip() for x in (p.stdout or "").splitlines() if x.strip()]
        for line in reversed(lines):
            if line.startswith("{"):
                return json.loads(line)
        raise RuntimeError("CET manager did not return status JSON.")

    def grsp_paths(self, game_root: str) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path]:
        game = pathlib.Path(game_root)
        target_dll = game / "red4ext" / "plugins" / "redscript_profiler_alpha.dll"
        data_dir = game / "red4ext" / "plugins" / "redscript_profiler_alpha"
        state = game / "red4ext" / "plugins" / ".gs_total_profiler_grsp_state.json"
        return target_dll, data_dir, state

    def install_grsp(self, game_root: str, scenario_label: str) -> dict[str, Any]:
        if is_game_running():
            raise RuntimeError("Cyberpunk 2077 is running. Close the game before installing profilers.")
        game = pathlib.Path(game_root)
        ok, msg = validate_game_root(game)
        if not ok:
            raise RuntimeError(msg)
        src = components_dir() / "grsp"
        src_dll = src / "red4ext" / "plugins" / "redscript_profiler_alpha.dll"
        src_scenario = src / "red4ext" / "plugins" / "redscript_profiler_alpha" / "RSP_Scenario.txt"
        if sha256(src_dll) != GRSP_DLL_SHA256:
            raise RuntimeError("Bundled GRSP DLL failed its package hash check.")
        target_dll, data_dir, state_path = self.grsp_paths(game_root)
        target_dll.parent.mkdir(parents=True, exist_ok=True)
        data_dir.mkdir(parents=True, exist_ok=True)

        state: dict[str, Any]
        if state_path.exists():
            state = json.loads(state_path.read_text(encoding="utf-8"))
            if target_dll.exists() and sha256(target_dll) == GRSP_DLL_SHA256:
                mode = "already-managed"
            else:
                raise RuntimeError("GRSP managed state exists but the installed DLL differs. Restore GRSP first.")
        else:
            mode = "added"
            backup = None
            original_hash = None
            if target_dll.exists():
                original_hash = sha256(target_dll)
                if original_hash == GRSP_DLL_SHA256:
                    mode = "preexisting-same"
                else:
                    mode = "replaced"
                    backup = target_dll.with_name("redscript_profiler_alpha.TOTALProfiler.ORIGINAL.dll")
                    if backup.exists():
                        raise RuntimeError(f"GRSP backup already exists: {backup}")
                    shutil.copy2(target_dll, backup)
            state = {
                "version": VERSION,
                "mode": mode,
                "original_hash": original_hash,
                "installed_hash": GRSP_DLL_SHA256,
                "backup": str(backup) if backup else "",
            }
            state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")

        shutil.copy2(src_dll, target_dll)
        if sha256(target_dll) != GRSP_DLL_SHA256:
            raise RuntimeError("GRSP DLL install verification failed.")
        scenario = data_dir / "RSP_Scenario.txt"
        if not scenario.exists() and src_scenario.exists():
            shutil.copy2(src_scenario, scenario)
        self.write_scenario(game_root, scenario_label)
        return {"mode": mode, "dll": str(target_dll), "scenario": str(scenario)}

    def restore_grsp_internal(self, game_root: str) -> str:
        if is_game_running():
            raise RuntimeError("Cyberpunk 2077 is running. Close the game before restoring GRSP.")
        target_dll, _, state_path = self.grsp_paths(game_root)
        if not state_path.exists():
            return "No TOTAL Profiler GRSP managed state was found."
        state = json.loads(state_path.read_text(encoding="utf-8"))
        mode = state.get("mode")
        if mode == "replaced":
            backup = pathlib.Path(state.get("backup", ""))
            if not backup.exists():
                raise RuntimeError("GRSP original DLL backup is missing.")
            shutil.copy2(backup, target_dll)
            expected = state.get("original_hash")
            if expected and sha256(target_dll) != expected:
                raise RuntimeError("GRSP original DLL restore verification failed.")
            backup.unlink(missing_ok=True)
        elif mode == "added":
            if target_dll.exists() and sha256(target_dll) == GRSP_DLL_SHA256:
                target_dll.unlink()
        # preexisting-same: nothing to restore.
        state_path.unlink(missing_ok=True)
        return "GRSP DLL managed state restored. Capture RESULTS were left untouched."

    def write_scenario(self, game_root: str, scenario_label: str) -> None:
        _, data_dir, _ = self.grsp_paths(game_root)
        if not data_dir.exists():
            return
        scenario = safe_name(scenario_label.strip().upper())
        (data_dir / "RSP_Scenario.txt").write_text(scenario + "\n", encoding="utf-8")

    def refresh_status_async(self) -> None:
        if self._busy:
            return
        snap = self.snapshot()
        def work():
            game = pathlib.Path(snap["game_dir"]) if snap["game_dir"] else pathlib.Path()
            ok, msg = validate_game_root(game) if snap["game_dir"] else (False, "Select game directory.")
            grsp = "NOT INSTALLED"
            cet_text = "NOT AVAILABLE"
            zero_text = "-"
            if ok:
                target_dll, _, _ = self.grsp_paths(snap["game_dir"])
                if target_dll.exists():
                    h = sha256(target_dll)
                    grsp = "INSTALLED ✓" if h == GRSP_DLL_SHA256 else f"OTHER BUILD ({h[:10]}…)"
                if os.name == "nt":
                    try:
                        s = self.call_cet("Status", snap["game_dir"], core_only=False)
                        cet_text = f"{s.get('cet')} · manager {s.get('packageVersion')} · live CSVs {s.get('liveResultCount')}"
                        if s.get("zeroEnginePresent"):
                            zero_text = f"{s.get('zeroEngineInit')} · {s.get('scheduler')}"
                            if s.get("managedMode"):
                                zero_text += f" · managed mode {s.get('managedMode')}"
                        else:
                            zero_text = "Not found — core CET profiling only"
                    except Exception as e:
                        cet_text = "STATUS ERROR: " + str(e).splitlines()[-1][:180]
            capx = pathlib.Path(snap["capframex_exe"]) if snap["capframex_exe"] else None
            capres = pathlib.Path(snap["capframex_results"]) if snap["capframex_results"] else None
            capx_text = "Select CapFrameX.exe"
            if capx and capx.exists():
                capx_text = "LINKED ✓"
                if capres:
                    capx_text += " · results " + ("FOUND ✓" if capres.exists() else "NOT FOUND")
            return ok, msg, grsp, cet_text, zero_text, capx_text

        def done(result):
            ok, msg, grsp, cet_text, zero_text, capx_text = result
            self.status_vars["game"].set(("FOUND ✓ · " if ok else "NOT FOUND · ") + msg)
            self.status_vars["grsp"].set(grsp)
            self.status_vars["cet"].set(cet_text)
            self.status_vars["zero"].set(zero_text)
            self.status_vars["capx"].set(capx_text)
        self.run_bg(work, done)

    def install_profilers(self) -> None:
        self.save_config()
        snap = self.snapshot()
        def work():
            game = pathlib.Path(snap["game_dir"])
            ok, msg = validate_game_root(game)
            if not ok:
                raise RuntimeError(msg)
            if is_game_running():
                raise RuntimeError("Cyberpunk 2077 is running. Close it before installation.")
            self.log("Installing GRSP 0.5.0...")
            grsp = self.install_grsp(snap["game_dir"], snap["scenario"])
            self.log(f"GRSP: {grsp['mode']} -> {grsp['dll']}")
            self.log(f"Scenario preset: {safe_name(snap['scenario'].strip().upper())}")

            self.log(f"Installing CET Runtime Profiler {CET_PROFILER_VERSION}...")
            cet = self.call_cet("Install", snap["game_dir"], results_root=pathlib.Path(snap["results_dir"]), core_only=snap["cet_core_only"])
            self.log(f"CET: {cet.get('cet')} · 0-Engine mode: {cet.get('managedMode') or 'not present'}")

            capx_msg = "CapFrameX is not linked; set its capture hotkey to F11 manually."
            capx = pathlib.Path(snap["capframex_exe"]) if snap["capframex_exe"] else None
            if capx and capx.exists():
                _, capx_msg = configure_capframex_f11(capx)
            self.log(capx_msg)
            return cet

        def done(_):
            self.save_config()
            self.refresh_status_async()
            messagebox.showinfo(
                APP_NAME,
                "Profiler install completed.\n\n"
                "GRSP uses F11 automatically.\n"
                "CapFrameX is linked; TOTAL Profiler attempted to set Capture Hotkey = F11 when its settings format exposed that field.\n\n"
                "CET requires one binding step in-game:\n"
                "  Profiler: START / PAUSE / RESUME -> F11\n"
                "  Profiler: CREATE CSV -> F12\n\n"
                "Then use the capture workflow shown in the main window."
            )
        self.run_bg(work, done)

    def collect_results(self) -> None:
        self.save_config()
        snap = self.snapshot()
        def work():
            if is_game_running():
                raise RuntimeError("Cyberpunk 2077 is still running. Close the game after F11/F12 before collecting results.")
            game = pathlib.Path(snap["game_dir"])
            ok, msg = validate_game_root(game)
            if not ok:
                raise RuntimeError(msg)
            cap_root = pathlib.Path(snap["capframex_results"])
            if not cap_root.exists():
                raise RuntimeError("CapFrameX results folder does not exist. Select it in Setup.")
            results_root = pathlib.Path(snap["results_dir"])
            results_root.mkdir(parents=True, exist_ok=True)

            grsp_src = grsp_latest_capture(game)
            if not grsp_src:
                raise RuntimeError("No GRSP capture was found. Did you press F11 twice? GRSP should have finalized on the second F11.")
            gm = grsp_capture_meta(grsp_src)
            self.log(f"GRSP found: {grsp_src.name} · {gm['duration_ms']/1000:.3f}s")

            stage_root = results_root / ".staging_cet"
            stage_root.mkdir(parents=True, exist_ok=True)
            cet_collect = self.call_cet("Collect", snap["game_dir"], results_root=stage_root, core_only=False)
            cet_src = pathlib.Path(cet_collect.get("destination", ""))
            if not cet_src.is_dir():
                raise RuntimeError("CET manager reported collection success but its archive folder was not found.")
            cm = cet_capture_meta(cet_src)
            self.log(f"CET found/exported: {cet_src.name} · {cm['duration_ms']/1000:.3f}s")

            target_duration = gm["duration_ms"]
            if cm["duration_ms"] > 0 and target_duration > 0:
                target_duration = (target_duration + cm["duration_ms"]) / 2.0
            capx_src, capx_dur = choose_capx_capture(cap_root, target_duration)
            if not capx_src:
                raise RuntimeError("No valid CapFrameX JSON capture was found in the configured results folder.")
            self.log(f"CapFrameX chosen: {capx_src.name} · {(capx_dur or 0)/1000:.3f}s")

            scenario = safe_name(snap["scenario"].strip().upper())
            stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
            capture = results_root / f"Capture_{stamp}_{scenario}"
            suffix = 1
            while capture.exists():
                capture = results_root / f"Capture_{stamp}_{scenario}_{suffix}"
                suffix += 1
            raw = capture / "Raw"
            grsp_dst = raw / "GRSP"
            cet_dst = raw / "CET"
            capx_dst = raw / "CapFrameX"
            grsp_dst.mkdir(parents=True, exist_ok=True)
            cet_dst.mkdir(parents=True, exist_ok=True)
            capx_dst.mkdir(parents=True, exist_ok=True)

            copy_tree_merge(grsp_src, grsp_dst)
            copy_tree_merge(cet_src, cet_dst)
            shutil.copy2(capx_src, capx_dst / capx_src.name)

            try:
                shutil.rmtree(cet_src)
                # Clear empty staging root where possible.
                if stage_root.exists() and not any(stage_root.iterdir()):
                    stage_root.rmdir()
            except Exception:
                pass

            start_delta = cm["start_unix_ms"] - gm["start_unix_ms"] if cm["start_unix_ms"] and gm["start_unix_ms"] else None
            duration_delta = cm["duration_ms"] - gm["duration_ms"] if cm["duration_ms"] and gm["duration_ms"] else None
            sync = "GOOD"
            if start_delta is None or abs(start_delta) > 100 or (duration_delta is not None and abs(duration_delta) > 500):
                sync = "CHECK"

            manifest = {
                "total_profiler_version": VERSION,
                "created_local": dt.datetime.now().astimezone().isoformat(),
                "scenario": scenario,
                "components": {
                    "GRSP": GRSP_VERSION,
                    "CET_Runtime_Profiler": CET_PROFILER_VERSION,
                    "CapFrameX": "external / version not locked",
                    "Correlator": CORRELATOR_VERSION,
                },
                "source_paths": {
                    "grsp": str(grsp_src),
                    "cet": str(cet_src),
                    "capframex": str(capx_src),
                },
                "capture": {
                    "sync_precheck": sync,
                    "grsp_start_unix_ms": gm["start_unix_ms"],
                    "cet_start_unix_ms": cm["start_unix_ms"],
                    "grsp_cet_start_delta_ms": start_delta,
                    "grsp_duration_ms": gm["duration_ms"],
                    "cet_duration_ms": cm["duration_ms"],
                    "grsp_cet_duration_delta_ms": duration_delta,
                    "capframex_duration_ms": capx_dur,
                },
            }
            (capture / "CaptureManifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

            self.log(f"Collected into: {capture}")
            if start_delta is not None:
                self.log(f"GRSP↔CET START delta: {start_delta:.3f} ms")
            if duration_delta is not None:
                self.log(f"GRSP↔CET duration delta: {duration_delta:.3f} ms")
            self.log(f"Capture precheck: {sync}")
            return capture, sync, start_delta, duration_delta

        def done(result):
            capture, sync, start_delta, duration_delta = result
            self.last_capture_var.set(str(capture))
            self.save_config()
            self.open_path(capture)
            details = f"Collected successfully.\n\n{capture}\n\nPrecheck: {sync}"
            if start_delta is not None:
                details += f"\nGRSP↔CET start delta: {start_delta:.3f} ms"
            if duration_delta is not None:
                details += f"\nDuration delta: {duration_delta:.3f} ms"
            details += "\n\nClick COMPARE RESULTS to run the correlator."
            messagebox.showinfo(APP_NAME, details)
            self.refresh_status_async()
        self.run_bg(work, done)

    def load_correlator(self):
        path = components_dir() / "correlator" / "GRSP_Correlator.py"
        spec = importlib.util.spec_from_file_location("gs_total_profiler_correlator", path)
        if not spec or not spec.loader:
            raise RuntimeError("Bundled correlator could not be loaded.")
        mod = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = mod
        spec.loader.exec_module(mod)
        return mod

    def compare_results(self) -> None:
        self.save_config()
        snap = self.snapshot()
        def work():
            capture = pathlib.Path(snap["last_capture"])
            if not capture.exists():
                raise RuntimeError("No collected capture selected. Run COLLECT RESULTS first.")
            raw = capture / "Raw"
            if not raw.exists():
                raise RuntimeError("The selected capture has no Raw folder.")
            combined = capture / "Combined"
            if combined.exists():
                shutil.rmtree(combined)
            combined.mkdir(parents=True, exist_ok=True)
            self.log("Running bundled GRSP correlator...")
            corr = self.load_correlator()
            result = corr.run(raw, combined)
            report = pathlib.Path(result["report"])
            self.log(f"Combined report: {report}")

            # Update capture manifest with correlator status summary.
            status_path = combined / "GRSP_Correlator_Status.txt"
            status_text = status_path.read_text(encoding="utf-8", errors="replace") if status_path.exists() else ""
            manifest_path = capture / "CaptureManifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
            manifest["correlated_local"] = dt.datetime.now().astimezone().isoformat()
            manifest["combined_report"] = str(report)
            manifest["correlator_status_excerpt"] = status_text[:6000]
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

            full_zip = capture.parent / (capture.name + "_FULL.zip")
            recursive_zip(capture, full_zip)
            self.log(f"Portable full package: {full_zip}")
            return report, combined, full_zip

        def done(result):
            report, combined, full_zip = result
            try:
                webbrowser.open(report.as_uri())
            except Exception:
                pass
            self.open_path(combined)
            messagebox.showinfo(
                APP_NAME,
                "Correlation complete.\n\n"
                f"Report:\n{report}\n\n"
                f"AI/machine files are in:\n{combined}\n\n"
                f"Full shareable capture package:\n{full_zip}"
            )
        self.run_bg(work, done)

    def restore_cet(self) -> None:
        if not messagebox.askyesno(APP_NAME, "Restore the original CET / managed 0-Engine state now?\n\nCyberpunk 2077 must be closed."):
            return
        snap = self.snapshot()
        def work():
            out = pathlib.Path(snap["results_dir"]) / "CET_Restore_Archive"
            out.mkdir(parents=True, exist_ok=True)
            r = self.call_cet("Restore", snap["game_dir"], results_root=out, core_only=False)
            self.log("CET restore completed." + (f" Final CET results: {r.get('archived')}" if r.get("archived") else ""))
            return r
        def done(_):
            self.refresh_status_async()
            messagebox.showinfo(APP_NAME, "CET managed state restored.")
        self.run_bg(work, done)

    def restore_grsp(self) -> None:
        if not messagebox.askyesno(APP_NAME, "Restore the GRSP DLL state managed by TOTAL Profiler?\n\nCapture result folders will NOT be deleted."):
            return
        snap = self.snapshot()
        def work():
            msg = self.restore_grsp_internal(snap["game_dir"])
            self.log(msg)
            return msg
        def done(msg):
            self.refresh_status_async()
            messagebox.showinfo(APP_NAME, msg)
        self.run_bg(work, done)


if __name__ == "__main__":
    app = TotalProfilerApp()
    app.mainloop()
