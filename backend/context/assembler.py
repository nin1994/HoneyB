"""
Context Assembler
Builds a focused, token-efficient prompt from snapshot data
to send to the local LLM.
"""

from __future__ import annotations
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from snapshot_store.store import SnapshotStore


MAX_PROMPT_CHARS = 6000   # conservative limit for smaller local models


class ContextAssembler:
    def __init__(self, store: "SnapshotStore"):
        self._store = store

    def build_prompt(self, snap_id: int, question: str) -> str:
        snap = self._store.load(snap_id)
        if snap is None:
            return self._minimal_prompt(question)

        parts = []

        # System instruction
        parts.append(
            "You are an expert debugger assistant. "
            "You are given the current state of a paused program and must answer "
            "the developer's question clearly and concisely. "
            "Focus on what is relevant to the question. "
            "If you spot a bug, explain it directly."
        )

        # Snapshot label and timestamp
        parts.append(f"\n## Snapshot: {snap.get('label', 'Unknown')}")

        # Call stack + locals
        frames = snap.get("frames", [])
        if frames:
            parts.append("\n## Call Stack and Local Variables")
            for i, frame in enumerate(frames):
                parts.append(
                    f"\nFrame {i}: {frame['function']} "
                    f"({frame.get('file', '?')}:{frame.get('line', '?')})"
                )
                locals_ = frame.get("locals", [])
                if locals_:
                    for var in locals_:
                        parts.append(f"  {var['type']} {var['name']} = {var['value']}")
                else:
                    parts.append("  (no locals)")

        # Source context if available
        source = snap.get("source_context")
        if source:
            parts.append(f"\n## Source Context\n{source}")

        # Recent changes (diff with previous snapshot)
        prev_id = snap_id - 1
        if prev_id >= 0:
            diff = self._store.diff(prev_id, snap_id)
            if diff and diff["changes"]:
                parts.append("\n## Changes Since Previous Pause")
                for change in diff["changes"][:10]:   # cap at 10
                    if change["change"] == "modified":
                        parts.append(
                            f"  {change['variable']}: "
                            f"{change['before']} → {change['after']}"
                        )
                    elif change["change"] == "added":
                        parts.append(f"  {change['variable']}: (new) = {change['value']}")
                    elif change["change"] == "removed":
                        parts.append(f"  {change['variable']}: (went out of scope)")

        # Developer question
        parts.append(f"\n## Developer Question\n{question}")
        parts.append("\n## Your Answer")

        prompt = "\n".join(parts)

        # Trim if over limit
        if len(prompt) > MAX_PROMPT_CHARS:
            prompt = prompt[:MAX_PROMPT_CHARS] + "\n[context truncated]\n## Your Answer"

        return prompt

    def _minimal_prompt(self, question: str) -> str:
        return (
            "You are a debugger assistant. "
            f"The developer asks: {question}\n"
            "No snapshot data is available yet.\n## Your Answer"
        )
