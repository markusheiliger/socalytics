import CryptoKit
import Foundation
import Security

enum BridgeStatus {
    static let ok: Int32 = 0
    static let unavailable: Int32 = -1
    static let keyMissing: Int32 = -2
    static let inaccessible: Int32 = -3
    static let bufferTooSmall: Int32 = -4
    static let softwareNotAllowed: Int32 = -5
    static let invalidArgument: Int32 = -6
    static let failed: Int32 = -7
}

struct KeyInfo {
    var reference: Data
    var x: Data
    var y: Data
    var protection: Int32
}

enum SecureEnclaveKeys {
    static let secureEnclaveTag: UInt8 = 0x53
    static let keychainTag: UInt8 = 0x4B
    static let labelPrefix = "SocAlytics-AnalystManager-"

    static var isAvailable: Bool { SecureEnclave.isAvailable }

    static func create(allowSoftware: Bool) -> (Int32, KeyInfo?) {
        if SecureEnclave.isAvailable {
            do {
                let key = try SecureEnclave.P256.Signing.PrivateKey()
                let raw = key.publicKey.rawRepresentation
                return (BridgeStatus.ok, KeyInfo(
                    reference: Data([secureEnclaveTag]) + key.dataRepresentation,
                    x: raw.prefix(32), y: raw.suffix(32), protection: 1))
            } catch {
                return (BridgeStatus.inaccessible, nil)
            }
        }
        guard allowSoftware else { return (BridgeStatus.softwareNotAllowed, nil) }
        let label = labelPrefix + UUID().uuidString.lowercased()
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecAttrIsExtractable as String: false,
            kSecAttrIsPermanent as String: true,
            kSecAttrLabel as String: label,
        ]
        var error: Unmanaged<CFError>?
        guard let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error) else {
            return (BridgeStatus.inaccessible, nil)
        }
        guard let (x, y) = publicCoordinates(key) else { return (BridgeStatus.failed, nil) }
        return (BridgeStatus.ok, KeyInfo(
            reference: Data([keychainTag]) + Data(label.utf8), x: x, y: y, protection: 2))
    }

    static func open(_ reference: Data) -> (Int32, KeyInfo?) {
        guard let tag = reference.first else { return (BridgeStatus.invalidArgument, nil) }
        let body = Data(reference.dropFirst())
        switch tag {
        case secureEnclaveTag:
            guard let key = try? SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: body) else {
                return (BridgeStatus.keyMissing, nil)
            }
            let raw = key.publicKey.rawRepresentation
            return (BridgeStatus.ok, KeyInfo(reference: reference, x: raw.prefix(32), y: raw.suffix(32), protection: 1))
        case keychainTag:
            let (status, key) = keychainKey(body)
            guard status == BridgeStatus.ok, let key else { return (status, nil) }
            guard let (x, y) = publicCoordinates(key) else { return (BridgeStatus.failed, nil) }
            return (BridgeStatus.ok, KeyInfo(reference: reference, x: x, y: y, protection: 2))
        default:
            return (BridgeStatus.invalidArgument, nil)
        }
    }

    static func sign(_ reference: Data, data: Data) -> (Int32, Data?) {
        guard let tag = reference.first else { return (BridgeStatus.invalidArgument, nil) }
        let body = Data(reference.dropFirst())
        if tag == secureEnclaveTag {
            guard let key = try? SecureEnclave.P256.Signing.PrivateKey(dataRepresentation: body) else {
                return (BridgeStatus.keyMissing, nil)
            }
            guard let signature = try? key.signature(for: data) else { return (BridgeStatus.inaccessible, nil) }
            return (BridgeStatus.ok, signature.rawRepresentation)
        }
        guard tag == keychainTag else { return (BridgeStatus.invalidArgument, nil) }
        let (status, key) = keychainKey(body)
        guard status == BridgeStatus.ok, let key else { return (status, nil) }
        var error: Unmanaged<CFError>?
        guard let der = SecKeyCreateSignature(key, .ecdsaSignatureMessageX962SHA256, data as CFData, &error) else {
            return (BridgeStatus.inaccessible, nil)
        }
        return (BridgeStatus.ok, der as Data)
    }

    static func delete(_ reference: Data) -> Int32 {
        guard let tag = reference.first else { return BridgeStatus.invalidArgument }
        if tag == secureEnclaveTag { return BridgeStatus.ok }
        guard tag == keychainTag else { return BridgeStatus.invalidArgument }
        let label = Data(reference.dropFirst())
        guard String(decoding: label, as: UTF8.self).hasPrefix(labelPrefix) else { return BridgeStatus.invalidArgument }
        switch SecItemDelete(keychainQuery(label) as CFDictionary) {
        case errSecSuccess: return BridgeStatus.ok
        case errSecItemNotFound: return BridgeStatus.keyMissing
        default: return BridgeStatus.inaccessible
        }
    }

    private static func keychainQuery(_ label: Data) -> [String: Any] {
        [
            kSecClass as String: kSecClassKey,
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrLabel as String: String(decoding: label, as: UTF8.self),
        ]
    }

    private static func keychainKey(_ label: Data) -> (Int32, SecKey?) {
        guard String(decoding: label, as: UTF8.self).hasPrefix(labelPrefix) else {
            return (BridgeStatus.invalidArgument, nil)
        }
        var query = keychainQuery(label)
        query[kSecReturnRef as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        switch SecItemCopyMatching(query as CFDictionary, &item) {
        case errSecSuccess: return (BridgeStatus.ok, (item as! SecKey))
        case errSecItemNotFound: return (BridgeStatus.keyMissing, nil)
        default: return (BridgeStatus.inaccessible, nil)
        }
    }

    private static func publicCoordinates(_ privateKey: SecKey) -> (Data, Data)? {
        guard let publicKey = SecKeyCopyPublicKey(privateKey),
              let external = SecKeyCopyExternalRepresentation(publicKey, nil) as Data?,
              external.count == 65, external.first == 0x04 else { return nil }
        return (external.subdata(in: 1..<33), external.subdata(in: 33..<65))
    }
}
