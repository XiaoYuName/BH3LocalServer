from __future__ import annotations

import subprocess
import threading
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID


ROOT = Path(__file__).resolve().parent
CERT_DIR = ROOT / "certs"
CA_KEY_PATH = CERT_DIR / "ca.key"
CA_CRT_PATH = CERT_DIR / "ca.crt"
CA_CER_PATH = CERT_DIR / "ca.cer"


def certs_ready() -> bool:
    return CA_KEY_PATH.exists() and CA_CRT_PATH.exists()


def _write_pem(path: Path, data: bytes) -> None:
    path.write_bytes(data)


def ensure_ca(log=print) -> tuple[rsa.RSAPrivateKey, x509.Certificate]:
    CERT_DIR.mkdir(parents=True, exist_ok=True)
    if certs_ready():
        key = serialization.load_pem_private_key(CA_KEY_PATH.read_bytes(), password=None)
        cert = x509.load_pem_x509_certificate(CA_CRT_PATH.read_bytes())
        log("CERT", f"已有本地 CA：{CA_CRT_PATH.name}")
        return key, cert

    log("CERT", "正在生成一次性本地 CA（只给本机 MITM 用）...")
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.now(timezone.utc)
    name = x509.Name(
        [
            x509.NameAttribute(NameOID.COUNTRY_NAME, "CN"),
            x509.NameAttribute(NameOID.ORGANIZATION_NAME, "BH3 Local Launcher"),
            x509.NameAttribute(NameOID.COMMON_NAME, "BH3 Local MITM CA"),
        ]
    )
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(minutes=5))
        .not_valid_after(now + timedelta(days=3650))
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(
            x509.KeyUsage(
                digital_signature=True,
                content_commitment=False,
                key_encipherment=False,
                data_encipherment=False,
                key_agreement=False,
                key_cert_sign=True,
                crl_sign=True,
                encipher_only=False,
                decipher_only=False,
            ),
            critical=True,
        )
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
        .sign(key, hashes.SHA256())
    )
    _write_pem(
        CA_KEY_PATH,
        key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        ),
    )
    pem = cert.public_bytes(serialization.Encoding.PEM)
    der = cert.public_bytes(serialization.Encoding.DER)
    _write_pem(CA_CRT_PATH, pem)
    CA_CER_PATH.write_bytes(der)
    log("CERT", f"CA 已写入 {CA_CRT_PATH}")
    return key, cert


def issue_leaf(hostname: str, ca_key: rsa.RSAPrivateKey, ca_cert: x509.Certificate) -> tuple[bytes, bytes]:
    hostname = hostname.split(":")[0]
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.now(timezone.utc)
    subject = x509.Name(
        [
            x509.NameAttribute(NameOID.COUNTRY_NAME, "CN"),
            x509.NameAttribute(NameOID.ORGANIZATION_NAME, "BH3 Local Launcher"),
            x509.NameAttribute(NameOID.COMMON_NAME, hostname),
        ]
    )
    alt_names = [x509.DNSName(hostname)]
    if hostname.count(".") >= 2:
        alt_names.append(x509.DNSName("*." + ".".join(hostname.split(".")[1:])))
    cert = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(ca_cert.subject)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(minutes=5))
        .not_valid_after(now + timedelta(days=825))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(
            x509.KeyUsage(
                digital_signature=True,
                content_commitment=False,
                key_encipherment=True,
                data_encipherment=False,
                key_agreement=False,
                key_cert_sign=False,
                crl_sign=False,
                encipher_only=False,
                decipher_only=False,
            ),
            critical=True,
        )
        .add_extension(
            x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH, ExtendedKeyUsageOID.CLIENT_AUTH]),
            critical=False,
        )
        .add_extension(x509.SubjectAlternativeName(alt_names), critical=False)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
        .add_extension(
            x509.AuthorityKeyIdentifier.from_issuer_subject_key_identifier(
                ca_cert.extensions.get_extension_for_class(x509.SubjectKeyIdentifier).value
            ),
            critical=False,
        )
        .sign(ca_key, hashes.SHA256())
    )
    cert_pem = cert.public_bytes(serialization.Encoding.PEM)
    key_pem = key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    )
    return cert_pem, key_pem


def is_root_installed() -> bool:
    try:
        completed = subprocess.run(
            ["certutil", "-user", "-store", "Root", "BH3 Local MITM CA"],
            capture_output=True,
            text=True,
            encoding="mbcs",
            errors="replace",
            timeout=8,
        )
        text = (completed.stdout or "") + "\n" + (completed.stderr or "")
        return completed.returncode == 0 and "BH3 Local MITM CA" in text
    except Exception:
        return False


def install_root_cert(log=print) -> str:
    ensure_ca(log)
    if is_root_installed():
        log("CERT", "根证书已在当前用户存储中，无需重复安装。")
        return "already-installed"
    target = CA_CER_PATH if CA_CER_PATH.exists() else CA_CRT_PATH
    log("CERT", "正在安装根证书。若弹出 Windows 安全提示，请点“是”。")
    completed = subprocess.run(
        ["certutil", "-user", "-addstore", "Root", str(target)],
        capture_output=True,
        text=True,
        encoding="mbcs",
        errors="replace",
        timeout=180,
    )
    output = ((completed.stdout or "") + "\n" + (completed.stderr or "")).strip()
    if completed.returncode != 0:
        raise RuntimeError(output or "certutil 安装失败")
    if is_root_installed():
        log("CERT", "根证书已安装到 当前用户\\受信任的根证书颁发机构。")
    else:
        log("CERT", "certutil 已返回，但证书存储里还没看到。可改用下载 ca.cer 双击安装。")
    return output


def install_root_cert_async(log=print) -> None:
    def worker():
        try:
            install_root_cert(log)
        except Exception as exc:
            log("CERT", f"安装失败：{exc}")

    threading.Thread(target=worker, name="bh3-cert-install", daemon=True).start()
