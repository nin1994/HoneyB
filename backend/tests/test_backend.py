"""
HoneyB Backend Tests
Run with: pytest tests/ -v
"""

import pytest
import sys
import os

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from snapshot_store.store import SnapshotStore
from context.assembler import ContextAssembler


# ── Fixtures ──────────────────────────────────────────────────────────────────

def make_snapshot(label="Breakpoint hit", function="Program.Main",
                  file="Program.cs", line=42, locals_=None):
    if locals_ is None:
        locals_ = [{"name": "counter", "type": "int", "value": "5"}]
    return {
        "label": label,
        "frames": [{"function": function, "file": file, "line": line, "locals": locals_}],
        "source_context": "for (int i = 0; i < 10; i++) { counter++; }",
    }


# ── Snapshot Store Tests ───────────────────────────────────────────────────────

class TestSnapshotStore:
    def test_save_returns_incrementing_ids(self):
        store = SnapshotStore()
        id0 = store.save(make_snapshot("first"))
        id1 = store.save(make_snapshot("second"))
        assert id0 == 0
        assert id1 == 1

    def test_load_returns_saved_snapshot(self):
        store = SnapshotStore()
        snap_id = store.save(make_snapshot("test snap"))
        loaded = store.load(snap_id)
        assert loaded["label"] == "test snap"

    def test_load_nonexistent_returns_none(self):
        store = SnapshotStore()
        assert store.load(999) is None

    def test_latest_id_returns_most_recent(self):
        store = SnapshotStore()
        store.save(make_snapshot())
        store.save(make_snapshot())
        last = store.save(make_snapshot())
        assert store.latest_id() == last

    def test_latest_id_empty_store_returns_none(self):
        store = SnapshotStore()
        assert store.latest_id() is None

    def test_count_is_accurate(self):
        store = SnapshotStore()
        assert store.count() == 0
        store.save(make_snapshot())
        store.save(make_snapshot())
        assert store.count() == 2

    def test_list_all_returns_metadata(self):
        store = SnapshotStore()
        store.save(make_snapshot("first"))
        store.save(make_snapshot("second"))
        listing = store.list_all()
        assert len(listing) == 2
        assert listing[0]["label"] == "first"
        assert "timestamp" in listing[0]
        assert "frame_count" in listing[0]

    def test_diff_detects_modified_variable(self):
        store = SnapshotStore()
        id0 = store.save(make_snapshot(locals_=[
            {"name": "counter", "type": "int", "value": "5"}
        ]))
        id1 = store.save(make_snapshot(locals_=[
            {"name": "counter", "type": "int", "value": "6"}
        ]))
        diff = store.diff(id0, id1)
        assert diff is not None
        assert len(diff["changes"]) == 1
        assert diff["changes"][0]["change"] == "modified"
        assert diff["changes"][0]["before"].endswith("5")
        assert diff["changes"][0]["after"].endswith("6")

    def test_diff_detects_added_variable(self):
        store = SnapshotStore()
        id0 = store.save(make_snapshot(locals_=[
            {"name": "x", "type": "int", "value": "1"}
        ]))
        id1 = store.save(make_snapshot(locals_=[
            {"name": "x", "type": "int", "value": "1"},
            {"name": "y", "type": "int", "value": "2"},
        ]))
        diff = store.diff(id0, id1)
        added = [c for c in diff["changes"] if c["change"] == "added"]
        assert len(added) == 1
        assert "y" in added[0]["variable"]

    def test_diff_no_changes_when_identical(self):
        store = SnapshotStore()
        snap = make_snapshot()
        id0 = store.save(snap)
        id1 = store.save(make_snapshot())   # identical structure
        diff = store.diff(id0, id1)
        assert diff["changes"] == []

    def test_diff_returns_none_for_missing_snapshot(self):
        store = SnapshotStore()
        store.save(make_snapshot())
        assert store.diff(0, 999) is None


# ── Context Assembler Tests ────────────────────────────────────────────────────

class TestContextAssembler:
    def test_prompt_contains_question(self):
        store = SnapshotStore()
        store.save(make_snapshot())
        assembler = ContextAssembler(store)
        prompt = assembler.build_prompt(0, "Why is counter 5?")
        assert "Why is counter 5?" in prompt

    def test_prompt_contains_variable_names(self):
        store = SnapshotStore()
        store.save(make_snapshot(locals_=[
            {"name": "myVar", "type": "int", "value": "42"}
        ]))
        assembler = ContextAssembler(store)
        prompt = assembler.build_prompt(0, "anything")
        assert "myVar" in prompt

    def test_prompt_contains_function_name(self):
        store = SnapshotStore()
        store.save(make_snapshot(function="MyClass.MyMethod"))
        assembler = ContextAssembler(store)
        prompt = assembler.build_prompt(0, "anything")
        assert "MyClass.MyMethod" in prompt

    def test_prompt_contains_source_context(self):
        store = SnapshotStore()
        store.save(make_snapshot())
        assembler = ContextAssembler(store)
        prompt = assembler.build_prompt(0, "anything")
        assert "counter++" in prompt

    def test_prompt_includes_diff_when_previous_exists(self):
        store = SnapshotStore()
        store.save(make_snapshot(locals_=[
            {"name": "x", "type": "int", "value": "1"}
        ]))
        store.save(make_snapshot(locals_=[
            {"name": "x", "type": "int", "value": "99"}
        ]))
        assembler = ContextAssembler(store)
        prompt = assembler.build_prompt(1, "anything")
        assert "Changes Since Previous" in prompt

    def test_prompt_is_within_char_limit(self):
        store = SnapshotStore()
        # Big locals list
        big_locals = [
            {"name": f"var{i}", "type": "string", "value": "x" * 200}
            for i in range(100)
        ]
        store.save(make_snapshot(locals_=big_locals))
        assembler = ContextAssembler(store)
        prompt = assembler.build_prompt(0, "question")
        assert len(prompt) <= assembler.__class__.__dict__.get(
            "MAX_PROMPT_CHARS", 6000) + 100   # small tolerance

    def test_minimal_prompt_when_no_snapshot(self):
        store = SnapshotStore()
        assembler = ContextAssembler(store)
        prompt = assembler.build_prompt(999, "my question")
        assert "my question" in prompt
