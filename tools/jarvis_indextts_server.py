"""Local-only IndexTTS2.5 bridge for Jarvis.

The reference voice is fixed when the process starts. Requests cannot choose an
arbitrary local file, and the HTTP server only listens on the loopback address.
"""

from __future__ import annotations

import argparse
import json
import os
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-root", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--port", type=int, default=17861)
    return parser.parse_args()


ARGS = parse_args()
MODEL_ROOT = Path(ARGS.model_root).resolve()
REFERENCE = Path(ARGS.reference).resolve()

if not REFERENCE.is_file():
    raise SystemExit(f"Reference audio does not exist: {REFERENCE}")
if not (MODEL_ROOT / "config.yaml").is_file():
    raise SystemExit(f"IndexTTS2.5 model is incomplete: {MODEL_ROOT}")

from indextts.infer_v2_5 import IndexTTS2  # noqa: E402


ENGINE = IndexTTS2(
    cfg_path=str(MODEL_ROOT / "config.yaml"),
    model_dir=str(MODEL_ROOT),
    use_bf16=True,
    use_cuda_kernel=False,
)
INFERENCE_LOCK = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    server_version = "JarvisIndexTTS/1.0"

    def do_GET(self) -> None:  # noqa: N802
        if self.path != "/health":
            self.send_error(404)
            return
        self._send_json(200, {"status": "ready", "engine": "IndexTTS2.5"})

    def do_POST(self) -> None:  # noqa: N802
        if self.path != "/synthesize":
            self.send_error(404)
            return

        try:
            content_length = int(self.headers.get("Content-Length", "0"))
            if content_length <= 0 or content_length > 64 * 1024:
                raise ValueError("request size is invalid")
            payload = json.loads(self.rfile.read(content_length))
            text = str(payload.get("text", "")).strip()
            language = str(payload.get("lang", "ZH")).upper()
            speed = float(payload.get("duration_factor", 0.9))
            if not text or len(text) > 4000:
                raise ValueError("text must contain 1 to 4000 characters")
            if language not in {"ZH", "EN", "JA", "ES", "AR"}:
                raise ValueError("unsupported language")
            if not 0.75 <= speed <= 1.25:
                raise ValueError("duration_factor must be between 0.75 and 1.25")

            output_path = ""
            try:
                with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as output:
                    output_path = output.name
                with INFERENCE_LOCK:
                    ENGINE.infer(
                        spk_audio_prompt=str(REFERENCE),
                        text=text,
                        lang=language,
                        output_path=output_path,
                        duration_factor=speed,
                        verbose=False,
                    )
                audio = Path(output_path).read_bytes()
            finally:
                if output_path:
                    try:
                        os.remove(output_path)
                    except FileNotFoundError:
                        pass

            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(audio)))
            self.end_headers()
            self.wfile.write(audio)
        except (ValueError, TypeError, json.JSONDecodeError) as error:
            self._send_json(400, {"error": str(error)})
        except Exception as error:  # Keep model internals out of the response.
            self._send_json(500, {"error": type(error).__name__})

    def log_message(self, format: str, *args: object) -> None:
        print(f"IndexTTS request: {format % args}", flush=True)

    def _send_json(self, status: int, value: object) -> None:
        body = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", ARGS.port), Handler)
    print(f"Jarvis IndexTTS2.5 ready on http://127.0.0.1:{ARGS.port}", flush=True)
    server.serve_forever()
