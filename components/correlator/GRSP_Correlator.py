#!/usr/bin/env python3
"""
GRSP Correlator v0.1.2

Standalone post-processing correlator for:
  - GRSP 0.5.x (G-REDscript Profiler)
  - CET Runtime Profiler v3 alpha6b-compatible output
  - CapFrameX JSON captures

The correlator never modifies the source captures. It aligns all three sources onto
GRSP's Unix/capture timeline, emits combined CSVs, and creates a self-contained
HTML report.

No third-party Python packages are required.
"""

from __future__ import annotations

import argparse
import bisect
import csv
import datetime as _dt
import html
import json
import math
import os
import pathlib
import shutil
import statistics
import sys
import tempfile
import textwrap
import webbrowser
import zipfile
from collections import defaultdict
from dataclasses import dataclass
from typing import Any, Iterable, Optional

VERSION = "0.1.2"
APP_NAME = "GRSP Correlator"


# ----------------------------- helpers -------------------------------------

def fnum(v: Any, default: float = 0.0) -> float:
    try:
        if v is None or v == "":
            return default
        return float(v)
    except (TypeError, ValueError):
        return default


def inum(v: Any, default: int = 0) -> int:
    try:
        if v is None or v == "":
            return default
        return int(float(v))
    except (TypeError, ValueError):
        return default


def pct(v: float) -> str:
    return f"{v:.1f}%"


def ms(v: float) -> str:
    if abs(v) >= 100:
        return f"{v:.1f} ms"
    if abs(v) >= 10:
        return f"{v:.2f} ms"
    return f"{v:.3f} ms"


def csv_rows(path: pathlib.Path) -> Iterable[dict[str, str]]:
    with path.open("r", encoding="utf-8-sig", newline="") as f:
        yield from csv.DictReader(f)


def read_first_csv_row(path: pathlib.Path) -> dict[str, str]:
    return next(iter(csv_rows(path)), {})


def write_csv(path: pathlib.Path, rows: list[dict[str, Any]], fields: list[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        w.writeheader()
        for row in rows:
            w.writerow(row)


def pearson(xs: list[float], ys: list[float]) -> float:
    n = min(len(xs), len(ys))
    if n < 3:
        return 0.0
    xs = xs[:n]
    ys = ys[:n]
    mx = sum(xs) / n
    my = sum(ys) / n
    dx = [x - mx for x in xs]
    dy = [y - my for y in ys]
    sx = sum(x * x for x in dx)
    sy = sum(y * y for y in dy)
    if sx <= 0 or sy <= 0:
        return 0.0
    return sum(a * b for a, b in zip(dx, dy)) / math.sqrt(sx * sy)


def percentile(values: list[float], p: float) -> float:
    if not values:
        return 0.0
    vals = sorted(values)
    if len(vals) == 1:
        return vals[0]
    k = (len(vals) - 1) * p
    lo = math.floor(k)
    hi = math.ceil(k)
    if lo == hi:
        return vals[lo]
    return vals[lo] * (hi - k) + vals[hi] * (k - lo)


def fmt_epoch(epoch_ms: float) -> str:
    try:
        dt = _dt.datetime.fromtimestamp(epoch_ms / 1000.0, tz=_dt.timezone.utc)
        return dt.isoformat(timespec="milliseconds").replace("+00:00", "Z")
    except Exception:
        return ""


def overlap_ms(a0: float, a1: float, b0: float, b1: float) -> float:
    return max(0.0, min(a1, b1) - max(a0, b0))


def safe_name(s: str) -> str:
    out = "".join(ch if ch.isalnum() or ch in "._-" else "_" for ch in s)
    return out.strip("._") or "capture"


# ----------------------------- discovery -----------------------------------

@dataclass
class Inputs:
    root: pathlib.Path
    grsp_dir: pathlib.Path
    cet_dir: pathlib.Path
    capx_json: pathlib.Path
    temp_dir: Optional[tempfile.TemporaryDirectory]

    def cleanup(self) -> None:
        if self.temp_dir:
            self.temp_dir.cleanup()


def _extract_if_zip(input_path: pathlib.Path) -> tuple[pathlib.Path, Optional[tempfile.TemporaryDirectory]]:
    if input_path.is_file() and input_path.suffix.lower() == ".zip":
        td = tempfile.TemporaryDirectory(prefix="grsp_correlator_")
        root = pathlib.Path(td.name)
        with zipfile.ZipFile(input_path, "r") as z:
            z.extractall(root)
        return root, td
    return input_path, None


def _find_required(root: pathlib.Path, filename: str) -> list[pathlib.Path]:
    return sorted(root.rglob(filename))


def discover_inputs(input_path: pathlib.Path) -> Inputs:
    root, td = _extract_if_zip(input_path)
    if not root.exists():
        raise FileNotFoundError(f"Input does not exist: {input_path}")

    # GRSP capture: exact public output names identify it reliably.
    grsp_summaries = _find_required(root, "GRSP_Summary.csv")
    if not grsp_summaries:
        raise FileNotFoundError("Could not find GRSP_Summary.csv in the input.")

    # If multiple captures exist, select the one with the latest start Unix time.
    grsp_candidates: list[tuple[float, pathlib.Path]] = []
    for s in grsp_summaries:
        row = read_first_csv_row(s)
        grsp_candidates.append((fnum(row.get("start_unix_ms")), s.parent))
    grsp_dir = max(grsp_candidates, key=lambda x: x[0])[1]

    cet_markers = _find_required(root, "CET_Runtime_Profile_Markers.csv")
    if not cet_markers:
        raise FileNotFoundError("Could not find CET_Runtime_Profile_Markers.csv in the input.")
    # Choose CET folder whose duration is closest to GRSP if more than one exists.
    grsp_summary = read_first_csv_row(grsp_dir / "GRSP_Summary.csv")
    gd = fnum(grsp_summary.get("duration_ms"))
    scored: list[tuple[float, pathlib.Path]] = []
    for m in cet_markers:
        rows = list(csv_rows(m))
        if rows:
            dur = max(fnum(r.get("CaptureMs")) for r in rows)
        else:
            dur = 0.0
        scored.append((abs(dur - gd), m.parent))
    cet_dir = min(scored, key=lambda x: x[0])[1]

    capx_candidates = sorted(root.rglob("*.json"))
    capx_valid: list[pathlib.Path] = []
    for p in capx_candidates:
        try:
            with p.open("r", encoding="utf-8-sig") as f:
                obj = json.load(f)
            if isinstance(obj, dict) and "Runs" in obj and "Info" in obj:
                runs = obj.get("Runs") or []
                if runs and isinstance(runs[0], dict) and "CaptureData" in runs[0]:
                    capx_valid.append(p)
        except Exception:
            continue
    if not capx_valid:
        raise FileNotFoundError("Could not find a CapFrameX JSON capture in the input.")

    # Choose CapX run whose duration is closest to GRSP.
    best_capx: tuple[float, pathlib.Path] | None = None
    for p in capx_valid:
        try:
            with p.open("r", encoding="utf-8-sig") as f:
                obj = json.load(f)
            for run in obj.get("Runs") or []:
                cd = run.get("CaptureData") or {}
                ts = cd.get("TimeInSeconds") or []
                if ts:
                    dur = float(ts[-1]) * 1000.0
                    score = abs(dur - gd)
                    if best_capx is None or score < best_capx[0]:
                        best_capx = (score, p)
        except Exception:
            pass
    capx_json = best_capx[1] if best_capx else capx_valid[0]

    return Inputs(root=root, grsp_dir=grsp_dir, cet_dir=cet_dir, capx_json=capx_json, temp_dir=td)


# ----------------------------- loaders -------------------------------------

@dataclass
class GRSPData:
    summary: dict[str, str]
    start_epoch_ms: float
    stop_epoch_ms: float
    duration_ms: float
    frames: list[dict[str, Any]]
    frame_by_id: dict[int, dict[str, Any]]
    buckets: dict[int, dict[str, Any]]
    spikes: list[dict[str, Any]]
    spikes_by_frame: dict[int, list[dict[str, Any]]]
    by_mod: list[dict[str, Any]]
    status_text: str


def load_grsp(grsp_dir: pathlib.Path) -> GRSPData:
    summary = read_first_csv_row(grsp_dir / "GRSP_Summary.csv")
    start_epoch = fnum(summary.get("start_unix_ms"))
    stop_epoch = fnum(summary.get("stop_unix_ms"))
    duration = fnum(summary.get("duration_ms"))

    frames: list[dict[str, Any]] = []
    frame_by_id: dict[int, dict[str, Any]] = {}
    for r in csv_rows(grsp_dir / "GRSP_Frames.csv"):
        x = {
            "frame_id": inum(r.get("frame_id")),
            "start_ms": fnum(r.get("frame_start_ms")),
            "end_ms": fnum(r.get("frame_end_ms")),
            "start_epoch_ms": fnum(r.get("frame_start_unix_ms")),
            "end_epoch_ms": fnum(r.get("frame_end_unix_ms")),
            "duration_ms": fnum(r.get("frame_duration_ms")),
            "total_calls": inum(r.get("total_calls")),
            "unique_owners": inum(r.get("unique_owners")),
            "inclusive_ms": fnum(r.get("observed_inclusive_ms")),
            "exclusive_ms": fnum(r.get("exclusive_instrumented_ms")),
            "largest_call_ms": fnum(r.get("largest_call_ms")),
            "spike_count": inum(r.get("spike_count")),
            "partial": str(r.get("partial", "")).lower() == "true",
        }
        frames.append(x)
        frame_by_id[x["frame_id"]] = x

    buckets: dict[int, dict[str, Any]] = {}
    for r in csv_rows(grsp_dir / "GRSP_Timeline.csv"):
        idx = inum(r.get("bucket_index"))
        b = buckets.setdefault(idx, {
            "index": idx,
            "start_ms": fnum(r.get("bucket_start_ms")),
            "end_ms": fnum(r.get("bucket_end_ms")),
            "start_epoch_ms": fnum(r.get("bucket_start_unix_ms")),
            "end_epoch_ms": fnum(r.get("bucket_end_unix_ms")),
            "exclusive_ms": 0.0,
            "inclusive_ms": 0.0,
            "calls": 0,
            "top_owner": "",
            "top_owner_ms": 0.0,
            "max_call_ms": 0.0,
            "spike_count": 0,
        })
        ex = fnum(r.get("exclusive_instrumented_ms"))
        b["exclusive_ms"] += ex
        b["inclusive_ms"] += fnum(r.get("observed_inclusive_ms"))
        b["calls"] += inum(r.get("calls"))
        b["max_call_ms"] = max(b["max_call_ms"], fnum(r.get("max_call_ms")))
        b["spike_count"] += inum(r.get("spike_count"))
        if ex > b["top_owner_ms"]:
            b["top_owner_ms"] = ex
            b["top_owner"] = r.get("owner", "")

    spikes: list[dict[str, Any]] = []
    spikes_by_frame: dict[int, list[dict[str, Any]]] = defaultdict(list)
    spath = grsp_dir / "GRSP_Spikes.csv"
    if spath.exists():
        for r in csv_rows(spath):
            x = {
                "capture_ms": fnum(r.get("capture_ms")),
                "epoch_ms": fnum(r.get("unix_ms")),
                "frame_id": inum(r.get("frame_id"), -1),
                "owner": r.get("owner", ""),
                "source_function": r.get("source_function", ""),
                "target": r.get("target", ""),
                "call_kind": r.get("call_kind", ""),
                "duration_ms": fnum(r.get("duration_ms")),
                "exclusive_ms": fnum(r.get("exclusive_instrumented_ms")),
                "depth": inum(r.get("depth")),
                "threshold": r.get("threshold", ""),
            }
            spikes.append(x)
            spikes_by_frame[x["frame_id"]].append(x)
    for lst in spikes_by_frame.values():
        lst.sort(key=lambda z: (z["exclusive_ms"], z["duration_ms"]), reverse=True)

    by_mod: list[dict[str, Any]] = []
    mpath = grsp_dir / "GRSP_ByMod.csv"
    if mpath.exists():
        for r in csv_rows(mpath):
            by_mod.append({
                "rank": inum(r.get("rank")),
                "owner": r.get("owner", ""),
                "calls_per_sec": fnum(r.get("calls_per_sec")),
                "exclusive_ms": fnum(r.get("exclusive_instrumented_ms")),
                "exclusive_ms_per_sec": fnum(r.get("exclusive_ms_per_sec")),
                "share_pct": fnum(r.get("observed_exclusive_share_pct")),
                "active_frame_pct": fnum(r.get("active_frame_pct")),
                "max_call_ms": fnum(r.get("max_call_ms")),
                "max_frame_ms": fnum(r.get("max_frame_exclusive_ms")),
                "spike_count": inum(r.get("spike_count")),
                "max_spike_ms": fnum(r.get("max_spike_ms")),
                "workload": r.get("workload_pattern", ""),
                "note": r.get("attribution_note", ""),
            })

    status_path = grsp_dir / "GRSP_Status.txt"
    status_text = status_path.read_text(encoding="utf-8", errors="replace") if status_path.exists() else ""
    return GRSPData(summary, start_epoch, stop_epoch, duration, frames, frame_by_id,
                    buckets, spikes, spikes_by_frame, by_mod, status_text)


@dataclass
class CETData:
    start_epoch_ms: float
    start_capture_ms: float
    epoch_offset_ms: float
    duration_ms: float
    buckets: dict[int, dict[str, Any]]
    spikes: list[dict[str, Any]]
    scheduler_spikes: list[dict[str, Any]]
    scheduler_bursts: list[dict[str, Any]]
    by_mod: list[dict[str, Any]]
    dropped_timeline: int
    dropped_spikes: int
    dropped_scheduler_spikes: int
    dropped_scheduler_bursts: int


def load_cet(cet_dir: pathlib.Path) -> CETData:
    markers = list(csv_rows(cet_dir / "CET_Runtime_Profile_Markers.csv"))
    if not markers:
        raise ValueError("CET marker file is empty.")
    start = next((r for r in markers if r.get("Label") == "CAPTURE_START"), markers[0])
    start_cap = fnum(start.get("CaptureMs"))
    start_epoch = fnum(start.get("UnixEpochMs"))
    epoch_offset = start_epoch - start_cap
    duration = max((fnum(r.get("CaptureMs")) for r in markers), default=0.0)

    buckets: dict[int, dict[str, Any]] = {}
    dropped_timeline = 0
    tpath = cet_dir / "CET_Runtime_Profile_Timeline.csv"
    if tpath.exists():
        for r in csv_rows(tpath):
            idx = inum(r.get("BucketIndex"))
            b = buckets.setdefault(idx, {
                "index": idx,
                "start_capture_ms": fnum(r.get("BucketStartMs")),
                "end_capture_ms": fnum(r.get("BucketEndMs")),
                "width_ms": fnum(r.get("BucketWidthMs"), 50.0),
                "exclusive_ms": 0.0,
                "calls": 0,
                "max_exclusive_ms": 0.0,
                "top_mod": "",
                "top_mod_ms": 0.0,
            })
            ex = fnum(r.get("ExclusiveMs"))
            b["exclusive_ms"] += ex
            b["calls"] += inum(r.get("Calls"))
            b["max_exclusive_ms"] = max(b["max_exclusive_ms"], fnum(r.get("MaxExclusiveMs")))
            if ex > b["top_mod_ms"]:
                b["top_mod_ms"] = ex
                b["top_mod"] = r.get("Mod", "")
            dropped_timeline = max(dropped_timeline, inum(r.get("DroppedTimelineRowsAtDump")))

    def load_spikes(filename: str, scheduler: bool = False) -> tuple[list[dict[str, Any]], int]:
        path = cet_dir / filename
        out: list[dict[str, Any]] = []
        dropped = 0
        if not path.exists():
            return out, dropped
        for r in csv_rows(path):
            if scheduler:
                cs = fnum(r.get("CaptureStartMs"))
                ce = fnum(r.get("CaptureEndMs"))
                x = {
                    "capture_start_ms": cs,
                    "capture_end_ms": ce,
                    "epoch_start_ms": epoch_offset + cs,
                    "epoch_end_ms": epoch_offset + ce,
                    "duration_ms": fnum(r.get("DurationMs")),
                    "owner": r.get("Owner", ""),
                    "kind": r.get("JobType", ""),
                    "target": r.get("Job", ""),
                    "exclusive_ms": fnum(r.get("DurationMs")),
                    "frame": inum(r.get("Frame"), -1),
                }
                dropped = max(dropped, inum(r.get("DroppedEventsAtDump")))
            else:
                cs = fnum(r.get("CaptureStartMs"))
                ce = fnum(r.get("CaptureEndMs"))
                x = {
                    "capture_start_ms": cs,
                    "capture_end_ms": ce,
                    "epoch_start_ms": epoch_offset + cs,
                    "epoch_end_ms": epoch_offset + ce,
                    "duration_ms": fnum(r.get("InclusiveMs")),
                    "owner": r.get("Mod", ""),
                    "kind": r.get("Kind", ""),
                    "target": r.get("Target", ""),
                    "exclusive_ms": fnum(r.get("ExclusiveMs")),
                    "thread_id": r.get("ThreadId", ""),
                }
                dropped = max(dropped, inum(r.get("DroppedEventsAtDump")))
            out.append(x)
        out.sort(key=lambda z: z["epoch_start_ms"])
        return out, dropped

    spikes, dropped_spikes = load_spikes("CET_Runtime_Profile_Spikes.csv", False)
    scheduler_spikes, dropped_scheduler_spikes = load_spikes("CET_Runtime_Profile_Scheduler_Spikes.csv", True)

    # Scheduler frame bursts have one row per job; aggregate per BurstSequence.
    scheduler_bursts: list[dict[str, Any]] = []
    dropped_scheduler_bursts = 0
    bpath = cet_dir / "CET_Runtime_Profile_Scheduler_FrameBursts.csv"
    if bpath.exists():
        agg: dict[int, dict[str, Any]] = {}
        for r in csv_rows(bpath):
            seq = inum(r.get("BurstSequence"))
            a = agg.setdefault(seq, {
                "sequence": seq,
                "frame": inum(r.get("Frame"), -1),
                "capture_start_ms": fnum(r.get("CaptureStartMs")),
                "capture_end_ms": fnum(r.get("CaptureEndMs")),
                "wall_ms": fnum(r.get("SchedulerWallMs")),
                "total_job_ms": fnum(r.get("TotalJobMs")),
                "job_count": inum(r.get("JobCount")),
                "top_owner": "",
                "top_job": "",
                "top_job_ms": 0.0,
            })
            jm = fnum(r.get("JobMs"))
            if jm > a["top_job_ms"]:
                a["top_job_ms"] = jm
                a["top_owner"] = r.get("Owner", "")
                a["top_job"] = r.get("Job", "")
            dropped_scheduler_bursts = max(dropped_scheduler_bursts, inum(r.get("DroppedBurstsAtDump")))
        for a in agg.values():
            a["epoch_start_ms"] = epoch_offset + a["capture_start_ms"]
            a["epoch_end_ms"] = epoch_offset + a["capture_end_ms"]
            scheduler_bursts.append(a)
        scheduler_bursts.sort(key=lambda z: z["epoch_start_ms"])

    by_mod: list[dict[str, Any]] = []
    mpath = cet_dir / "CET_Runtime_Profile_ByMod.csv"
    if mpath.exists():
        for r in csv_rows(mpath):
            by_mod.append({
                "mod": r.get("Mod", ""),
                "calls_per_sec": fnum(r.get("CallsPerSecond")),
                "exclusive_ms": fnum(r.get("ExclusiveTotalMs")),
                "exclusive_ms_per_sec": fnum(r.get("ExclusiveMsPerSecond")),
                "one_core_pct": fnum(r.get("MeasuredOneCorePct")),
                "max_exclusive_ms": fnum(r.get("MaxExclusiveMs")),
                "share_pct": fnum(r.get("MeasuredExclusiveSharePct")),
                "coverage": r.get("Coverage", ""),
            })
        by_mod.sort(key=lambda r: r["exclusive_ms_per_sec"], reverse=True)

    return CETData(start_epoch, start_cap, epoch_offset, duration, buckets, spikes,
                   scheduler_spikes, scheduler_bursts, by_mod, dropped_timeline,
                   dropped_spikes, dropped_scheduler_spikes, dropped_scheduler_bursts)


@dataclass
class CapXData:
    info: dict[str, Any]
    run_index: int
    times_ms: list[float]
    frame_ms: list[float]
    cpu_active_ms: list[float]
    gpu_active_ms: list[float]
    dropped: list[bool]
    frame_type: list[str]
    duration_ms: float


def load_capx(path: pathlib.Path, target_duration_ms: float) -> CapXData:
    with path.open("r", encoding="utf-8-sig") as f:
        obj = json.load(f)
    runs = obj.get("Runs") or []
    if not runs:
        raise ValueError("CapFrameX JSON contains no runs.")
    best: tuple[float, int, dict[str, Any]] | None = None
    for idx, run in enumerate(runs):
        cd = run.get("CaptureData") or {}
        ts = cd.get("TimeInSeconds") or []
        fm = cd.get("MsBetweenPresents") or []
        if not ts or not fm:
            continue
        dur = float(ts[-1]) * 1000.0
        score = abs(dur - target_duration_ms)
        if best is None or score < best[0]:
            best = (score, idx, cd)
    if best is None:
        raise ValueError("CapFrameX JSON has no usable CaptureData run.")
    _, idx, cd = best
    times = [fnum(x) * 1000.0 for x in cd.get("TimeInSeconds", [])]
    frames = [fnum(x) for x in cd.get("MsBetweenPresents", [])]
    n = min(len(times), len(frames))
    times, frames = times[:n], frames[:n]
    cpu = [fnum(x) for x in (cd.get("CpuActive") or [])[:n]]
    gpu = [fnum(x) for x in (cd.get("GpuActive") or [])[:n]]
    if len(cpu) < n:
        cpu += [0.0] * (n - len(cpu))
    if len(gpu) < n:
        gpu += [0.0] * (n - len(gpu))
    dropped_raw = (cd.get("Dropped") or [])[:n]
    dropped = [bool(x) for x in dropped_raw] + [False] * max(0, n - len(dropped_raw))
    ft = [str(x) for x in (cd.get("FrameType") or [])[:n]]
    ft += [""] * max(0, n - len(ft))
    duration = times[-1] if times else sum(frames)
    return CapXData(obj.get("Info") or {}, idx, times, frames, cpu, gpu, dropped[:n], ft[:n], duration)


# ----------------------------- alignment -----------------------------------

@dataclass
class Alignment:
    capx_to_grsp_offset: int
    correlation: float
    median_abs_frame_delta_ms: float
    mean_abs_frame_delta_ms: float
    pairs: int
    grsp_cet_start_delta_ms: float
    grsp_cet_duration_delta_ms: float
    quality: str


def align_capx_to_grsp(capx: CapXData, grsp: GRSPData, max_offset: int = 50) -> Alignment:
    gd = [f["duration_ms"] for f in grsp.frames]
    best: tuple[float, float, int, int, float] | None = None
    # Score primarily by correlation; use median delta as tie breaker.
    for off in range(-max_offset, max_offset + 1):
        xs: list[float] = []
        ys: list[float] = []
        deltas: list[float] = []
        for i, x in enumerate(capx.frame_ms):
            j = i + off
            if j < 0 or j >= len(grsp.frames):
                continue
            gf = grsp.frames[j]
            if gf["partial"]:
                continue
            y = gf["duration_ms"]
            xs.append(x)
            ys.append(y)
            deltas.append(abs(x - y))
        if len(xs) < 100:
            continue
        corr = pearson(xs, ys)
        med = statistics.median(deltas)
        mean = sum(deltas) / len(deltas)
        score = corr - min(med, 10.0) * 0.001
        if best is None or score > best[0]:
            best = (score, corr, off, len(xs), mean)
            best_med = med
    if best is None:
        raise ValueError("Could not align CapFrameX frames to GRSP frames.")
    _, corr, off, pairs, mean = best
    med = best_med

    start_delta = 0.0  # filled by caller after CET available
    duration_delta = 0.0
    quality = "GOOD" if corr >= 0.90 and med <= 2.0 else ("FAIR" if corr >= 0.70 else "POOR")
    return Alignment(off, corr, med, mean, pairs, start_delta, duration_delta, quality)


# ----------------------------- correlation ---------------------------------

def top_overlapping_event(events: list[dict[str, Any]], start_epoch: float, end_epoch: float) -> tuple[Optional[dict[str, Any]], float]:
    best = None
    best_score = -1.0
    best_ov = 0.0
    # Event sets are small in current profiler output. A direct scan is clearer and robust.
    for e in events:
        if e["epoch_end_ms"] <= start_epoch or e["epoch_start_ms"] >= end_epoch:
            continue
        ov = overlap_ms(start_epoch, end_epoch, e["epoch_start_ms"], e["epoch_end_ms"])
        # Prefer higher exclusive measured work; overlap breaks ties.
        score = e.get("exclusive_ms", e.get("duration_ms", 0.0)) * 1000.0 + ov
        if score > best_score:
            best_score = score
            best = e
            best_ov = ov
    return best, best_ov


def classify_frame(frame_ms: float, rs_ms: float, cet_event_ms: float) -> str:
    """Evidence class, deliberately not a causal verdict."""
    if frame_ms <= 0:
        return "UNKNOWN"
    rr = rs_ms / frame_ms
    cr = cet_event_ms / frame_ms
    rs20 = rs_ms >= 5.0 and rr >= 0.20
    ce20 = cet_event_ms >= 5.0 and cr >= 0.20
    # A strong signal in one runtime plus a clearly material signal in the other
    # is more useful to a user as MIXED than as a single-runtime label.
    rs_material = rs_ms >= 10.0 and rr >= 0.10
    ce_material = cet_event_ms >= 10.0 and cr >= 0.10
    if (rs20 and ce20) or (rs_material and ce20) or (ce_material and rs20):
        return "MIXED_SCRIPT_SIGNAL"
    if rs_ms >= 5.0 and rr >= 0.35:
        return "REDSCRIPT_HEAVY"
    if cet_event_ms >= 5.0 and cr >= 0.35:
        return "CET_HEAVY"
    if rs_ms >= 3.0 and rr >= 0.12:
        return "REDSCRIPT_SIGNAL"
    if cet_event_ms >= 3.0 and cr >= 0.12:
        return "CET_SIGNAL"
    if frame_ms >= 33.3:
        return "LARGELY_UNEXPLAINED"
    return "NORMAL"


def bucket_index_for_ms(t_ms: float, width_ms: float = 50.0) -> int:
    return max(0, int(math.floor(t_ms / width_ms)))


def build_combined_frames(grsp: GRSPData, cet: CETData, capx: CapXData, alignment: Alignment) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    off = alignment.capx_to_grsp_offset
    cet_shift = cet.start_epoch_ms - grsp.start_epoch_ms

    for i, frame_ms_val in enumerate(capx.frame_ms):
        j = i + off
        if j < 0 or j >= len(grsp.frames):
            continue
        gf = grsp.frames[j]
        start_epoch = gf["start_epoch_ms"]
        end_epoch = gf["end_epoch_ms"]
        start_ms = gf["start_ms"]
        end_ms = gf["end_ms"]

        cet_event, cet_ov = top_overlapping_event(cet.spikes, start_epoch, end_epoch)
        sched_event, sched_ov = top_overlapping_event(cet.scheduler_spikes, start_epoch, end_epoch)
        rs_events = grsp.spikes_by_frame.get(gf["frame_id"], [])
        rs_event = rs_events[0] if rs_events else None

        # CET 50 ms bucket containing the midpoint, shifted onto GRSP time axis.
        midpoint_grsp_ms = (start_ms + end_ms) / 2.0
        midpoint_cet_ms = midpoint_grsp_ms - cet_shift
        cb = cet.buckets.get(bucket_index_for_ms(midpoint_cet_ms, 50.0), {})
        gb = grsp.buckets.get(bucket_index_for_ms(midpoint_grsp_ms, 50.0), {})

        cet_exact = cet_event["exclusive_ms"] if cet_event else 0.0
        cls = classify_frame(frame_ms_val, gf["exclusive_ms"], cet_exact)

        row = {
            "capx_frame_index": i,
            "grsp_frame_id": gf["frame_id"],
            "frame_start_ms": start_ms,
            "frame_end_ms": end_ms,
            "frame_start_unix_ms": start_epoch,
            "frame_start_utc": fmt_epoch(start_epoch),
            "capx_frametime_ms": frame_ms_val,
            "capx_cpu_active_ms": capx.cpu_active_ms[i] if i < len(capx.cpu_active_ms) else 0.0,
            "capx_gpu_active_ms": capx.gpu_active_ms[i] if i < len(capx.gpu_active_ms) else 0.0,
            "capx_dropped": capx.dropped[i] if i < len(capx.dropped) else False,
            "grsp_exclusive_ms": gf["exclusive_ms"],
            "grsp_inclusive_ms": gf["inclusive_ms"],
            "grsp_calls": gf["total_calls"],
            "grsp_top_event_owner": rs_event["owner"] if rs_event else "",
            "grsp_top_event_target": rs_event["target"] if rs_event else "",
            "grsp_top_event_exclusive_ms": rs_event["exclusive_ms"] if rs_event else 0.0,
            "grsp_bucket_top_owner": gb.get("top_owner", ""),
            "grsp_bucket_top_owner_ms": gb.get("top_owner_ms", 0.0),
            "cet_exact_mod": cet_event["owner"] if cet_event else "",
            "cet_exact_kind": cet_event["kind"] if cet_event else "",
            "cet_exact_target": cet_event["target"] if cet_event else "",
            "cet_exact_exclusive_ms": cet_exact,
            "cet_exact_duration_ms": cet_event["duration_ms"] if cet_event else 0.0,
            "cet_exact_overlap_ms": cet_ov,
            "cet_bucket_observed_exclusive_ms": cb.get("exclusive_ms", 0.0),
            "cet_bucket_top_mod": cb.get("top_mod", ""),
            "cet_bucket_top_mod_ms": cb.get("top_mod_ms", 0.0),
            "scheduler_owner": sched_event["owner"] if sched_event else "",
            "scheduler_job": sched_event["target"] if sched_event else "",
            "scheduler_duration_ms": sched_event["duration_ms"] if sched_event else 0.0,
            "scheduler_overlap_ms": sched_ov,
            "evidence_class": cls,
        }
        out.append(row)
    return out


def build_combined_timeline(grsp: GRSPData, cet: CETData, combined_frames: list[dict[str, Any]], width_ms: float = 50.0) -> list[dict[str, Any]]:
    max_ms = max(grsp.duration_ms, cet.duration_ms + (cet.start_epoch_ms - grsp.start_epoch_ms))
    count = int(math.ceil(max_ms / width_ms))

    capx_bucket: dict[int, dict[str, Any]] = {}
    for f in combined_frames:
        idx = bucket_index_for_ms(f["frame_start_ms"], width_ms)
        a = capx_bucket.setdefault(idx, {"sum": 0.0, "count": 0, "max": 0.0, "bad33": 0, "bad50": 0, "bad100": 0})
        ft = f["capx_frametime_ms"]
        a["sum"] += ft
        a["count"] += 1
        a["max"] = max(a["max"], ft)
        a["bad33"] += int(ft >= 33.3)
        a["bad50"] += int(ft >= 50.0)
        a["bad100"] += int(ft >= 100.0)

    cet_by_grsp_bucket: dict[int, dict[str, Any]] = {}
    for b in cet.buckets.values():
        # Remap CET buckets by actual wall-clock overlap. A 3 ms start skew, for
        # example, should not arbitrarily move an entire 50 ms bucket.
        src_start = (cet.epoch_offset_ms + b["start_capture_ms"]) - grsp.start_epoch_ms
        src_end = (cet.epoch_offset_ms + b["end_capture_ms"]) - grsp.start_epoch_ms
        src_width = max(0.001, src_end - src_start)
        first = bucket_index_for_ms(max(0.0, src_start), width_ms)
        last = bucket_index_for_ms(max(0.0, src_end - 1e-9), width_ms)
        for idx in range(first, last + 1):
            dst_start = idx * width_ms
            dst_end = dst_start + width_ms
            ov = overlap_ms(src_start, src_end, dst_start, dst_end)
            if ov <= 0:
                continue
            frac = ov / src_width
            a = cet_by_grsp_bucket.setdefault(idx, {"exclusive_ms": 0.0, "top_mod": "", "top_mod_ms": 0.0, "calls": 0.0})
            a["exclusive_ms"] += b["exclusive_ms"] * frac
            a["calls"] += b["calls"] * frac
            candidate_top = b["top_mod_ms"] * frac
            if candidate_top > a["top_mod_ms"]:
                a["top_mod_ms"] = candidate_top
                a["top_mod"] = b["top_mod"]

    rows: list[dict[str, Any]] = []
    for idx in range(count):
        start = idx * width_ms
        end = start + width_ms
        gb = grsp.buckets.get(idx, {})
        cb = cet_by_grsp_bucket.get(idx, {})
        xb = capx_bucket.get(idx, {})
        rows.append({
            "bucket_index": idx,
            "bucket_start_ms": start,
            "bucket_end_ms": end,
            "bucket_start_unix_ms": grsp.start_epoch_ms + start,
            "capx_avg_frametime_ms": (xb.get("sum", 0.0) / xb.get("count", 1)) if xb.get("count", 0) else 0.0,
            "capx_max_frametime_ms": xb.get("max", 0.0),
            "capx_frames": xb.get("count", 0),
            "capx_frames_ge_33_3ms": xb.get("bad33", 0),
            "capx_frames_ge_50ms": xb.get("bad50", 0),
            "capx_frames_ge_100ms": xb.get("bad100", 0),
            "grsp_observed_exclusive_ms": gb.get("exclusive_ms", 0.0),
            "grsp_top_owner": gb.get("top_owner", ""),
            "grsp_top_owner_ms": gb.get("top_owner_ms", 0.0),
            "cet_observed_exclusive_ms": cb.get("exclusive_ms", 0.0),
            "cet_top_mod": cb.get("top_mod", ""),
            "cet_top_mod_ms": cb.get("top_mod_ms", 0.0),
        })
    return rows


# ----------------------------- report --------------------------------------

def html_escape(v: Any) -> str:
    return html.escape(str(v), quote=True)


def _json_compact(obj: Any) -> str:
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def report_html(grsp: GRSPData, cet: CETData, capx: CapXData, alignment: Alignment,
                frames: list[dict[str, Any]], timeline: list[dict[str, Any]],
                source_label: str) -> str:
    bad = [r for r in frames if r["capx_frametime_ms"] >= 33.3]
    bad_sorted = sorted(bad, key=lambda r: r["capx_frametime_ms"], reverse=True)
    fts = [r["capx_frametime_ms"] for r in frames if r["capx_frametime_ms"] > 0]
    avg_ft = sum(fts) / len(fts) if fts else 0.0
    avg_fps = 1000.0 / avg_ft if avg_ft else 0.0
    p95 = percentile(fts, 0.95)
    p99 = percentile(fts, 0.99)
    max_ft = max(fts, default=0.0)

    classes = defaultdict(int)
    for r in bad:
        classes[r["evidence_class"]] += 1

    # Compact chart series. Keep every 50ms bucket; ~8k points is fine in Canvas.
    chart = [{
        "t": round(r["bucket_start_ms"] / 1000.0, 3),
        "f": round(r["capx_max_frametime_ms"], 4),
        "r": round(r["grsp_observed_exclusive_ms"], 4),
        "c": round(r["cet_observed_exclusive_ms"], 4),
    } for r in timeline]

    bad_js = [{
        "i": r["capx_frame_index"], "g": r["grsp_frame_id"],
        "t": round(r["frame_start_ms"] / 1000.0, 3),
        "ft": round(r["capx_frametime_ms"], 4),
        "rs": round(r["grsp_exclusive_ms"], 4),
        "ro": r["grsp_top_event_owner"] or r["grsp_bucket_top_owner"],
        "rt": r["grsp_top_event_target"],
        "ce": round(r["cet_exact_exclusive_ms"], 4),
        "co": r["cet_exact_mod"], "ct": r["cet_exact_target"],
        "cb": round(r["cet_bucket_observed_exclusive_ms"], 4),
        "cm": r["cet_bucket_top_mod"],
        "so": r["scheduler_owner"], "sj": r["scheduler_job"],
        "sd": round(r["scheduler_duration_ms"], 4),
        "cl": r["evidence_class"],
    } for r in bad_sorted[:500]]

    grsp_mods = grsp.by_mod[:25]
    cet_mods = cet.by_mod[:25]

    sync_quality = alignment.quality
    sync_cls = "good" if sync_quality == "GOOD" else ("warn" if sync_quality == "FAIR" else "bad")
    capture_health_good = (
        str(grsp.summary.get("frame_quality", "")).upper() == "GOOD" and
        inum(grsp.summary.get("dropped_spikes")) == 0 and
        inum(grsp.summary.get("dropped_hot_paths")) == 0 and
        cet.dropped_timeline == 0 and cet.dropped_spikes == 0 and
        cet.dropped_scheduler_spikes == 0 and cet.dropped_scheduler_bursts == 0
    )

    css = r"""
:root{--bg:#0b0d10;--panel:#12161b;--panel2:#171d23;--line:#26303a;--txt:#e8edf2;--muted:#9aa8b5;--accent:#54e1ff;--accent2:#f6d365;--red:#ff6b6b;--green:#58d68d;--purple:#c792ea;--orange:#ffae57}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.45 Segoe UI,Inter,Arial,sans-serif}.wrap{max-width:1500px;margin:auto;padding:28px}.hero{display:flex;gap:22px;align-items:flex-end;justify-content:space-between;margin-bottom:18px}.hero h1{font-size:30px;margin:0 0 3px}.sub{color:var(--muted)}.badge{display:inline-block;border:1px solid var(--line);background:#101419;padding:4px 9px;border-radius:999px;color:var(--muted)}.badge.good{color:var(--green);border-color:#245c3d}.badge.warn{color:var(--accent2);border-color:#665a27}.badge.bad{color:var(--red);border-color:#6d3131}.grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin:14px 0}.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px;min-width:0}.card .k{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.05em}.card .v{font-size:23px;font-weight:700;margin-top:4px}.card .s{font-size:12px;color:var(--muted);margin-top:4px}.section{margin-top:18px}.section h2{font-size:19px;margin:0 0 10px}.note{background:#0f1419;border-left:3px solid var(--accent);padding:10px 12px;color:#cbd5dd;border-radius:5px;margin:8px 0}.warning{border-left-color:var(--accent2)}.chartbox{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:10px}.toolbar{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-bottom:8px}.toolbar button,.toolbar select{background:#151b21;color:var(--txt);border:1px solid #33404b;border-radius:6px;padding:6px 9px}.toolbar button:hover{border-color:var(--accent);cursor:pointer}canvas{width:100%;height:430px;display:block;background:#0d1115;border-radius:6px}.legend{display:flex;gap:18px;flex-wrap:wrap;color:var(--muted);font-size:12px;margin-top:7px}.sw{display:inline-block;width:12px;height:3px;margin-right:5px;vertical-align:middle}.sw.ft{background:#e6e6e6}.sw.rs{background:#54e1ff}.sw.cet{background:#c792ea}table{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--line);border-radius:8px;overflow:hidden}th,td{padding:7px 9px;border-bottom:1px solid #202832;text-align:left;vertical-align:top}th{position:sticky;top:0;background:#171d23;color:#b8c5cf;font-size:12px;z-index:1}td.num{text-align:right;font-variant-numeric:tabular-nums}tbody tr:hover{background:#151c22}.tablewrap{max-height:520px;overflow:auto;border-radius:8px}.class{font-size:11px;font-weight:700;white-space:nowrap}.REDSCRIPT_HEAVY{color:var(--accent)}.CET_HEAVY{color:var(--purple)}.MIXED_SCRIPT_SIGNAL{color:var(--orange)}.LARGELY_UNEXPLAINED{color:var(--red)}.REDSCRIPT_SIGNAL{color:#88edff}.CET_SIGNAL{color:#d9a6f5}.two{display:grid;grid-template-columns:1fr 1fr;gap:12px}.small{font-size:12px;color:var(--muted)}code{background:#0d1115;border:1px solid #26303a;padding:1px 5px;border-radius:4px}.footer{color:var(--muted);font-size:12px;margin:24px 0 10px}.owner{max-width:290px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.tooltip{position:fixed;pointer-events:none;background:#090c0f;border:1px solid #41505c;color:#eef4f8;padding:7px 9px;border-radius:6px;font-size:12px;display:none;z-index:10;box-shadow:0 6px 30px #0008}.healthline{display:grid;grid-template-columns:220px 1fr;gap:8px;padding:4px 0;border-bottom:1px solid #202832}.healthline:last-child{border-bottom:0}.muted{color:var(--muted)}
@media(max-width:1000px){.grid{grid-template-columns:repeat(2,1fr)}.two{grid-template-columns:1fr}.wrap{padding:14px}.hero{display:block}}
"""

    # tables generated server side for zero-JS fallback
    def bad_rows_html(rows: list[dict[str, Any]]) -> str:
        parts = []
        for r in rows[:200]:
            ro = r["grsp_top_event_owner"] or r["grsp_bucket_top_owner"] or "—"
            rt = r["grsp_top_event_target"] or ""
            cetdesc = "—"
            if r["cet_exact_mod"]:
                cetdesc = f"{html_escape(r['cet_exact_mod'])}<br><span class='small'>{html_escape(r['cet_exact_target'])}</span>"
            sched = "—"
            if r["scheduler_owner"]:
                sched = f"{html_escape(r['scheduler_owner'])}<br><span class='small'>{html_escape(r['scheduler_job'])}</span>"
            parts.append(
                f"<tr data-ft='{r['capx_frametime_ms']:.4f}' data-class='{html_escape(r['evidence_class'])}'>"
                f"<td class='num'>{r['frame_start_ms']/1000:.3f}</td>"
                f"<td class='num'><b>{r['capx_frametime_ms']:.2f}</b></td>"
                f"<td><span class='class {html_escape(r['evidence_class'])}'>{html_escape(r['evidence_class'])}</span></td>"
                f"<td class='num'>{r['grsp_exclusive_ms']:.3f}</td>"
                f"<td class='owner'>{html_escape(ro)}<br><span class='small'>{html_escape(rt)}</span></td>"
                f"<td class='num'>{r['cet_exact_exclusive_ms']:.3f}</td>"
                f"<td class='owner'>{cetdesc}</td>"
                f"<td class='num'>{r['cet_bucket_observed_exclusive_ms']:.3f}</td>"
                f"<td class='owner'>{sched}</td></tr>"
            )
        return "".join(parts)

    def grsp_mod_rows(rows: list[dict[str, Any]]) -> str:
        s=[]
        for r in rows:
            note = r["note"] or ""
            s.append(f"<tr><td>{r['rank']}</td><td class='owner'>{html_escape(r['owner'])}</td>"
                     f"<td class='num'>{r['exclusive_ms_per_sec']:.3f}</td><td class='num'>{r['share_pct']:.2f}%</td>"
                     f"<td class='num'>{r['active_frame_pct']:.1f}%</td><td class='num'>{r['max_frame_ms']:.3f}</td>"
                     f"<td>{html_escape(r['workload'])}</td><td class='small'>{html_escape(note)}</td></tr>")
        return "".join(s)

    def cet_mod_rows(rows: list[dict[str, Any]]) -> str:
        s=[]
        for i,r in enumerate(rows,1):
            s.append(f"<tr><td>{i}</td><td class='owner'>{html_escape(r['mod'])}</td>"
                     f"<td class='num'>{r['exclusive_ms_per_sec']:.3f}</td><td class='num'>{r['share_pct']:.2f}%</td>"
                     f"<td class='num'>{r['one_core_pct']:.3f}%</td><td class='num'>{r['max_exclusive_ms']:.3f}</td></tr>")
        return "".join(s)

    class_cards = "".join(
        f"<div class='card'><div class='k'>{html_escape(k.replace('_',' '))}</div><div class='v'>{v}</div><div class='s'>frames ≥33.3 ms</div></div>"
        for k,v in sorted(classes.items(), key=lambda kv: kv[1], reverse=True)[:4]
    ) or "<div class='card'><div class='k'>Bad frames</div><div class='v'>0</div></div>"

    html_doc = f"""<!doctype html>
<html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>GRSP Combined Report</title><style>{css}</style></head><body><div class='wrap'>
<div class='hero'><div><h1>GRSP Combined Report</h1><div class='sub'>GRSP + CET Runtime Profiler + CapFrameX · Correlator v{VERSION}</div></div>
<div><span class='badge {sync_cls}'>SYNC {html_escape(sync_quality)}</span> <span class='badge {'good' if capture_health_good else 'warn'}'>CAPTURE {'HEALTHY' if capture_health_good else 'CHECK WARNINGS'}</span></div></div>

<div class='grid'>
<div class='card'><div class='k'>Capture duration</div><div class='v'>{grsp.duration_ms/1000:.2f} s</div><div class='s'>{len(frames):,} aligned CapFrameX frames</div></div>
<div class='card'><div class='k'>Average</div><div class='v'>{avg_fps:.1f} FPS</div><div class='s'>{avg_ft:.2f} ms mean frametime</div></div>
<div class='card'><div class='k'>P99 frametime</div><div class='v'>{p99:.2f} ms</div><div class='s'>P95 {p95:.2f} ms · max {max_ft:.2f} ms</div></div>
<div class='card'><div class='k'>Frames ≥33.3 ms</div><div class='v'>{len(bad):,}</div><div class='s'>≥50 ms {sum(x>=50 for x in fts):,} · ≥100 ms {sum(x>=100 for x in fts):,}</div></div>
</div>

<div class='section'><h2>Synchronization</h2><div class='two'>
<div class='card'>
<div class='healthline'><span class='muted'>GRSP start</span><span>{fmt_epoch(grsp.start_epoch_ms)}</span></div>
<div class='healthline'><span class='muted'>CET start</span><span>{fmt_epoch(cet.start_epoch_ms)}</span></div>
<div class='healthline'><span class='muted'>GRSP ↔ CET start delta</span><span><b>{alignment.grsp_cet_start_delta_ms:.3f} ms</b></span></div>
<div class='healthline'><span class='muted'>GRSP ↔ CET duration delta</span><span>{alignment.grsp_cet_duration_delta_ms:.3f} ms</span></div>
</div><div class='card'>
<div class='healthline'><span class='muted'>CapX → GRSP frame offset</span><span>{alignment.capx_to_grsp_offset:+d} frames</span></div>
<div class='healthline'><span class='muted'>Frametime correlation</span><span><b>{alignment.correlation:.4f}</b></span></div>
<div class='healthline'><span class='muted'>Median duration delta</span><span>{alignment.median_abs_frame_delta_ms:.3f} ms</span></div>
<div class='healthline'><span class='muted'>Aligned pairs</span><span>{alignment.pairs:,}</span></div>
</div></div>
<div class='note'>The correlator uses GRSP's Unix timeline as the common clock. CET is mapped using its <code>CAPTURE_START</code> epoch marker. CapFrameX is aligned to GRSP by the observed frametime sequence, not by filename time.</div></div>

<div class='section'><h2>Combined timeline</h2><div class='chartbox'><div class='toolbar'>
<button onclick='zoomAll()'>Full capture</button><button onclick='zoomBad()'>Around largest hitch</button>
<label class='small'>Scale <select id='scale' onchange='draw()'><option value='auto'>Auto</option><option value='100'>0–100 ms</option><option value='250'>0–250 ms</option><option value='500'>0–500 ms</option></select></label>
</div><canvas id='chart'></canvas><div class='legend'><span><i class='sw ft'></i>CapFrameX max frametime / 50 ms</span><span><i class='sw rs'></i>GRSP observed REDscript exclusive work</span><span><i class='sw cet'></i>CET observed exclusive work</span></div></div>
<div class='note warning'><b>Do not add or subtract these series as if they were one CPU budget.</b> CET buckets may contain concurrent work and long events can cross frame/bucket boundaries. GRSP measures observed instrumented REDscript edges. The report uses them as synchronized evidence layers.</div></div>

<div class='section'><h2>Bad-frame evidence</h2><div class='grid'>{class_cards}</div>
<div class='toolbar'><label>Show <select id='thresh' onchange='filterRows()'><option value='33.3'>≥33.3 ms</option><option value='50'>≥50 ms</option><option value='100'>≥100 ms</option></select></label>
<label>Class <select id='classfilter' onchange='filterRows()'><option value='ALL'>All</option><option>REDSCRIPT_HEAVY</option><option>CET_HEAVY</option><option>MIXED_SCRIPT_SIGNAL</option><option>REDSCRIPT_SIGNAL</option><option>CET_SIGNAL</option><option>LARGELY_UNEXPLAINED</option></select></label></div>
<div class='tablewrap'><table id='badtable'><thead><tr><th>t (s)</th><th>Frame</th><th>Evidence class</th><th>GRSP ms</th><th>REDscript evidence</th><th>CET exact ms</th><th>CET exact event</th><th>CET 50ms observed</th><th>Scheduler evidence</th></tr></thead><tbody>{bad_rows_html(bad_sorted)}</tbody></table></div>
<div class='small' style='margin-top:6px'>Classes are evidence labels, not uninstall recommendations and not causal proof. “Largely unexplained” means neither profiler shows a large script signal for that frame; it does not prove the remainder is native/engine work.</div></div>

<div class='section'><h2>Top observed runtime owners</h2><div class='two'>
<div><h3>REDscript / GRSP</h3><div class='tablewrap'><table><thead><tr><th>#</th><th>Owner</th><th>exclusive ms/s</th><th>share</th><th>active frames</th><th>max frame</th><th>pattern</th><th>note</th></tr></thead><tbody>{grsp_mod_rows(grsp_mods)}</tbody></table></div></div>
<div><h3>CET / Lua</h3><div class='tablewrap'><table><thead><tr><th>#</th><th>Mod</th><th>exclusive ms/s</th><th>share</th><th>1-core %</th><th>max event</th></tr></thead><tbody>{cet_mod_rows(cet_mods)}</tbody></table></div></div>
</div></div>

<div class='section'><h2>Capture health</h2><div class='card'>
<div class='healthline'><span class='muted'>GRSP frame quality</span><span>{html_escape(grsp.summary.get('frame_quality',''))}</span></div>
<div class='healthline'><span class='muted'>GRSP shard merge</span><span>{html_escape(grsp.summary.get('shard_merge_ok',''))}</span></div>
<div class='healthline'><span class='muted'>GRSP unresolved static calls</span><span>{inum(grsp.summary.get('unresolved_static_calls')):,}</span></div>
<div class='healthline'><span class='muted'>GRSP dropped spikes / hot paths</span><span>{inum(grsp.summary.get('dropped_spikes'))} / {inum(grsp.summary.get('dropped_hot_paths'))}</span></div>
<div class='healthline'><span class='muted'>CET dropped timeline / spikes</span><span>{cet.dropped_timeline} / {cet.dropped_spikes}</span></div>
<div class='healthline'><span class='muted'>CET dropped scheduler spikes / bursts</span><span>{cet.dropped_scheduler_spikes} / {cet.dropped_scheduler_bursts}</span></div>
<div class='healthline'><span class='muted'>CapFrameX source</span><span>{html_escape(capx.info.get('GameName',''))} · {html_escape(capx.info.get('GPU',''))}</span></div>
</div></div>

<div class='section'><h2>Interpretation model</h2>
<div class='note'>GRSP <b>exclusive_instrumented_ms</b> is observed instrumented-edge time, not guaranteed complete VM self-time. Wrapper-heavy owners can include work performed beneath the wrapper and are flagged by GRSP where appropriate.</div>
<div class='note'>CET exact spikes are preferred for hitch attribution because they contain precise start/end intervals. The 50 ms CET timeline remains useful for continuous workload correlation, but its values are not guaranteed to be additive wall-clock frame time.</div>
<div class='note'>CapFrameX is the authoritative rendered frametime layer. The correlator does <b>not</b> compute <code>frame - GRSP - CET = native remainder</code>, because the measurement domains can overlap and can execute concurrently.</div>
</div>

<div class='footer'>Source: {html_escape(source_label)} · Generated by {APP_NAME} v{VERSION}. Output CSVs preserve the aligned data used by this report.</div>
</div><div id='tip' class='tooltip'></div>
<script>
const SERIES={_json_compact(chart)};
const BAD={_json_compact(bad_js)};
let view0=0, view1=SERIES.length?SERIES[SERIES.length-1].t:1;
const canvas=document.getElementById('chart'),ctx=canvas.getContext('2d'),tip=document.getElementById('tip');
function resize(){{const dpr=window.devicePixelRatio||1;const r=canvas.getBoundingClientRect();canvas.width=Math.max(300,Math.floor(r.width*dpr));canvas.height=Math.floor(430*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);draw();}}
function zoomAll(){{view0=0;view1=SERIES.length?SERIES[SERIES.length-1].t:1;draw();}}
function zoomBad(){{if(!BAD.length)return;let b=BAD.reduce((a,x)=>x.ft>a.ft?x:a,BAD[0]);view0=Math.max(0,b.t-3);view1=b.t+3;draw();}}
function maxVisible(){{let m=16.67;for(const p of SERIES)if(p.t>=view0&&p.t<=view1)m=Math.max(m,p.f,p.r,p.c);return m*1.08;}}
function draw(){{if(!SERIES.length)return;const W=canvas.clientWidth,H=430,padL=55,padR=12,padT=12,padB=32;ctx.clearRect(0,0,W,H);let sv=document.getElementById('scale').value;let ymax=sv==='auto'?maxVisible():+sv;const x=t=>padL+(t-view0)/(view1-view0)*(W-padL-padR);const y=v=>padT+(1-Math.min(v,ymax)/ymax)*(H-padT-padB);ctx.strokeStyle='#28323b';ctx.lineWidth=1;ctx.fillStyle='#8c9aa6';ctx.font='11px Segoe UI';for(let k=0;k<=5;k++){{let v=ymax*k/5,yy=y(v);ctx.beginPath();ctx.moveTo(padL,yy);ctx.lineTo(W-padR,yy);ctx.stroke();ctx.fillText(v.toFixed(v>=100?0:1)+' ms',4,yy+4);}}for(let k=0;k<=6;k++){{let t=view0+(view1-view0)*k/6,xx=x(t);ctx.fillText(t.toFixed(view1-view0>100?0:1)+'s',xx-12,H-10);}}function line(key,color){{ctx.strokeStyle=color;ctx.lineWidth=1.4;ctx.beginPath();let begun=false;for(const p of SERIES){{if(p.t<view0||p.t>view1)continue;let xx=x(p.t),yy=y(p[key]);if(!begun){{ctx.moveTo(xx,yy);begun=true}}else ctx.lineTo(xx,yy)}}ctx.stroke();}}line('f','#e6e6e6');line('r','#54e1ff');line('c','#c792ea');}}
canvas.addEventListener('mousemove',e=>{{const r=canvas.getBoundingClientRect();let t=view0+(e.clientX-r.left-55)/(r.width-67)*(view1-view0);let idx=Math.max(0,Math.min(SERIES.length-1,Math.round(t/0.05)));let p=SERIES[idx];if(!p)return;tip.style.display='block';tip.style.left=(e.clientX+14)+'px';tip.style.top=(e.clientY+14)+'px';tip.innerHTML=`<b>${{p.t.toFixed(3)}} s</b><br>CapX max: ${{p.f.toFixed(2)}} ms<br>GRSP: ${{p.r.toFixed(3)}} ms<br>CET observed: ${{p.c.toFixed(3)}} ms`;}});canvas.addEventListener('mouseleave',()=>tip.style.display='none');
function filterRows(){{let th=+document.getElementById('thresh').value,cf=document.getElementById('classfilter').value;for(const tr of document.querySelectorAll('#badtable tbody tr')){{let ok=+tr.dataset.ft>=th&&(cf==='ALL'||tr.dataset.class===cf);tr.style.display=ok?'':'none';}}}}
window.addEventListener('resize',resize);resize();
</script></body></html>"""
    return html_doc



def _top_hitch_export_row(r: dict[str, Any]) -> dict[str, Any]:
    """Compact, stable hitch representation for machine/AI analysis."""
    return {
        "time_s": round(fnum(r.get("frame_start_ms")) / 1000.0, 6),
        "capx_frame_index": inum(r.get("capx_frame_index"), -1),
        "frametime_ms": round(fnum(r.get("capx_frametime_ms")), 6),
        "evidence_class": r.get("evidence_class", ""),
        "grsp_exclusive_ms": round(fnum(r.get("grsp_exclusive_ms")), 6),
        "grsp_top_owner": r.get("grsp_top_event_owner") or r.get("grsp_bucket_top_owner") or "",
        "grsp_top_target": r.get("grsp_top_event_target") or "",
        "grsp_top_event_ms": round(fnum(r.get("grsp_top_event_exclusive_ms")), 6),
        "cet_exact_mod": r.get("cet_exact_mod", ""),
        "cet_exact_kind": r.get("cet_exact_kind", ""),
        "cet_exact_target": r.get("cet_exact_target", ""),
        "cet_exact_exclusive_ms": round(fnum(r.get("cet_exact_exclusive_ms")), 6),
        "cet_exact_duration_ms": round(fnum(r.get("cet_exact_duration_ms")), 6),
        "cet_bucket_observed_ms": round(fnum(r.get("cet_bucket_observed_exclusive_ms")), 6),
        "scheduler_owner": r.get("scheduler_owner", ""),
        "scheduler_job": r.get("scheduler_job", ""),
        "scheduler_duration_ms": round(fnum(r.get("scheduler_duration_ms")), 6),
    }


def write_analysis_exports(
    out_dir: pathlib.Path,
    source_label: str,
    grsp: GRSPData,
    cet: CETData,
    capx: CapXData,
    alignment: Alignment,
    frames: list[dict[str, Any]],
    timeline: list[dict[str, Any]],
    hitches: list[dict[str, Any]],
) -> tuple[pathlib.Path, pathlib.Path]:
    """Write compact machine-readable and AI-friendly analysis files."""
    fts = [fnum(r.get("capx_frametime_ms")) for r in frames]
    avg_ft = statistics.fmean(fts) if fts else 0.0
    avg_fps = (1000.0 / avg_ft) if avg_ft > 0 else 0.0
    p95 = percentile(fts, 0.95)
    p99 = percentile(fts, 0.99)
    max_ft = max(fts, default=0.0)

    classes: dict[str, int] = defaultdict(int)
    for r in hitches:
        classes[str(r.get("evidence_class", ""))] += 1

    top_grsp = []
    for r in grsp.by_mod[:30]:
        top_grsp.append({
            "rank": r.get("rank"),
            "owner": r.get("owner", ""),
            "exclusive_ms_per_sec": round(fnum(r.get("exclusive_ms_per_sec")), 6),
            "share_pct": round(fnum(r.get("share_pct")), 6),
            "active_frame_pct": round(fnum(r.get("active_frame_pct")), 6),
            "max_call_ms": round(fnum(r.get("max_call_ms")), 6),
            "max_frame_ms": round(fnum(r.get("max_frame_ms")), 6),
            "spike_count": inum(r.get("spike_count")),
            "max_spike_ms": round(fnum(r.get("max_spike_ms")), 6),
            "workload_pattern": r.get("workload", ""),
            "attribution_note": r.get("note", ""),
        })

    top_cet = []
    for i, r in enumerate(cet.by_mod[:30], 1):
        top_cet.append({
            "rank": i,
            "mod": r.get("mod", ""),
            "exclusive_ms_per_sec": round(fnum(r.get("exclusive_ms_per_sec")), 6),
            "share_pct": round(fnum(r.get("share_pct")), 6),
            "measured_one_core_pct": round(fnum(r.get("one_core_pct")), 6),
            "max_exclusive_ms": round(fnum(r.get("max_exclusive_ms")), 6),
            "coverage": r.get("coverage", ""),
        })

    payload = {
        "schema": "grsp-correlator-analysis-v1",
        "correlator_version": VERSION,
        "source": source_label,
        "interpretation": {
            "capframex_role": "Rendered frametime evidence.",
            "grsp_role": "Observed instrumented REDscript work; not guaranteed complete VM self-time.",
            "cet_role": "Observed CET/Lua runtime work and exact spike spans where available.",
            "warning": "GRSP and CET measurements are synchronized evidence layers and must not be blindly subtracted from CapFrameX frametime.",
            "largely_unexplained": "Neither script profiler shows a large signal for the frame; this does not prove the remainder is native/engine work.",
        },
        "sync": {
            "quality": alignment.quality,
            "grsp_start_unix_ms": grsp.start_epoch_ms,
            "cet_start_unix_ms": cet.start_epoch_ms,
            "start_delta_ms": alignment.grsp_cet_start_delta_ms,
            "grsp_duration_ms": grsp.duration_ms,
            "cet_duration_ms": cet.duration_ms,
            "duration_delta_ms": alignment.grsp_cet_duration_delta_ms,
            "capx_to_grsp_frame_offset": alignment.capx_to_grsp_offset,
            "frametime_correlation": alignment.correlation,
            "median_abs_frame_duration_delta_ms": alignment.median_abs_frame_delta_ms,
            "mean_abs_frame_duration_delta_ms": alignment.mean_abs_frame_delta_ms,
            "aligned_frame_pairs": alignment.pairs,
        },
        "capture_health": {
            "grsp_frame_quality": grsp.summary.get("frame_quality", ""),
            "grsp_shard_merge_ok": grsp.summary.get("shard_merge_ok", ""),
            "grsp_unresolved_static_calls": inum(grsp.summary.get("unresolved_static_calls")),
            "grsp_dropped_spikes": inum(grsp.summary.get("dropped_spikes")),
            "grsp_dropped_hot_paths": inum(grsp.summary.get("dropped_hot_paths")),
            "cet_dropped_timeline_rows": cet.dropped_timeline,
            "cet_dropped_spikes": cet.dropped_spikes,
            "cet_dropped_scheduler_spikes": cet.dropped_scheduler_spikes,
            "cet_dropped_scheduler_bursts": cet.dropped_scheduler_bursts,
        },
        "performance_summary": {
            "capture_duration_s": round(grsp.duration_ms / 1000.0, 6),
            "aligned_capx_frames": len(frames),
            "average_fps": round(avg_fps, 6),
            "average_frametime_ms": round(avg_ft, 6),
            "p95_frametime_ms": round(p95, 6),
            "p99_frametime_ms": round(p99, 6),
            "maximum_frametime_ms": round(max_ft, 6),
            "frames_ge_33_3_ms": sum(x >= 33.3 for x in fts),
            "frames_ge_50_ms": sum(x >= 50.0 for x in fts),
            "frames_ge_100_ms": sum(x >= 100.0 for x in fts),
            "hitch_evidence_classes": dict(sorted(classes.items())),
        },
        "top_redscript_owners": top_grsp,
        "top_cet_owners": top_cet,
        "top_hitches": [_top_hitch_export_row(r) for r in hitches[:100]],
        "files": {
            "full_frame_data": "GRSP_Combined_Frames.csv",
            "full_50ms_timeline": "GRSP_Combined_Timeline.csv",
            "all_hitches_ge_33_3ms": "GRSP_Combined_Hitches.csv",
            "human_report": "GRSP_Combined_Report.html",
            "status": "GRSP_Correlator_Status.txt",
        },
    }

    json_path = out_dir / "GRSP_Analysis.json"
    json_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    md_lines = [
        f"# GRSP Combined Analysis — Correlator v{VERSION}",
        "",
        "This file is intentionally compact and suitable for attaching to an AI assistant or reviewing as plain text.",
        "For exhaustive per-frame/per-bucket data, use the CSV files in the same folder.",
        "",
        "## Interpretation rules",
        "",
        "- CapFrameX is the rendered frametime layer.",
        "- GRSP is observed instrumented REDscript work, not guaranteed complete VM self-time.",
        "- CET is observed CET/Lua runtime work; exact spike intervals are preferred for hitch attribution.",
        "- Do **not** calculate `frametime - GRSP - CET = native`. The measurement domains can overlap and run concurrently.",
        "- `LARGELY_UNEXPLAINED` means neither script profiler shows a large signal; it does not prove native/engine causation.",
        "",
        "## Synchronization",
        "",
        f"- Quality: **{alignment.quality}**",
        f"- GRSP ↔ CET start delta: **{alignment.grsp_cet_start_delta_ms:.3f} ms**",
        f"- GRSP ↔ CET duration delta: **{alignment.grsp_cet_duration_delta_ms:.3f} ms**",
        f"- CapFrameX → GRSP frame offset: **{alignment.capx_to_grsp_offset:+d}**",
        f"- Frametime-sequence correlation: **{alignment.correlation:.6f}**",
        f"- Median frame-duration delta: **{alignment.median_abs_frame_delta_ms:.6f} ms**",
        "",
        "## Capture health",
        "",
        f"- GRSP frame quality: **{grsp.summary.get('frame_quality','')}**",
        f"- GRSP shard merge: **{grsp.summary.get('shard_merge_ok','')}**",
        f"- GRSP unresolved static calls: **{inum(grsp.summary.get('unresolved_static_calls'))}**",
        f"- GRSP dropped spikes / hot paths: **{inum(grsp.summary.get('dropped_spikes'))} / {inum(grsp.summary.get('dropped_hot_paths'))}**",
        f"- CET dropped timeline / spikes: **{cet.dropped_timeline} / {cet.dropped_spikes}**",
        f"- CET dropped scheduler spikes / bursts: **{cet.dropped_scheduler_spikes} / {cet.dropped_scheduler_bursts}**",
        "",
        "## Performance summary",
        "",
        f"- Duration: **{grsp.duration_ms/1000.0:.3f} s**",
        f"- Aligned frames: **{len(frames):,}**",
        f"- Average: **{avg_fps:.2f} FPS / {avg_ft:.3f} ms**",
        f"- P95 / P99 frametime: **{p95:.3f} / {p99:.3f} ms**",
        f"- Maximum frametime: **{max_ft:.3f} ms**",
        f"- Frames ≥33.3 / 50 / 100 ms: **{sum(x>=33.3 for x in fts)} / {sum(x>=50 for x in fts)} / {sum(x>=100 for x in fts)}**",
        "",
        "## Top REDscript owners",
        "",
        "| # | Owner | ms/s | Share | Active frames | Max frame | Pattern |",
        "|---:|---|---:|---:|---:|---:|---|",
    ]
    for r in top_grsp[:20]:
        owner = str(r["owner"]).replace("|", "\\|")
        pat = str(r["workload_pattern"]).replace("|", "\\|")
        md_lines.append(
            f"| {r['rank']} | {owner} | {r['exclusive_ms_per_sec']:.3f} | "
            f"{r['share_pct']:.2f}% | {r['active_frame_pct']:.1f}% | {r['max_frame_ms']:.3f} | {pat} |"
        )

    md_lines += [
        "",
        "## Top CET/Lua owners",
        "",
        "| # | Mod | ms/s | Share | 1-core % | Max event |",
        "|---:|---|---:|---:|---:|---:|",
    ]
    for r in top_cet[:20]:
        mod = str(r["mod"]).replace("|", "\\|")
        md_lines.append(
            f"| {r['rank']} | {mod} | {r['exclusive_ms_per_sec']:.3f} | "
            f"{r['share_pct']:.2f}% | {r['measured_one_core_pct']:.3f}% | {r['max_exclusive_ms']:.3f} |"
        )

    md_lines += [
        "",
        "## Largest hitches",
        "",
        "| t (s) | Frametime | Class | GRSP | REDscript owner | CET exact | CET owner/target | Scheduler |",
        "|---:|---:|---|---:|---|---:|---|---|",
    ]
    for r in hitches[:50]:
        ro = (r.get("grsp_top_event_owner") or r.get("grsp_bucket_top_owner") or "—").replace("|", "\\|")
        cm = (r.get("cet_exact_mod") or "—").replace("|", "\\|")
        ct = (r.get("cet_exact_target") or "").replace("|", "\\|")
        sched = (r.get("scheduler_owner") or "—").replace("|", "\\|")
        if ct:
            cm = f"{cm} / {ct}"
        md_lines.append(
            f"| {fnum(r.get('frame_start_ms'))/1000.0:.3f} | {fnum(r.get('capx_frametime_ms')):.2f} ms | "
            f"{r.get('evidence_class','')} | {fnum(r.get('grsp_exclusive_ms')):.3f} ms | {ro} | "
            f"{fnum(r.get('cet_exact_exclusive_ms')):.3f} ms | {cm} | {sched} |"
        )

    md_lines += [
        "",
        "## Files for deeper analysis",
        "",
        "- `GRSP_Combined_Frames.csv` — aligned frame-level evidence.",
        "- `GRSP_Combined_Timeline.csv` — common 50 ms timeline.",
        "- `GRSP_Combined_Hitches.csv` — all frames ≥33.3 ms.",
        "- `GRSP_Analysis.json` — structured machine/AI-readable summary.",
        "- `GRSP_Combined_Report.html` — interactive human-facing report.",
        "",
        f"Source: `{source_label}`",
        "",
    ]

    md_path = out_dir / "GRSP_AI_Analysis.md"
    md_path.write_text("\n".join(md_lines), encoding="utf-8")
    return json_path, md_path


def _open_output_folder(path: pathlib.Path) -> None:
    """Best-effort reveal of the persistent output folder."""
    try:
        if os.name == "nt":
            os.startfile(str(path))
        elif sys.platform == "darwin":
            import subprocess
            subprocess.Popen(["open", str(path)])
        else:
            import subprocess
            subprocess.Popen(["xdg-open", str(path)])
    except Exception:
        pass


# ----------------------------- main ----------------------------------------

def run(input_path: pathlib.Path, out_dir: pathlib.Path) -> dict[str, pathlib.Path]:
    inputs = discover_inputs(input_path)
    try:
        grsp = load_grsp(inputs.grsp_dir)
        cet = load_cet(inputs.cet_dir)
        capx = load_capx(inputs.capx_json, grsp.duration_ms)
        alignment = align_capx_to_grsp(capx, grsp)
        alignment.grsp_cet_start_delta_ms = cet.start_epoch_ms - grsp.start_epoch_ms
        alignment.grsp_cet_duration_delta_ms = cet.duration_ms - grsp.duration_ms
        # Tighten overall quality with independent GRSP/CET checks.
        if abs(alignment.grsp_cet_start_delta_ms) > 25 or abs(alignment.grsp_cet_duration_delta_ms) > 250:
            alignment.quality = "FAIR" if alignment.quality == "GOOD" else alignment.quality
        if abs(alignment.grsp_cet_start_delta_ms) > 100 or alignment.correlation < 0.70:
            alignment.quality = "POOR"

        frames = build_combined_frames(grsp, cet, capx, alignment)
        timeline = build_combined_timeline(grsp, cet, frames, 50.0)

        out_dir.mkdir(parents=True, exist_ok=True)
        frames_path = out_dir / "GRSP_Combined_Frames.csv"
        timeline_path = out_dir / "GRSP_Combined_Timeline.csv"
        hitches_path = out_dir / "GRSP_Combined_Hitches.csv"
        status_path = out_dir / "GRSP_Correlator_Status.txt"
        report_path = out_dir / "GRSP_Combined_Report.html"
        analysis_json_path = out_dir / "GRSP_Analysis.json"
        ai_md_path = out_dir / "GRSP_AI_Analysis.md"
        package_path = out_dir / "GRSP_Analysis_Package.zip"

        frame_fields = [
            "capx_frame_index","grsp_frame_id","frame_start_ms","frame_end_ms","frame_start_unix_ms","frame_start_utc",
            "capx_frametime_ms","capx_cpu_active_ms","capx_gpu_active_ms","capx_dropped",
            "grsp_exclusive_ms","grsp_inclusive_ms","grsp_calls","grsp_top_event_owner","grsp_top_event_target","grsp_top_event_exclusive_ms",
            "grsp_bucket_top_owner","grsp_bucket_top_owner_ms",
            "cet_exact_mod","cet_exact_kind","cet_exact_target","cet_exact_exclusive_ms","cet_exact_duration_ms","cet_exact_overlap_ms",
            "cet_bucket_observed_exclusive_ms","cet_bucket_top_mod","cet_bucket_top_mod_ms",
            "scheduler_owner","scheduler_job","scheduler_duration_ms","scheduler_overlap_ms","evidence_class"
        ]
        write_csv(frames_path, frames, frame_fields)

        timeline_fields = list(timeline[0].keys()) if timeline else []
        write_csv(timeline_path, timeline, timeline_fields)
        hitches = sorted((r for r in frames if r["capx_frametime_ms"] >= 33.3), key=lambda r: r["capx_frametime_ms"], reverse=True)
        write_csv(hitches_path, hitches, frame_fields)

        source_label = str(input_path)
        report_path.write_text(report_html(grsp, cet, capx, alignment, frames, timeline, source_label), encoding="utf-8")
        analysis_json_path, ai_md_path = write_analysis_exports(
            out_dir, source_label, grsp, cet, capx, alignment, frames, timeline, hitches
        )

        status = f"""{APP_NAME} v{VERSION}

INPUT
  Root: {input_path}
  GRSP: {inputs.grsp_dir}
  CET:  {inputs.cet_dir}
  CapX: {inputs.capx_json}

SYNC
  Quality: {alignment.quality}
  GRSP start Unix ms: {grsp.start_epoch_ms:.3f}
  CET start Unix ms:  {cet.start_epoch_ms:.3f}
  Start delta: {alignment.grsp_cet_start_delta_ms:.3f} ms
  GRSP duration: {grsp.duration_ms:.3f} ms
  CET duration:  {cet.duration_ms:.3f} ms
  Duration delta: {alignment.grsp_cet_duration_delta_ms:.3f} ms
  CapX -> GRSP frame offset: {alignment.capx_to_grsp_offset:+d}
  Frametime correlation: {alignment.correlation:.6f}
  Median abs frame-duration delta: {alignment.median_abs_frame_delta_ms:.6f} ms
  Mean abs frame-duration delta: {alignment.mean_abs_frame_delta_ms:.6f} ms
  Aligned frame pairs: {alignment.pairs}

HEALTH
  GRSP frame quality: {grsp.summary.get('frame_quality','')}
  GRSP shard merge: {grsp.summary.get('shard_merge_ok','')}
  GRSP unresolved static calls: {grsp.summary.get('unresolved_static_calls','')}
  GRSP dropped spikes: {grsp.summary.get('dropped_spikes','')}
  GRSP dropped hot paths: {grsp.summary.get('dropped_hot_paths','')}
  CET dropped timeline rows: {cet.dropped_timeline}
  CET dropped spikes: {cet.dropped_spikes}
  CET dropped scheduler spikes: {cet.dropped_scheduler_spikes}
  CET dropped scheduler bursts: {cet.dropped_scheduler_bursts}

OUTPUT
  {report_path.name}
  {frames_path.name}
  {timeline_path.name}
  {hitches_path.name}
  {analysis_json_path.name}
  {ai_md_path.name}
  {package_path.name}

INTERPRETATION
  Evidence classes are synchronized profiling signals, not causal proof.
  GRSP and CET measurements are not blindly subtracted from CapFrameX frametime.
  LARGELY_UNEXPLAINED means neither script profiler shows a large signal for that frame.
"""
        status_path.write_text(status, encoding="utf-8")

        # Portable analysis package: everything needed for human review or AI upload.
        with zipfile.ZipFile(package_path, "w", zipfile.ZIP_DEFLATED) as z:
            for p in (
                report_path, frames_path, timeline_path, hitches_path,
                analysis_json_path, ai_md_path, status_path
            ):
                z.write(p, p.name)

        return {
            "report": report_path,
            "frames": frames_path,
            "timeline": timeline_path,
            "hitches": hitches_path,
            "analysis_json": analysis_json_path,
            "ai_markdown": ai_md_path,
            "package": package_path,
            "status": status_path,
            "output_dir": out_dir,
        }
    finally:
        inputs.cleanup()


def parse_args(argv: list[str]) -> argparse.Namespace:
    ap = argparse.ArgumentParser(
        prog="GRSP_Correlator",
        description="Correlate GRSP + CET Runtime Profiler + CapFrameX captures into one report.")
    ap.add_argument("input", help="Combined capture ZIP or a folder containing GRSP, CET and CapFrameX outputs.")
    ap.add_argument("-o", "--output", help="Output directory. Default: <input>_Combined_Report or ./GRSP_Combined_Report")
    ap.add_argument("--open", action="store_true", dest="open_report", help="Open the HTML report after generation.")
    ap.add_argument("--version", action="version", version=f"%(prog)s {VERSION}")
    return ap.parse_args(argv)


def _wait_for_enter(message: str = "Press Enter to close...") -> None:
    try:
        input(f"\n{message}")
    except (EOFError, KeyboardInterrupt):
        pass


def _interactive_select_input() -> str | None:
    print(f"{APP_NAME} v{VERSION}")
    print("Select a combined GRSP + CET + CapFrameX capture ZIP or folder.")
    print("A file picker should open now.")
    print()

    selected = ""
    root = None
    try:
        import tkinter as tk
        from tkinter import filedialog

        root = tk.Tk()
        root.withdraw()
        try:
            root.attributes("-topmost", True)
        except Exception:
            pass

        selected = filedialog.askopenfilename(
            title="GRSP Correlator - Select combined capture ZIP",
            filetypes=[
                ("Combined capture ZIP", "*.zip"),
                ("All files", "*.*"),
            ],
        )

        # A normal file picker cannot select both files and folders at once.
        # If the ZIP dialog is cancelled, offer a folder picker before falling
        # back to a console path prompt.
        if not selected:
            selected = filedialog.askdirectory(
                title="GRSP Correlator - Or select a combined capture folder"
            )
    except Exception as e:
        print(f"File picker unavailable ({e}).")
    finally:
        if root is not None:
            try:
                root.destroy()
            except Exception:
                pass

    if selected:
        return selected

    try:
        typed = input("Paste the combined capture ZIP/folder path (blank = cancel): ").strip()
    except (EOFError, KeyboardInterrupt):
        return None

    if len(typed) >= 2 and typed[0] == typed[-1] and typed[0] in ('"', "'"):
        typed = typed[1:-1]
    return typed or None


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    interactive = len(args) == 0

    if interactive:
        selected = _interactive_select_input()
        if not selected:
            print("No input selected.")
            _wait_for_enter()
            return 0
        args = [selected, "--open"]

    ns = parse_args(args)
    inp = pathlib.Path(ns.input).expanduser().resolve()
    if ns.output:
        out = pathlib.Path(ns.output).expanduser().resolve()
    else:
        if inp.is_file():
            out = inp.with_suffix("").parent / (safe_name(inp.stem) + "_Combined_Report")
        else:
            out = inp / "GRSP_Combined_Report"

    print(f"Input:  {inp}")
    print(f"Output: {out}")
    print("Correlating captures...")

    try:
        result = run(inp, out)
    except Exception as e:
        print()
        print(f"ERROR: {e}", file=sys.stderr)
        if os.environ.get("GRSP_CORRELATOR_DEBUG"):
            raise
        if interactive:
            _wait_for_enter("Correlation failed. Press Enter to close...")
        return 1

    print()
    print(f"{APP_NAME} v{VERSION}")
    print("Correlation complete.")
    print(f"Saved output folder: {result['output_dir']}")
    print(f"Report:       {result['report']}")
    print(f"AI Markdown:  {result['ai_markdown']}")
    print(f"Analysis JSON:{result['analysis_json']}")
    print(f"Package ZIP:  {result['package']}")
    print(f"Frames:       {result['frames']}")
    print(f"Timeline:     {result['timeline']}")
    print(f"Hitches:      {result['hitches']}")
    print(f"Status:       {result['status']}")

    if interactive:
        _open_output_folder(result["output_dir"])

    if ns.open_report:
        try:
            webbrowser.open(result["report"].as_uri())
            print("Opening the combined HTML report...")
        except Exception as e:
            print(f"Could not open the report automatically: {e}")

    if interactive:
        _wait_for_enter()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
