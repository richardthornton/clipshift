#!/usr/bin/env python3
"""Statistics for the ClipShift performance harness, per issue #13 section 5.

Deliberately stdlib-only. This has to still run in a year on a machine that has had
its Python rebuilt, and a bootstrap needs nothing that ``random`` does not provide.

Reads a session directory produced by Run-Pairs.ps1 and reports:

  - per-run mean and 99th-percentile frame time
  - the PAIRED difference between arms, with a bootstrapped 95% CI
  - dropped-frame counts
  - a verdict against #13's budget, using the CI's UPPER BOUND rather than the point
    estimate, which is what #13 section 5.3 actually requires

Usage:
    python analyze.py <session-dir> [--floor <aa-session-dir>] [--boot 20000]

``--floor`` supplies a previously-measured A/A control session. Any difference smaller
than that noise floor is reported as NOT A RESULT, per #13 section 5.5.
"""

from __future__ import annotations

import argparse
import csv
import json
import random
import statistics
import sys
from pathlib import Path

# #13 section 1. Targets chosen against a 60 Hz perceptual argument, not observations.
BUDGET_MEAN_MS = 0.30
BUDGET_P99_MS = 1.00

# Column candidates, most-preferred first. PresentMon's v1 and v2 metric sets differ,
# and 2.x renamed things again; fail loudly rather than silently measuring the wrong one.
FRAMETIME_COLS = ("MsBetweenPresents", "FrameTime")
DISPLAY_COLS = ("MsBetweenDisplayChange",)
DROPPED_COLS = ("Dropped",)


def percentile(values: list[float], q: float) -> float:
    """Linear-interpolated percentile. q in [0, 100]."""
    if not values:
        raise ValueError("percentile of empty sample")
    s = sorted(values)
    if len(s) == 1:
        return s[0]
    pos = (len(s) - 1) * (q / 100.0)
    lo = int(pos)
    hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (pos - lo)


def pick_column(header: list[str], candidates: tuple[str, ...], csv_path: Path) -> str:
    for c in candidates:
        if c in header:
            return c
    raise SystemExit(
        f"{csv_path.name}: none of {candidates} present.\n"
        f"  Columns found: {', '.join(header)}\n"
        f"  The harness passes --v1_metrics for exactly this reason; if that changed, "
        f"the budget in #13 no longer maps onto these columns and the mapping has to be "
        f"re-derived before any number here means anything."
    )


class Run:
    def __init__(self, path: Path, arm: str, index: int):
        self.path, self.arm, self.index = path, arm, index
        with path.open(newline="", encoding="utf-8-sig") as fh:
            reader = csv.DictReader(fh)
            if reader.fieldnames is None:
                raise SystemExit(f"{path.name}: empty CSV")
            header = list(reader.fieldnames)
            ft_col = pick_column(header, FRAMETIME_COLS, path)
            dr_col = next((c for c in DROPPED_COLS if c in header), None)

            self.frame_times: list[float] = []
            self.dropped = 0
            for row in reader:
                raw = (row.get(ft_col) or "").strip()
                if not raw or raw.upper() == "NA":
                    continue
                try:
                    v = float(raw)
                except ValueError:
                    continue
                # A present interval of 0 or a negative one is a parse artefact, not a frame.
                if v > 0:
                    self.frame_times.append(v)
                if dr_col:
                    d = (row.get(dr_col) or "").strip()
                    if d in ("1", "true", "True"):
                        self.dropped += 1

        if len(self.frame_times) < 100:
            raise SystemExit(
                f"{path.name}: only {len(self.frame_times)} usable frames. "
                f"A run this short cannot support a percentile. Check that PresentMon "
                f"targeted the right process and that --delay did not exceed --timed."
            )

        self.mean = statistics.fmean(self.frame_times)
        self.p99 = percentile(self.frame_times, 99.0)
        self.fps = 1000.0 / self.mean


def bootstrap_ci(paired_diffs: list[float], iterations: int, seed: int = 20260813
                 ) -> tuple[float, float, float]:
    """Paired bootstrap over pairs. Returns (point estimate, lo95, hi95).

    Resamples PAIRS, not frames: the pair is the unit of independent replication here,
    because both arms of a pair share a thermal and clock state by construction.
    """
    point = statistics.fmean(paired_diffs)
    if len(paired_diffs) < 2:
        return point, float("nan"), float("nan")
    rng = random.Random(seed)
    n = len(paired_diffs)
    means = []
    for _ in range(iterations):
        means.append(statistics.fmean([paired_diffs[rng.randrange(n)] for _ in range(n)]))
    means.sort()
    return point, percentile(means, 2.5), percentile(means, 97.5)


def load_session(session: Path) -> tuple[dict, list[Run]]:
    manifest_path = session / "manifest.json"
    if not manifest_path.exists():
        raise SystemExit(f"No manifest.json in {session}. Was this produced by Run-Pairs.ps1?")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    entries = manifest.get("runs") or []
    if isinstance(entries, dict):
        entries = [entries]
    runs = [Run(session / e["csv"], e["arm"], e["index"]) for e in entries]
    if not runs:
        raise SystemExit(f"{session}: manifest lists no runs.")
    return manifest, runs


def pair_up(runs: list[Run], mode: str) -> list[tuple[Run, Run]]:
    """Runs come out of the harness strictly interleaved, two per pair."""
    ordered = sorted(runs, key=lambda r: r.index)
    if len(ordered) % 2:
        print(f"  ! odd run count ({len(ordered)}); dropping the trailing unpaired run",
              file=sys.stderr)
        ordered = ordered[:-1]
    pairs = [(ordered[i], ordered[i + 1]) for i in range(0, len(ordered), 2)]
    for a, b in pairs:
        expected = "off" if mode == "AA" else "on"
        if a.arm != "off" or b.arm != expected:
            raise SystemExit(
                f"Pair ({a.index}, {b.index}) has arms ({a.arm}, {b.arm}); expected "
                f"(off, {expected}) for mode {mode}. The interleaving is broken and the "
                f"pairing cannot be trusted."
            )
    return pairs


def fmt(v: float, width: int = 7) -> str:
    return "   n/a " if v != v else f"{v:{width}.3f}"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("session", type=Path)
    ap.add_argument("--floor", type=Path, default=None,
                    help="A/A control session directory establishing the noise floor")
    ap.add_argument("--boot", type=int, default=20000, help="bootstrap iterations")
    args = ap.parse_args()

    manifest, runs = load_session(args.session)
    mode = manifest.get("mode", "AB")
    pairs = pair_up(runs, mode)

    print(f"\n{'=' * 78}")
    print(f"  Session : {args.session.name}")
    print(f"  Mode    : {mode}   ({len(pairs)} pairs, {manifest.get('seconds')}s per run, "
          f"{manifest.get('warmupSeconds')}s discarded)")
    print(f"  Load    : {manifest.get('processName')}   OBS: "
          f"{'streaming' if manifest.get('obsRunning') else 'NOT RUNNING'}")
    if manifest.get("clipShiftArgs"):
        print(f"  Spike   : {manifest.get('clipShiftArgs')}")
    print(f"{'=' * 78}")

    if not manifest.get("obsRunning"):
        print("\n  ! OBS was not running. #13 section 2 makes an OBS stream part of the")
        print("    baseline scenario, so these numbers are not comparable to the budget.")

    print(f"\n  {'run':>4}  {'arm':<4}  {'frames':>7}  {'mean ms':>8}  {'p99 ms':>8}  "
          f"{'fps':>7}  {'dropped':>7}")
    print(f"  {'-' * 4}  {'-' * 4}  {'-' * 7}  {'-' * 8}  {'-' * 8}  {'-' * 7}  {'-' * 7}")
    for r in sorted(runs, key=lambda r: r.index):
        print(f"  {r.index:>4}  {r.arm:<4}  {len(r.frame_times):>7}  {r.mean:>8.3f}  "
              f"{r.p99:>8.3f}  {r.fps:>7.1f}  {r.dropped:>7}")

    mean_diffs = [b.mean - a.mean for a, b in pairs]
    p99_diffs = [b.p99 - a.p99 for a, b in pairs]
    drop_diffs = [b.dropped - a.dropped for a, b in pairs]

    m_pt, m_lo, m_hi = bootstrap_ci(mean_diffs, args.boot)
    p_pt, p_lo, p_hi = bootstrap_ci(p99_diffs, args.boot)

    label = "second-minus-first" if mode == "AA" else "ClipShift ON minus OFF"
    print(f"\n  Paired difference ({label}), 95% CI by paired bootstrap over "
          f"{len(pairs)} pairs:\n")
    print(f"    mean frame time   {fmt(m_pt)} ms   [{fmt(m_lo)}, {fmt(m_hi)} ] ms")
    print(f"    p99  frame time   {fmt(p_pt)} ms   [{fmt(p_lo)}, {fmt(p_hi)} ] ms")
    print(f"    dropped frames    {statistics.fmean(drop_diffs):>7.2f}      "
          f"(per-pair delta, total {sum(drop_diffs)})")

    if mode == "AA":
        floor_mean = max(abs(m_lo), abs(m_hi)) if m_lo == m_lo else abs(m_pt)
        floor_p99 = max(abs(p_lo), abs(p_hi)) if p_lo == p_lo else abs(p_pt)
        print(f"\n  {'-' * 74}")
        print("  NOISE FLOOR (this is the deliverable of an A/A control run)")
        print(f"  {'-' * 74}")
        print("  Both arms had ClipShift OFF, so the true difference is zero by")
        print("  construction. Whatever spread appears above is the instrument plus the")
        print("  load, not a signal.\n")
        print(f"    mean frame time floor : +/- {floor_mean:.3f} ms")
        print(f"    p99  frame time floor : +/- {floor_p99:.3f} ms")
        print("\n  Any later A/B difference smaller than these is NOT A RESULT (#13 s5.5).")
        if floor_mean > BUDGET_MEAN_MS:
            print(f"\n  ** The noise floor ({floor_mean:.3f} ms) is WIDER than the mean budget")
            print(f"     ({BUDGET_MEAN_MS} ms). This harness cannot resolve the budget it was")
            print("     built to test. Fix that before running any A/B: more pairs, longer")
            print("     runs, or a steadier load. Do not proceed and hope.")
        else:
            print(f"\n  The floor sits inside the {BUDGET_MEAN_MS} ms mean budget, so the "
                  f"harness can resolve it.")
        return 0

    # --- A/B verdict, on the CI upper bound, per #13 s5.3 ---------------------------
    print(f"\n  {'-' * 74}")
    print("  VERDICT (against the CI upper bound, not the point estimate -- #13 s5.3)")
    print(f"  {'-' * 74}")

    floor_mean = floor_p99 = None
    if args.floor:
        f_manifest, f_runs = load_session(args.floor)
        f_pairs = pair_up(f_runs, f_manifest.get("mode", "AA"))
        _, fm_lo, fm_hi = bootstrap_ci([b.mean - a.mean for a, b in f_pairs], args.boot)
        _, fp_lo, fp_hi = bootstrap_ci([b.p99 - a.p99 for a, b in f_pairs], args.boot)
        floor_mean, floor_p99 = max(abs(fm_lo), abs(fm_hi)), max(abs(fp_lo), abs(fp_hi))
        print(f"  Noise floor from {args.floor.name}: "
              f"mean +/-{floor_mean:.3f} ms, p99 +/-{floor_p99:.3f} ms\n")
    else:
        print("  ! No --floor given. #13 s5.5 requires an A/A control; without it there is")
        print("    no way to tell a small difference from instrument noise.\n")

    failures, unresolved = [], []
    for name, hi, budget, floor in (
        ("mean", m_hi, BUDGET_MEAN_MS, floor_mean),
        ("p99", p_hi, BUDGET_P99_MS, floor_p99),
    ):
        if hi != hi:
            print(f"    {name:<5} : INCONCLUSIVE (no CI from a single pair)")
            unresolved.append(name)
            continue
        if floor is not None and abs(hi) < floor:
            print(f"    {name:<5} : NOT A RESULT -- CI upper bound {hi:.3f} ms is inside "
                  f"the {floor:.3f} ms noise floor")
            unresolved.append(name)
        elif hi <= budget:
            print(f"    {name:<5} : PASS -- CI upper bound {hi:.3f} ms <= {budget} ms budget")
        else:
            print(f"    {name:<5} : FAIL -- CI upper bound {hi:.3f} ms > {budget} ms budget")
            failures.append(name)

    total_extra = sum(drop_diffs)
    if total_extra > 0:
        print(f"    drops : FAIL -- {total_extra} additional dropped frames; budget is zero")
        failures.append("dropped frames")
    else:
        print("    drops : PASS -- no additional dropped frames")

    if failures:
        print(f"\n  Missed: {', '.join(failures)}.")
        print("  #13 section 8 tiers this: an NVENC overrun is a config knob, but an in-game")
        print("  frame-time overrun is architectural and reopens the capture-API decision in")
        print("  #2. It does not get papered over.")
    elif unresolved:
        # A metric swallowed by the noise floor has NOT passed -- it was never measured.
        # Reporting that as a pass is how a harness launders its own blind spot into a
        # green light, so it is called out as loudly as a failure.
        print(f"\n  INCONCLUSIVE: {', '.join(unresolved)} could not be resolved above the")
        print("  noise floor. This is NOT a pass -- the harness cannot see a difference this")
        print("  small, so the budget is untested. Tighten the floor (more pairs, longer")
        print("  runs, a steadier load) and re-run before claiming anything.")
    else:
        print("\n  All measured budgets held.")

    print()
    return 1 if failures else (2 if unresolved else 0)


if __name__ == "__main__":
    sys.exit(main())
