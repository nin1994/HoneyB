"""
AI Debugger Backend
FastAPI server that receives snapshots from the VS extension,
stores them, and answers AI questions about them.
"""

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from typing import Optional
import uvicorn

from snapshot_store.store import SnapshotStore
from context.assembler import ContextAssembler
from llm.llama_client import LlamaClient

app = FastAPI(title="AI Debugger Backend")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# Singletons
store = SnapshotStore()
assembler = ContextAssembler(store)
llm = LlamaClient()


# ── Request / Response models ────────────────────────────────────────────────

class Variable(BaseModel):
    name: str
    type: str
    value: str

class StackFrame(BaseModel):
    function: str
    file: str
    line: int
    locals: list[Variable]

class SnapshotRequest(BaseModel):
    label: str                        # e.g. "Breakpoint hit at Program.cs:42"
    frames: list[StackFrame]
    source_context: Optional[str] = None   # few lines of source around breakpoint

class QueryRequest(BaseModel):
    question: str
    snapshot_id: Optional[int] = None     # None = use latest


# ── Endpoints ────────────────────────────────────────────────────────────────

@app.post("/snapshot")
def save_snapshot(req: SnapshotRequest):
    """Called by the VS extension every time execution pauses."""
    snap_id = store.save(req.model_dump())
    return {"snapshot_id": snap_id, "total_snapshots": store.count()}


@app.get("/snapshots")
def list_snapshots():
    """Returns metadata for all stored snapshots."""
    return store.list_all()


@app.get("/snapshot/{snap_id}")
def get_snapshot(snap_id: int):
    snap = store.load(snap_id)
    if not snap:
        raise HTTPException(status_code=404, detail="Snapshot not found")
    return snap


@app.get("/snapshot/diff")
def diff_snapshots(from_id: int, to_id: int):
    """Returns what changed between two snapshots."""
    result = store.diff(from_id, to_id)
    if result is None:
        raise HTTPException(status_code=404, detail="One or both snapshots not found")
    return result


@app.post("/query")
def query(req: QueryRequest):
    """
    Accepts a natural language question, assembles context from
    the snapshot store, sends to llama.cpp, returns the answer.
    """
    snap_id = req.snapshot_id if req.snapshot_id is not None else store.latest_id()
    if snap_id is None:
        raise HTTPException(status_code=400, detail="No snapshots available yet")

    prompt = assembler.build_prompt(snap_id, req.question)
    answer = llm.query(prompt)
    return {"answer": answer, "snapshot_id": snap_id}


@app.get("/health")
def health():
    return {"status": "ok", "llm_ready": llm.is_ready()}


if __name__ == "__main__":
    uvicorn.run("main:app", host="127.0.0.1", port=5678, reload=True)
