/**
 * @winche-technologies/winche-functions — write Winche functions.
 *
 *   import { onDocument, callable, schedule, secrets } from "@winche-technologies/winche-functions";
 *   import { documents, FieldValue } from "@winche-technologies/winche-admin";
 *
 *   export const onVisit = onDocument.created("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
 *     await documents.doc(`patients/${e.params.patientId}`).update({ lastVisit: e.data.get("at"), visits: FieldValue.increment(1) });
 *   });
 *
 *   export const hello = callable(async (data: { name: string }, ctx) => ({ greeting: `Hello ${data.name}`, uid: ctx.caller?.uid }));
 *
 *   const mailKey = secrets.define("MAIL_KEY");
 *   export const nightly = schedule("0 2 * * *", async (_e, ctx) => ctx.log("Nightly clean-up", mailKey.isSet()), { secrets: [mailKey] });
 *
 * Functions run on the Winche server in a sandbox: they can use the network (fetch) but not its files or settings.
 * Their data comes from @winche-technologies/winche-admin, which acts with the server's own rights inside them (access rules don't apply,
 * unless `as(ctx.caller)`).
 */
import { type Call, type Json, type DocumentData, DocumentSnapshot, FileSnapshot, UserSnapshot } from "@winche-technologies/winche-admin";
export type { Json } from "@winche-technologies/winche-admin";
/** Who called: a callable's signed-in caller, or the request a before-function checks. */
export interface Caller {
    uid: string;
    /** The caller's token claims (custom claims among them). */
    token: {
        [claim: string]: Json;
    };
}
/**
 * A secret the deployment uses (an API key, a password), set in the console (the Secrets page) or with
 * `winche functions secrets:set NAME`. Declared once (`secrets.define("NAME")`) and given to the functions that read it
 * (`{ secrets: [handle] }` in their options). Values are hidden in the functions' logs.
 */
export interface Secret {
    readonly name: string;
    /** Its current value, while a function that lists it runs. Throws when the function doesn't list it, or it isn't set. */
    value(): string;
    /** Whether it's set (for a function that lists it). */
    isSet(): boolean;
}
export declare const secrets: {
    /** Declares a secret: names in capitals, digits and underscores (e.g. "MAIL_KEY"). */
    define(name: string): Secret;
};
/**
 * A file of the functions folder's resources/ folder, deployed with the code (fonts, translations, templates, images).
 * Read-only; paths are relative to resources/ ("fonts/Roboto.ttf"). They change only with a deployment.
 */
export interface Resource {
    readonly path: string;
    /** Its bytes, or null when there's no such resource. */
    bytes(): Promise<Uint8Array | null>;
    /** Its contents as UTF-8 text, or null. */
    text(): Promise<string | null>;
    /** Whether it's there. */
    exists(): boolean;
}
export declare const resources: {
    /** Declares a resource, read while a function runs. */
    define(path: string): Resource;
    /** The resource paths in a folder and its subfolders (all of them without one), sorted. */
    list: (folder?: string) => Promise<string[]>;
};
/** Who called: a callable's signed-in caller, or the request a before-function checks. */
export interface Caller {
    uid: string;
    /** The caller's token claims (custom claims among them). */
    token: {
        [claim: string]: Json;
    };
}
/** A console role in the project: who may see a page. */
export type ConsoleRole = "viewer" | "member" | "admin";
/** The call: what a function gets beside its data or event. Its data comes from @winche-technologies/winche-admin. */
export interface Context {
    /** This function's name (its export name). */
    name: string;
    /** For callables: who called (null when signed out). Act as them with `as(ctx.caller)` from @winche-technologies/winche-admin. */
    caller: Caller | null;
    /**
     * Whether the server itself called: another function (`functions.call`), a run from the console, a server with the
     * admin SDK, a trigger or schedule. False for apps (signed in or not) and HTTP requests: a callable only the
     * project's own code may use checks it.
     */
    byServer: boolean;
    /** Writes to the server's logs (Logs → functions); console.log does too. */
    log(...args: unknown[]): void;
}
/** @internal What the runtime hands a run. */
interface RawContext {
    call: Call;
    functionName: string;
    auth: Caller | null;
    /** The server's own call (absent before 3.1: false). */
    server?: boolean;
    log(...args: unknown[]): void;
}
/** @internal The context a handler gets. */
export declare function contextOf(raw: RawContext): Context;
export interface DocumentEvent {
    type: "created" | "updated" | "deleted";
    path: string;
    /** The path pattern's captures, e.g. { patientId: "p1" }. */
    params: {
        [name: string]: string;
    };
    /** The document now (exists is false when deleted). */
    data: DocumentSnapshot;
    /** The document before (exists is false when created). */
    before: DocumentSnapshot;
    /** The same on retries: use it to ignore repeats. */
    id: string;
    at: Date;
}
export interface FileEvent {
    type: "uploaded" | "deleted" | "metadataUpdated";
    path: string;
    params: {
        [name: string]: string;
    };
    /** The file now (exists is false when deleted). */
    file: FileSnapshot;
    id: string;
    at: Date;
}
export interface UserEvent {
    type: "created" | "deleted" | "disabled" | "enabled" | "claimsChanged" | "signedIn";
    uid: string;
    user: UserSnapshot;
    /** For signedIn: how (password, google, emailLink, …). */
    provider: string | null;
    id: string;
    at: Date;
}
/** A write about to be saved, for documents.beforeWrite. Throw HttpsError to refuse it; return changes to make. */
export interface BeforeDocumentEvent {
    operation: "create" | "update" | "delete";
    path: string;
    params: {
        [name: string]: string;
    };
    /** The document as stored now (exists is false when it's being created). */
    before: DocumentSnapshot;
    /** The fields it will have (null when deleted), with earlier before-functions' changes. */
    data: DocumentData | null;
    /** Who made the request (null when signed out, or the app's own server code). */
    caller: Caller | null;
}
/** Changes to the document being written: fields to set (dotted paths) and to remove. */
export interface DocumentChanges {
    set?: {
        [fieldPath: string]: unknown;
    };
    remove?: string[];
}
/** A file write about to be saved, for files.beforeWrite (registering, a metadata change, a delete). */
export interface BeforeFileEvent {
    operation: "create" | "update" | "delete";
    path: string;
    params: {
        [name: string]: string;
    };
    /** The file as stored now (exists is false when new). */
    before: FileSnapshot;
    /** What will be saved (null when deleted). */
    file: {
        mimeType: string;
        sizeBytes: number;
        metadata: {
            [key: string]: Json;
        };
        contentDisposition: string | null;
        cacheControl: string | null;
    } | null;
    caller: Caller | null;
}
/** Changes to the file being written: metadata keys to set (null removes one), and its download headers (null clears). */
export interface FileChanges {
    metadata?: {
        [key: string]: Json;
    };
    contentDisposition?: string | null;
    cacheControl?: string | null;
}
/** Someone about to become a user (users.beforeCreate). Throw HttpsError to refuse; its message reaches them. */
export interface BeforeCreateEvent {
    /** Null for guests and accounts without an email. */
    email: string | null;
    /** password, anonymous, emailLink, custom, or a provider (google, apple, keycloak, …). */
    provider: string;
    ipAddress: string | null;
}
/** A user about to be signed in (users.beforeSignIn), their credentials already checked. */
export interface BeforeSignInEvent {
    user: UserSnapshot;
    provider: string;
    ipAddress: string | null;
}
/** A release published (released) or a channel pointed at a release (channelMoved). */
export interface AssetEvent {
    type: "released" | "channelMoved";
    /** The release published, or the one the channel points at now. */
    release: number;
    /** The channel moved (for released: the one published to, or null). */
    channel: string | null;
    /** The release's note (released). */
    note: string | null;
    /** Who published it (released). */
    by: string | null;
    id: string;
    at: Date;
}
export interface ScheduleEvent {
    /** When this run was due. */
    scheduledAt: Date;
    id: string;
}
export interface FunctionOptions {
    /**
     * Seconds it may run: default 60 (the project's default), at most the server's longest (9 minutes unless the server
     * allows more). The console can give a function its own time over this. Before-functions: default 5, at most 30.
     */
    timeoutSeconds?: number;
    /** The secrets it reads: handles from `secrets.define("NAME")`. */
    secrets?: Secret[];
    /** What it does, in a sentence (up to 300 characters): shown in the console. */
    description?: string;
    /**
     * The shape of what it takes, as JSON Schema: shown in the console, which starts a run by hand from an input made
     * from it (unless `example` is given). Not checked: the function checks its own input.
     */
    input?: JsonSchema;
    /** The shape of what it returns, as JSON Schema: shown in the console. Not checked. */
    output?: JsonSchema;
    /** A sample input, to start a run by hand from (instead of one made from `input`). */
    example?: Json;
    /**
     * A new sandbox for every run, with nothing kept from the one before. Without it, runs reuse a warm worker (the code
     * already loaded, so a run starts in well under a millisecond), and what the module keeps at the top level (a client,
     * a parsed file) stays from run to run: never keep one caller's data there. `fresh` costs each run the time to load
     * the code again.
     */
    fresh?: boolean;
    /**
     * The most memory, in MB, an instance (a worker with this function's code loaded) may hold after a run: past it the
     * instance is replaced, its memory freed. Default 256 (the project's default), 64 to the server's most (1024 unless
     * it allows more). The console can set it over this.
     */
    memory?: number;
    /** Instances kept loaded even when idle, so a call never waits for one. Default 1 for callables and HTTP functions, else 0. */
    minInstances?: number;
    /** The most runs of this function at once; more wait their turn (within their timeout). Default 10 (the project's default). */
    maxInstances?: number;
    /** Runs of the project's limit kept for this function, so busy functions can't take them all. Default 0. */
    reservedInstances?: number;
    /**
     * A process of its own: a run that uses too much memory, or crashes, stops only this function's runs, never another's.
     * Costs the memory of a second runtime. A function asking for more memory than the shared process allows gets one anyway.
     */
    isolate?: boolean;
}
/** A JSON Schema, e.g. { type: "object", properties: { lang: { enum: ["en", "de"] } }, required: ["lang"] }. */
export type JsonSchema = {
    [keyword: string]: Json;
};
export interface BeforeOptions {
    /** Default 5, at most 30: the write (and its locks) waits for it. */
    timeoutSeconds?: number;
    /** The secrets it reads: handles from `secrets.define("NAME")`. */
    secrets?: Secret[];
    /**
     * When the function crashes or runs out of time: refuse the write (the default), or let it through as it is.
     * A thrown HttpsError always refuses it.
     */
    failOpen?: boolean;
}
/** The kinds of error every Winche API and SDK shares: the `code` an app receives. */
export type FunctionsErrorCode = "cancelled" | "unknown" | "invalid-argument" | "deadline-exceeded" | "not-found" | "already-exists" | "permission-denied" | "resource-exhausted" | "failed-precondition" | "aborted" | "out-of-range" | "unimplemented" | "internal" | "unavailable" | "data-loss" | "unauthenticated";
/**
 * Throw it from a callable to send the app a code, a message and, optionally, details (anything JSON can carry).
 * Other errors reach the app as "internal" with no message; theirs stays in the logs.
 */
export declare class HttpsError extends Error {
    code: FunctionsErrorCode;
    details?: unknown | undefined;
    constructor(code: FunctionsErrorCode, message: string, details?: unknown | undefined);
}
/** A page of the project's own in the console: its files are yours (HTML, JS, CSS), shown in a sandboxed frame. */
export interface PageOptions {
    /** In the console's sidebar. */
    title: string;
    /** The folder of the page's files, next to index.ts (e.g. "console/reports"): it must hold an index.html. */
    files: string;
    /** A Tabler icon name (e.g. "chart-bar"); the default is a page icon. */
    icon?: string;
    /** Who sees it (default viewer). What it may do is their role's (@winche-technologies/winche-admin in the page acts as them). */
    minRole?: ConsoleRole;
    /** Where it goes in the sidebar, lowest first (default 0, then by title). */
    order?: number;
}
export type Trigger = {
    kind: "documents";
    events: string[];
    path: string;
} | {
    kind: "files";
    events: string[];
    path: string;
} | {
    kind: "users";
    events: string[];
} | {
    kind: "schedule";
    cron: string;
    timeZone?: string;
} | {
    kind: "assets";
    events: string[];
    channel?: string;
} | {
    kind: "callable";
} | {
    kind: "consolePage";
} | {
    kind: "http";
    events?: string[];
} | {
    kind: "before";
    on: "documents" | "files" | "users";
    events: string[];
    path?: string;
};
/** What an exported function is: Winche reads these when the code is deployed. */
export interface WincheFunction<A = unknown, R = unknown> {
    __winche: 1;
    trigger: Trigger;
    options: FunctionOptions;
    /** @internal Called by the runtime with what the server sent and the run's raw context. */
    handler: (arg: unknown, raw: never) => Promise<unknown>;
    /** The function as written, for tests: call it with an event and a context of your own. */
    run: (arg: A, ctx: Context) => R | Promise<R>;
}
type Handler<E> = (event: E, ctx: Context) => unknown;
/** Document triggers. Paths are patterns: {name} matches one part (in event.params), {rest=**} the rest. */
export declare const onDocument: {
    created: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => WincheFunction<DocumentEvent, unknown>;
    updated: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => WincheFunction<DocumentEvent, unknown>;
    deleted: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => WincheFunction<DocumentEvent, unknown>;
    /** Created, updated or deleted. */
    written: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => WincheFunction<DocumentEvent, unknown>;
    /** Before a document is created, updated or deleted (each document of a batch, and of a recursive delete, once). */
    willBeWritten: (path: string, handler: (e: BeforeDocumentEvent, ctx: Context) => DocumentChanges | void | Promise<DocumentChanges | void>, options?: BeforeOptions) => WincheFunction<BeforeDocumentEvent, void | DocumentChanges>;
};
/** File triggers. */
export declare const onFile: {
    uploaded: (path: string, handler: Handler<FileEvent>, options?: FunctionOptions) => WincheFunction<FileEvent, unknown>;
    deleted: (path: string, handler: Handler<FileEvent>, options?: FunctionOptions) => WincheFunction<FileEvent, unknown>;
    metadataUpdated: (path: string, handler: Handler<FileEvent>, options?: FunctionOptions) => WincheFunction<FileEvent, unknown>;
    /** Before a file is registered or replaced, its metadata changes, or it's deleted (each file under a deleted folder too). */
    willBeWritten: (path: string, handler: (e: BeforeFileEvent, ctx: Context) => FileChanges | void | Promise<FileChanges | void>, options?: BeforeOptions) => WincheFunction<BeforeFileEvent, void | FileChanges>;
};
/** User triggers (Winche Auth). */
export declare const onUser: {
    created: (handler: Handler<UserEvent>, options?: FunctionOptions) => WincheFunction<UserEvent, unknown>;
    deleted: (handler: Handler<UserEvent>, options?: FunctionOptions) => WincheFunction<UserEvent, unknown>;
    signedIn: (handler: Handler<UserEvent>, options?: FunctionOptions) => WincheFunction<UserEvent, unknown>;
    disabled: (handler: Handler<UserEvent>, options?: FunctionOptions) => WincheFunction<UserEvent, unknown>;
    /** Before someone becomes a user: throw HttpsError to refuse the sign-up. */
    willBeCreated: (handler: (e: BeforeCreateEvent, ctx: Context) => void | Promise<void>, options?: BeforeOptions) => WincheFunction<BeforeCreateEvent, void>;
    /** Before a sign-in succeeds: throw HttpsError to refuse it. */
    willSignIn: (handler: (e: BeforeSignInEvent, ctx: Context) => void | Promise<void>, options?: BeforeOptions) => WincheFunction<BeforeSignInEvent, void>;
};
/** Asset triggers: a release published, a channel moved. With options.channel, only that channel's. */
export declare const onAssets: {
    released: (handler: Handler<AssetEvent>, options?: FunctionOptions & {
        channel?: string;
    }) => WincheFunction<AssetEvent, unknown>;
    channelMoved: (handler: Handler<AssetEvent>, options?: FunctionOptions & {
        channel?: string;
    }) => WincheFunction<AssetEvent, unknown>;
};
/** Runs on a schedule: a cron expression ("minute hour day month weekday"), in UTC unless timeZone is given. */
export declare const schedule: (cron: string, handler: Handler<ScheduleEvent>, options?: FunctionOptions & {
    timeZone?: string;
}) => WincheFunction<ScheduleEvent, unknown>;
/** Called by apps (the SDKs' functions.call("name", data)); returns JSON to the caller. */
export declare const callable: <D = Json, R = Json>(handler: (data: D, ctx: Context) => R | Promise<R>, options?: FunctionOptions) => WincheFunction<D, R>;
/**
 * A page in the console made of your own files: `pages({ title: "Reports", files: "console/reports" })` shows
 * console/reports/index.html in a sandboxed frame. In it, @winche-technologies/winche-admin acts as the person who opened it.
 */
export declare const pages: (options: PageOptions) => WincheFunction<never, null>;
export interface HttpOptions extends FunctionOptions {
    /** The methods it answers (GET, POST, PUT, PATCH, DELETE, HEAD); every method when not given. */
    methods?: ("GET" | "POST" | "PUT" | "PATCH" | "DELETE" | "HEAD")[];
}
/** The request an HTTP function gets: a standard Request, with the caller's address. */
export type HttpRequest = Request & {
    ip: string | null;
};
/**
 * Answers web requests at /functions/v1/http/{name} (and below it): links in emails, other services' webhooks. It gets
 * a standard Request and returns a Response (or text, or JSON). Anyone can call it: check what you need yourself (a
 * signature with a secret, a token in the link). Bodies up to 1 MB.
 */
export declare const http: {
    onRequest: (handler: (req: HttpRequest, ctx: Context) => Response | string | Json | void | Promise<Response | string | Json | void>, options?: HttpOptions) => WincheFunction<HttpRequest, string | number | boolean | void | Json[] | {
        [key: string]: Json;
    } | Response | null>;
};
/**
 * An installed copy of a plugin, as its functions see it: the id it was installed under, the settings the installer
 * gave (the ones its winche-plugin.json lists, with their defaults), and its secrets.
 */
export interface PluginInstance {
    /** The id it's installed under (e.g. "sms"): its functions are called "sms.send", …. */
    readonly id: string;
    /** Every setting its winche-plugin.json lists, as the installer set it (or its default). */
    readonly settings: {
        readonly [name: string]: Json;
    };
    /** One setting's value; throws for a name winche-plugin.json doesn't list. */
    setting<T extends Json = Json>(name: string): T;
    /**
     * One of its secrets (a name winche-plugin.json lists, e.g. "AUTH_TOKEN"): a handle to list in a function's
     * `secrets` and read with `value()`, as for `secrets.define`. Each installed copy has its own (the project's secret
     * `SMS__AUTH_TOKEN` for the copy installed as "sms").
     */
    secret(name: string): Secret;
}
/** @internal How the server installs a plugin: what its wrapper module passes. */
export interface PluginInstallation {
    id: string;
    settings: {
        [name: string]: Json;
    };
    /** The plugin's secret names → the project secrets they are for this copy. */
    secrets: {
        [name: string]: string;
    };
}
/** A plugin package's default export: its functions, made for each installed copy. */
export interface Plugin {
    readonly __wincheplugin: 1;
    /** Makes one installed copy's functions (the server does, for each copy). Tests can call it too. */
    install(installation: PluginInstallation): PluginFunctions;
}
/** @internal One installed copy's functions, as the server finds them. */
export interface PluginFunctions {
    readonly __wincheGroup: 1;
    readonly id: string;
    readonly functions: {
        readonly [name: string]: WincheFunction<any, any>;
    };
}
/**
 * Defines a plugin: functions that projects install, configure and upgrade from the console or winche.json instead
 * of writing them. The package's default export; its winche-plugin.json lists the settings and secrets an installer
 * gives. `make` runs once for each installed copy and returns its functions by name:
 *
 *   export default plugin((p) => {
 *     const token = p.secret("AUTH_TOKEN");
 *     return {
 *       send: callable(async (data: { to: string; body: string }, ctx) => { … token.value() … p.setting("FROM") … }, { secrets: [token] }),
 *       queued: onDocument.created(`${p.setting("QUEUE")}/{id}`, async (e) => { … }, { secrets: [token] }),
 *     };
 *   });
 *
 * Apps call a copy's callables by its id: functions.call("sms.send", { … }).
 */
export declare function plugin(make: (p: PluginInstance) => {
    [name: string]: WincheFunction<any, any>;
}): Plugin;
