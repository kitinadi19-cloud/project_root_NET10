#!/usr/bin/env python3
"""Kiểm chứng độc lập summary.csv bằng dữ liệu raw_<run_id>.csv.

Usage:
  python verify.py [raw_dir] [run_id ...]

Nếu không truyền run_id, chọn một run đầu tiên có ở mỗi mức Client 1, 10, 50.
Chỉ sử dụng thư viện chuẩn Python.
"""
import csv
import math
import sys
from pathlib import Path

TOLERANCE = {
    "ok": 0,
    "errors": 0,
    "throughput_rps": 0.002,
    "error_rate": 1e-5,
    "mean_ms": 0.002,
    "p95_ms": 0.002,
}
REQUIRED_RAW = {"latency_us", "status"}
REQUIRED_SUMMARY = {
    "run_id", "clients", "duration_s", "ok", "errors",
    "throughput_rps", "error_rate", "mean_ms", "p95_ms",
}


def read_csv(path):
    if not path.is_file():
        raise FileNotFoundError(f"Không tìm thấy file: {path}")
    with path.open("r", newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        if not reader.fieldnames:
            raise ValueError(f"CSV không có header: {path}")
        return reader.fieldnames, list(reader)


def nearest_rank(values, percentile):
    """Nearest-rank: x[ceil(p*n)-1], với danh sách đã sắp tăng."""
    if not values:
        return math.nan
    return values[math.ceil(percentile * len(values)) - 1]


def recompute(raw_path, duration_s):
    if duration_s <= 0:
        raise ValueError(f"duration_s phải > 0, nhận được {duration_s}")
    fields, rows = read_csv(raw_path)
    missing = REQUIRED_RAW - set(fields)
    if missing:
        raise ValueError(f"{raw_path}: thiếu cột {sorted(missing)}")

    latencies = []
    errors = 0
    for line_no, row in enumerate(rows, start=2):
        status = (row.get("status") or "").strip()
        if status == "Ok":
            try:
                latency = float(row["latency_us"]) / 1000.0
            except (TypeError, ValueError):
                raise ValueError(f"{raw_path}:{line_no}: latency_us không hợp lệ")
            if latency < 0:
                raise ValueError(f"{raw_path}:{line_no}: latency_us âm")
            latencies.append(latency)
        else:
            errors += 1

    latencies.sort()
    ok = len(latencies)
    total = ok + errors
    return {
        "ok": ok,
        "errors": errors,
        "throughput_rps": ok / duration_s,
        "error_rate": errors / total if total else 0.0,
        "mean_ms": sum(latencies) / ok if ok else math.nan,
        "p95_ms": nearest_rank(latencies, 0.95),
    }


def close_enough(actual, expected, tolerance):
    if math.isnan(actual) or math.isnan(expected):
        return math.isnan(actual) and math.isnan(expected)
    return abs(actual - expected) <= tolerance


def main():
    raw_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("experiments/raw-data")
    summary_path = raw_dir / "summary.csv"
    fields, rows = read_csv(summary_path)
    missing = REQUIRED_SUMMARY - set(fields)
    if missing:
        raise ValueError(f"{summary_path}: thiếu cột {sorted(missing)}")

    summary = {row["run_id"]: row for row in rows}
    if len(summary) != len(rows):
        raise ValueError("summary.csv có run_id trùng lặp")

    requested = sys.argv[2:]
    if requested:
        run_ids = requested
    else:
        run_ids = []
        for level in (1, 10, 50):
            candidate = next((r["run_id"] for r in rows
                              if int(r["clients"]) == level), None)
            if candidate:
                run_ids.append(candidate)

    if not run_ids:
        raise ValueError("Không tìm thấy run phù hợp; truyền run_id cụ thể.")

    all_passed = True
    for run_id in run_ids:
        if run_id not in summary:
            raise KeyError(f"run_id '{run_id}' không có trong summary.csv")
        row = summary[run_id]
        raw_path = raw_dir / f"raw_{run_id}.csv"
        computed = recompute(raw_path, float(row["duration_s"]))
        print(f"\n== {run_id} | clients={row['clients']} ==")
        for key, tolerance in TOLERANCE.items():
            try:
                reported = float(row[key])
            except (TypeError, ValueError):
                raise ValueError(f"{summary_path}: giá trị {key} của {run_id} không hợp lệ")
            passed = close_enough(computed[key], reported, tolerance)
            all_passed &= passed
            print(f"{key:16s} summary={reported:14.6f} "
                  f"recomputed={computed[key]:14.6f} "
                  f"{'PASS' if passed else 'FAIL'}")

    print("\nKẾT LUẬN:", "PASS – dữ liệu khớp trong sai số cho phép."
          if all_passed else "FAIL – cần kiểm tra lại dữ liệu hoặc cách tính.")
    return 0 if all_passed else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)
