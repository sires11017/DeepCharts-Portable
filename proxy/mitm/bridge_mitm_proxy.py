#!/usr/bin/env python3
"""
Bridge MITM Proxy — intercepts VolumetricaBridge ↔ CQG WebAPI WebSocket.
Patches logon credentials and logs every protobuf message in both directions.
"""
# made by illnoobis
import asyncio
import concurrent.futures
import os
import logging
import ssl
import struct
import sys
import time
from datetime import datetime, timezone, timedelta
from asyncio import get_running_loop
from cryptography import x509
from cryptography.x509.oid import NameOID
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
import config

# ─── Configuration ─────────────────────────────────────────────────────────────
PROXY_PORT          = config.BRIDGE_PROXY_PORT
REAL_CQG_HOST       = config.REAL_CQG_HOST
REAL_CQG_PORT       = config.REAL_CQG_PORT
SNI_HOST            = config.SNI_HOST

TARGET_PRIVATE_LABEL  = config.TARGET_PRIVATE_LABEL
TARGET_CLIENT_APP_ID  = config.TARGET_CLIENT_APP_ID
TARGET_CLIENT_VERSION = config.TARGET_CLIENT_VERSION

CA_DIR   = config.CA_DIR
CA_CERT  = config.CA_CERT
CA_KEY   = config.CA_KEY
CERT     = config.CERT
KEY      = config.KEY_ENV
os.makedirs(config.LOG_DIR, exist_ok=True)
LOGFILE  = os.path.join(config.LOG_DIR, f"bridge_mitm_{datetime.now(timezone.utc).strftime('%Y%m%d_%H%M%S')}.log")

# ─── Logging ───────────────────────────────────────────────────────────────────
log_level = getattr(logging, config.LOG_LEVEL.upper(), logging.DEBUG)
_file_handler   = logging.FileHandler(LOGFILE, encoding="utf-8")
_file_handler.setLevel(logging.DEBUG)
_stream_handler = logging.StreamHandler(sys.stdout)
_stream_handler.setLevel(logging.INFO)

_fmt = logging.Formatter("%(asctime)s [%(levelname)s] %(message)s")
_file_handler.setFormatter(_fmt)
_stream_handler.setFormatter(_fmt)

log = logging.getLogger("bridge-mitm")
log.setLevel(log_level)
log.addHandler(_file_handler)
log.addHandler(_stream_handler)
log.propagate = False

# ─── Protobuf imports ──────────────────────────────────────────────────────────
_reporoot = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
possible_paths = [
    os.path.join(_reporoot, "proxy", "cqg"),
    os.path.join(_reporoot, "cqg_test"),
]

PROTOBUF_AVAILABLE = False
for _path in possible_paths:
    if os.path.exists(_path):
        sys.path.insert(0, os.path.abspath(_path))
        try:
            from WebAPI.webapi_2_pb2 import ClientMsg, ServerMsg, InformationReport
            from WebAPI.user_session_2_pb2 import LogonResult, Ping, Pong
            from WebAPI.historical_2_pb2 import TimeBarReport, TimeBarRequest
            from WebAPI.market_data_2_pb2 import (
                MarketDataSubscription, MarketDataSubscriptionStatus, RealTimeMarketData, Quote
            )
            PROTOBUF_AVAILABLE = True
            log.info(f"[IMPORT] CQG protobufs loaded from: {_path}")
            break
        except Exception as _e:
            log.warning(f"[IMPORT] Failed from {_path}: {_e}")
            if os.path.abspath(_path) in sys.path:
                sys.path.remove(os.path.abspath(_path))

if not PROTOBUF_AVAILABLE:
    log.error("[!] CQG protobufs NOT found — patching and decoding will NOT work!")

# Thread pool for CPU-bound Protobuf parsing — generous size for bursty historical data
_executor = concurrent.futures.ThreadPoolExecutor(max_workers=32)


# ─── CA / Certificate management ───────────────────────────────────────────────
def ensure_ca():
    os.makedirs(CA_DIR, exist_ok=True)
    ca_exists  = os.path.exists(CA_CERT) and os.path.exists(CA_KEY)
    srv_exists = os.path.exists(CERT)    and os.path.exists(KEY)

    if ca_exists and srv_exists:
        log.info("[CA] Using existing CA and server certificates.")
        return

    now = datetime.now(timezone.utc)

    if ca_exists:
        log.info("[CA] Loading existing CA …")
        try:
            with open(CA_CERT, "rb") as f: ca_cert = x509.load_pem_x509_certificate(f.read())
            with open(CA_KEY,  "rb") as f: ca_key  = serialization.load_pem_private_key(f.read(), password=None)
        except Exception as e:
            log.warning(f"[CA] Failed to load existing CA ({e}), regenerating …")
            ca_exists = False

    if not ca_exists:
        log.info("[CA] Generating new CA …")
        ca_key  = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Bridge MITM CA")])
        ca_cert = (
            x509.CertificateBuilder()
            .subject_name(ca_name).issuer_name(ca_name)
            .public_key(ca_key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - timedelta(days=1)).not_valid_after(now + timedelta(days=3650))
            .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
            .sign(ca_key, hashes.SHA256())
        )
        with open(CA_CERT, "wb") as f: f.write(ca_cert.public_bytes(serialization.Encoding.PEM))
        with open(CA_KEY,  "wb") as f: f.write(ca_key.private_bytes(
            serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL, serialization.NoEncryption()))
        log.info(f"[CA] CA saved to {CA_CERT}")

    if not srv_exists:
        log.info("[CA] Generating server certificate …")
        srv_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        csr = (
            x509.CertificateSigningRequestBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, SNI_HOST)]))
            .add_extension(x509.SubjectAlternativeName([x509.DNSName(SNI_HOST), x509.DNSName(config.ADDITIONAL_SNI)]), critical=False)
            .sign(srv_key, hashes.SHA256())
        )
        srv_cert = (
            x509.CertificateBuilder()
            .subject_name(csr.subject).issuer_name(ca_cert.subject)
            .public_key(csr.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - timedelta(days=1)).not_valid_after(now + timedelta(days=3650))
            .add_extension(x509.SubjectAlternativeName([x509.DNSName(SNI_HOST), x509.DNSName(config.ADDITIONAL_SNI)]), critical=False)
            .sign(ca_key, hashes.SHA256())
        )
        with open(CERT, "wb") as f: f.write(srv_cert.public_bytes(serialization.Encoding.PEM))
        with open(KEY,  "wb") as f: f.write(srv_key.private_bytes(
            serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL, serialization.NoEncryption()))
        log.info("[CA] Server certificate generated.")


# ─── WebSocket frame builder ────────────────────────────────────────────────────
def build_ws_frame(opcode: int, payload: bytes, fin: int = 1, mask: bool = True) -> bytes:
    hdr = bytearray([(0x80 if fin else 0) | opcode])
    mask_bit = 0x80 if mask else 0
    plen = len(payload)
    if plen < 126:
        hdr.append(mask_bit | plen)
    elif plen < 65536:
        hdr.extend([mask_bit | 126, (plen >> 8) & 0xFF, plen & 0xFF])
    else:
        hdr.extend([mask_bit | 127] + list(struct.pack(">Q", plen)))
    if mask:
        mk = os.urandom(4)
        hdr.extend(mk)
        return bytes(hdr) + bytes(b ^ mk[i % 4] for i, b in enumerate(payload))
    return bytes(hdr) + payload


# ─── WebSocket frame extractor ──────────────────────────────────────────────────
class FrameBuffer:
    def __init__(self): self.buf = bytearray()
    def feed(self, data: bytes): self.buf.extend(data)

    def extract_frame(self):
        if len(self.buf) < 2: return None
        b0, b1  = self.buf[0], self.buf[1]
        fin     = (b0 >> 7) & 1
        opcode  = b0 & 0x0F
        masked  = (b1 >> 7) & 1
        plen    = b1 & 0x7F
        pos     = 2
        if plen == 126:
            if len(self.buf) < pos + 2: return None
            plen = struct.unpack(">H", self.buf[pos:pos+2])[0]; pos += 2
        elif plen == 127:
            if len(self.buf) < pos + 8: return None
            plen = struct.unpack(">Q", self.buf[pos:pos+8])[0]; pos += 8
        mask_key = None
        if masked:
            if len(self.buf) < pos + 4: return None
            mask_key = bytes(self.buf[pos:pos+4]); pos += 4
        if len(self.buf) < pos + plen: return None
        payload   = bytes(self.buf[pos : pos + plen])
        raw_frame = bytes(self.buf[:pos + plen])
        self.buf  = self.buf[pos + plen:]
        return opcode, masked, mask_key, payload, raw_frame, fin


# ─── Protobuf decoders ──────────────────────────────────────────────────────────
def log_client_msg(payload: bytes, mask_key: bytes):
    """Decode and log a ClientMsg (client→CQG direction)."""
    if not PROTOBUF_AVAILABLE: return
    try:
        raw = bytearray(payload)
        for i in range(len(raw)): raw[i] ^= mask_key[i % 4]
        msg = ClientMsg()
        msg.ParseFromString(bytes(raw))

        if msg.HasField("logon"):
            g = msg.logon
            log.info(f"  [C->S] LOGON: user='{g.user_name}' private_label='{g.private_label}' "
                     f"client_app_id='{g.client_app_id}' version='{g.client_version}'")

        if msg.HasField("logoff"):
            log.info("  [C->S] LOGOFF requested by client")

        if msg.HasField("ping"):
            log.debug("  [C->S] PING")

        if msg.HasField("pong"):
            log.debug("  [C->S] PONG")

        for req in msg.market_data_subscriptions:
            log.info(f"  [C->S] MARKET_DATA_SUBSCRIBE: contract_id={req.contract_id} "
                     f"request_id={req.request_id} level={req.level}")

        for req in msg.time_bar_requests:
            p = req.time_bar_parameters if req.HasField("time_bar_parameters") else None
            if p:
                log.info(f"  [C->S] TIME_BAR_REQUEST: request_id={req.request_id} "
                         f"contract_id={p.contract_id} bar_unit={p.bar_unit} "
                         f"unit_number={p.unit_number} "
                         f"from={p.from_utc_time} to={p.to_utc_time} "
                         f"request_type={req.request_type}")
            else:
                log.info(f"  [C->S] TIME_BAR_REQUEST: request_id={req.request_id} (no params)")

        for req in msg.non_timed_bar_requests:
            log.info(f"  [C->S] NON_TIMED_BAR_REQUEST: request_id={req.request_id}")

        for req in msg.time_and_sales_requests:
            log.info(f"  [C->S] TIME_AND_SALES_REQUEST: request_id={req.request_id}")

        for req in msg.information_requests:
            log.info(f"  [C->S] INFORMATION_REQUEST: id={req.id} subscribe={req.subscribe}")

        for req in msg.trade_subscriptions:
            log.info(f"  [C->S] TRADE_SUBSCRIPTION: id={req.id}")

        for req in msg.order_requests:
            log.info(f"  [C->S] ORDER_REQUEST: id={req.request_id}")

    except Exception as e:
        unmasked = bytearray(payload)
        for i in range(len(unmasked)): unmasked[i] ^= mask_key[i % 4]
        log.warning(f"  [C->S] Could not decode ClientMsg: {e}")
        log.warning(f"  [C->S]   masked_hex={payload.hex()}")
        log.warning(f"  [C->S]   unmasked_hex={bytes(unmasked[:48]).hex()}")


def build_pong_response(payload: bytes, mask_key: bytes):
    """
    Parse a client binary frame and if it contains a Ping (field 107),
    build a ServerMsg Pong response frame. Returns the frame bytes, or None.
    """
    if not PROTOBUF_AVAILABLE:
        return None
    try:
        raw = bytearray(payload)
        for i in range(len(raw)): raw[i] ^= mask_key[i % 4]
        msg = ClientMsg()
        msg.ParseFromString(bytes(raw))
        if not msg.HasField("ping"):
            return None
        ping = msg.ping
        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        sm = ServerMsg()
        sm.pong.token = ping.token if ping.HasField("token") else ""
        sm.pong.ping_utc_time = ping.ping_utc_time
        sm.pong.pong_utc_time = now_ms
        log.debug("  [PATCH] Injecting local PONG for client PING")
        return build_ws_frame(2, sm.SerializeToString(), fin=1, mask=False)
    except Exception as e:
        log.error(f"  [PATCH] Failed to build PONG response: {e}")
        return None


# ─── Logon patcher ─────────────────────────────────────────────────────────────
def patch_logon_protobuf(payload: bytes, mask_key: bytes, fin: int, opcode: int):
    raw = bytearray(payload)
    for i in range(len(raw)): raw[i] ^= mask_key[i % 4]
    msg = ClientMsg()
    try:
        msg.ParseFromString(bytes(raw))
        if msg.HasField("logon"):
            old_pl = msg.logon.private_label
            old_ci = msg.logon.client_app_id
            msg.logon.private_label  = TARGET_PRIVATE_LABEL
            msg.logon.client_app_id  = TARGET_CLIENT_APP_ID
            if msg.logon.client_version:
                msg.logon.client_version = TARGET_CLIENT_VERSION
            log.info("  [PATCH] *** LOGON INTERCEPTED AND PATCHED ***")
            log.info(f"  [PATCH] private_label : '{old_pl}' -> '{TARGET_PRIVATE_LABEL}'")
            log.info(f"  [PATCH] client_app_id : '{old_ci}' -> '{TARGET_CLIENT_APP_ID}'")
            log.info(f"  [PATCH] client_version: -> '{TARGET_CLIENT_VERSION}'")
            return build_ws_frame(opcode, msg.SerializeToString(), fin=fin, mask=True)
    except Exception as e:
        log.error(f"  [PATCH] Failed to parse/patch logon: {e}")
    return None


# ─── Client → CQG forwarder ────────────────────────────────────────────────────
async def forward_client_to_cqg(client_r, cqg_w, client_w, initial_remaining=b"", http_done=False, is_historical=False):
    buf = FrameBuffer()
    if initial_remaining:
        buf.feed(initial_remaining)
    try:
        while True:
            while True:
                frame = buf.extract_frame()
                if not frame: break
                opcode, masked, mask_key, payload, raw_frame, fin = frame

                if opcode == 8:
                    log.info("  [C->S] WebSocket CLOSE frame — client is disconnecting.")
                    cqg_w.write(raw_frame)
                    await cqg_w.drain()
                    return
                elif opcode == 9:
                    log.debug("  [C->S] PING — responding locally with PONG")
                    pong_frame = build_ws_frame(10, payload, fin=1, mask=False)
                    client_w.write(pong_frame)
                    await client_w.drain()
                    continue
                elif opcode == 10:
                    log.debug("  [C->S] PONG")
                    cqg_w.write(raw_frame); await cqg_w.drain(); continue

                if opcode == 2 and masked:
                    if not is_historical:
                        log_client_msg(payload, mask_key)
                        pong_frame = build_pong_response(payload, mask_key)
                        if pong_frame:
                            client_w.write(pong_frame)
                            await client_w.drain()
                        patched = patch_logon_protobuf(payload, mask_key, fin, opcode)
                        cqg_w.write(patched if patched else raw_frame)
                    else:
                        cqg_w.write(raw_frame)
                else:
                    cqg_w.write(raw_frame)

            chunk = await client_r.read(65536)
            if not chunk:
                log.info("  [C->S] Client closed connection.")
                break
            buf.feed(chunk)

            if not http_done:
                if b"\r\n\r\n" not in buf.buf:
                    continue
                idx = buf.buf.find(b"\r\n\r\n") + 4
                http_part = bytes(buf.buf[:idx])
                log.info(f"  [C->S] HTTP Upgrade: {http_part.splitlines()[0].decode(errors='replace')}")
                cqg_w.write(http_part)
                await cqg_w.drain()
                buf.buf = buf.buf[idx:]
                http_done = True
                log.info("  [CLIENT->CQG] HTTP handshake forwarded.")

            await cqg_w.drain()

    except Exception as e:
        log.error(f"  [C->S] Error: {e}")


async def run_in_thread(func, *args):
    loop = get_running_loop()
    return await loop.run_in_executor(_executor, func, *args)


# ─── CQG → Client forwarder ────────────────────────────────────────────────────
async def forward_cqg_to_client(cqg_r, client_w, is_historical=False):
    http_done = False
    buf = FrameBuffer()
    try:
        while True:
            chunk = await cqg_r.read(65536)
            if not chunk:
                log.info("  [S->C] CQG server closed connection.")
                break
            buf.feed(chunk)

            if not http_done:
                if b"\r\n\r\n" not in buf.buf:
                    continue
                idx = buf.buf.find(b"\r\n\r\n") + 4
                http_part = bytes(buf.buf[:idx])
                log.info(f"  [S->C] HTTP Response: {http_part.splitlines()[0].decode(errors='replace')}")
                client_w.write(http_part)
                await client_w.drain()
                buf.buf = buf.buf[idx:]
                http_done = True
                log.info("  [CQG->CLIENT] HTTP response forwarded.")

            while True:
                frame = buf.extract_frame()
                if not frame:
                    break
                opcode, masked, mask_key, payload, raw_frame, fin = frame

                if opcode == 8:
                    log.warning("  [S->C] WebSocket CLOSE frame from CQG.")
                    client_w.write(raw_frame)
                    await client_w.drain()
                    return
                elif opcode == 9:
                    log.debug("  [S->C] PING from server")
                    client_w.write(raw_frame)
                    await client_w.drain()
                    continue
                elif opcode == 10:
                    log.debug("  [S->C] PONG from server")
                    client_w.write(raw_frame)
                    await client_w.drain()
                    continue

                if opcode == 2:
                    if not is_historical:
                        patched = await run_in_thread(process_and_patch_server_msg, payload, fin, opcode)
                        client_w.write(patched if patched is not None else raw_frame)
                    else:
                        client_w.write(raw_frame)
                else:
                    client_w.write(raw_frame)

            await client_w.drain()

    except Exception as e:
        log.error(f"  [S->C] Error: {e}")


def process_and_patch_server_msg(payload: bytes, fin: int, opcode: int):
    """
    Called inside background thread. Parses the message once, logs it, patches if needed.
    """
    if not PROTOBUF_AVAILABLE:
        return None
    try:
        msg = ServerMsg()
        msg.ParseFromString(payload)

        log_server_msg_parsed(msg)

        patched = False
        for tsr in msg.time_and_sales_reports:
            if tsr.result_code == 0 and len(tsr.quotes) > 0:
                has_bba = any(q.type in (1, 2, 3, 4) for q in tsr.quotes)
                if has_bba:
                    log.debug("  [PATCH] TimeAndSales already has BBA quotes. Skipping patch.")
                    continue

                patched = True
                log.debug(f"  [PATCH] Injecting BBA quotes into TimeAndSales report (original ticks={len(tsr.quotes)})")

                new_quotes = []
                last_price = None

                for quote in tsr.quotes:
                    if quote.type == 0:
                        P = quote.scaled_price

                        is_buy = True
                        if quote.HasField("sales_condition"):
                            sc = quote.sales_condition
                            if sc in (1, 5):
                                is_buy = False
                            elif sc in (2, 4):
                                is_buy = True
                        elif last_price is not None:
                            if P > last_price:
                                is_buy = True
                            elif P < last_price:
                                is_buy = False

                        last_price = P

                        bid_quote = Quote()
                        bid_quote.type = 1
                        bid_quote.scaled_price = P if is_buy else (P - 25)
                        if quote.HasField("quote_utc_time"):
                            bid_quote.quote_utc_time = quote.quote_utc_time
                        if quote.HasField("price_yield"):
                            bid_quote.price_yield = quote.price_yield
                        new_quotes.append(bid_quote)

                        ask_quote = Quote()
                        ask_quote.type = 2
                        ask_quote.scaled_price = (P + 25) if is_buy else P
                        if quote.HasField("quote_utc_time"):
                            ask_quote.quote_utc_time = quote.quote_utc_time
                        if quote.HasField("price_yield"):
                            ask_quote.price_yield = quote.price_yield
                        new_quotes.append(ask_quote)

                        new_quotes.append(quote)
                    else:
                        new_quotes.append(quote)

                del tsr.quotes[:]
                tsr.quotes.extend(new_quotes)
                log.debug(f"  [PATCH] Injection complete (new ticks={len(tsr.quotes)})")

        if patched:
            return build_ws_frame(opcode, msg.SerializeToString(), fin=fin, mask=False)

    except Exception as e:
        log.error(f"  [PATCH] Thread failed to process/patch ServerMsg: {e}")
    return None


def log_server_msg_parsed(msg: ServerMsg):
    """Logs the parsed message in the background thread."""
    try:
        if msg.HasField("logon_result"):
            r = msg.logon_result
            level = logging.INFO if r.result_code == 0 else logging.ERROR
            log.log(level, f"  [S->C] LOGON_RESULT: code={r.result_code} "
                           f"text='{r.text_message}' base_time='{r.base_time}' "
                           f"user_id={r.user_id} "
                           f"proto={r.protocol_version_major}.{r.protocol_version_minor}")

        if msg.HasField("logged_off"):
            lo = msg.logged_off
            log.warning(f"  [S->C] LOGGED_OFF: code={lo.result_code} text='{lo.text_message}'")

        if msg.HasField("ping"):
            log.debug("  [S->C] PING from server")
        if msg.HasField("pong"):
            log.debug("  [S->C] PONG from server")

        for um in msg.user_messages:
            log.info(f"  [S->C] USER_MESSAGE: type={um.message_type} "
                     f"subject='{um.subject}' text='{um.text}'")

        for ir in msg.information_reports:
            log.info(f"  [S->C] INFORMATION_REPORT: id={ir.id} "
                     f"status={ir.status_code} complete={ir.is_report_complete} "
                     f"text='{ir.text_message}'")
            if ir.HasField("symbol_resolution_report"):
                srr = ir.symbol_resolution_report
                try:
                    cm = srr.contract_metadata
                    log.info(f"    SYMBOL: contract_id={cm.contract_id} "
                             f"symbol='{cm.contract_symbol}' cqg='{cm.cqg_contract_symbol}' "
                             f"desc='{cm.description}' "
                             f"tick={cm.tick_size} tickval={cm.tick_value}")
                except Exception as e:
                    log.debug(f"    SYMBOL decode skipped: {e}")
            if ir.HasField("accounts_report"):
                for brok in ir.accounts_report.brokerages:
                    log.info(f"    BROKERAGE: id={brok.id} name='{brok.name}'")
                    for ss in brok.sales_series:
                        for acct in ss.accounts:
                            try:
                                brok_id = acct.brokerage_account_id
                            except Exception:
                                brok_id = '?'
                            log.info(f"      ACCOUNT: id={acct.account_id} "
                                     f"name='{acct.name}' brok_id='{brok_id}'")

        for s in msg.market_data_subscription_statuses:
            level = logging.INFO if s.status_code == 0 else logging.WARNING
            log.log(level, f"  [S->C] MKT_DATA_STATUS: contract_id={s.contract_id} "
                           f"status_code={s.status_code} level={s.level} "
                           f"text='{s.text_message}'")

        for rtd in msg.real_time_market_data:
            log.debug(f"  [S->C] REAL_TIME_DATA: contract_id={rtd.contract_id} "
                      f"snapshot={rtd.is_snapshot} quotes={len(rtd.quotes)}")

        for tbr in msg.time_bar_reports:
            level = logging.INFO if tbr.status_code == 0 else logging.ERROR
            log.log(level, f"  [S->C] TIME_BAR_REPORT: request_id={tbr.request_id} "
                           f"status={tbr.status_code} bars={len(tbr.time_bars)} "
                           f"complete={tbr.is_report_complete} "
                           f"reached_start={tbr.reached_start_of_data} "
                           f"text='{tbr.text_message}'")

        for nbr in msg.non_timed_bar_reports:
            level = logging.INFO if nbr.status_code == 0 else logging.ERROR
            log.log(level, f"  [S->C] NON_TIMED_BAR_REPORT: request_id={nbr.request_id} "
                           f"status={nbr.status_code} bars={len(nbr.non_timed_bars)} "
                           f"complete={nbr.is_report_complete} text='{nbr.text_message}'")

        for tsr in msg.time_and_sales_reports:
            log.debug(f"  [S->C] TIME_AND_SALES_REPORT: request_id={tsr.request_id} "
                      f"result_code={tsr.result_code} ticks={len(tsr.quotes)} "
                      f"complete={tsr.is_report_complete} text='{tsr.text_message}'")

        for os_ in msg.order_statuses:
            log.info(f"  [S->C] ORDER_STATUS: order_id={os_.order_id}")
        for ps in msg.position_statuses:
            log.info(f"  [S->C] POSITION_STATUS: account_id={ps.account_id}")

    except Exception as e:
        log.error(f"  [S->C] Thread logger failed: {e}")


# ─── Server PING watchdog ──────────────────────────────────────────────────────
async def server_ping_watchdog(client_w, last_msg_time, stop_event):
    """
    Periodically inject WebSocket PING frames to the client if no server message
    has been received for 45 seconds. This prevents the client from timing out
    if the real CQG server stops sending data.
    """
    PING_INTERVAL = 30.0
    TIMEOUT = 45.0
    while not stop_event.is_set():
        await asyncio.sleep(PING_INTERVAL)
        elapsed = time.monotonic() - last_msg_time[0]
        if elapsed > TIMEOUT:
            log.info(f"  [WATCHDOG] No server message for {elapsed:.0f}s — injecting server PING frame to client")
            try:
                client_w.write(build_ws_frame(9, b"", fin=1, mask=False))
                await client_w.drain()
            except Exception as e:
                log.warning(f"  [WATCHDOG] Failed to inject PING: {e}")
                break


# ─── Connection handler ─────────────────────────────────────────────────────────
def is_historical_route(sni, path):
    """Determine if this connection should be routed to the local mock historical server."""
    if sni and ("historical" in sni or "deepcharts" in sni):
        return True
    if path == "/":
        return True
    return False


async def handle(client_r, client_w):
    peer = client_w.get_extra_info("peername")
    log.info(f"[+] Client connected from {peer}")

    sslobj = client_w.get_extra_info("ssl_object")
    sni = sslobj.server_hostname if sslobj else None
    log.info(f"[SNI] Requested SNI server_hostname: '{sni}'")

    handshake, remaining = b"", b""
    path = ""
    http_done = False
    try:
        initial_buf = bytearray()
        while b"\r\n\r\n" not in initial_buf and len(initial_buf) < 8192:
            chunk = await asyncio.wait_for(client_r.read(4096), timeout=config.BRIDGE_HTTP_TIMEOUT)
            if not chunk:
                break
            initial_buf.extend(chunk)
        if b"\r\n\r\n" in initial_buf:
            idx = initial_buf.find(b"\r\n\r\n") + 4
            handshake = bytes(initial_buf[:idx])
            remaining = bytes(initial_buf[idx:])
            http_done = True
            first_line = handshake.splitlines()[0].decode(errors='replace')
            log.info(f"[+] Client request line: {first_line}")
            parts = first_line.split()
            if len(parts) > 1:
                path = parts[1]
    except Exception as e:
        log.warning(f"[-] Failed to read HTTP handshake: {e}")
        if initial_buf:
            remaining = bytes(initial_buf)

    is_historical = is_historical_route(sni, path)

    try:
        if is_historical:
            log.info(f"[+] Routing '{sni or 'None'}' locally to mock Volumetrica Historical Server on port {config.BRIDGE_LOCAL_MOCK_PORT} (path='{path}')")
            cqg_r, cqg_w = await asyncio.open_connection(config.BRIDGE_LOCAL_MOCK_HOST, config.BRIDGE_LOCAL_MOCK_PORT, ssl=None)
            log.info(f"[+] Local Volumetrica mock connection established ({config.BRIDGE_LOCAL_MOCK_HOST}:{config.BRIDGE_LOCAL_MOCK_PORT})")
        else:
            log.info(f"[+] Routing '{sni or 'None'}' upstream to CQG ({REAL_CQG_HOST}:{REAL_CQG_PORT}) (path='{path}')")
            cqg_r, cqg_w = await asyncio.open_connection(
                REAL_CQG_HOST, REAL_CQG_PORT, ssl=client_ctx, server_hostname=SNI_HOST)
            log.info(f"[+] Upstream CQG connection established ({REAL_CQG_HOST}:{REAL_CQG_PORT})")
    except Exception as e:
        log.error(f"[!] Cannot establish upstream/mock connection: {e}")
        client_w.close()
        return

    if handshake:
        cqg_w.write(handshake)
        await cqg_w.drain()

    # Shared mutable state for watchdog
    last_server_msg_time = [time.monotonic()]
    stop_event = asyncio.Event()

    t1 = asyncio.create_task(forward_client_to_cqg(client_r, cqg_w, client_w, initial_remaining=remaining, http_done=http_done, is_historical=is_historical))
    t2 = asyncio.create_task(forward_cqg_to_client(cqg_r, client_w, is_historical=is_historical))

    # Start watchdog if this is a CQG (non-historical) connection
    t3 = None
    if not is_historical:
        t3 = asyncio.create_task(server_ping_watchdog(client_w, last_server_msg_time, stop_event))

    all_tasks = [t1, t2]
    if t3:
        all_tasks.append(t3)

    done, pending = await asyncio.wait(all_tasks, return_when=asyncio.FIRST_COMPLETED)
    stop_event.set()
    for p in pending:
        p.cancel()
    await asyncio.gather(*all_tasks, return_exceptions=True)

    for w in (client_w, cqg_w):
        try: w.close(); await w.wait_closed()
        except: pass

    log.info(f"[-] Disconnected {peer}")


# ─── Main ───────────────────────────────────────────────────────────────────────
async def main():
    global client_ctx
    ensure_ca()

    server_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server_ctx.load_cert_chain(CERT, KEY)

    client_ctx = ssl.create_default_context()
    client_ctx.check_hostname = False
    client_ctx.verify_mode    = ssl.CERT_NONE

    server = await asyncio.start_server(handle, config.BRIDGE_PROXY_BIND_HOST, PROXY_PORT, ssl=server_ctx)
    log.info("=" * 60)
    log.info(f"[*] Bridge MITM Proxy listening on {config.BRIDGE_PROXY_BIND_HOST}:{PROXY_PORT}")
    log.info(f"[*] Upstream: {REAL_CQG_HOST}:{REAL_CQG_PORT} (SNI={SNI_HOST})")
    log.info(f"[*] Full log: {LOGFILE}")
    log.info("=" * 60)

    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        log.info("[*] Shutdown")
