"""
vol_hist_server.py — Mock Volumetrica Historical Server

Listens on port 12010 via WebSocket, intercepts Deepchart.exe's connection.
Responds with {"IsComplete":true} as a valid compressed protobuf so Deepchart doesn't hang/time out.
"""
# made by illnoobis
import asyncio, logging, datetime, json, subprocess, zlib, os
import websockets
import config

os.makedirs(config.LOG_DIR, exist_ok=True)
LOG_FILE = os.path.join(config.LOG_DIR, f"vol_hist_{datetime.datetime.now().strftime('%Y%m%d_%H%M%S')}.log")

logging.basicConfig(
    level=getattr(logging, config.LOG_LEVEL.upper(), logging.DEBUG),
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[logging.StreamHandler(), logging.FileHandler(LOG_FILE)],
)
log = logging.getLogger("vol-hist")

# Cache for PowerShell signatures (session_key -> signature)
_sig_cache = {}


def encode_varint(value):
    """Encode an integer as a protobuf varint."""
    res = bytearray()
    while True:
        towrite = value & 0x7f
        value >>= 7
        if value:
            res.append(towrite | 0x80)
        else:
            res.append(towrite)
            break
    return bytes(res)


def get_powershell_signature(key):
    """
    Executes standard .NET Cryptography in PowerShell to encrypt "-" with the session key.
    This exactly replicates the client's custom Rijndael-256 CBC decryption check.
    """
    ps_script = f"""
$plainBytes = [System.Text.Encoding]::UTF8.GetBytes('-')
$salt = New-Object Byte[] 32
$iv = New-Object Byte[] 32
$rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
$rng.GetBytes($salt)
$rng.GetBytes($iv)
$pbkdf2 = [System.Security.Cryptography.Rfc2898DeriveBytes]::new('{key}', $salt, 1230)
$keyBytes = $pbkdf2.GetBytes(32)
$rijndael = [System.Security.Cryptography.RijndaelManaged]::new()
$rijndael.KeySize = 256
$rijndael.BlockSize = 256
$rijndael.Mode = [System.Security.Cryptography.CipherMode]::CBC
$rijndael.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
$rijndael.Key = $keyBytes
$rijndael.IV = $iv
$encryptor = $rijndael.CreateEncryptor()
$ms = [System.IO.MemoryStream]::new()
$cs = [System.Security.Cryptography.CryptoStream]::new($ms, $encryptor, [System.Security.Cryptography.CryptoStreamMode]::Write)
$cs.Write($plainBytes, 0, $plainBytes.Length)
$cs.FlushFinalBlock()
$encryptedBytes = $ms.ToArray()
$cs.Dispose()
$ms.Dispose()
$rijndael.Dispose()
$pbkdf2.Dispose()
$rng.Dispose()
$result = New-Object Byte[] (32 + 32 + $encryptedBytes.Length)
[System.Buffer]::BlockCopy($salt, 0, $result, 0, 32)
[System.Buffer]::BlockCopy($iv, 0, $result, 32, 32)
[System.Buffer]::BlockCopy($encryptedBytes, 0, $result, 64, $encryptedBytes.Length)
[System.Convert]::ToBase64String($result)
"""
    try:
        log.info(f"  [SIGN] Invoking PowerShell to encrypt '-' with session key...")
        res = subprocess.run(["powershell", "-Command", ps_script], capture_output=True, text=True, check=True)
        sig = res.stdout.strip()
        log.info(f"  [SIGN] Signature generated: {sig[:32]}...")
        return sig
    except Exception as e:
        log.error(f"  [SIGN] Failed to generate PowerShell signature: {e}")
        return ""


def build_keepalive():
    inner_bytes = b'\x20\x01\x2a\x00'
    outer_bytes = b'\x0a' + encode_varint(len(inner_bytes)) + inner_bytes
    compressor = zlib.compressobj(level=9, method=zlib.DEFLATED, wbits=-15)
    return compressor.compress(outer_bytes) + compressor.flush()

async def handle_client(ws):
    addr = ws.remote_address
    log.info(f"[+] WS client connected from {addr}")
    use_compression = False

    last_activity = asyncio.get_event_loop().time()
    last_session_key = None
    keepalive_interval = 15

    async def keepalive_loop():
        nonlocal last_activity
        while True:
            await asyncio.sleep(keepalive_interval)
            idle = asyncio.get_event_loop().time() - last_activity
            if idle >= keepalive_interval:
                try:
                    ka = build_keepalive()
                    await ws.send(ka)
                    log.info(f"  [KEEPALIVE] Sent keepalive ({len(ka)} bytes, idle={idle:.0f}s)")
                except websockets.ConnectionClosed:
                    break
                except Exception as e:
                    log.warning(f"  [KEEPALIVE] Send failed: {e}")
                    break

    async def respond(session_key):
        if session_key:
            # Use cached signature if available
            if session_key in _sig_cache:
                sig = _sig_cache[session_key]
                log.info(f"  [SIGN] Using cached signature for session")
            else:
                sig = get_powershell_signature(session_key)
                if sig:
                    _sig_cache[session_key] = sig

            if not sig:
                log.warning("  [RESPOND] Signature generation failed — sending unsigned fallback")
                compressed = build_keepalive()
                await ws.send(compressed)
                return

            sig_bytes = sig.encode('ascii')
            inner_bytes = b'\x20\x01\x2a' + encode_varint(len(sig_bytes)) + sig_bytes

            outer_bytes = b'\x0a' + encode_varint(len(inner_bytes)) + inner_bytes

            compressor = zlib.compressobj(level=9, method=zlib.DEFLATED, wbits=-15)
            compressed = compressor.compress(outer_bytes) + compressor.flush()

            log.info(f"  [SEND] Sending compressed protobuf response ({len(compressed)} bytes)...")
            await ws.send(compressed)
        else:
            log.warning("  [SESSION KEY] No session key — sending keepalive fallback.")
            compressed = build_keepalive()
            await ws.send(compressed)

    try:
        keepalive_task = asyncio.create_task(keepalive_loop())

        try:
            async for message in ws:
                last_activity = asyncio.get_event_loop().time()

                if isinstance(message, bytes):
                    log.info(f"  [BINARY] Received {len(message)} bytes")
                    try:
                        decompressed = zlib.decompress(message, -15)
                        log.info(f"  [BINARY] Decompressed: {len(decompressed)} bytes")
                    except Exception as ex:
                        log.error(f"  [BINARY] Failed to decompress raw request: {ex}")
                        continue

                    session_key = None
                    idx = decompressed.find(b'\x42\x5f')
                    if idx != -1:
                        session_key = decompressed[idx+2 : idx+2+95].decode('ascii', errors='ignore')
                        log.info(f"  [SESSION KEY] Extracted session key: '{session_key}'")
                    else:
                        log.warning("  [SESSION KEY] Marker 0x425f not found in decompressed request.")

                    if session_key:
                        last_session_key = session_key
                    await respond(session_key or last_session_key)

                else:
                    log.info(f"  [TEXT] Received: {message[:500]}")
                    if message.strip() == "compress":
                        use_compression = True
                        log.info("  [COMPRESSION] Handshake 'compress' received. Compression enabled.")
                        continue

        except websockets.ConnectionClosed as e:
            log.info(f"  [CLOSED] {e.code} {e.reason}")
        finally:
            keepalive_task.cancel()
            try:
                await keepalive_task
            except asyncio.CancelledError:
                pass

    except Exception as e:
        log.error(f"  [ERROR] {e}")
    finally:
        log.info(f"[-] {addr} disconnected")


async def process_request(connection, request):
    """Handle any connection that fails the WebSocket handshake gracefully."""
    return None  # None = continue with normal WebSocket handshake


async def main():
    log.info("=" * 60)
    log.info(f"[*] Volumetrica Historical Mock Server on ws://{config.VOL_HIST_HOST}:{config.VOL_HIST_PORT}")
    log.info(f"[*] Full log: {LOG_FILE}")
    log.info("=" * 60)
    log.info("Make sure hosts file has:")
    log.info(f"  {config.VOL_HIST_HOSTS_ENTRY}")
    log.info("=" * 60)

    logging.getLogger("websockets.server").setLevel(logging.WARNING)

    async with websockets.serve(
        handle_client,
        config.VOL_HIST_HOST,
        config.VOL_HIST_PORT,
        process_request=process_request,
        ping_interval=10,
        ping_timeout=5,
        close_timeout=30,
    ):
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())
