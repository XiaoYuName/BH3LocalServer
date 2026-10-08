from __future__ import annotations

import base64
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Callable
from urllib.parse import parse_qs, urlparse

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.padding import PKCS7


ROOT = Path(__file__).resolve().parent
HOTFIX_PATH = ROOT / "hotfix.json"

# 9.0.0 确认可用。9.1.0 客户端若仍用同一把钥就能过 DispatchDecryption。
AES_KEYS = {
    "9.0.0": b"58260189ad8b4ab54d3dde91cb3b24d3",
    "9.1.0": b"58260189ad8b4ab54d3dde91cb3b24d3",
}

COMBO_TOKEN = "bh3-local-combo-token"
ACCOUNT_UID = "10001"
ACCOUNT_NAME = "captain"


def extract_version(raw: str | None) -> str:
    raw = raw or ""
    return raw.split("_", 1)[0] if "_" in raw else raw


def encrypt_dispatch(version: str, payload: dict) -> str:
    key = AES_KEYS.get(version) or AES_KEYS["9.1.0"]
    data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    padder = PKCS7(128).padder()
    padded = padder.update(data) + padder.finalize()
    encryptor = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
    return base64.b64encode(encryptor.update(padded) + encryptor.finalize()).decode("ascii")


def load_hotfix() -> dict:
    if HOTFIX_PATH.exists():
        return json.loads(HOTFIX_PATH.read_text(encoding="utf-8"))
    return {}


def ok(data=None, message="OK"):
    return {"retcode": 0, "success": True, "message": message, "data": data}


class StubHandler(BaseHTTPRequestHandler):
    server_version = "BH3LocalStub/0.2"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args) -> None:
        logger = getattr(self.server, "app_log", None)
        if logger:
            logger("STUB", f"{self.command} {self.path} {fmt % args}")

    def _send(self, body: bytes, content_type: str, status: int = 200) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, payload, status=200):
        self._send(json.dumps(payload, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8", status)

    def _dispatch(self, version: str, payload: dict) -> None:
        cipher = encrypt_dispatch(version, payload)
        # 官方是裸 base64，没有 JSON 引号，也没有 Content-Type。
        self._send(cipher.encode("ascii"), "text/plain; charset=utf-8")

    def _read_json(self) -> dict:
        length = int(self.headers.get("Content-Length") or "0")
        raw = self.rfile.read(length) if length else b""
        if not raw:
            return {}
        try:
            return json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError:
            return {}

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)
        version_raw = (query.get("version") or [""])[0]
        version = extract_version(version_raw)
        cfg = getattr(self.server, "cfg", {})
        local_http = f"http://{cfg.get('local_http_host', '127.0.0.1')}:{cfg.get('local_http_port', 20100)}"
        public_base = (cfg.get("public_dispatch_base") or "https://outer-dp-pc01.bh3.com").rstrip("/")
        logger = getattr(self.server, "app_log", print)

        if path == "/query_dispatch":
            logger("STUB", f"query_dispatch version={version_raw}")
            official = ROOT / "sample_dispatch_91.bin"
            if version.startswith("9.1") and official.exists():
                logger("STUB", "query_dispatch -> official 9.1 ciphertext")
                self._send(official.read_bytes().strip(), "text/plain; charset=utf-8")
                return
            payload = {
                "region_list": [
                    {
                        "dispatch_url": "https://outer-dp-yyb01.bh3.com/query_gameserver",
                        "ext": {"1794460900": "Recommend"},
                        "name": "yyb01",
                        "retcode": 0,
                        "title": "应用宝服",
                    },
                    {
                        "dispatch_url": "https://outer-dp-pc01.bh3.com/query_gameserver",
                        "ext": {"1794460900": "Recommend"},
                        "name": "pc01",
                        "retcode": 0,
                        "title": "pc01",
                    },
                ],
                "retcode": 0,
            }
            self._dispatch(version, payload)
            return
        if path in ("/query_gateway", "/query_gameserver"):
            logger("STUB", f"query_gameserver version={version_raw}")
            official = ROOT / "official_gameserver_90.json"
            payload = json.loads(official.read_text(encoding="utf-8")) if official.exists() else load_hotfix()
            if isinstance(payload, dict) and "manifest" not in payload:
                payload = {"manifest": payload}
            payload["retcode"] = 0
            payload["msg"] = ""
            payload["is_data_ready"] = True
            payload["force_update_url"] = ""
            payload["server_cur_time"] = int(time.time())
            payload["server_cur_timezone"] = 8
            payload["region_name"] = "pc01"
            payload["account_url"] = public_base + "/"
            payload["account_url_backup"] = public_base + "/"
            payload["oaserver_url"] = public_base
            payload["gameserver"] = {"ip": "127.0.0.1", "is_kcp": True, "port": int(cfg.get("game_port", 21000))}
            payload["gateway"] = {"ip": "127.0.0.1", "is_kcp": True, "port": int(cfg.get("game_port", 21000))}
            ext = payload.get("ext") if isinstance(payload.get("ext"), dict) else {}
            ext["block_error_dialog"] = "1"
            ext["network_feedback_enable"] = "0"
            payload["ext"] = ext
            self._dispatch(version, payload)
            return
        if path.endswith("/mdk/shield/api/loadConfig"):
            self._json(ok(self._mdk_config(path)))
            return
        if path.endswith("/combo/granter/api/getConfig"):
            self._json(ok(self._combo_config(public_base)))
            return
        if path.endswith("/combo/granter/api/compareProtocolVersion"):
            self._json(ok({"modified": False}))
            return
        if path.endswith("/account/ma-passport/api/getConfig"):
            self._json(ok({"protocol": True, "qr_enabled": False, "log_level": "INFO"}))
            return
        if path == "/combo/box/api/config/sdk/combo":
            self._json(ok({"vals": {"telemetry_config": '{ "dataupload_enable": 0 }'}}))
            return
        if path == "/combo/box/api/config/sw/precache":
            self._json(ok({"entries": [], "version": "0", "enable": False}))
            return
        if path == "/device-fp/api/getExtList":
            self._json(ok({"code": 200, "msg": "ok", "ext_list": ["deviceModel", "osVersion"], "pkg_list": []}))
            return
        if path in ("/_ts", "/report"):
            self._json({"code": 0, "message": "OK"})
            return
        if path.endswith("/sw.html") or path.endswith("/announcement/index.html"):
            self._send(b"<html><body>local</body></html>", "text/html; charset=utf-8")
            return
        if path.endswith("-version.json"):
            self._json({"version": 1})
            return
        if path == "/query_security_file":
            file_key = (query.get("file_key") or ["9.1.0_gf_pc"])[0]
            cached = ROOT / f"security_file_{file_key}.json"
            if cached.exists():
                logger("STUB", f"query_security_file {file_key} -> {cached.name}")
                self._send(cached.read_bytes(), "application/json; charset=utf-8")
                return
            logger("STUB", f"query_security_file 无缓存 {file_key}")
            self._json({"retcode": -1, "msg": "security file not exist"})
            return
        logger("STUB", f"未匹配 GET {path}，回空成功")
        self._json(ok({}))

    def do_HEAD(self):
        self.do_GET()

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Allow", "GET, POST, HEAD, OPTIONS")
        self.send_header("Content-Length", "0")
        self.send_header("Connection", "close")
        self.end_headers()

    def do_POST(self):
        parsed = urlparse(self.path)
        path = parsed.path
        body = self._read_json()
        logger = getattr(self.server, "app_log", print)
        logger("STUB", f"POST {path}")
        if path.endswith("/account/ma-passport/api/appLoginByPassword") or path.endswith("/account/ma-cn-passport/app/loginByPassword"):
            self._json(ok(self._login_data()))
            return
        if path.endswith("/account/ma-passport/token/getByGameToken"):
            self._json(ok(self._login_data()))
            return
        if path.endswith("/combo/granter/login/v2/login"):
            self._json(
                ok(
                    {
                        "account_type": 1,
                        "data": '{"guest": false}',
                        "heartbeat": False,
                        "open_id": ACCOUNT_UID,
                        "combo_token": COMBO_TOKEN,
                    }
                )
            )
            return
        if path.endswith("/account/ma-passport/api/logout"):
            self._json(ok())
            return
        if path == "/device-fp/api/getFp":
            self._json(ok({"device_fp": body.get("device_fp") or body.get("deviceFp") or "localfp", "code": 0, "msg": "ok"}))
            return
        if path.endswith("/dataUpload") or path.endswith("/log/batch"):
            self._json({"code": 0, "message": "OK"})
            return
        if path.endswith("/data_abtest_api/config/experiment/list"):
            self._json(ok([]))
            return
        self._json(ok())

    def _mdk_config(self, path: str) -> dict:
        product = "bh3_usa" if "/bh3_usa/" in path else "bh3_cn"
        return {
            "id": 16,
            "game_key": product,
            "client": "PC",
            "identity": "I_IDENTITY",
            "guest": False,
            "ignore_versions": "",
            "scene": "S_NORMAL",
            "name": "BH3Local",
            "disable_regist": False,
            "enable_email_captcha": False,
            "thirdparty": [],
            "disable_mmt": True,
            "server_guest": False,
        }

    def _combo_config(self, local_http: str) -> dict:
        return {
            "protocol": True,
            "qr_enabled": False,
            "log_level": "INFO",
            "announce_url": local_http.rstrip("/") + "/announcement/index.html",
            "push_alias_type": 2,
            "disable_ysdk_guard": True,
            "enable_announce_popup": False,
            "app_name": "BH3Local",
            "enable_user_center": False,
        }

    def _login_data(self) -> dict:
        return {
            "token": {"token_type": 1, "token": COMBO_TOKEN},
            "user_info": {
                "aid": ACCOUNT_UID,
                "mid": "",
                "account_name": ACCOUNT_NAME,
                "email": "captain@local",
                "is_email_verify": 0,
                "area_code": "**",
                "country": "CN",
                "is_adult": 1,
            },
            "ext_user_info": {"guardian_email": "", "birth": "0"},
            "reactivate_action_ticket": "",
            "bind_email_action_ticket": "",
        }


class StubServer:
    def __init__(self, host: str, port: int, cfg: dict, log: Callable[..., None] = print):
        self.host = host
        self.port = port
        self.cfg = cfg
        self.log = log
        self.httpd: ThreadingHTTPServer | None = None
        self._thread: threading.Thread | None = None
        self.running = False

    def start(self) -> None:
        if self.running:
            self.log("STUB", f"本地桩服务已在 {self.host}:{self.port} 运行。")
            return
        self.httpd = ThreadingHTTPServer((self.host, self.port), StubHandler)
        self.httpd.app_log = self.log
        self.httpd.cfg = self.cfg
        self._thread = threading.Thread(target=self.httpd.serve_forever, name="bh3-stub", daemon=True)
        self._thread.start()
        self.running = True
        self.log("STUB", f"本地桩服务已启动：http://{self.host}:{self.port}/")

    def stop(self) -> None:
        if self.httpd is not None:
            self.httpd.shutdown()
            self.httpd.server_close()
            self.httpd = None
        self.running = False
        self.log("STUB", "本地桩服务已停止。")
