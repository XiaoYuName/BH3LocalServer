from __future__ import annotations

import select
import socket
import ssl
import tempfile
import threading
from http.client import HTTPConnection
from pathlib import Path
from typing import Callable
from urllib.parse import urlsplit

from certs import issue_leaf


HOST_SUFFIXES = (
    ".mihoyo.com",
    ".hoyoverse.com",
    ".honkaiimpact3.com",
    ".bh3.com",
)

GATEWAY_PATHS = ("/query_dispatch", "/query_gateway", "/query_gameserver")
DEVICE_FP_PATHS = ("/device-fp/api/getExtList", "/device-fp/api/getFp")
LOGIN_SDK_PATHS = (
    "/bh3_usa/mdk/shield/api/loadConfig",
    "/bh3_usa/combo/granter/api/getConfig",
    "/bh3_usa/combo/granter/api/compareProtocolVersion",
    "/combo/box/api/config/sdk/combo",
    "/combo/box/api/config/sw/precache",
    "/bh3_usa/account/ma-passport/api/getConfig",
    "/bh3_usa/account/ma-passport/api/appLoginByPassword",
    "/bh3_usa/account/ma-passport/token/getByGameToken",
    "/bh3_usa/account/ma-passport/api/logout",
    "/bh3_usa/combo/granter/login/v2/login",
    "/bh3_cn/mdk/shield/api/loadConfig",
    "/bh3_cn/combo/granter/api/getConfig",
    "/bh3_cn/combo/granter/api/compareProtocolVersion",
    "/bh3_cn/account/ma-passport/api/getConfig",
    "/bh3_cn/account/ma-passport/api/appLoginByPassword",
    "/bh3_cn/account/ma-passport/token/getByGameToken",
    "/bh3_cn/account/ma-passport/api/logout",
    "/account/ma-cn-passport/app/loginByPassword",
    "/bh3_cn/combo/granter/login/v2/login",
)
TELEMETRY_PATHS = ("/sdk/dataUpload", "/common/h5log/log/batch")
WEB_RESOURCE_PATHS = (
    "/sw.html",
    "/admin/mi18n/plat_oversea/m2020030410/m2020030410-version.json",
    "/admin/mi18n/plat_os/m09291531181441/m09291531181441-version.json",
)
ROUTED_PATHS = set(
    GATEWAY_PATHS
    + DEVICE_FP_PATHS
    + LOGIN_SDK_PATHS
    + TELEMETRY_PATHS
    + WEB_RESOURCE_PATHS
)
ROUTED_PREFIXES = (
    "/bh3_cn/",
    "/bh3_usa/",
    "/account/",
    "/combo/",
    "/device-fp/",
    "/query_dispatch",
    "/query_gateway",
    "/query_gameserver",
    "/sdk/",
    "/common/h5log/",
    "/data_abtest_api/",
    "/game_weather/",
    "/report",
)
REMOTE_RESOURCE_PREFIXES = (
    "/asset_bundle/",
    "/tmp/",
    "/com.miHoYo.",
)

SKIP_REQ_HEADERS = {
    "host",
    "content-length",
    "connection",
    "proxy-connection",
    "proxy-authorization",
    "transfer-encoding",
    "expect",
}
SKIP_RESP_HEADERS = {
    "content-length",
    "transfer-encoding",
    "connection",
    "proxy-connection",
    "keep-alive",
}


def host_matches(host: str) -> bool:
    host = (host or "").split(":")[0].lower()
    return any(host == suffix[1:] or host.endswith(suffix) for suffix in HOST_SUFFIXES)


def is_remote_resource(path: str) -> bool:
    return any(path.startswith(prefix) for prefix in REMOTE_RESOURCE_PREFIXES)


SKIP_MITM_HOSTS = (
    "client-report.bh3.com",
    "log-upload.bh3.com",
    "log-upload-os.hoyoverse.com",
)


def should_route(host: str, path: str) -> bool:
    host = (host or "").split(":")[0].lower()
    if host in SKIP_MITM_HOSTS:
        return False
    if path.startswith("/query_dispatch") or path.startswith("/query_security_file"):
        return True
    if path.startswith("/query_gateway") or path.startswith("/query_gameserver"):
        return False
    # query_gameserver 在 9.1 AES 钥未解出前放行官方，避免二次 DispatchDecryption NRE。
    if host in ("127.0.0.1", "localhost"):
        return any(path.startswith(prefix) for prefix in ROUTED_PREFIXES) or path in ROUTED_PATHS
    if not host_matches(host):
        return False
    if is_remote_resource(path):
        return False
    if path == "/" or path.startswith("/?"):
        return False
    return True


class LocalMitmProxy:
    def __init__(self, listen_host: str, listen_port: int, local_http: str, ca_key, ca_cert, log: Callable[..., None] = print):
        self.listen_host = listen_host
        self.listen_port = listen_port
        self.local_http = local_http.rstrip("/")
        self.ca_key = ca_key
        self.ca_cert = ca_cert
        self.log = log
        self._sock: socket.socket | None = None
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()
        self._contexts: dict[str, ssl.SSLContext] = {}
        self._ctx_lock = threading.Lock()
        self.running = False
        local = urlsplit(self.local_http)
        self._local_host = local.hostname or "127.0.0.1"
        self._local_port = local.port or (443 if local.scheme == "https" else 80)

    def start(self) -> None:
        if self.running:
            self.log("PROXY", f"本地代理已在 {self.listen_host}:{self.listen_port} 运行。")
            return
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((self.listen_host, self.listen_port))
        server.listen(128)
        server.settimeout(0.5)
        self._sock = server
        self._stop.clear()
        self.running = True
        self._thread = threading.Thread(target=self._accept_loop, name="bh3-mitm", daemon=True)
        self._thread.start()
        self.log("PROXY", f"本地代理已启动，监听 {self.listen_host}:{self.listen_port}。")
        self.log("PROXY", f"dispatch / SDK 将转发至 {self.local_http}/ 。")

    def stop(self) -> None:
        self._stop.set()
        self.running = False
        sock = self._sock
        self._sock = None
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=2)
        self._thread = None
        self.log("PROXY", "本地代理已停止。")

    def _context_for(self, hostname: str) -> ssl.SSLContext:
        hostname = hostname.split(":")[0]
        with self._ctx_lock:
            cached = self._contexts.get(hostname)
            if cached is not None:
                return cached
            cert_pem, key_pem = issue_leaf(hostname, self.ca_key, self.ca_cert)
            tmp = tempfile.TemporaryDirectory(prefix="bh3-mitm-")
            cert_path = Path(tmp.name) / "leaf.crt"
            key_path = Path(tmp.name) / "leaf.key"
            cert_path.write_bytes(cert_pem)
            key_path.write_bytes(key_pem)
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            ctx.load_cert_chain(str(cert_path), str(key_path))
            try:
                ctx.set_alpn_protocols(["http/1.1"])
            except ssl.SSLError:
                pass
            ctx._tmp_dir = tmp  # keep files alive
            self._contexts[hostname] = ctx
            return ctx

    def _accept_loop(self) -> None:
        assert self._sock is not None
        while not self._stop.is_set():
            try:
                client, addr = self._sock.accept()
            except TimeoutError:
                continue
            except OSError:
                break
            threading.Thread(target=self._handle_client, args=(client, addr), daemon=True).start()

    def _handle_client(self, client: socket.socket, addr) -> None:
        client.settimeout(30)
        try:
            header_bytes = _recv_headers(client)
            if not header_bytes:
                return
            first_line, headers, rest = _parse_headers(header_bytes)
            parts = first_line.split(" ")
            method = parts[0].upper() if parts else ""
            target = parts[1] if len(parts) > 1 else ""
            if method == "CONNECT":
                host, _, port_text = target.partition(":")
                port = int(port_text or "443")
                client.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
                host_l = host.split(":")[0].lower()
                if host_l in SKIP_MITM_HOSTS:
                    self._tunnel(client, host, port)
                elif host_matches(host):
                    self._mitm_https(client, host, port)
                else:
                    self._tunnel(client, host, port)
                return
            self._handle_plain_http(client, first_line, headers, rest)
        except Exception as exc:
            self.log("PROXY", f"连接处理失败 {addr}: {exc}")
        finally:
            try:
                client.close()
            except OSError:
                pass

    def _mitm_https(self, client: socket.socket, host: str, port: int) -> None:
        context = self._context_for(host)
        tls = context.wrap_socket(client, server_side=True, do_handshake_on_connect=True)
        tls.settimeout(30)
        try:
            while True:
                header_bytes = _recv_headers(tls)
                if not header_bytes:
                    return
                first_line, headers, rest = _parse_headers(header_bytes)
                parts = first_line.split(" ")
                method = parts[0].upper() if parts else "GET"
                target = parts[1] if len(parts) > 1 else "/"
                path = urlsplit(target).path or "/"
                body = _read_body(tls, headers, rest)
                if should_route(host, path):
                    self.log("PROXY", f"拦截 {method} {host}{target}")
                    status, reason, resp_headers, resp_body = self._forward_local(method, target, headers, body)
                    self.log("PROXY", f"转发到 {self.local_http}{path}  -> {status}")
                    _send_http_response(tls, status, reason, resp_headers, resp_body)
                else:
                    self.log("PROXY", f"放行 {method} {host}{path}")
                    self._forward_upstream_https(tls, host, port, method, target, headers, body)
                if headers.get("connection", "").lower() == "close":
                    return
        finally:
            try:
                tls.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                tls.close()
            except OSError:
                pass

    def _handle_plain_http(self, client: socket.socket, first_line: str, headers: dict[str, str], rest: bytes) -> None:
        parts = first_line.split(" ")
        method = parts[0].upper() if parts else "GET"
        target = parts[1] if len(parts) > 1 else "/"
        parsed = urlsplit(target)
        host = (headers.get("host") or parsed.netloc).split(":")[0]
        path = parsed.path or "/"
        query = (("?" + parsed.query) if parsed.query else "")
        body = _read_body(client, headers, rest)
        if should_route(host, path):
            self.log("PROXY", f"拦截 {method} {host}{path}")
            status, reason, resp_headers, resp_body = self._forward_local(method, path + query, headers, body)
            _send_http_response(client, status, reason, resp_headers, resp_body)
            return
        hostport = parsed.netloc or headers.get("host", "")
        dest_host, _, dest_port = hostport.partition(":")
        self._tunnel_request(client, dest_host, int(dest_port or 80), method, path + query, headers, body)

    def _forward_local(self, method: str, target: str, headers: dict[str, str], body: bytes):
        parsed = urlsplit(target)
        path = parsed.path or "/"
        if parsed.query:
            path += "?" + parsed.query
        conn = HTTPConnection(self._local_host, self._local_port, timeout=30)
        try:
            fwd_headers = {k: v for k, v in headers.items() if k.lower() not in SKIP_REQ_HEADERS}
            fwd_headers["Host"] = f"{self._local_host}:{self._local_port}"
            conn.request(method, path, body=body or None, headers=fwd_headers)
            response = conn.getresponse()
            data = response.read()
            resp_headers = [(name, value) for name, value in response.getheaders() if name.lower() not in SKIP_RESP_HEADERS]
            return response.status, response.reason, resp_headers, data
        finally:
            conn.close()

    def _forward_upstream_https(self, client_tls: ssl.SSLSocket, host: str, port: int, method: str, target: str, headers: dict[str, str], body: bytes) -> None:
        context = ssl.create_default_context()
        with socket.create_connection((host, port), timeout=30) as raw:
            with context.wrap_socket(raw, server_hostname=host) as upstream:
                parsed = urlsplit(target)
                path = parsed.path or "/"
                if parsed.query:
                    path += "?" + parsed.query
                payload = [f"{method} {path} HTTP/1.1", f"Host: {host}"]
                for key, value in headers.items():
                    if key.lower() in SKIP_REQ_HEADERS or key.lower() == "host":
                        continue
                    payload.append(f"{key}: {value}")
                payload.append(f"Content-Length: {len(body)}")
                payload.append("Connection: close")
                payload.append("")
                upstream.sendall(("\r\n".join(payload) + "\r\n").encode("latin1") + body)
                while True:
                    chunk = upstream.recv(65536)
                    if not chunk:
                        break
                    client_tls.sendall(chunk)

    def _tunnel(self, client: socket.socket, host: str, port: int) -> None:
        remote = socket.create_connection((host, port), timeout=15)
        try:
            _splice(client, remote)
        finally:
            remote.close()

    def _tunnel_request(self, client: socket.socket, host: str, port: int, method: str, path: str, headers: dict[str, str], body: bytes) -> None:
        remote = socket.create_connection((host, port), timeout=15)
        try:
            lines = [f"{method} {path} HTTP/1.1", f"Host: {host}"]
            for key, value in headers.items():
                if key.lower() in SKIP_REQ_HEADERS or key.lower() == "host":
                    continue
                lines.append(f"{key}: {value}")
            lines.append(f"Content-Length: {len(body)}")
            lines.append("Connection: close")
            lines.append("")
            remote.sendall(("\r\n".join(lines) + "\r\n").encode("latin1") + body)
            _splice(client, remote)
        finally:
            remote.close()


def _recv_headers(sock: socket.socket) -> bytes:
    data = b""
    while b"\r\n\r\n" not in data:
        chunk = sock.recv(4096)
        if not chunk:
            return data
        data += chunk
        if len(data) > 1024 * 1024:
            raise RuntimeError("HTTP 头过长")
    return data


def _parse_headers(raw: bytes) -> tuple[str, dict[str, str], bytes]:
    header_blob, _, rest = raw.partition(b"\r\n\r\n")
    lines = header_blob.decode("latin1").split("\r\n")
    first = lines[0] if lines else ""
    headers: dict[str, str] = {}
    for line in lines[1:]:
        if ":" not in line:
            continue
        name, value = line.split(":", 1)
        headers[name.strip().lower()] = value.strip()
    return first, headers, rest


def _read_body(sock: socket.socket, headers: dict[str, str], already: bytes) -> bytes:
    length = int(headers.get("content-length") or "0")
    body = already
    while len(body) < length:
        chunk = sock.recv(length - len(body))
        if not chunk:
            break
        body += chunk
    return body[:length]


def _send_http_response(sock: socket.socket, status: int, reason: str, headers: list[tuple[str, str]], body: bytes) -> None:
    lines = [f"HTTP/1.1 {status} {reason}"]
    for name, value in headers:
        lines.append(f"{name}: {value}")
    lines.append(f"Content-Length: {len(body)}")
    lines.append("Connection: close")
    lines.append("")
    sock.sendall(("\r\n".join(lines) + "\r\n").encode("latin1") + body)


def _splice(a: socket.socket, b: socket.socket) -> None:
    sockets = [a, b]
    while True:
        readable, _, _ = select.select(sockets, [], [], 30)
        if not readable:
            return
        for src in readable:
            data = src.recv(65536)
            if not data:
                return
            dst = b if src is a else a
            dst.sendall(data)
