from __future__ import annotations

import ctypes
import json
import subprocess
from pathlib import Path

import winreg


INTERNET_SETTINGS = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings"
INTERNET_OPTION_SETTINGS_CHANGED = 39
INTERNET_OPTION_REFRESH = 37
BACKUP_PATH = Path(__file__).resolve().parent / "certs" / "proxy_backup.json"


def _notify_wininet() -> None:
    internet_set_option = ctypes.windll.Wininet.InternetSetOptionW
    internet_set_option(0, INTERNET_OPTION_SETTINGS_CHANGED, None, 0)
    internet_set_option(0, INTERNET_OPTION_REFRESH, None, 0)


def _read_current() -> dict:
    key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, INTERNET_SETTINGS)
    try:
        def _get(name, default):
            try:
                value, _ = winreg.QueryValueEx(key, name)
                return value
            except FileNotFoundError:
                return default

        return {
            "ProxyEnable": int(_get("ProxyEnable", 0)),
            "ProxyServer": str(_get("ProxyServer", "")),
            "ProxyOverride": str(_get("ProxyOverride", "")),
        }
    finally:
        winreg.CloseKey(key)


def _write(settings: dict) -> None:
    key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, INTERNET_SETTINGS, 0, winreg.KEY_SET_VALUE)
    try:
        winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, int(settings.get("ProxyEnable", 0)))
        winreg.SetValueEx(key, "ProxyServer", 0, winreg.REG_SZ, str(settings.get("ProxyServer", "")))
        winreg.SetValueEx(key, "ProxyOverride", 0, winreg.REG_SZ, str(settings.get("ProxyOverride", "")))
    finally:
        winreg.CloseKey(key)
    _notify_wininet()


def enable_system_proxy(host: str, port: int, log=print) -> None:
    BACKUP_PATH.parent.mkdir(parents=True, exist_ok=True)
    if not BACKUP_PATH.exists():
        BACKUP_PATH.write_text(json.dumps(_read_current(), ensure_ascii=False, indent=2), encoding="utf-8")
        log("PROXY", f"已备份当前系统代理到 {BACKUP_PATH.name}")
    _write(
        {
            "ProxyEnable": 1,
            "ProxyServer": f"{host}:{port}",
            "ProxyOverride": "localhost;127.0.0.1;<local>",
        }
    )
    log("PROXY", f"系统代理已设为 {host}:{port}")
    completed = subprocess.run(
        ["netsh", "winhttp", "import", "proxy", "source=ie"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if completed.returncode == 0:
        log("PROXY", "WinHTTP 代理已同步。")
    else:
        log("PROXY", "WinHTTP 同步失败：" + ((completed.stderr or completed.stdout or "").strip() or "unknown"))


def restore_system_proxy(log=print) -> None:
    if BACKUP_PATH.exists():
        settings = json.loads(BACKUP_PATH.read_text(encoding="utf-8"))
        _write(settings)
        BACKUP_PATH.unlink(missing_ok=True)
        log("PROXY", "系统代理已还原。")
        subprocess.run(["netsh", "winhttp", "reset", "proxy"], capture_output=True)
        log("PROXY", "WinHTTP 代理已重置。")
        return
    _write({"ProxyEnable": 0, "ProxyServer": "", "ProxyOverride": ""})
    subprocess.run(["netsh", "winhttp", "reset", "proxy"], capture_output=True)
    log("PROXY", "未找到备份，已关闭系统代理。")
