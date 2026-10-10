# SocAlyticsMacBridge

Swift bridge that gives the Analyst Manager (.NET) access to the Secure Enclave, a Keychain fallback key, and `SMAppService` autostart. It builds only on macOS; the Linux runner never builds it.

## Requirements

- macOS 13 or later and the Xcode command-line tools (Swift 5.9 or later).
- A signed `.app`. Secure Enclave and Keychain keys may need a `keychain-access-groups` entitlement; this is pending the Mac spike (plan risk R-02).

## Build

```sh
./build.sh <existing-app-output-directory>
```

This produces a universal arm64/x86_64 `libSocAlyticsMacBridge.dylib`. The Manager loads it from the app directory or the bundle `Frameworks` folder. When it is absent, the `macos-bridge` provider is unavailable.

## C ABI

Every function returns an `int32` status: `0` ok, `-1` unavailable, `-2` key missing, `-3` key inaccessible, `-4` buffer too small, `-5` software key not allowed, `-6` invalid argument, `-7` operation failed. Buffers are caller-allocated.

| Export | Purpose |
| --- | --- |
| `socalytics_se_available()` | `1` when a Secure Enclave is usable, else `0`. |
| `socalytics_key_create(allowSoftware, ref, refCap, refLen*, x[32], y[32], protection*)` | Creates a P-256 key. `protection`: `1` secure-enclave, `2` software (Keychain, only when `allowSoftware` is non-zero). |
| `socalytics_key_open(ref, refLen, x[32], y[32], protection*)` | Opens a key by reference. |
| `socalytics_key_sign(ref, refLen, data, dataLen, sig, sigCap, sigLen*)` | SHA-256 ECDSA: raw `r‖s` (64 bytes) for the Secure Enclave, DER for the Keychain key. |
| `socalytics_key_public(ref, refLen, x[32], y[32])` | Public coordinates. |
| `socalytics_key_delete(ref, refLen)` | Deletes the key (a Secure Enclave blob has no store entry). |
| `socalytics_autostart_register()` / `socalytics_autostart_unregister()` | `SMAppService.mainApp` register or unregister. |
| `socalytics_autostart_status()` | `0` not registered, `1` enabled, `2` requires approval, `3` not found, negative on error. |
| `socalytics_free(ptr)` | Frees a pointer allocated by the bridge. |

A reference is opaque: byte `S` followed by the Secure Enclave `dataRepresentation`, or byte `K` followed by the UTF-8 Keychain label.
