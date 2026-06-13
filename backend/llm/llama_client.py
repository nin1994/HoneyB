"""
LlamaClient
Talks to a locally running llama.cpp server.

llama.cpp ships with a built-in HTTP server. Start it like:
  ./llama-server -m models/codellama-7b-instruct.Q4_K_M.gguf --port 8080

This client hits that server's /completion endpoint.
"""

import requests
from typing import Optional


LLAMA_SERVER_URL = "http://127.0.0.1:8080"
DEFAULT_TIMEOUT = 120    # seconds — local models can be slow


class LlamaClient:
    def __init__(self, server_url: str = LLAMA_SERVER_URL):
        self._url = server_url.rstrip("/")
        self._completion_url = f"{self._url}/completion"
        self._health_url = f"{self._url}/health"

    def is_ready(self) -> bool:
        try:
            r = requests.get(self._health_url, timeout=3)
            return r.status_code == 200
        except Exception:
            return False

    def query(self, prompt: str, max_tokens: int = 512) -> str:
        payload = {
            "prompt": prompt,
            "n_predict": max_tokens,
            "temperature": 0.2,        # low temp = more deterministic, better for debugging
            "top_p": 0.9,
            "stop": ["## Developer Question", "## Snapshot"],   # prevent runaway generation
            "stream": False,
        }

        try:
            response = requests.post(
                self._completion_url,
                json=payload,
                timeout=DEFAULT_TIMEOUT,
            )
            response.raise_for_status()
            data = response.json()
            return data.get("content", "").strip()

        except requests.exceptions.ConnectionError:
            return (
                "llama.cpp server is not running. "
                "Start it with: ./llama-server -m your_model.gguf --port 8080"
            )
        except requests.exceptions.Timeout:
            return "Request timed out. The model may be too slow — try a smaller/quantized model."
        except Exception as e:
            return f"LLM error: {str(e)}"
