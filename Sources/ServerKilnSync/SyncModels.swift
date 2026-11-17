import Foundation
import ServerKilnCore
import ServerKilnSSH

/// Which of the two dedicated keys a credential is.
public enum KeyRole: String, Codable, CaseIterable, Sendable {
    /// Forced to the read-only collector by `authorized_keys`.
    case monitor
    /// Interactive shell; the phone asks for Face ID before using it.
    case terminal
}

/// A server as the iPhone sees it: everything needed to connect without
/// `~/.ssh/config`. Private keys are not part of it — they live only in the
/// iCloud keychain, keyed by `id` and role.
public struct SyncedServer: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var endpoint: SSHEndpoint
    /// Roles deployed to the server. The phone hides the terminal without it.
    public var roles: Set<KeyRole>
    /// Position in the Mac's server list.
    public var order: Int
    public var muted: Bool
    public var deployedAt: Date
    /// Added on an iPhone (see `LocalServerStore`) rather than deployed by
    /// a Mac. Such servers sync between iPhones only; Macs never read them.
    public var local: Bool?
    /// On a server added on an iPhone: the collect-only key installed next
    /// to the login, so the widget can poll it too (`ReadOnlyKey`).
    public var readOnlyKey: ReadOnlyKeyInstall?

    public init(id: UUID, name: String, endpoint: SSHEndpoint, roles: Set<KeyRole>, order: Int = 0,
                muted: Bool = false, deployedAt: Date = Date(), local: Bool? = nil,
                readOnlyKey: ReadOnlyKeyInstall? = nil) {
        self.id = id
        self.name = name
        self.endpoint = endpoint
        self.roles = roles
        self.order = order
        self.muted = muted
        self.deployedAt = deployedAt
        self.local = local
        self.readOnlyKey = readOnlyKey
    }

    public var hasTerminal: Bool { roles.contains(.terminal) }
    public var isLocal: Bool { local == true }

    /// The same machine added on several Macs has different IDs but the
    /// same endpoint; the phone shows it once.
    public var endpointKey: String { "\(endpoint.user)@\(endpoint.host.lowercased()):\(endpoint.port)" }
}

/// What `ReadOnlyKey.install` put on a server.
public struct ReadOnlyKeyInstall: Codable, Hashable, Sendable {
    /// `ssh-ed25519 AAAA…` now in `authorized_keys`, pinned to `collect.sh`.
    public var publicKey: String
    /// `AuthorizedKeys.collectorVersion` of the `collect.sh` written with it.
    public var collector: String

    public init(publicKey: String, collector: String) {
        self.publicKey = publicKey
        self.collector = collector
    }
}

/// The iPhone's notification choices, shared via CloudKit; the Mac checks
/// them before posting an event, so muted servers and switched-off kinds
/// never create a push.
///
/// Servers are muted by `SyncedServer.endpointKey`, so a machine added on
/// several Macs is muted once. `disabledKinds` is nil in records written by
/// iPhones before 1.0.6 (everything on).
public struct NotificationPrefs: Codable, Hashable, Sendable {
    public var mutedEndpoints: Set<String>
    public var disabledKinds: Set<ActivityEvent.Kind>?
    /// Macs switched off in the iPhone's 同步来源 (by `macID`): the phones
    /// ignore their lists and status, and they post no notifications. Nil in
    /// records written before 1.0.6.
    public var excludedMacs: Set<String>?
    public var updatedAt: Date

    public init(mutedEndpoints: Set<String> = [], disabledKinds: Set<ActivityEvent.Kind>? = nil,
                excludedMacs: Set<String>? = nil, updatedAt: Date = Date()) {
        self.mutedEndpoints = mutedEndpoints
        self.disabledKinds = disabledKinds
        self.excludedMacs = excludedMacs
        self.updatedAt = updatedAt
    }

    /// Whether the Mac with this ID may notify the iPhones at all.
    public func includes(mac macID: String) -> Bool {
        !(excludedMacs?.contains(macID) ?? false)
    }

    /// Whether the Mac should post `kind` for `endpointKey`.
    public func allows(_ kind: ActivityEvent.Kind, endpointKey: String?) -> Bool {
        if let endpointKey, mutedEndpoints.contains(endpointKey) { return false }
        return !(disabledKinds?.contains(kind) ?? false)
    }
}

/// A busy/idle/offline transition the Mac observed, pushed to the phone.
public struct ActivityEvent: Codable, Hashable, Identifiable, Sendable {
    public enum Kind: String, Codable, CaseIterable, Sendable {
        case idle, busy, offline, online
    }

    public var id: UUID
    public var serverID: UUID
    public var kind: Kind
    public var title: String
    public var body: String
    public var date: Date

    public init(id: UUID = UUID(), serverID: UUID, kind: Kind, title: String, body: String, date: Date = Date()) {
        self.id = id
        self.serverID = serverID
        self.kind = kind
        self.title = title
        self.body = body
        self.date = date
    }
}

/// What the Mac publishes: the deployed servers and its busy thresholds, so
/// the phone counts busy GPUs exactly like the menu bar.
///
/// Each Mac writes its own payload (see `CloudStore`); `merge` combines them.
public struct SyncedPayload: Codable, Hashable, Sendable {
    public var servers: [SyncedServer]
    public var policy: BusyPolicy
    /// Which Mac wrote it, for display and to merge deterministically.
    public var macID: String?
    public var macName: String?
    public var updatedAt: Date?

    public init(servers: [SyncedServer], policy: BusyPolicy, macName: String? = nil, updatedAt: Date? = Date()) {
        self.servers = servers
        self.policy = policy
        self.macName = macName
        self.updatedAt = updatedAt
    }

    /// Resolve a notification's original Mac ID after endpoint deduplication.
    public static func resolveServerID(_ id: UUID, servers: [SyncedServer], sources: [SyncedServer]) -> UUID? {
        if servers.contains(where: { $0.id == id }) { return id }
        guard let source = sources.first(where: { $0.id == id }) else { return nil }
        return servers.first(where: { $0.endpointKey == source.endpointKey })?.id
    }

    /// One list from every Mac's payload: a server deployed from two Macs
    /// keeps the most recent deployment (whose keys are the live ones on the
    /// server); the most recently updated Mac's busy thresholds win.
    public static func merge(_ payloads: [SyncedPayload]) -> SyncedPayload {
        let sorted = payloads.sorted {
            if $0.updatedAt != $1.updatedAt { return ($0.updatedAt ?? .distantPast) > ($1.updatedAt ?? .distantPast) }
            return ($0.macID ?? $0.macName ?? "") < ($1.macID ?? $1.macName ?? "")
        }
        var best: [String: SyncedServer] = [:]
        var firstSeen: [String: Int] = [:]
        var position = 0
        for payload in sorted {
            for server in payload.servers.sorted(by: { $0.order < $1.order }) {
                let key = server.endpointKey
                if firstSeen[key] == nil {
                    firstSeen[key] = position
                    position += 1
                }
                if let current = best[key] {
                    // A relay-only observation must never displace deployed
                    // credentials for the same endpoint from another Mac.
                    if !current.roles.isEmpty && server.roles.isEmpty { continue }
                    if current.roles.isEmpty == server.roles.isEmpty && current.deployedAt >= server.deployedAt { continue }
                }
                best[key] = server
            }
        }
        var servers = best.values.sorted { firstSeen[$0.endpointKey]! < firstSeen[$1.endpointKey]! }
        for i in servers.indices { servers[i].order = i }
        return SyncedPayload(servers: servers, policy: sorted.first?.policy ?? BusyPolicy(),
                             macName: nil, updatedAt: sorted.first?.updatedAt)
    }
}

public extension ActivityEvent {
    /// Record name shared by every Mac observing the same transition on the
    /// same machine within a 10-minute window, so a second Mac's save
    /// collides with the first instead of pushing a duplicate.
    static func dedupName(endpointKey: String, kind: Kind, date: Date, previous: Bool = false) -> String {
        let bucket = Int(date.timeIntervalSince1970 / 600) - (previous ? 1 : 0)
        let raw = "\(endpointKey)|\(kind.rawValue)|\(bucket)"
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for b in raw.utf8 {
            hash ^= UInt64(b)
            hash = hash &* 0x100_0000_01b3
        }
        return "evt-" + String(hash, radix: 16)
    }
}

/// One Mac's latest view of every server it monitors (PRD F9.9), so the
/// phone can show machines only the Mac can reach.
public struct RelayStatus: Codable, Hashable, Sendable {
    public struct Server: Codable, Hashable, Sendable {
        /// Roles are empty for servers not deployed to the phone: those are
        /// shown from the relay only (no direct polling, no terminal).
        public var server: SyncedServer
        public var snapshot: ServerSnapshot?
        public var error: String?
        public var sampledAt: Date
        public var finishedTasks: [TaskRecord]?

        public init(server: SyncedServer, snapshot: ServerSnapshot?, error: String?, sampledAt: Date, finishedTasks: [TaskRecord]? = nil) {
            self.server = server
            self.snapshot = snapshot
            self.error = error
            self.sampledAt = sampledAt
            self.finishedTasks = finishedTasks
        }
    }

    public var macID: String?
    public var macName: String
    public var updatedAt: Date
    public var servers: [Server]

    public init(macName: String, updatedAt: Date = Date(), servers: [Server]) {
        self.macName = macName
        self.updatedAt = updatedAt
        self.servers = servers
    }

    /// Relay data older than this is ignored (the Mac is probably asleep).
    public static let maxAge: TimeInterval = 10 * 60

    /// Freshest sample per machine across all Macs, keyed by `endpointKey`.
    public static func latest(_ statuses: [RelayStatus], now: Date = Date()) -> [String: (Server, String)] {
        var out: [String: (Server, String)] = [:]
        for status in statuses {
            for s in status.servers where now.timeIntervalSince(s.sampledAt) < maxAge {
                let key = s.server.endpointKey
                if let current = out[key], current.0.sampledAt >= s.sampledAt { continue }
                out[key] = (s, status.macName)
            }
        }
        return out
    }
}

/// Servers added on the iPhone, without a Mac, as every iPhone on the same
/// Apple ID shares them through the CloudKit private database
/// (`ServerList/phone-servers`, see `CloudStore`).
public struct PhoneServerList: Codable, Hashable, Sendable {
    public var servers: [SyncedServer]
    /// Newest list wins between iPhones (adds, edits and deletions alike).
    public var updatedAt: Date

    public init(servers: [SyncedServer] = [], updatedAt: Date = .distantPast) {
        self.servers = servers
        self.updatedAt = updatedAt
    }
}
