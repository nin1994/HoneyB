# HoneyB

Snapshot-based AI debugging assistant for Visual Studio.
Captures variable state every time execution pauses, stores a history of
snapshots, and lets you ask a local LLM natural language questions about
what your program is doing.

---

## Architecture

```
Visual Studio (VSIX extension)
  │  DTE API — reads locals, stack frames
  ↓
Python Backend (FastAPI on port 5678)
  ├── Snapshot Store  — stores & diffs variable state
  ├── Context Assembler — builds focused prompts
  └── LLM Client — calls llama.cpp server
        ↓
llama.cpp (HTTP server on port 8080)
  └── CodeLlama 7B (or any GGUF model)
```

---

## Prerequisites

- Visual Studio 2022 (Community or higher)
- Visual Studio SDK / Extensibility workload installed
- Python 3.11+
- Git
- llama.cpp built from source (or pre-built binaries from releases)
- A GGUF model — recommended: `codellama-7b-instruct.Q4_K_M.gguf`

---

## Setup

### 1. Clone the repo

```bash
git clone https://github.com/yourname/HoneyB.git
cd HoneyB
```

### 2. Set up the Python backend

```bash
cd backend
python -m venv venv
venv\Scripts\activate        # Windows
pip install -r requirements.txt
```

### 3. Download a model

Get a GGUF model from Hugging Face. Recommended:
https://huggingface.co/TheBloke/CodeLlama-7B-Instruct-GGUF

Download `codellama-7b-instruct.Q4_K_M.gguf` and place it in:
```
HoneyB/models/codellama-7b-instruct.Q4_K_M.gguf
```

### 4. Build llama.cpp

```bash
git clone https://github.com/ggerganov/llama.cpp
cd llama.cpp
cmake -B build
cmake --build build --config Release
```

### 5. Open the extension in Visual Studio

Open `extension/HoneyB.csproj` in Visual Studio.
Make sure the "Visual Studio extension development" workload is installed.
Press F5 to launch an Experimental VS instance with the extension loaded.

---

## Running

**Terminal 1 — Start llama.cpp server:**
```bash
cd llama.cpp
.\build\bin\Release\llama-server.exe \
  -m ..\HoneyB\models\codellama-7b-instruct.Q4_K_M.gguf \
  --port 8080 \
  --ctx-size 4096
```

**Terminal 2 — Start Python backend:**
```bash
cd HoneyB\backend
venv\Scripts\activate
python main.py
```

**Visual Studio:**
- Open your project
- Go to View > Other Windows > HoneyB
- Set a breakpoint and press F5
- When execution pauses, a snapshot is captured automatically
- Type a question in the chat panel and press Ask

---

## Example Questions

- "Why is this value null?"
- "What changed since the last breakpoint?"
- "Is there an off-by-one error here?"
- "Explain what this function is doing with these inputs"
- "Which variable looks suspicious?"

---

## Project Structure

```
HoneyB/
├── backend/
│   ├── main.py                    # FastAPI server entry point
│   ├── requirements.txt
│   ├── snapshot_store/
│   │   └── store.py               # Snapshot save / load / diff
│   ├── context/
│   │   └── assembler.py           # Prompt builder
│   └── llm/
│       └── llama_client.py        # llama.cpp HTTP client
├── extension/
│   ├── HoneyB.csproj
│   ├── HoneyBPackage.cs       # VS package entry point
│   ├── DebuggerEventListener.cs   # Hooks breakpoint events, sends snapshots
│   ├── DebuggerChatWindow.cs      # Tool window UI (WPF)
│   ├── OpenChatWindowCommand.cs   # View menu command
│   └── source.extension.vsixmanifest
├── models/                        # Put your .gguf model here
└── README.md
```

---

## API Reference

| Endpoint | Method | Description |
|---|---|---|
| `/snapshot` | POST | Save a new snapshot |
| `/snapshots` | GET | List all snapshots |
| `/snapshot/{id}` | GET | Get a specific snapshot |
| `/snapshot/diff?from=1&to=2` | GET | Diff two snapshots |
| `/query` | POST | Ask AI a question |
| `/health` | GET | Check backend + LLM status |

---

## Roadmap

- [ ] Snapshot persistence to disk (currently in-memory)
- [ ] Multi-level object graph inspection
- [ ] Snapshot timeline scrubber UI
- [ ] Diff viewer showing changed variables visually
- [ ] Support for VS Code via DAP

---

## Testing & Debugging the Extension

### Python backend tests (no VS needed)
```bash
cd backend
pip install pytest
pytest tests/ -v
```

### C# extension tests (no VS instance needed)
Open `HoneyB.sln` in Visual Studio.
The solution has two projects:
- `HoneyB.Extension` — the actual VSIX
- `HoneyB.Tests` — xUnit tests that mock the backend with WireMock

Run tests via Test Explorer (Test > Test Explorer > Run All).

Tests verify:
- Snapshot payloads are correctly shaped
- Variable serialization works
- Backend HTTP calls are formed correctly
- All without needing a running VS debug session

### Debugging the extension itself
The standard VS extension debugging approach:
1. Open `HoneyB.sln`
2. Set `HoneyB.Extension` as startup project
3. Press F5 — this launches an **Experimental VS instance** with HoneyB loaded
4. In the Experimental instance, open any C# project and debug it
5. Set breakpoints in HoneyB's own source in the main VS window to debug it

This means you have two VS windows: one running HoneyB, one being debugged by HoneyB.
