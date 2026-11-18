/**
 * @winche/admin — a Winche project's data: documents, files, the app's users, assets, and calling functions.
 *
 *   import { documents, files, FieldValue } from "@winche/admin";
 *
 *   await documents.doc("patients/p1").update({ visits: FieldValue.increment(1) });
 *   const report = await files.child("reports/a.pdf").getData();
 *
 * The same API wherever it runs; only who it acts as differs:
 *
 * - in functions: the server (full rights, access rules don't apply);
 * - in a console page: the person who opened it, limited by their role in the project (viewer reads, member writes
 *   data, admin does everything);
 * - anywhere else: `initializeAdmin({ server, project, key })` (or a trusted `identityToken`), as a service account,
 *   limited by its role.
 *
 * `as(user)` gives the same API acting as one of the app's users: every documents and files call is checked against
 * the project's access rules for them, as if the app had made it.
 *
 * References (`doc`, `collection`, `child`, `user`) and the snapshots their reads return read like the Dart SDKs.
 */
export type Json = null | boolean | number | string | Json[] | {
    [key: string]: Json;
};
/** A latitude / longitude pair (geoPointValue). */
export declare class GeoPoint {
    readonly latitude: number;
    readonly longitude: number;
    constructor(latitude: number, longitude: number);
    isEqual(other: GeoPoint): boolean;
}
export type LinkKind = "document" | "file" | "user";
/**
 * A typed reference stored in a document: to a document, a file or a user. Reads give every stored reference back as a
 * Link; `data()` is what it points to when the read expanded it (`expand(["patient"])`), else null. Write one with
 * `Link.document(path)`, `Link.file(path)`, `Link.user(uid)`, or a reference (`documents.doc(...)`, …).
 */
export declare class Link<T = unknown> {
    readonly kind: LinkKind;
    readonly target: string;
    private readonly resolved;
    /** @internal */ constructor(kind: LinkKind, target: string, resolved?: (() => T | null) | null);
    static document(path: string): Link<DocumentData>;
    static file(path: string): Link<FileData>;
    static user(uid: string): Link<UserData>;
    /** What it points to, when the read expanded it and the target exists; null otherwise. */
    data(): T | null;
    isEqual(other: unknown): boolean;
    toString(): string;
}
type FieldValueKind = "delete" | "serverTimestamp" | "increment" | "maximum" | "minimum" | "arrayUnion" | "arrayRemove";
/** Sentinels for set / update: server-side transforms, and removing a field. */
export declare class FieldValue {
    readonly kind: FieldValueKind;
    readonly operand?: unknown;
    private constructor();
    /** Removes the field (update, or set with merge / mergeFields). */
    static delete(): FieldValue;
    /** The write's commit time. */
    static serverTimestamp(): FieldValue;
    /** Adds n (integers stay integers; a double makes a double). A missing or non-number field becomes n. */
    static increment(n: number | bigint): FieldValue;
    /** Keeps the larger of the field and n. */
    static maximum(n: number | bigint): FieldValue;
    /** Keeps the smaller of the field and n. */
    static minimum(n: number | bigint): FieldValue;
    /** Appends the elements not already in the array. */
    static arrayUnion(...elements: unknown[]): FieldValue;
    /** Removes every element equal to one of these. */
    static arrayRemove(...elements: unknown[]): FieldValue;
}
/** A document's fields, decoded: numbers, strings, booleans, null, Date (timestamps), Uint8Array (bytes), GeoPoint, Link, arrays and maps. */
export type DocumentData = {
    [field: string]: any;
};
type Tagged = {
    [tag: string]: any;
};
/** Where links read from a document find what they point to (the read's included documents and resolved files/users). */
interface Expansion {
    included: {
        [path: string]: any;
    };
    resolved: {
        [key: string]: any;
    };
}
/** @internal JS value → tagged wire value. */
export declare function encodeValue(v: unknown): Tagged;
/** @internal */
export declare function encodeFields(fields: {
    [k: string]: unknown;
}): {
    [k: string]: Tagged;
};
/** @internal Tagged wire value → JS value. */
export declare function decodeValue(t: Tagged | null | undefined, x?: Expansion): any;
/** @internal */
export declare function decodeFields(fields: {
    [k: string]: Tagged;
} | null | undefined, x?: Expansion): DocumentData;
/**
 * The protocol this SDK speaks with Winche (its operations and how it finds the server where it runs). A server says
 * which it speaks; one that doesn't speak this one refuses the SDK's calls, saying which version to use.
 */
export declare const PROTOCOL = 1;
/** @internal A call into the server: the runtime's bridge in functions, the console's channel in pages, HTTP elsewhere. */
export type Call = (op: string, args: {
    [k: string]: unknown;
}) => Promise<any>;
/** An error from the server: its code (e.g. "permission-denied", "not-found") and message. */
export declare class WincheError extends Error {
    code: string;
    constructor(code: string, message: string);
}
/** A write condition: the document exists (or not), or is unchanged since a read (`snapshot.updateTimeRaw`). */
export interface Precondition {
    exists?: boolean;
    updateTime?: string;
}
export interface SetOptions {
    /** Merge into the existing document (deep, maps into maps) instead of replacing it. */
    merge?: boolean;
    /** Write only these field paths (others are left as they are); a listed path missing from data is removed. */
    mergeFields?: string[];
    precondition?: Precondition;
}
export type WhereOp = "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" | "not-in" | "array-contains" | "array-contains-any" | "array-contains-all" | "contains" | "starts-with" | "ends-with" | "matches";
/** Filters to combine: `query.where(Filter.or(Filter.where("a", "==", 1), Filter.where("b", "==", 2)))`. */
export declare class Filter {
    readonly wire: Tagged;
    private constructor();
    static where(field: string, op: WhereOp, value: unknown): Filter;
    static and(...filters: Filter[]): Filter;
    static or(...filters: Filter[]): Filter;
    static not(filter: Filter): Filter;
    /** The field is null. */
    static isNull(field: string): Filter;
    /** The field is NaN. */
    static isNan(field: string): Filter;
    /** The field is there (any value, null included). */
    static exists(field: string): Filter;
}
export type Aggregate = {
    kind: "count";
} | {
    kind: "sum";
    field: string;
} | {
    kind: "average";
    field: string;
};
export declare const Aggregate: {
    count: () => Aggregate;
    sum: (field: string) => Aggregate;
    average: (field: string) => Aggregate;
};
interface QueryState {
    collection: string;
    group: boolean;
    filters: Tagged[];
    orderBy: {
        field: string;
        direction: "asc" | "desc";
    }[];
    limit?: number;
    limitToLast?: number;
    offset?: number;
    start?: {
        values: unknown[] | DocumentSnapshot;
        before: boolean;
    };
    end?: {
        values: unknown[] | DocumentSnapshot;
        before: boolean;
    };
    select?: string[];
    expand?: string[];
}
/** A read of a collection (or a collection group): chain filters and order, then get(), count(), sum(), … */
export declare class Query<T = DocumentData> {
    protected readonly call: Call;
    protected readonly state: QueryState;
    /** @internal */ constructor(call: Call, /** @internal */ state: QueryState);
    private with;
    /** `where("age", ">=", 18)`, or a combined `Filter`. */
    where(field: string, op: WhereOp, value: unknown): Query<T>;
    where(filter: Filter): Query<T>;
    orderBy(field: string, direction?: "asc" | "desc"): Query<T>;
    limit(n: number): Query<T>;
    /** The last n of an ordered query (in order). */
    limitToLast(n: number): Query<T>;
    offset(n: number): Query<T>;
    /** Cursors: the orderBy fields' values, or a snapshot (its values for them, and its path). */
    startAt(...at: unknown[]): Query<T>;
    startAfter(...at: unknown[]): Query<T>;
    endAt(...at: unknown[]): Query<T>;
    endBefore(...at: unknown[]): Query<T>;
    /** Only these fields in the results (the server leaves the rest out). */
    select(...fields: string[]): Query<T>;
    /** Follow these reference fields: their Links' data() is what they point to. */
    expand(paths: string[]): Query<T>;
    /** @internal The query in the wire format. */
    toWire(): Tagged;
    get(): Promise<QuerySnapshot<T>>;
    /** How many documents match (no limit). */
    count(): Promise<number>;
    /** The sum of a numeric field over the matches. */
    sum(field: string): Promise<number>;
    /** The average of a numeric field over the matches (null when none). */
    average(field: string): Promise<number | null>;
    /** Several at once: `aggregate({ n: Aggregate.count(), total: Aggregate.sum("amount") })`. */
    aggregate<K extends string>(aggregates: {
        [alias in K]: Aggregate;
    }): Promise<{
        [alias in K]: number | null;
    }>;
}
/** A collection: a query over its documents, and where new ones go. */
export declare class CollectionReference<T = DocumentData> extends Query<T> {
    readonly path: string;
    /** @internal */ constructor(call: Call, path: string);
    get id(): string;
    /** The document this collection is under (null at the top). */
    get parent(): DocumentReference | null;
    /** A document in it; without an id, a new random one. */
    doc(id?: string): DocumentReference<T>;
    /** Adds a document with a new id. */
    add(data: T & DocumentData): Promise<DocumentReference<T>>;
}
/** A document: read it, write it, reach its sub-collections. */
export declare class DocumentReference<T = DocumentData> {
    private readonly call;
    readonly path: string;
    private readonly expandPaths?;
    /** @internal */ constructor(call: Call, path: string, /** @internal */ expandPaths?: string[]);
    get id(): string;
    get parent(): CollectionReference<T>;
    collection(path: string): CollectionReference;
    /** Follow these reference fields on get(): their Links' data() is what they point to. */
    expand(paths: string[]): DocumentReference<T>;
    get(): Promise<DocumentSnapshot<T>>;
    /** Replaces the document (or merges into it, or writes the mergeFields only). */
    set(data: T & DocumentData, options?: SetOptions): Promise<void>;
    /** Changes these fields (dotted paths reach into maps) of an existing document. */
    update(data: {
        [fieldPath: string]: unknown;
    }, options?: {
        precondition?: Precondition;
    }): Promise<void>;
    /** Deletes it; with cascade, everything under it too (in one write: for big trees, use documents.recursiveDelete). */
    delete(options?: {
        cascade?: boolean;
        precondition?: Precondition;
    }): Promise<void>;
    isEqual(other: unknown): boolean;
}
/** A document as read: `exists`, `data()`, and its metadata. */
export declare class DocumentSnapshot<T = DocumentData> {
    readonly reference: DocumentReference<T>;
    readonly exists: boolean;
    private readonly fields;
    private readonly meta;
    /** @internal */ constructor(reference: DocumentReference<T>, exists: boolean, fields: DocumentData | null, meta: {
        createTime: string;
        updateTime: string;
        version: number;
    } | null);
    /** @internal From the wire (with the read's expansion for its links). */
    static of<T>(call: Call, d: Tagged, x?: Expansion): DocumentSnapshot<T>;
    get id(): string;
    /** The fields (undefined when it doesn't exist). */
    data(): T | undefined;
    /** One field, by dotted path ("address.city"); undefined when it isn't there. */
    get(fieldPath: string): any;
    get createTime(): Date | null;
    get updateTime(): Date | null;
    /** The update time exactly as the server has it (microseconds), for `precondition: { updateTime }`. */
    get updateTimeRaw(): string | null;
    get version(): number | null;
}
export declare class QuerySnapshot<T = DocumentData> {
    readonly docs: DocumentSnapshot<T>[];
    readonly hasMore: boolean;
    /** @internal */ constructor(docs: DocumentSnapshot<T>[], /** True when more matched past the limit. */ hasMore: boolean);
    get size(): number;
    get empty(): boolean;
    forEach(fn: (doc: DocumentSnapshot<T>) => void): void;
}
/** Up to 500 writes, all or none. */
export declare class WriteBatch {
    private readonly call;
    private readonly writes;
    /** @internal */ constructor(call: Call);
    set<T>(ref: DocumentReference<T>, data: T & DocumentData, options?: SetOptions): this;
    update(ref: DocumentReference<any>, data: {
        [fieldPath: string]: unknown;
    }, options?: {
        precondition?: Precondition;
    }): this;
    delete(ref: DocumentReference<any>, options?: {
        cascade?: boolean;
        precondition?: Precondition;
    }): this;
    get size(): number;
    commit(): Promise<void>;
}
/**
 * A transaction's reads and writes. Optimistic: each document it read and then writes must be unchanged when it
 * commits (else the whole function runs again, up to maxAttempts). Reads must come before writes.
 */
export declare class Transaction {
    private readonly call;
    private readonly writes;
    /** @internal path → updateTimeRaw (null: it didn't exist) */ readonly seen: Map<string, string>;
    /** @internal */ constructor(call: Call);
    get<T>(ref: DocumentReference<T>): Promise<DocumentSnapshot<T>>;
    private guard;
    set<T>(ref: DocumentReference<T>, data: T & DocumentData, options?: SetOptions): this;
    update(ref: DocumentReference<any>, data: {
        [fieldPath: string]: unknown;
    }, options?: {
        precondition?: Precondition;
    }): this;
    delete(ref: DocumentReference<any>, options?: {
        precondition?: Precondition;
    }): this;
    /** @internal */ commit(): Promise<void>;
}
export declare class Documents {
    private readonly call;
    /** @internal */ constructor(call: Call);
    /** A collection by path ("patients", "patients/p1/visits"). */
    collection(path: string): CollectionReference;
    /** A document by path ("patients/p1"). */
    doc(path: string): DocumentReference;
    /** Every collection with this id, under any parent ("sessionHistory"). */
    collectionGroup(id: string): Query;
    /** Several documents in one call, in order. */
    getAll(refs: DocumentReference<any>[]): Promise<DocumentSnapshot[]>;
    batch(): WriteBatch;
    /** Deletes a document and everything under it, in batches (any size); how many documents went. */
    recursiveDelete(ref: DocumentReference<any>): Promise<number>;
    /** Runs `fn` with a transaction and commits it; runs it again when something it read changed meanwhile. */
    runTransaction<R>(fn: (tx: Transaction) => Promise<R>, options?: {
        maxAttempts?: number;
    }): Promise<R>;
}
/** A file's record: what it is, not its bytes. */
export interface FileData {
    id: string;
    path: string;
    directory: string;
    mimeType: string;
    sizeBytes: number;
    uploadStatus: "pending" | "complete";
    contentDisposition: string | null;
    cacheControl: string | null;
    contentHash: string | null;
    metadata: {
        [key: string]: Json;
    };
    version: number;
    createdAt: Date;
    updatedAt: Date;
}
/** @internal */
export declare function fileData(r: any): FileData;
export interface PutOptions {
    /** Default: text/plain for text, application/octet-stream for bytes. */
    mimeType?: string;
    metadata?: {
        [key: string]: Json;
    };
    /** What browsers do with it when downloaded, e.g. 'attachment; filename="report.pdf"'. */
    contentDisposition?: string;
    cacheControl?: string;
}
/** A file (or a folder) by path. */
export declare class ChildReference {
    private readonly call;
    readonly path: string;
    /** @internal */ constructor(call: Call, path: string);
    /** The last part of the path. */
    get name(): string;
    get parent(): ChildReference | null;
    child(path: string): ChildReference;
    /** The file's record; `exists` is false when there's none. */
    getSnapshot(): Promise<FileSnapshot>;
    /** The bytes (up to 32 MB), or null when there's no file. */
    getData(): Promise<Uint8Array | null>;
    /** The contents as UTF-8 text, or null. */
    getText(): Promise<string | null>;
    /** Writes the file (new or replacing one), up to 32 MB; its triggers and hooks run as for an upload. */
    putData(data: Uint8Array | ArrayBuffer | string, options?: PutOptions): Promise<FileSnapshot>;
    /**
     * Writes the file from a URL (a GET with these headers), up to 5 GB: the bytes go from the URL to storage on the
     * server, never through the function. The type is the one given, else the response's.
     */
    putFromUrl(url: string, options?: PutOptions & {
        headers?: {
            [name: string]: string;
        };
    }): Promise<FileSnapshot>;
    /** One page of the folder: files and sub-folders in name order. */
    list(options?: {
        maxResults?: number;
        pageToken?: string;
        mimeType?: string;
    }): Promise<DirectorySnapshot>;
    /** Every file and sub-folder of the folder (all pages). */
    listChildren(options?: {
        mimeType?: string;
    }): Promise<DirectorySnapshot>;
    /** Merges metadata (null removes a key); optionally changes the download headers ("" clears one), only at ifVersion. */
    updateMetadata(metadata: {
        [key: string]: Json;
    }, options?: {
        contentDisposition?: string;
        cacheControl?: string;
        ifVersion?: number;
    }): Promise<FileSnapshot>;
    /** Deletes the file, or everything under the folder. False when nothing was there. */
    delete(): Promise<boolean>;
    /** A link anyone holding it can download the file with, until expiresAt. */
    downloadUrl(): Promise<{
        url: string;
        expiresAt: Date;
    }>;
    isEqual(other: unknown): boolean;
}
export declare class FileSnapshot {
    readonly reference: ChildReference;
    readonly data: FileData | null;
    /** @internal */ constructor(reference: ChildReference, data: FileData | null);
    get exists(): boolean;
}
export declare class DirectorySnapshot {
    readonly reference: ChildReference;
    readonly files: FileSnapshot[];
    readonly folders: ChildReference[];
    readonly nextPageToken: string | null;
    /** @internal */ constructor(reference: ChildReference, files: FileSnapshot[], folders: ChildReference[], nextPageToken: string | null);
}
export declare class Files {
    private readonly call;
    /** @internal */ constructor(call: Call);
    /** A file or folder by path; "" is the top. */
    child(path: string): ChildReference;
}
/** A user's account. */
export interface UserData {
    uid: string;
    email: string | null;
    emailVerified: boolean;
    displayName: string | null;
    photoUrl: string | null;
    isAnonymous: boolean;
    disabled: boolean;
    mfaEnabled: boolean;
    /** How they sign in: password, anonymous, google, … */
    providers: string[];
    customClaims: {
        [claim: string]: Json;
    };
    createdAt: Date | null;
    lastSignInAt: Date | null;
}
/** @internal */
export declare function userData(u: any): UserData;
/** A user, by uid. */
export declare class UserReference {
    private readonly call;
    readonly uid: string;
    /** @internal */ constructor(call: Call, uid: string);
    /** Their account; `exists` is false when there's no such user. */
    get(): Promise<UserSnapshot>;
    /** Their custom claims ({} when none). Throws not-found when there's no such user. */
    getCustomClaims(): Promise<{
        [claim: string]: Json;
    }>;
    /** Replaces their custom claims (null clears them); they're in the user's next token. */
    setCustomClaims(claims: {
        [claim: string]: Json;
    } | null): Promise<void>;
    isEqual(other: unknown): boolean;
}
export declare class UserSnapshot {
    readonly reference: UserReference;
    readonly data: UserData | null;
    /** @internal */ constructor(reference: UserReference, data: UserData | null);
    get uid(): string;
    get exists(): boolean;
    /** Their custom claims ({} when none, or no user). */
    get customClaims(): {
        [claim: string]: Json;
    };
}
export declare class Auth {
    private readonly call;
    /** @internal */ constructor(call: Call);
    user(uid: string): UserReference;
}
/** A file of a release: its path, its content's SHA-256, size and type. */
export interface AssetEntry {
    path: string;
    hash: string;
    size: number;
    mimeType: string;
}
/** A file of a release, to read. */
export declare class AssetFileReference {
    private readonly call;
    readonly release: number;
    readonly path: string;
    /** @internal */ constructor(call: Call, release: number, path: string);
    /** Its bytes (up to 32 MB: larger ones through downloadUrl). */
    bytes(): Promise<Uint8Array>;
    /** Its contents as UTF-8 text. */
    text(): Promise<string>;
    /** Its contents decoded from JSON. */
    json<T = Json>(): Promise<T>;
    /** A short-lived link to its bytes (to hand to someone, or to fetch a large one). */
    downloadUrl(): Promise<{
        url: string;
        expiresAt: Date;
    }>;
}
/** A published release: numbered, never changed. */
export declare class AssetRelease {
    private readonly call;
    readonly number: number;
    readonly note: string | null;
    readonly by: string | null;
    readonly at: Date;
    readonly files: AssetEntry[];
    /** @internal */ constructor(call: Call, number: number, note: string | null, by: string | null, at: Date, files: AssetEntry[]);
    /** @internal */ static of(call: Call, r: any): AssetRelease;
    /** Whether it has a file at path. */
    has(path: string): boolean;
    /** The paths under a folder ("translations"), or all of them. */
    paths(folder?: string): string[];
    /** A file of this release (reading one it hasn't throws not-found). */
    file(path: string): AssetFileReference;
}
/** A channel: a name pointing at a release. */
export declare class AssetChannelReference {
    private readonly call;
    readonly name: string;
    /** @internal */ constructor(call: Call, name: string);
    /** The release it points at now, or null when there's no such channel. */
    get(): Promise<AssetRelease | null>;
}
export declare class Assets {
    private readonly call;
    /** @internal */ constructor(call: Call);
    /** A channel by name ("production"). */
    channel(name: string): AssetChannelReference;
    /** A release by number, or null. */
    release(number: number): Promise<AssetRelease | null>;
    /** The channels and the release each points at. */
    channels(): Promise<{
        name: string;
        release: number;
        updatedAt: Date;
        by: string | null;
    }[]>;
    /** The releases, newest first, without their files. */
    releases(limit?: number): Promise<{
        number: number;
        note: string | null;
        by: string | null;
        at: Date;
        files: number;
        size: number;
    }[]>;
}
export declare class Functions {
    private readonly send;
    /** @internal */ constructor(send: Call);
    /** Runs the callable `name` with `data` and returns what it returns (its HttpsError as a WincheError with its code). */
    call<R = Json>(name: string, data?: Json): Promise<R>;
}
/** One of the app's users, to act as with `as(user)`: their uid and token claims (custom claims among them). */
export interface AppUser {
    uid: string;
    token?: {
        [claim: string]: Json;
    };
}
/** A project's data, acting as someone: `documents`, `files`, `auth`, `assets`, `functions`. */
export declare class Admin {
    private readonly transport;
    readonly documents: Documents;
    readonly files: Files;
    readonly auth: Auth;
    readonly assets: Assets;
    readonly functions: Functions;
    /** @internal */ constructor(transport: Call);
    /**
     * The same API acting as one of the app's users (`ctx.caller` in a callable): every documents and files call is
     * checked against the project's access rules for them, and refused with permission-denied as an app would be.
     * Auth, assets and functions calls are refused (rules don't cover them).
     */
    as(user: AppUser): Admin;
}
/** How `initializeAdmin` reaches a Winche server, and who it signs in as (one of `key`, `identityToken`, `token`). */
export interface AdminOptions {
    /** The server's address, e.g. "https://winche.example.com". */
    server: string;
    /** The project's id. */
    project: string;
    /**
     * A service account's key (`wsk_…`, from the console or `winche accounts:keys:add`): exchanged for an access token,
     * renewed before it runs out. Keep it in your server's secrets.
     */
    key?: string;
    /**
     * The current token this host's or CI job's identity provider signs about it (OpenID Connect), for a service account
     * that trusts that identity: no key to keep. Called again whenever a new access token is needed.
     */
    identityToken?: () => string | Promise<string>;
    /** An access token as it is (or a function that returns the current one), when you get it some other way. */
    token?: string | (() => string | Promise<string>);
    /** The fetch to use (default: the global one). */
    fetch?: typeof fetch;
}
/** The SDK for a project on a server, from anywhere (another server, a script): calls go over HTTPS as a service account. */
export declare function initializeAdmin(options: AdminOptions): Admin;
/** Documents (in functions: with the server's rights; in a page: the person's role). */
export declare const documents: Documents;
/** Files. */
export declare const files: Files;
/** The app's users. */
export declare const auth: Auth;
/** The app's assets (releases and channels). */
export declare const assets: Assets;
/** Calling callables. */
export declare const functions: Functions;
/** The same API acting as one of the app's users: their access rules apply. */
export declare const as: (user: AppUser) => Admin;
export {};
