# Pairing protocol

Pairing exchanges and pins certificates between a client and a host using a 6-digit PIN
shown in the host tray. The PIN is verified with SPAKE2, a password-authenticated key
exchange, so an attacker who intercepts or relays the exchange gets one online guess per
attempt and cannot brute-force the PIN offline. Certificates are bound into the exchange,
so a successful run proves both sides saw the same two certificates.

After pairing, every API call uses mutual TLS. The host accepts a client certificate only
if its SHA-256 fingerprint belongs to a paired device. The client accepts the host only if
the presented certificate matches the pinned one.

## Flow

1. The client creates a P-256 key pair and a self-signed certificate (once per install).
2. The client calls `POST /pairing/requests` with its device name and certificate. TLS uses
   the host certificate, which the client records but cannot yet trust.
3. The host generates a PIN, a secret scalar `y`, and its share `Y`, shows the PIN in the
   tray, and returns `pairingId`, `hostShare = Y`, and `expiresAt`.
4. The user types the PIN into the client. The client computes `x`, `X`, the shared
   element `K`, and both confirmation values, then calls
   `POST /pairing/requests/{pairingId}/confirm` with `clientShare = X` and
   `clientConfirmation = cA`.
5. The host computes `K` and `cA`. On a match it stores the device and returns `deviceId`,
   `hostId`, `hostCertificatePem`, and `hostConfirmation = cB`. On a mismatch it counts a
   failed attempt.
6. The client verifies `cB` and that `hostCertificatePem` is the certificate it saw in TLS,
   then pins it.

A request expires 120 seconds after creation or after 5 failed confirmations. Only one
request can be pending at a time; a new request with the same client certificate replaces
it, and the client can cancel with `DELETE /pairing/requests/{pairingId}`. Every request
gets a new PIN, so replacing a request gives no extra guesses against an earlier PIN. The
host rejects pairing while no tray app is connected, because the user could not see the PIN.

## Group

All arithmetic uses the 3072-bit MODP group from RFC 3526, section 4:

- `p` is the RFC 3526 3072-bit prime, `g = 2`.
- `q = (p - 1) / 2` is prime. Shares live in the subgroup of order `q` (the quadratic
  residues mod `p`), which `g` generates because `p mod 8 = 7`.
- Integers are encoded as 384-byte unsigned big-endian values, left-padded with zeros.

### Fixed elements M and N

`M` and `N` are derived so that nobody knows their discrete logarithm:

```
expand(label) = SHA-512(label || 0x00) || SHA-512(label || 0x01) || ... || SHA-512(label || 0x06)
hash_to_group(label) = (int(expand(label)) mod p)^2 mod p
M = hash_to_group("HyperHarbor SPAKE2 MODP-3072 M")
N = hash_to_group("HyperHarbor SPAKE2 MODP-3072 N")
```

`expand` produces 448 bytes, read as an unsigned big-endian integer. Labels are ASCII.

### Password scalar w

```
w = int(SHA-512("HyperHarbor SPAKE2 w" || 0x00 || pairingId || 0x00 || PIN)) mod q
```

`pairingId` is the lowercase canonical UUID string and `PIN` is the 6-digit string, both
encoded as ASCII.

## Exchange

```
x, y      uniform random in [1, q - 1]
X = g^x * M^w mod p                 (client share)
Y = g^y * N^w mod p                 (host share)
client:   K = (Y * N^(q - w))^x mod p
host:     K = (X * M^(q - w))^y mod p
```

`N^(q - w)` is the inverse of `N^w` because `N` has order `q`.

A received share `E` must satisfy `1 < E < p - 1` and `E^q mod p = 1`. Otherwise the
request is rejected without counting an attempt.

## Transcript and confirmation

The transcript `TT` concatenates these fields, each preceded by its length as an 8-byte
little-endian integer:

1. `"HyperHarbor-PAKE-v1"` (ASCII)
2. `pairingId` (lowercase canonical UUID, ASCII)
3. `SHA-256(clientCertDer)`
4. `SHA-256(hostCertDer)`, where the client uses the certificate presented in TLS
5. `X` (384 bytes)
6. `Y` (384 bytes)
7. `K` (384 bytes)
8. `w` (384 bytes)

```
Ka  = SHA-256(TT)
KcA = HMAC-SHA256(Ka, "client confirmation")
KcB = HMAC-SHA256(Ka, "host confirmation")
cA  = HMAC-SHA256(KcA, TT)          (clientConfirmation)
cB  = HMAC-SHA256(KcB, TT)          (hostConfirmation)
```

Confirmation values are compared in constant time. The PIN, `w`, `x`, `y`, and `K` are
never logged or stored.

## Test vectors

`host/tests/Host.Tests/Pairing/Spake2Vectors.json` holds fixed inputs and expected outputs.
The .NET tests and the Rust tests (`client/src-tauri/src/spake2.rs`) both check them, which
proves the two implementations agree byte for byte.
