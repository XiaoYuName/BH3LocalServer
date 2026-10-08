from __future__ import annotations

import socket
import struct
import threading
from typing import Callable


HEADER_CONST = 0x01234567
TAIL_CONST = 0x89ABCDEF
CMD_GET_PLAYER_TOKEN_REQ = 4
CMD_GET_PLAYER_TOKEN_RSP = 5
CMD_PLAYER_LOGIN_REQ = 6
CMD_PLAYER_LOGIN_RSP = 7


def _u32be(n: int) -> bytes:
    return struct.pack(">I", n & 0xFFFFFFFF)


def _i32be(n: int) -> bytes:
    return struct.pack(">i", n)


def _u16be(n: int) -> bytes:
    return struct.pack(">H", n & 0xFFFF)


def encode_varint(n: int) -> bytes:
    out = bytearray()
    n &= (1 << 64) - 1
    while n > 0x7F:
        out.append((n & 0x7F) | 0x80)
        n >>= 7
    out.append(n & 0x7F)
    return bytes(out)


def pb_field(number: int, wire: int, payload: bytes) -> bytes:
    return encode_varint((number << 3) | wire) + payload


def pb_uint32(number: int, value: int) -> bytes:
    return pb_field(number, 0, encode_varint(value))


def pb_string(number: int, value: str) -> bytes:
    data = value.encode("utf-8")
    return pb_field(number, 2, encode_varint(len(data)) + data)


def build_game_packet(cmd_id: int, body: bytes, user_id: int = 0) -> bytes:
    header = b""
    return b"".join(
        [
            _u32be(HEADER_CONST),
            _u16be(1),
            _u16be(0),
            _u32be(0),
            _u32be(user_id),
            _u32be(0),
            _u32be(0),
            _u16be(0),
            _u16be(cmd_id),
            _u16be(len(header)),
            _u32be(len(body)),
            header,
            body,
            _u32be(TAIL_CONST),
        ]
    )


def get_player_token_rsp(uid: int = 10001, token: str = "bh3-local-combo-token") -> bytes:
    body = b"".join(
        [
            pb_uint32(1, 0),
            pb_uint32(2, uid),
            pb_string(3, token),
            pb_uint32(4, 1),
            pb_string(5, str(uid)),
        ]
    )
    return build_game_packet(CMD_GET_PLAYER_TOKEN_RSP, body, uid)


def player_login_rsp(uid: int = 10001) -> bytes:
    body = b"".join(
        [
            pb_uint32(1, 0),
            pb_uint32(4, 248),
            pb_uint32(6, 0),
            pb_uint32(12, uid),
            pb_string(16, "BH3Local"),
        ]
    )
    return build_game_packet(CMD_PLAYER_LOGIN_RSP, body, uid)


class KcpHandshakeServer:
    """BH3 UDP handshake + raw packet dump. Full KCP conversation comes next."""

    def __init__(self, host: str, port: int, log: Callable[..., None] = print):
        self.host = host
        self.port = port
        self.log = log
        self.running = False
        self._sock: socket.socket | None = None
        self._thread: threading.Thread | None = None
        self._conv = 1

    def start(self) -> None:
        if self.running:
            self.log("KCP", f"游戏服已在 {self.host}:{self.port} 运行。")
            return
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((self.host, self.port))
        sock.settimeout(0.5)
        self._sock = sock
        self.running = True
        self._thread = threading.Thread(target=self._loop, name="bh3-kcp", daemon=True)
        self._thread.start()
        self.log("KCP", f"游戏服 UDP 已监听 {self.host}:{self.port}（握手 + 登录包）。")

    def stop(self) -> None:
        self.running = False
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
            self._sock = None
        self.log("KCP", "游戏服已停止。")

    def _loop(self) -> None:
        assert self._sock is not None
        while self.running:
            try:
                data, addr = self._sock.recvfrom(2048)
            except TimeoutError:
                continue
            except OSError:
                break
            if len(data) < 20:
                self.log("KCP", f"短包 {addr} len={len(data)}")
                continue
            code = struct.unpack(">i", data[:4])[0]
            if code == 0x000000FF:
                enet = struct.unpack(">i", data[12:16])[0]
                conv = self._conv
                self._conv += 1
                resp = b"".join(
                    [
                        _i32be(0x00000145),
                        struct.pack("<i", conv >> 32),
                        struct.pack("<i", conv & 0xFFFFFFFF),
                        _i32be(enet),
                        _i32be(0x14514545),
                    ]
                )
                self._sock.sendto(resp, addr)
                self.log("KCP", f"握手 {addr} conv={conv} enet={enet}")
                continue
            if code == 0x00000194:
                self.log("KCP", f"断开请求 {addr}")
                continue
            self.log("KCP", f"数据 {addr} len={len(data)} head={data[:8].hex()}")
