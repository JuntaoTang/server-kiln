import CryptoKit
import Foundation
import ServerKilnCore
import ServerKilnSSH
@testable import ServerKilnSync
import XCTest

/// Importing a user's own keys (shared with the iPhone companion).
final class KeyImportTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws {
        guard FileManager.default.isExecutableFile(atPath: "/usr/bin/ssh-keygen") else { throw XCTSkip("no ssh-keygen") }
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("sk-keys-\(UUID().uuidString.prefix(8))")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDown() {
        if let dir { try? FileManager.default.removeItem(at: dir) }
    }

    /// Writes a key with ssh-keygen and returns (private text, public line).
    func keygen(_ args: [String], passphrase: String = "") throws -> (String, String) {
        let path = dir.appendingPathComponent(UUID().uuidString).path
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ssh-keygen")
        p.arguments = ["-q", "-N", passphrase, "-f", path] + args
        try p.run()
        p.waitUntilExit()
        return (try String(contentsOfFile: path, encoding: .utf8), try String(contentsOfFile: path + ".pub", encoding: .utf8))
    }

    func publicPart(_ line: String) -> String { line.split(separator: " ").prefix(2).joined(separator: " ") }

    func testOpenSSHKeysOfEverySupportedType() throws {
        for args in [["-t", "ed25519"], ["-t", "ecdsa", "-b", "256"], ["-t", "ecdsa", "-b", "384"], ["-t", "ecdsa", "-b", "521"]] {
            let (priv, pub) = try keygen(args)
            let key = try SSHKeyImport.parse(priv)
            XCTAssertEqual(key.openSSHPublicKey, publicPart(pub), "\(args)")
        }
    }

    func testPEMECDSAKey() throws {
        let (priv, pub) = try keygen(["-t", "ecdsa", "-b", "256", "-m", "PEM"])
        XCTAssertTrue(priv.contains("BEGIN EC PRIVATE KEY"))
        XCTAssertEqual(try SSHKeyImport.parse(priv).openSSHPublicKey, publicPart(pub))
    }

    /// macOS's ssh-keygen (LibreSSL) drops the scalar's leading zero bytes in
    /// about 1 of 256 PEM keys, and in about half of P-521 ones.
    func testSEC1ScalarWithoutLeadingZeros() throws {
        func pem(_ scalar: Data) -> String {
            let d = Array(scalar.drop { $0 == 0 })
            let der: [UInt8] = [0x30, UInt8(5 + d.count), 0x02, 0x01, 0x01, 0x04, UInt8(d.count)] + d
            let label = "EC PRIVATE KEY"
            return "-----BEGIN \(label)-----\n\(Data(der).base64EncodedString())\n-----END \(label)-----\n"
        }
        var p256 = P256.Signing.PrivateKey()
        while p256.rawRepresentation.first != 0 { p256 = P256.Signing.PrivateKey() }
        guard case .p256(let back256) = try SSHKeyImport.parse(pem(p256.rawRepresentation)) else { return XCTFail("not P-256") }
        XCTAssertEqual(back256.rawRepresentation, p256.rawRepresentation)
        var p521 = P521.Signing.PrivateKey()
        while p521.rawRepresentation.first != 0 { p521 = P521.Signing.PrivateKey() }
        guard case .p521(let back521) = try SSHKeyImport.parse(pem(p521.rawRepresentation)) else { return XCTFail("not P-521") }
        XCTAssertEqual(back521.rawRepresentation, p521.rawRepresentation)
    }

    func testRSAAndEncryptedKeysAreExplained() throws {
        let (rsa, _) = try keygen(["-t", "rsa", "-b", "2048"])
        XCTAssertThrowsError(try SSHKeyImport.parse(rsa)) { XCTAssertEqual($0 as? SSHKeyImport.ImportError, .rsa) }
        let (encrypted, _) = try keygen(["-t", "ed25519"], passphrase: "secret phrase")
        XCTAssertThrowsError(try SSHKeyImport.parse(encrypted)) { XCTAssertEqual($0 as? SSHKeyImport.ImportError, .encrypted) }
        XCTAssertThrowsError(try SSHKeyImport.parse("hello")) { XCTAssertEqual($0 as? SSHKeyImport.ImportError, .unrecognized) }
    }

    func testStoredCredentialRoundTrip() throws {
        let (priv, _) = try keygen(["-t", "ecdsa", "-b", "384"])
        let key = try SSHKeyImport.parse(priv)
        for credential in [SSHCredential.password("pa ss"), .key(key), .identity(SSHIdentity())] {
            let stored = StoredCredential(credential)
            let back = try JSONDecoder().decode(StoredCredential.self, from: JSONEncoder().encode(stored))
            XCTAssertEqual(StoredCredential(try back.credential()), stored)
        }
    }
}

/// Models shared with the iPhone companion, and the Mac's side of its settings.
final class CompanionModelTests: XCTestCase {
    func testLegacyServerIsNotLocal() throws {
        let s = SyncedServer(id: UUID(), name: "a", endpoint: SSHEndpoint(host: "h", user: "u", hostKeys: []), roles: [.monitor])
        var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(s)) as! [String: Any]
        json["local"] = nil
        let back = try JSONDecoder().decode(SyncedServer.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertFalse(back.isLocal)
    }

    /// `EventIdle` never delivered in Production; both training kinds are
    /// posted as `EventBusy` and filtered by the Mac.
    func testTrainingFinishedIsPostedAsEventBusy() {
        XCTAssertEqual(CloudStore.Channel(.idle), .busy)
        XCTAssertEqual(CloudStore.Channel(.busy), .busy)
        XCTAssertEqual(CloudStore.Channel(.offline), .connection)
    }

    func testPrefsFilterKindsAndServers() throws {
        let legacy = try JSONDecoder().decode(NotificationPrefs.self, from: Data(#"{"mutedEndpoints":["u@a:22"],"updatedAt":0}"#.utf8))
        XCTAssertNil(legacy.disabledKinds)
        XCTAssertTrue(legacy.allows(.idle, endpointKey: "u@b:22"))
        XCTAssertFalse(legacy.allows(.idle, endpointKey: "u@a:22"))
        let prefs = NotificationPrefs(mutedEndpoints: [], disabledKinds: [.busy])
        XCTAssertTrue(prefs.allows(.idle, endpointKey: nil))
        XCTAssertFalse(prefs.allows(.busy, endpointKey: nil))
        XCTAssertEqual(try JSONDecoder().decode(NotificationPrefs.self, from: JSONEncoder().encode(prefs)), prefs)
    }

    /// 同步来源 on the iPhone reaches the Macs: a switched-off Mac posts nothing.
    func testSwitchedOffMacSendsNothing() throws {
        let legacy = try JSONDecoder().decode(NotificationPrefs.self, from: Data(#"{"mutedEndpoints":[],"disabledKinds":[],"updatedAt":0}"#.utf8))
        XCTAssertNil(legacy.excludedMacs)
        XCTAssertTrue(legacy.includes(mac: "mbp"))
        let prefs = NotificationPrefs(excludedMacs: ["mbp"])
        XCTAssertFalse(prefs.includes(mac: "mbp"))
        XCTAssertTrue(prefs.includes(mac: "mini"))
        XCTAssertEqual(try JSONDecoder().decode(NotificationPrefs.self, from: JSONEncoder().encode(prefs)), prefs)
    }

    func testPhoneServerListRoundTrip() throws {
        let s = SyncedServer(id: UUID(), name: "a", endpoint: SSHEndpoint(host: "h", user: "u", hostKeys: []), roles: [.monitor], local: true)
        let list = PhoneServerList(servers: [s], updatedAt: Date(timeIntervalSince1970: 100))
        XCTAssertEqual(try JSONDecoder().decode(PhoneServerList.self, from: JSONEncoder().encode(list)), list)
    }

    func testPipedScriptRunsUnderAnyShellAsOneLine() throws {
        let cmd = AuthorizedKeys.piped("set -e\nx='a b'\necho \"$x\"\necho @@DONE\n")
        XCTAssertFalse(cmd.contains("\n"))
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", cmd]
        let out = Pipe()
        p.standardOutput = out
        try p.run()
        p.waitUntilExit()
        XCTAssertEqual(String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self), "a b\n@@DONE\n")
    }

    func testServerWithoutReadOnlyKeyDecodes() throws {
        let s = SyncedServer(id: UUID(), name: "a", endpoint: SSHEndpoint(host: "h", user: "u", hostKeys: []), roles: [.monitor], local: true)
        var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(s)) as! [String: Any]
        json["readOnlyKey"] = nil
        XCTAssertNil(try JSONDecoder().decode(SyncedServer.self, from: JSONSerialization.data(withJSONObject: json)).readOnlyKey)
    }

    func testPhoneKeyReferenceHoldsNoSecret() throws {
        let stored = StoredCredential.phoneKey
        XCTAssertTrue(stored.data.isEmpty)
        XCTAssertThrowsError(try stored.credential())
        XCTAssertEqual(try JSONDecoder().decode(StoredCredential.self, from: JSONEncoder().encode(stored)), stored)
    }
}
