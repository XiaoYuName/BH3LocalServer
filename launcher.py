from __future__ import annotations

import atexit
import json
import os
import subprocess
import sys
import threading
import time
import webbrowser
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from certs import CA_CER_PATH, CA_CRT_PATH, ensure_ca, install_root_cert, is_root_installed
from kcp_server import KcpHandshakeServer
from proxy_server import LocalMitmProxy
from stub_server import StubServer
from win_proxy import enable_system_proxy, restore_system_proxy


CFG_PATH = ROOT / "config.json"
WEB_DIR = ROOT / "web"
LOG_DIR = ROOT / "logs"
LOG_FILE = LOG_DIR / "launcher.log"
LOG_LOCK = threading.Lock()
LOGS: list[str] = []
STATE = {
    "system_proxy": False,
    "game_pid": None,
    "cert_installing": False,
}
CERT_CACHE = {"installed": False, "checked_at": 0.0}


def log(tag: str, message: str) -> None:
    line = f"[{tag}] {message}"
    with LOG_LOCK:
        LOGS.append(line)
        if len(LOGS) > 400:
            del LOGS[: len(LOGS) - 400]
        try:
            LOG_DIR.mkdir(parents=True, exist_ok=True)
            with LOG_FILE.open("a", encoding="utf-8") as fh:
                fh.write(time.strftime("%H:%M:%S ") + line + "\n")
        except OSError:
            pass
    print(line, flush=True)


def load_cfg() -> dict:
    cfg = json.loads(CFG_PATH.read_text(encoding="utf-8"))
    game = (ROOT / cfg["game_exe"]).resolve()
    cfg["game_exe_resolved"] = str(game)
    cfg["local_http"] = f"http://{cfg['local_http_host']}:{cfg['local_http_port']}"
    return cfg


CFG = load_cfg()
CA_KEY, CA_CERT = ensure_ca(log)
STUB = StubServer(CFG["local_http_host"], int(CFG["local_http_port"]), CFG, log)
STUB80: StubServer | None = None
KCP = KcpHandshakeServer("0.0.0.0", int(CFG.get("game_port", 21000)), log)
PROXY = LocalMitmProxy(CFG["proxy_host"], int(CFG["proxy_port"]), CFG["local_http"], CA_KEY, CA_CERT, log)


def cert_installed(force: bool = False) -> bool:
    now = time.time()
    if CERT_CACHE["installed"] and not force:
        return True
    if not force and now - CERT_CACHE["checked_at"] < 4:
        return CERT_CACHE["installed"]
    installed = is_root_installed()
    CERT_CACHE["installed"] = installed
    CERT_CACHE["checked_at"] = now
    if installed:
        STATE["cert_installing"] = False
    return installed


def begin_install_cert() -> None:
    if cert_installed(force=True):
        log("CERT", "根证书已安装，无需重复操作。")
        return
    if STATE["cert_installing"]:
        log("CERT", "正在等待 Windows 确认弹窗，请点“是”。")
        return
    STATE["cert_installing"] = True

    def worker():
        try:
            install_root_cert(log)
        except Exception as exc:
            log("CERT", f"安装失败：{exc}")
        finally:
            CERT_CACHE["checked_at"] = 0.0
            STATE["cert_installing"] = False
            cert_installed(force=True)

    threading.Thread(target=worker, name="bh3-cert-install", daemon=True).start()
    log("CERT", "已在后台启动证书安装。若弹出安全提示，请点“是”。")


def status() -> dict:
    installed = cert_installed()
    return {
        "stub": STUB.running,
        "kcp": KCP.running,
        "proxy": PROXY.running,
        "system_proxy": STATE["system_proxy"],
        "cert": CA_CRT_PATH.exists(),
        "cert_installed": installed,
        "cert_installing": STATE["cert_installing"] and not installed,
        "game": Path(CFG["game_exe_resolved"]).exists(),
        "stub_addr": CFG["local_http"],
        "proxy_addr": f"{CFG['proxy_host']}:{CFG['proxy_port']}",
        "game_exe": CFG["game_exe_resolved"],
        "logs": "\n".join(LOGS[-120:]),
    }


def start_stack() -> None:
    global STUB80
    STUB.start()
    if STUB80 is None:
        STUB80 = StubServer("127.0.0.1", 80, CFG, log)
    try:
        STUB80.start()
    except OSError as exc:
        log("STUB", f"80 端口未绑定（可能需要管理员）：{exc}")
        STUB80 = None
    KCP.start()
    PROXY.start()
    if CFG.get("set_system_proxy", True):
        enable_system_proxy(CFG["proxy_host"], int(CFG["proxy_port"]), log)
        STATE["system_proxy"] = True


def stop_stack() -> None:
    try:
        PROXY.stop()
    except Exception as exc:
        log("PROXY", f"停止失败：{exc}")
    try:
        STUB.stop()
    except Exception as exc:
        log("STUB", f"停止失败：{exc}")
    if STUB80 is not None:
        try:
            STUB80.stop()
        except Exception as exc:
            log("STUB", f"停止 80 端口失败：{exc}")
    try:
        KCP.stop()
    except Exception as exc:
        log("KCP", f"停止失败：{exc}")
    if STATE["system_proxy"] or CFG.get("set_system_proxy", True):
        try:
            restore_system_proxy(log)
        except Exception as exc:
            log("PROXY", f"还原系统代理失败：{exc}")
        STATE["system_proxy"] = False


def launch_game() -> None:
    exe = Path(CFG["game_exe_resolved"])
    if not exe.exists():
        raise FileNotFoundError(f"未找到客户端：{exe}")
    env = os.environ.copy()
    proxy_url = f"http://{CFG['proxy_host']}:{CFG['proxy_port']}"
    env["HTTP_PROXY"] = proxy_url
    env["HTTPS_PROXY"] = proxy_url
    env["http_proxy"] = proxy_url
    env["https_proxy"] = proxy_url
    proc = subprocess.Popen([str(exe)], cwd=str(exe.parent), env=env)
    STATE["game_pid"] = proc.pid
    log("GAME", f"已启动 BH3.exe PID={proc.pid}")


class UiHandler(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(WEB_DIR), **kwargs)

    def log_message(self, fmt: str, *args) -> None:
        return

    def _json(self, payload: dict, code: int = 200) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/api/status":
            self._json(status())
            return
        if path in ("/ca.cer", "/certs/ca.cer"):
            target = CA_CER_PATH if CA_CER_PATH.exists() else CA_CRT_PATH
            data = target.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "application/x-x509-ca-cert")
            self.send_header("Content-Disposition", 'attachment; filename="bh3-local-ca.cer"')
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        if path in ("/", "/index.html"):
            html = (WEB_DIR / "index.html").read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(html)))
            self.end_headers()
            self.wfile.write(html)
            return
        super().do_GET()

    def do_POST(self):
        path = urlparse(self.path).path
        try:
            if path == "/api/start":
                start_stack()
                self._json({"ok": True, **status()})
                return
            if path == "/api/stop":
                stop_stack()
                self._json({"ok": True, **status()})
                return
            if path == "/api/launch-game":
                launch_game()
                self._json({"ok": True, **status()})
                return
            if path == "/api/install-cert":
                begin_install_cert()
                self._json({"ok": True, "started": True, **status()})
                return
            self._json({"error": "unknown action"}, 404)
        except Exception as exc:
            log("UI", str(exc))
            self._json({"error": str(exc)}, 500)


def main() -> None:
    atexit.register(stop_stack)
    host = CFG.get("ui_host", "127.0.0.1")
    port = int(CFG.get("ui_port", 17890))
    httpd = ThreadingHTTPServer((host, port), UiHandler)
    url = f"http://{host}:{port}/"
    log("UI", f"登录器面板：{url}")
    if CFG.get("open_browser", True):
        webbrowser.open(url)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()
        stop_stack()


if __name__ == "__main__":
    os.chdir(ROOT)
    main()
