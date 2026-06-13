"""
Snapshot Store
Stores debug snapshots in memory, persists the full ordered timeline
to timeline.json in the project root after every new snapshot.
"""

import json
import time
import threading
from pathlib import Path
from typing import Optional


class SnapshotStore:
    def __init__(self,
                 persist_dir: Optional[str] = None,
                 timeline_path: Optional[str] = None):
        self._snapshots: dict[int, dict] = {}
        self._next_id = 0
        self._persist_dir = Path(persist_dir) if persist_dir else None
        self._timeline_path = Path(timeline_path) if timeline_path else None
        self._lock = threading.Lock()

        if self._persist_dir:
            self._persist_dir.mkdir(parents=True, exist_ok=True)
            self._load_from_disk()

        # Bootstrap from timeline.json if it exists (survives backend restarts)
        if self._timeline_path and self._timeline_path.exists():
            self._load_from_timeline()

    # ── Write ─────────────────────────────────────────────────────────────────

    def save(self, snapshot_data: dict) -> int:
        with self._lock:
            snap_id = self._next_id
            self._next_id += 1

            snapshot_data["id"] = snap_id
            snapshot_data["timestamp"] = time.time()
            self._snapshots[snap_id] = snapshot_data

            if self._persist_dir:
                path = self._persist_dir / f"snapshot_{snap_id}.json"
                path.write_text(json.dumps(snapshot_data, indent=2))

            # Rewrite timeline.json after every save
            self._write_timeline()

            return snap_id

    # ── Read ──────────────────────────────────────────────────────────────────

    def load(self, snap_id: int) -> Optional[dict]:
        return self._snapshots.get(snap_id)

    def latest_id(self) -> Optional[int]:
        if not self._snapshots:
            return None
        return max(self._snapshots.keys())

    def count(self) -> int:
        return len(self._snapshots)

    def list_all(self) -> list[dict]:
        """Returns lightweight metadata for all snapshots."""
        result = []
        for snap_id, snap in sorted(self._snapshots.items()):
            result.append({
                "id": snap_id,
                "label": snap.get("label", ""),
                "timestamp": snap.get("timestamp", 0),
                "frame_count": len(snap.get("frames", [])),
            })
        return result

    def timeline(self) -> list[dict]:
        """Returns all events as timeline entries with computed changed_vars."""
        return [
            self._to_timeline_entry(snap_id, snap)
            for snap_id, snap in sorted(self._snapshots.items())
        ]

    def _to_timeline_entry(self, snap_id: int, snap: dict) -> dict:
        """Shapes a stored snapshot into a richer timeline entry for the UI."""
        frames = snap.get("frames", [])
        top_frame = frames[0] if frames else {}

        # Diff vs the immediately preceding snapshot to find changed variables
        changed_vars = []
        if snap_id > 0 and (snap_id - 1) in self._snapshots:
            diff = self.diff(snap_id - 1, snap_id)
            if diff:
                changed_vars = [c["variable"] for c in diff.get("changes", [])]

        return {
            "id": snap_id,
            "timestamp": snap.get("timestamp", 0),
            "label": snap.get("label", ""),
            "event_kind": snap.get("event_kind", "breakpoint"),
            "thread_name": snap.get("thread_name", "Main Thread"),
            "top_function": top_frame.get("function", "?"),
            "file": top_frame.get("file", "?"),
            "line": top_frame.get("line", 0),
            "changed_vars": changed_vars,
            "frames": frames,
        }

    def diff(self, from_id: int, to_id: int) -> Optional[dict]:
        snap_a = self._snapshots.get(from_id)
        snap_b = self._snapshots.get(to_id)
        if snap_a is None or snap_b is None:
            return None

        changes = []
        vars_a = self._flatten_vars(snap_a)
        vars_b = self._flatten_vars(snap_b)
        all_names = set(vars_a.keys()) | set(vars_b.keys())

        for name in sorted(all_names):
            val_a = vars_a.get(name)
            val_b = vars_b.get(name)

            if val_a is None:
                changes.append({"variable": name, "change": "added", "value": val_b})
            elif val_b is None:
                changes.append({"variable": name, "change": "removed", "value": val_a})
            elif val_a != val_b:
                changes.append({
                    "variable": name,
                    "change": "modified",
                    "before": val_a,
                    "after": val_b,
                })

        return {
            "from_snapshot": from_id,
            "to_snapshot": to_id,
            "changes": changes,
            "unchanged_count": len(all_names) - len(changes),
        }

    # ── Persistence ────────────────────────────────────────────────────────────

    def _flatten_vars(self, snapshot: dict) -> dict[str, str]:
        """Flatten all locals (including one level of children) into name→value."""
        result = {}
        for frame in snapshot.get("frames", []):
            fn = frame.get("function", "?")
            for var in frame.get("locals", []):
                key = f"{fn}.{var['name']}"
                result[key] = f"{var['type']}={var['value']}"
                # Include first-level children for diff accuracy
                for child in var.get("children", []):
                    ckey = f"{fn}.{var['name']}.{child['name']}"
                    result[ckey] = f"{child['type']}={child['value']}"
        return result

    def _write_timeline(self):
        """Rewrite timeline.json with the current ordered list of events."""
        if not self._timeline_path:
            return
        try:
            entries = self.timeline()
            self._timeline_path.write_text(json.dumps(entries, indent=2))
        except Exception:
            pass  # non-fatal — UI still works from in-memory store

    def _load_from_timeline(self):
        """Restore snapshots from timeline.json on backend startup."""
        try:
            data = json.loads(self._timeline_path.read_text())
            for entry in data:
                snap_id = entry["id"]
                # Reconstruct a minimal snapshot from the timeline entry
                self._snapshots[snap_id] = {
                    "id": snap_id,
                    "timestamp": entry.get("timestamp", 0),
                    "label": entry.get("label", ""),
                    "event_kind": entry.get("event_kind", "breakpoint"),
                    "thread_name": entry.get("thread_name", "Main Thread"),
                    "frames": entry.get("frames", []),
                }
                self._next_id = max(self._next_id, snap_id + 1)
        except Exception:
            pass  # malformed or missing — start fresh

    def _load_from_disk(self):
        if not self._persist_dir:
            return
        for path in sorted(self._persist_dir.glob("snapshot_*.json")):
            try:
                data = json.loads(path.read_text())
                snap_id = data["id"]
                self._snapshots[snap_id] = data
                self._next_id = max(self._next_id, snap_id + 1)
            except Exception:
                pass
