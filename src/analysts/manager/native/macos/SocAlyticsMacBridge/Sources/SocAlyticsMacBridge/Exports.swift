import Foundation

private func copy(_ data: Data, to buffer: UnsafeMutablePointer<UInt8>?, capacity: Int32, length: UnsafeMutablePointer<Int32>?) -> Int32 {
    guard let buffer, let length else { return BridgeStatus.invalidArgument }
    length.pointee = Int32(data.count)
    guard data.count <= Int(capacity) else { return BridgeStatus.bufferTooSmall }
    data.copyBytes(to: buffer, count: data.count)
    return BridgeStatus.ok
}

private func input(_ pointer: UnsafePointer<UInt8>?, _ length: Int32) -> Data? {
    guard let pointer, length >= 0 else { return nil }
    return Data(bytes: pointer, count: Int(length))
}

private func fill(_ info: KeyInfo, x: UnsafeMutablePointer<UInt8>?, y: UnsafeMutablePointer<UInt8>?) -> Int32 {
    guard let x, let y, info.x.count == 32, info.y.count == 32 else { return BridgeStatus.failed }
    info.x.copyBytes(to: x, count: 32)
    info.y.copyBytes(to: y, count: 32)
    return BridgeStatus.ok
}

@_cdecl("socalytics_se_available")
public func socalytics_se_available() -> Int32 { SecureEnclaveKeys.isAvailable ? 1 : 0 }

@_cdecl("socalytics_key_create")
public func socalytics_key_create(
    _ allowSoftware: Int32,
    _ ref: UnsafeMutablePointer<UInt8>?, _ refCap: Int32, _ refLen: UnsafeMutablePointer<Int32>?,
    _ x: UnsafeMutablePointer<UInt8>?, _ y: UnsafeMutablePointer<UInt8>?,
    _ protection: UnsafeMutablePointer<Int32>?
) -> Int32 {
    guard let protection else { return BridgeStatus.invalidArgument }
    let (status, info) = SecureEnclaveKeys.create(allowSoftware: allowSoftware != 0)
    guard status == BridgeStatus.ok, let info else { return status }
    let copied = copy(info.reference, to: ref, capacity: refCap, length: refLen)
    guard copied == BridgeStatus.ok else {
        _ = SecureEnclaveKeys.delete(info.reference)
        return copied
    }
    protection.pointee = info.protection
    return fill(info, x: x, y: y)
}

@_cdecl("socalytics_key_open")
public func socalytics_key_open(
    _ ref: UnsafePointer<UInt8>?, _ refLen: Int32,
    _ x: UnsafeMutablePointer<UInt8>?, _ y: UnsafeMutablePointer<UInt8>?,
    _ protection: UnsafeMutablePointer<Int32>?
) -> Int32 {
    guard let reference = input(ref, refLen), let protection else { return BridgeStatus.invalidArgument }
    let (status, info) = SecureEnclaveKeys.open(reference)
    guard status == BridgeStatus.ok, let info else { return status }
    protection.pointee = info.protection
    return fill(info, x: x, y: y)
}

@_cdecl("socalytics_key_sign")
public func socalytics_key_sign(
    _ ref: UnsafePointer<UInt8>?, _ refLen: Int32,
    _ data: UnsafePointer<UInt8>?, _ dataLen: Int32,
    _ sig: UnsafeMutablePointer<UInt8>?, _ sigCap: Int32, _ sigLen: UnsafeMutablePointer<Int32>?
) -> Int32 {
    guard let reference = input(ref, refLen), let payload = input(data, dataLen) else { return BridgeStatus.invalidArgument }
    let (status, signature) = SecureEnclaveKeys.sign(reference, data: payload)
    guard status == BridgeStatus.ok, let signature else { return status }
    return copy(signature, to: sig, capacity: sigCap, length: sigLen)
}

@_cdecl("socalytics_key_public")
public func socalytics_key_public(
    _ ref: UnsafePointer<UInt8>?, _ refLen: Int32,
    _ x: UnsafeMutablePointer<UInt8>?, _ y: UnsafeMutablePointer<UInt8>?
) -> Int32 {
    guard let reference = input(ref, refLen) else { return BridgeStatus.invalidArgument }
    let (status, info) = SecureEnclaveKeys.open(reference)
    guard status == BridgeStatus.ok, let info else { return status }
    return fill(info, x: x, y: y)
}

@_cdecl("socalytics_key_delete")
public func socalytics_key_delete(_ ref: UnsafePointer<UInt8>?, _ refLen: Int32) -> Int32 {
    guard let reference = input(ref, refLen) else { return BridgeStatus.invalidArgument }
    return SecureEnclaveKeys.delete(reference)
}

@_cdecl("socalytics_autostart_register")
public func socalytics_autostart_register() -> Int32 { Autostart.register() }

@_cdecl("socalytics_autostart_unregister")
public func socalytics_autostart_unregister() -> Int32 { Autostart.unregister() }

@_cdecl("socalytics_autostart_status")
public func socalytics_autostart_status() -> Int32 { Autostart.status() }

@_cdecl("socalytics_free")
public func socalytics_free(_ pointer: UnsafeMutableRawPointer?) { free(pointer) }
