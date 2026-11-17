import Crypto
import Foundation
import NIOSSH
import ServerKilnCore

/// A client private key the SSH client can sign with. RSA is not supported
/// for client authentication (swift-nio-ssh cannot sign with it).
public enum SSHPrivateKey: Sendable {
    case ed25519(Curve25519.Signing.PrivateKey)
    case p256(P256.Signing.PrivateKey)
    case p384(P384.Signing.PrivateKey)
    case p521(P521.Signing.PrivateKey)

    var nio: NIOSSHPrivateKey {
        switch self {
        case let .ed25519(k): return NIOSSHPrivateKey(ed25519Key: k)
        case let .p256(k): return NIOSSHPrivateKey(p256Key: k)
        case let .p384(k): return NIOSSHPrivateKey(p384Key: k)
        case let .p521(k): return NIOSSHPrivateKey(p521Key: k)
        }
    }

    /// `ssh-ed25519 AAAA…` / `ecdsa-sha2-nistp256 AAAA…`, for `authorized_keys`.
    public var openSSHPublicKey: String { String(openSSHPublicKey: nio.publicKey) }

    public var typeName: String {
        switch self {
        case .ed25519: return "Ed25519"
        case .p256: return "ECDSA P-256"
        case .p384: return "ECDSA P-384"
        case .p521: return "ECDSA P-521"
        }
    }
}

/// How the client proves who it is: one key, or a password.
public enum SSHCredential: Sendable {
    case key(SSHPrivateKey)
    case password(String)

    public static func identity(_ identity: SSHIdentity) -> SSHCredential { .key(.ed25519(identity.key)) }
}

/// Keychain-friendly encoding of a credential.
public struct StoredCredential: Codable, Hashable, Sendable {
    /// `phoneKey`: no secret of its own, the iPhone's shared key (see
    /// `PhoneKey` in ServerKilnSync) is used.
    public enum Kind: String, Codable, Sendable { case password, ed25519, p256, p384, p521, phoneKey }
    public var kind: Kind
    public var data: Data

    public init(_ credential: SSHCredential) {
        switch credential {
        case let .password(p): (kind, data) = (.password, Data(p.utf8))
        case let .key(.ed25519(k)): (kind, data) = (.ed25519, k.rawRepresentation)
        case let .key(.p256(k)): (kind, data) = (.p256, k.rawRepresentation)
        case let .key(.p384(k)): (kind, data) = (.p384, k.rawRepresentation)
        case let .key(.p521(k)): (kind, data) = (.p521, k.rawRepresentation)
        }
    }

    public static let phoneKey = StoredCredential(kind: .phoneKey, data: Data())

    init(kind: Kind, data: Data) {
        self.kind = kind
        self.data = data
    }

    public func credential() throws -> SSHCredential {
        switch kind {
        case .phoneKey: throw SSHKeyImport.ImportError.unrecognized
        case .password: return .password(String(decoding: data, as: UTF8.self))
        case .ed25519: return .key(.ed25519(try .init(rawRepresentation: data)))
        case .p256: return .key(.p256(try .init(rawRepresentation: data)))
        case .p384: return .key(.p384(try .init(rawRepresentation: data)))
        case .p521: return .key(.p521(try .init(rawRepresentation: data)))
        }
    }
}

/// Reads private keys the user already has: OpenSSH (`BEGIN OPENSSH
/// PRIVATE KEY`, unencrypted), and PEM / PKCS#8 ECDSA or Ed25519.
public enum SSHKeyImport {
    public enum ImportError: Error, LocalizedError, Equatable {
        case unrecognized
        case encrypted
        case rsa
        case unsupported(String)

        public var errorDescription: String? {
            switch self {
            case .unrecognized: return String(appLocalized: "无法识别的私钥格式")
            case .encrypted: return String(appLocalized: "暂不支持带口令的私钥，可用 ssh-keygen -p 去掉口令，或改用新建密钥")
            case .rsa: return String(appLocalized: "暂不支持 RSA 私钥，请使用 Ed25519 / ECDSA，或改用新建密钥")
            case let .unsupported(t): return String(appLocalized: "不支持的密钥类型 \(t)")
            }
        }
    }

    public static func parse(_ text: String) throws -> SSHPrivateKey {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.contains("BEGIN OPENSSH PRIVATE KEY") {
            return try openSSH(base64Body(trimmed))
        }
        if trimmed.contains("BEGIN RSA PRIVATE KEY") { throw ImportError.rsa }
        if trimmed.contains("ENCRYPTED") { throw ImportError.encrypted }
        if trimmed.contains("BEGIN EC PRIVATE KEY") || trimmed.contains("BEGIN PRIVATE KEY") {
            if let k = try? P256.Signing.PrivateKey(pemRepresentation: trimmed) { return .p256(k) }
            if let k = try? P384.Signing.PrivateKey(pemRepresentation: trimmed) { return .p384(k) }
            if let k = try? P521.Signing.PrivateKey(pemRepresentation: trimmed) { return .p521(k) }
            let der = try base64Body(trimmed)
            // SEC1 with explicit curve parameters (what `ssh-keygen -m PEM`
            // writes), which CryptoKit refuses: take the scalar directly.
            if trimmed.contains("BEGIN EC PRIVATE KEY"), let d = sec1Scalar(Array(der)) {
                switch d.count {
                case 32: return .p256(try .init(rawRepresentation: d))
                case 48: return .p384(try .init(rawRepresentation: d))
                case 66: return .p521(try .init(rawRepresentation: d))
                default: break
                }
            }
            // PKCS#8 Ed25519: fixed 16-byte prefix + 32-byte seed.
            let prefix: [UInt8] = [0x30, 0x2e, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x04, 0x22, 0x04, 0x20]
            if der.count == 48, Array(der.prefix(16)) == prefix {
                return .ed25519(try .init(rawRepresentation: der.suffix(32)))
            }
            if der.range(of: Data([0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01])) != nil { throw ImportError.rsa }
        }
        throw ImportError.unrecognized
    }

    static func base64Body(_ pem: String) throws -> Data {
        let body = pem.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.hasPrefix("-----") && !$0.contains(":") }
            .joined()
        guard let data = Data(base64Encoded: body) else { throw ImportError.unrecognized }
        return data
    }

    /// PROTOCOL.key: "openssh-key-v1\0", cipher, kdf, kdf options, key count,
    /// public key, then the (here unencrypted) private section.
    static func openSSH(_ blob: Data) throws -> SSHPrivateKey {
        var r = Reader(Array(blob))
        let magic = Array("openssh-key-v1\0".utf8)
        guard r.take(magic.count) == magic else { throw ImportError.unrecognized }
        let cipher = try r.string()
        _ = try r.bytes() // kdf name
        _ = try r.bytes() // kdf options
        guard cipher == "none" else { throw ImportError.encrypted }
        guard try r.uint32() >= 1 else { throw ImportError.unrecognized }
        _ = try r.bytes() // public key
        var p = Reader(try r.bytes())
        let check1 = try p.uint32(), check2 = try p.uint32()
        guard check1 == check2 else { throw ImportError.unrecognized }
        let type = try p.string()
        switch type {
        case "ssh-ed25519":
            _ = try p.bytes()
            let secret = try p.bytes()
            guard secret.count == 64 else { throw ImportError.unrecognized }
            return .ed25519(try .init(rawRepresentation: secret.prefix(32)))
        case "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521":
            _ = try p.bytes() // curve name
            _ = try p.bytes() // public point
            let d = try p.bytes()
            switch type {
            case "ecdsa-sha2-nistp256": return .p256(try .init(rawRepresentation: fixed(d, 32)))
            case "ecdsa-sha2-nistp384": return .p384(try .init(rawRepresentation: fixed(d, 48)))
            default: return .p521(try .init(rawRepresentation: fixed(d, 66)))
            }
        case "ssh-rsa": throw ImportError.rsa
        default: throw ImportError.unsupported(type)
        }
    }

    /// `ECPrivateKey ::= SEQUENCE { version INTEGER 1, privateKey OCTET STRING, … }`
    static func sec1Scalar(_ der: [UInt8]) -> [UInt8]? {
        var i = 0
        func length() -> Int? {
            guard i < der.count else { return nil }
            let first = Int(der[i]); i += 1
            guard first & 0x80 != 0 else { return first }
            let n = first & 0x7f
            guard n <= 3, i + n <= der.count else { return nil }
            defer { i += n }
            return der[i..<i + n].reduce(0) { $0 << 8 | Int($1) }
        }
        guard der.first == 0x30 else { return nil }
        i = 1
        guard length() != nil, i + 3 <= der.count, der[i] == 0x02, der[i + 1] == 0x01, der[i + 2] == 0x01 else { return nil }
        i += 3
        guard i < der.count, der[i] == 0x04 else { return nil }
        i += 1
        guard let n = length(), i + n <= der.count else { return nil }
        return Array(der[i..<i + n])
    }

    /// An SSH mpint, stripped of its sign byte and left-padded to `n` bytes.
    static func fixed(_ mpint: [UInt8], _ n: Int) throws -> [UInt8] {
        var v = Array(mpint.drop { $0 == 0 })
        guard v.count <= n else { throw ImportError.unrecognized }
        v.insert(contentsOf: [UInt8](repeating: 0, count: n - v.count), at: 0)
        return v
    }

    struct Reader {
        var buf: [UInt8]
        var i = 0
        init(_ bytes: [UInt8]) { buf = bytes }

        mutating func take(_ n: Int) -> [UInt8]? {
            guard n >= 0, i + n <= buf.count else { return nil }
            defer { i += n }
            return Array(buf[i..<i + n])
        }

        mutating func uint32() throws -> UInt32 {
            guard let b = take(4) else { throw ImportError.unrecognized }
            return b.reduce(0) { $0 << 8 | UInt32($1) }
        }

        mutating func bytes() throws -> [UInt8] {
            let n = Int(try uint32())
            guard let b = take(n) else { throw ImportError.unrecognized }
            return b
        }

        mutating func string() throws -> String { String(decoding: try bytes(), as: UTF8.self) }
    }
}
