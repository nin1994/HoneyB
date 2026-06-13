"""
Snapshot Store
Stores debug snapshots in memory (with optional disk persistence).
Each snapshot is a full variable state at a point in time.
"""

import json
import time
from pathlib import Path
from typing import Optional


class SnapshotStore:
    def __init__(self, persist_dir: Optional[str] = None):
        self._snapshots: dict[int, dict] = {}
        self._next_id = 0
        self._persist_dir = Path(persist_dir) if persist_dir else None

        if self._persist_dir:
            self._persist_dir.mkdir(parents=True, exist_ok=True)
            self._load_from_disk()

    def save(self, snapshot_data: dict) -> int:
        snap_id = self._next_id
        self._next_id += 1

        snapshot_data["id"] = snap_id
        snapshot_data["timestamp"] = time.time()
        self._snapshots[snap_id] = snapshot_data

        if self._persist_dir:
            path = self._persist_dir / f"snapshot_{snap_id}.json"
            path.write_text(json.dumps(snapshot_data, indent=2))

        return snap_id

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

    def diff(self, from_id: int, to_id: int) -> Optional[dict]:
        snap_a = self._snapshots.get(from_id)
        snap_b = self._snapshots.get(to_id)
        if snap_a is None or snap_b is None:
            return None

        changes = []

        # Build flat variable maps for both snapshots (top frame only for now)
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

    def _flatten_vars(self, snapshot: dict) -> dict[str, str]:
        """Flatten all locals from all frames into a name→value map."""
        result = {}
        for frame in snapshot.get("frames", []):
            for var in frame.get("locals", []):
                key = f"{frame['function']}.{var['name']}"
                result[key] = f"{var['type']}={var['value']}"
        return result

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
