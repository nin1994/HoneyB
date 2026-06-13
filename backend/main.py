"""
AI Debugger Backend
FastAPI server that receives snapshots from the VS extension,
stores them, and answers AI questions about them.

New in this version:
  - POST /watch           — receives evaluated watch entries from the extension
  - GET  /watch/stream    — WebSocket that streams watch updates to browsers
  - GET  /dashboard       — serves the self-contained dashboard.html
"""

import asyncio
import json
import time
from pathlib import Path
from typing import Optional, List

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse
from pydantic import BaseModel
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
root_dir = Path(__file__).parent.parent
store = SnapshotStore(timeline_path=str(root_dir / "timeline.json"))
assembler = ContextAssembler(store)
llm = LlamaClient()

# ── Watch state (in-memory, not persisted) ────────────────────────────────────

# path → latest WatchEntry dict
_watch_state: dict[str, dict] = {}
# Active WebSocket connections for /watch/stream
_watch_connections: list[WebSocket] = []


# ── Request / Response models ─────────────────────────────────────────────────

class Variable(BaseModel):
    name: str
    type: str
    value: str
    children: Optional[list['Variable']] = None

class StackFrame(BaseModel):
    function: str
    file: str
    line: int
    locals: list[Variable]

class SnapshotRequest(BaseModel):
    label: str                        # e.g. "Breakpoint hit at Program.cs:42"
    event_kind: str = "breakpoint"
    thread_name: str = "Main Thread"
    frames: list[StackFrame]
    source_context: Optional[str] = None   # few lines of source around breakpoint

class QueryRequest(BaseModel):
    question: str
    snapshot_id: Optional[int] = None     # None = use latest

class WatchEntry(BaseModel):
    path: str
    label: str
    value: Optional[str] = None           # None means not in scope
    seenIn: str = ""
    timestamp: Optional[int] = None       # Unix ms


# ── Endpoints ─────────────────────────────────────────────────────────────────

@app.post("/snapshot")
def save_snapshot(req: SnapshotRequest):
    """Called by the VS extension every time execution pauses."""
    snap_id = store.save(req.model_dump())

    # Get the rich timeline entry so the UI can update immediately
    entries = store.timeline()
    timeline_entry = next((e for e in entries if e["id"] == snap_id), None)

    return {
        "snapshot_id": snap_id,
        "total_snapshots": store.count(),
        "timeline_entry": timeline_entry
    }


@app.post("/watch")
async def post_watch(entries: List[WatchEntry]):
    """
    Called by the VS extension after evaluating pinned paths at each pause.
    Updates in-memory state and broadcasts to all connected /watch/stream clients.
    """
    global _watch_state

    for entry in entries:
        _watch_state[entry.path] = {
            "path":      entry.path,
            "label":     entry.label,
            "value":     entry.value,
            "seenIn":    entry.seenIn,
            "timestamp": entry.timestamp or int(time.time() * 1000),
        }

    # Broadcast updated state to all WebSocket connections
    message = json.dumps(list(_watch_state.values()))
    dead: list[WebSocket] = []
    for ws in list(_watch_connections):
        try:
            await ws.send_text(message)
        except Exception:
            dead.append(ws)
    for ws in dead:
        _watch_connections.remove(ws)

    return {"updated": len(entries), "total_watched": len(_watch_state)}


@app.websocket("/watch/stream")
async def watch_stream(websocket: WebSocket):
    """
    WebSocket endpoint for the browser dashboard.
    On connect: immediately sends current watch state.
    Stays connected and receives pushed updates whenever /watch is called.
    """
    await websocket.accept()
    _watch_connections.append(websocket)

    try:
        # Send current state immediately so fresh connections get data right away
        await websocket.send_text(json.dumps(list(_watch_state.values())))

        # Keep connection alive until client disconnects
        while True:
            # We only receive to detect disconnect; we don't process client messages
            await asyncio.wait_for(websocket.receive_text(), timeout=30)
    except (WebSocketDisconnect, asyncio.TimeoutError):
        pass
    except Exception:
        pass
    finally:
        if websocket in _watch_connections:
            _watch_connections.remove(websocket)


@app.get("/dashboard")
def dashboard():
    """Serves the self-contained live watch dashboard."""
    dashboard_path = Path(__file__).parent / "dashboard.html"
    if not dashboard_path.exists():
        raise HTTPException(status_code=404, detail="dashboard.html not found")
    return FileResponse(str(dashboard_path), media_type="text/html")


@app.get("/timeline")
def get_timeline():
    """Returns the full chronological execution flow."""
    return store.timeline()


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
