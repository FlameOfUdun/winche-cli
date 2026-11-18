// @ts-self-types="./index.d.ts"
// src/admin.ts
var GeoPoint = class _GeoPoint {
  latitude;
  longitude;
  constructor(latitude, longitude) {
    this.latitude = latitude;
    this.longitude = longitude;
  }
  isEqual(other) {
    return other instanceof _GeoPoint && other.latitude === this.latitude && other.longitude === this.longitude;
  }
};
var Link = class _Link {
  kind;
  target;
  resolved;
  /** @internal */
  constructor(kind, target, resolved = null) {
    this.kind = kind;
    this.target = target;
    this.resolved = resolved;
  }
  static document(path) {
    return new _Link("document", trimSlashes(path));
  }
  static file(path) {
    return new _Link("file", trimSlashes(path));
  }
  static user(uid) {
    return new _Link("user", uid);
  }
  /** What it points to, when the read expanded it and the target exists; null otherwise. */
  data() {
    return this.resolved?.() ?? null;
  }
  isEqual(other) {
    return other instanceof _Link && other.kind === this.kind && other.target === this.target;
  }
  toString() {
    return `${this.kind}:${this.target}`;
  }
};
var FieldValue = class _FieldValue {
  kind;
  operand;
  constructor(kind, operand) {
    this.kind = kind;
    this.operand = operand;
  }
  /** Removes the field (update, or set with merge / mergeFields). */
  static delete() {
    return new _FieldValue("delete");
  }
  /** The write's commit time. */
  static serverTimestamp() {
    return new _FieldValue("serverTimestamp");
  }
  /** Adds n (integers stay integers; a double makes a double). A missing or non-number field becomes n. */
  static increment(n) {
    return new _FieldValue("increment", n);
  }
  /** Keeps the larger of the field and n. */
  static maximum(n) {
    return new _FieldValue("maximum", n);
  }
  /** Keeps the smaller of the field and n. */
  static minimum(n) {
    return new _FieldValue("minimum", n);
  }
  /** Appends the elements not already in the array. */
  static arrayUnion(...elements) {
    return new _FieldValue("arrayUnion", elements);
  }
  /** Removes every element equal to one of these. */
  static arrayRemove(...elements) {
    return new _FieldValue("arrayRemove", elements);
  }
};
var base64 = {
  encode(bytes) {
    let s = "";
    for (let i = 0; i < bytes.length; i += 32768) s += String.fromCharCode(...bytes.subarray(i, i + 32768));
    return btoa(s);
  },
  decode(text) {
    return Uint8Array.from(atob(text), (c) => c.charCodeAt(0));
  }
};
var wireTime = (d) => {
  if (isNaN(d.getTime())) throw new Error("An invalid Date can't be stored.");
  return d.toISOString().replace(/\.(\d{3})Z$/, ".$1000Z");
};
var NO_EXPANSION = {
  included: {},
  resolved: {}
};
function encodeValue(v) {
  if (v === null || v === void 0) return {
    nullValue: null
  };
  if (typeof v === "boolean") return {
    booleanValue: v
  };
  if (typeof v === "bigint") return {
    integerValue: v.toString()
  };
  if (typeof v === "number") {
    if (Number.isNaN(v)) return {
      doubleValue: "NaN"
    };
    if (v === Infinity) return {
      doubleValue: "Infinity"
    };
    if (v === -Infinity) return {
      doubleValue: "-Infinity"
    };
    return Number.isInteger(v) && Number.isSafeInteger(v) ? {
      integerValue: String(v)
    } : {
      doubleValue: v
    };
  }
  if (typeof v === "string") return {
    stringValue: v
  };
  if (v instanceof Date) return {
    timestampValue: wireTime(v)
  };
  if (v instanceof Uint8Array) return {
    bytesValue: base64.encode(v)
  };
  if (v instanceof GeoPoint) return {
    geoPointValue: {
      latitude: v.latitude,
      longitude: v.longitude
    }
  };
  if (v instanceof Link) return v.kind === "document" ? {
    documentReferenceValue: v.target
  } : v.kind === "file" ? {
    fileReferenceValue: v.target
  } : {
    userReferenceValue: v.target
  };
  if (v instanceof DocumentReference) return {
    documentReferenceValue: v.path
  };
  if (v instanceof ChildReference) return {
    fileReferenceValue: v.path
  };
  if (v instanceof UserReference) return {
    userReferenceValue: v.uid
  };
  if (v instanceof FieldValue) throw new Error(`FieldValue.${v.kind}() can't be used here (only as a field of set or update, not inside an array).`);
  if (Array.isArray(v)) return {
    arrayValue: {
      values: v.map(encodeValue)
    }
  };
  if (typeof v === "object") return {
    mapValue: {
      fields: encodeFields(v)
    }
  };
  throw new Error(`A ${typeof v} can't be stored in a document.`);
}
function encodeFields(fields) {
  const out = {};
  for (const [k, v] of Object.entries(fields)) if (v !== void 0) out[k] = encodeValue(v);
  return out;
}
function decodeValue(t, x = NO_EXPANSION) {
  if (!t || typeof t !== "object") return null;
  if ("nullValue" in t) return null;
  if ("booleanValue" in t) return t.booleanValue;
  if ("integerValue" in t) {
    const n = Number(t.integerValue);
    return Number.isSafeInteger(n) ? n : BigInt(t.integerValue);
  }
  if ("doubleValue" in t) return typeof t.doubleValue === "number" ? t.doubleValue : Number(t.doubleValue);
  if ("stringValue" in t) return t.stringValue;
  if ("timestampValue" in t) return new Date(t.timestampValue);
  if ("bytesValue" in t) return base64.decode(t.bytesValue);
  if ("geoPointValue" in t) return new GeoPoint(t.geoPointValue.latitude, t.geoPointValue.longitude);
  if ("documentReferenceValue" in t) {
    const path = t.documentReferenceValue;
    return new Link("document", path, () => x.included[path] ? decodeFields(x.included[path].fields, x) : null);
  }
  if ("fileReferenceValue" in t) {
    const path = t.fileReferenceValue;
    return new Link("file", path, () => x.resolved[`file:${path}`] ? fileData(x.resolved[`file:${path}`]) : null);
  }
  if ("userReferenceValue" in t) {
    const uid = t.userReferenceValue;
    return new Link("user", uid, () => x.resolved[`user:${uid}`] ? userData(x.resolved[`user:${uid}`]) : null);
  }
  if ("arrayValue" in t) return (t.arrayValue?.values ?? []).map((v) => decodeValue(v, x));
  if ("mapValue" in t) return decodeFields(t.mapValue?.fields ?? {}, x);
  return null;
}
function decodeFields(fields, x = NO_EXPANSION) {
  const out = {};
  for (const [k, v] of Object.entries(fields ?? {})) out[k] = decodeValue(v, x);
  return out;
}
var isPlainObject = (v) => !!v && typeof v === "object" && !Array.isArray(v) && Object.getPrototypeOf(v) === Object.prototype;
function writeFields(data, dotted) {
  const transforms = [];
  const walk = (obj, prefix) => {
    const out = {};
    for (const [k, v] of Object.entries(obj)) {
      if (v === void 0) continue;
      const path = prefix ? `${prefix}.${k}` : k;
      if (v instanceof FieldValue) {
        if (v.kind === "delete") out[k] = {
          deleteField: true
        };
        else transforms.push(v.kind === "serverTimestamp" ? {
          field: path,
          kind: v.kind
        } : {
          field: path,
          kind: v.kind,
          operand: v.kind === "arrayUnion" || v.kind === "arrayRemove" ? encodeValue(v.operand) : encodeValue(v.operand)
        });
      } else if (isPlainObject(v)) {
        const inner = walk(v, path);
        if (Object.keys(inner).length > 0 || Object.keys(v).length === 0) out[k] = {
          mapValue: {
            fields: inner
          }
        };
      } else out[k] = encodeValue(v);
    }
    return out;
  };
  if (!dotted) return {
    fields: walk(data, ""),
    transforms
  };
  const fields = {};
  for (const [k, v] of Object.entries(data)) {
    if (v === void 0) continue;
    if (v instanceof FieldValue) {
      if (v.kind === "delete") fields[k] = {
        deleteField: true
      };
      else transforms.push(v.kind === "serverTimestamp" ? {
        field: k,
        kind: v.kind
      } : {
        field: k,
        kind: v.kind,
        operand: encodeValue(v.operand)
      });
    } else if (isPlainObject(v)) {
      const inner = walk(v, k);
      fields[k] = {
        mapValue: {
          fields: inner
        }
      };
    } else fields[k] = encodeValue(v);
  }
  return {
    fields,
    transforms
  };
}
var trimSlashes = (p) => p.replace(/^\/+|\/+$/g, "");
var segments = (p) => trimSlashes(p).split("/").filter((s) => s.length > 0);
var PROTOCOL = 1;
var WincheError = class extends Error {
  code;
  constructor(code, message) {
    super(message), this.code = code;
    this.name = "WincheError";
  }
};
var OPS = {
  "==": "eq",
  "!=": "ne",
  "<": "lt",
  "<=": "lte",
  ">": "gt",
  ">=": "gte",
  "in": "in",
  "not-in": "notIn",
  "array-contains": "arrayContains",
  "array-contains-any": "arrayContainsAny",
  "array-contains-all": "arrayContainsAll",
  "contains": "contains",
  "starts-with": "startsWith",
  "ends-with": "endsWith",
  "matches": "regex"
};
var Filter = class _Filter {
  wire;
  constructor(wire) {
    this.wire = wire;
  }
  static where(field, op, value) {
    if (!(op in OPS)) throw new Error(`'${op}' isn't an operator (${Object.keys(OPS).join(", ")}).`);
    return new _Filter({
      field,
      op: OPS[op],
      value: encodeValue(value)
    });
  }
  static and(...filters) {
    return new _Filter({
      and: filters.map((f) => f.wire)
    });
  }
  static or(...filters) {
    return new _Filter({
      or: filters.map((f) => f.wire)
    });
  }
  static not(filter) {
    return new _Filter({
      not: filter.wire
    });
  }
  /** The field is null. */
  static isNull(field) {
    return new _Filter({
      unary: "isNull",
      field
    });
  }
  /** The field is NaN. */
  static isNan(field) {
    return new _Filter({
      unary: "isNan",
      field
    });
  }
  /** The field is there (any value, null included). */
  static exists(field) {
    return new _Filter({
      unary: "exists",
      field
    });
  }
};
var Aggregate = {
  count: () => ({
    kind: "count"
  }),
  sum: (field) => ({
    kind: "sum",
    field
  }),
  average: (field) => ({
    kind: "average",
    field
  })
};
var Query = class _Query {
  call;
  state;
  /** @internal */
  constructor(call, state) {
    this.call = call;
    this.state = state;
  }
  with(change) {
    return new _Query(this.call, {
      ...this.state,
      ...change
    });
  }
  where(a, op, value) {
    const f = a instanceof Filter ? a : Filter.where(a, op, value);
    return this.with({
      filters: [
        ...this.state.filters,
        f.wire
      ]
    });
  }
  orderBy(field, direction = "asc") {
    return this.with({
      orderBy: [
        ...this.state.orderBy,
        {
          field,
          direction
        }
      ]
    });
  }
  limit(n) {
    return this.with({
      limit: n,
      limitToLast: void 0
    });
  }
  /** The last n of an ordered query (in order). */
  limitToLast(n) {
    return this.with({
      limitToLast: n,
      limit: void 0
    });
  }
  offset(n) {
    return this.with({
      offset: n
    });
  }
  /** Cursors: the orderBy fields' values, or a snapshot (its values for them, and its path). */
  startAt(...at) {
    return this.with({
      start: {
        values: cursor(at),
        before: true
      }
    });
  }
  startAfter(...at) {
    return this.with({
      start: {
        values: cursor(at),
        before: false
      }
    });
  }
  endAt(...at) {
    return this.with({
      end: {
        values: cursor(at),
        before: false
      }
    });
  }
  endBefore(...at) {
    return this.with({
      end: {
        values: cursor(at),
        before: true
      }
    });
  }
  /** Only these fields in the results (the server leaves the rest out). */
  select(...fields) {
    return this.with({
      select: fields
    });
  }
  /** Follow these reference fields: their Links' data() is what they point to. */
  expand(paths) {
    return this.with({
      expand: paths
    });
  }
  /** @internal The query in the wire format. */
  toWire() {
    const s = this.state;
    const cursorWire = (c) => {
      if (!c) return void 0;
      if (c.values instanceof DocumentSnapshot) {
        const snap = c.values;
        const values = s.orderBy.map((o) => encodeValue(snap.get(o.field)));
        values.push({
          documentReferenceValue: snap.reference.path
        });
        return {
          values,
          before: c.before
        };
      }
      return {
        values: c.values.map(encodeValue),
        before: c.before
      };
    };
    return {
      collection: s.collection,
      ...s.group ? {
        collectionGroup: true
      } : {},
      ...s.filters.length === 1 ? {
        where: s.filters[0]
      } : s.filters.length > 1 ? {
        where: {
          and: s.filters
        }
      } : {},
      ...s.orderBy.length ? {
        orderBy: s.orderBy
      } : {},
      ...s.limit !== void 0 ? {
        limit: s.limit
      } : {},
      ...s.limitToLast !== void 0 ? {
        limitToLast: s.limitToLast
      } : {},
      ...s.offset !== void 0 ? {
        offset: s.offset
      } : {},
      ...s.start ? {
        start: cursorWire(s.start)
      } : {},
      ...s.end ? {
        end: cursorWire(s.end)
      } : {},
      ...s.select ? {
        select: s.select
      } : {}
    };
  }
  async get() {
    const r = await this.call("db.query", {
      query: this.toWire(),
      ...this.state.expand ? {
        expand: this.state.expand
      } : {}
    });
    const x = {
      included: r.included ?? {},
      resolved: r.resolved ?? {}
    };
    return new QuerySnapshot((r.documents ?? []).map((d) => DocumentSnapshot.of(this.call, d, x)), r.hasMore === true);
  }
  /** How many documents match (no limit). */
  async count() {
    return (await this.call("db.count", {
      query: this.toWire()
    })).count;
  }
  /** The sum of a numeric field over the matches. */
  async sum(field) {
    return (await this.aggregate({
      v: Aggregate.sum(field)
    })).v;
  }
  /** The average of a numeric field over the matches (null when none). */
  async average(field) {
    return (await this.aggregate({
      v: Aggregate.average(field)
    })).v;
  }
  /** Several at once: `aggregate({ n: Aggregate.count(), total: Aggregate.sum("amount") })`. */
  async aggregate(aggregates) {
    const r = await this.call("db.aggregate", {
      query: this.toWire(),
      aggregations: Object.entries(aggregates).map(([alias, a]) => ({
        alias,
        ...a
      }))
    });
    return decodeFields(r.result);
  }
};
var cursor = (at) => at.length === 1 && at[0] instanceof DocumentSnapshot ? at[0] : at;
var CollectionReference = class extends Query {
  path;
  /** @internal */
  constructor(call, path) {
    super(call, {
      collection: path,
      group: false,
      filters: [],
      orderBy: []
    }), this.path = path;
  }
  get id() {
    return this.path.slice(this.path.lastIndexOf("/") + 1);
  }
  /** The document this collection is under (null at the top). */
  get parent() {
    const i = this.path.lastIndexOf("/");
    return i < 0 ? null : new DocumentReference(this.call, this.path.slice(0, i));
  }
  /** A document in it; without an id, a new random one. */
  doc(id) {
    return new DocumentReference(this.call, `${this.path}/${id ?? newId()}`);
  }
  /** Adds a document with a new id. */
  async add(data) {
    const r = await this.call("db.add", {
      collection: this.path,
      fields: encodeFields(data)
    });
    return new DocumentReference(this.call, r.document.path);
  }
};
var ID_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
var newId = () => [
  ...crypto.getRandomValues(new Uint8Array(20))
].map((b) => ID_CHARS[b % 62]).join("");
var DocumentReference = class _DocumentReference {
  call;
  path;
  expandPaths;
  /** @internal */
  constructor(call, path, expandPaths) {
    this.call = call;
    this.path = path;
    this.expandPaths = expandPaths;
  }
  get id() {
    return this.path.slice(this.path.lastIndexOf("/") + 1);
  }
  get parent() {
    return new CollectionReference(this.call, this.path.slice(0, this.path.lastIndexOf("/")));
  }
  collection(path) {
    return new CollectionReference(this.call, `${this.path}/${trimSlashes(path)}`);
  }
  /** Follow these reference fields on get(): their Links' data() is what they point to. */
  expand(paths) {
    return new _DocumentReference(this.call, this.path, paths);
  }
  async get() {
    const r = await this.call("db.get", {
      path: this.path,
      ...this.expandPaths ? {
        expand: this.expandPaths
      } : {}
    });
    return r.document ? DocumentSnapshot.of(this.call, r.document, {
      included: r.included ?? {},
      resolved: r.resolved ?? {}
    }) : new DocumentSnapshot(this, false, null, null);
  }
  /** Replaces the document (or merges into it, or writes the mergeFields only). */
  async set(data, options) {
    await commit(this.call, [
      setWrite(this.path, data, options)
    ]);
  }
  /** Changes these fields (dotted paths reach into maps) of an existing document. */
  async update(data, options) {
    await commit(this.call, [
      updateWrite(this.path, data, options?.precondition)
    ]);
  }
  /** Deletes it; with cascade, everything under it too (in one write: for big trees, use documents.recursiveDelete). */
  async delete(options) {
    await commit(this.call, [
      deleteWrite(this.path, options)
    ]);
  }
  isEqual(other) {
    return other instanceof _DocumentReference && other.path === this.path;
  }
};
var setWrite = (path, data, o) => {
  const { fields, transforms } = writeFields(data, false);
  return {
    set: {
      path,
      fields,
      ...o?.merge ? {
        merge: true
      } : {},
      ...o?.mergeFields ? {
        mergeFields: o.mergeFields
      } : {},
      ...transforms.length ? {
        transforms
      } : {},
      ...o?.precondition ? {
        precondition: o.precondition
      } : {}
    }
  };
};
var updateWrite = (path, data, precondition) => {
  const { fields, transforms } = writeFields(data, true);
  return {
    update: {
      path,
      fields,
      ...transforms.length ? {
        transforms
      } : {},
      ...precondition ? {
        precondition
      } : {}
    }
  };
};
var deleteWrite = (path, o) => ({
  delete: {
    path,
    ...o?.cascade ? {
      cascade: true
    } : {},
    ...o?.precondition ? {
      precondition: o.precondition
    } : {}
  }
});
var commit = (call, writes) => call("db.write", {
  writes
});
var DocumentSnapshot = class _DocumentSnapshot {
  reference;
  exists;
  fields;
  meta;
  /** @internal */
  constructor(reference, exists, fields, meta) {
    this.reference = reference;
    this.exists = exists;
    this.fields = fields;
    this.meta = meta;
  }
  /** @internal From the wire (with the read's expansion for its links). */
  static of(call, d, x = NO_EXPANSION) {
    return new _DocumentSnapshot(new DocumentReference(call, d.path), true, decodeFields(d.fields, x), {
      createTime: d.createTime,
      updateTime: d.updateTime,
      version: d.version
    });
  }
  get id() {
    return this.reference.id;
  }
  /** The fields (undefined when it doesn't exist). */
  data() {
    return this.fields ?? void 0;
  }
  /** One field, by dotted path ("address.city"); undefined when it isn't there. */
  get(fieldPath) {
    let v = this.fields;
    for (const k of fieldPath.split(".")) {
      if (v === null || typeof v !== "object" || !(k in v)) return void 0;
      v = v[k];
    }
    return v;
  }
  get createTime() {
    return this.meta ? new Date(this.meta.createTime) : null;
  }
  get updateTime() {
    return this.meta ? new Date(this.meta.updateTime) : null;
  }
  /** The update time exactly as the server has it (microseconds), for `precondition: { updateTime }`. */
  get updateTimeRaw() {
    return this.meta?.updateTime ?? null;
  }
  get version() {
    return this.meta?.version ?? null;
  }
};
var QuerySnapshot = class {
  docs;
  hasMore;
  /** @internal */
  constructor(docs, hasMore) {
    this.docs = docs;
    this.hasMore = hasMore;
  }
  get size() {
    return this.docs.length;
  }
  get empty() {
    return this.docs.length === 0;
  }
  forEach(fn) {
    this.docs.forEach(fn);
  }
};
var WriteBatch = class {
  call;
  writes;
  /** @internal */
  constructor(call) {
    this.call = call;
    this.writes = [];
  }
  set(ref, data, options) {
    this.writes.push(setWrite(ref.path, data, options));
    return this;
  }
  update(ref, data, options) {
    this.writes.push(updateWrite(ref.path, data, options?.precondition));
    return this;
  }
  delete(ref, options) {
    this.writes.push(deleteWrite(ref.path, options));
    return this;
  }
  get size() {
    return this.writes.length;
  }
  async commit() {
    if (this.writes.length > 0) await commit(this.call, this.writes);
  }
};
var Transaction = class {
  call;
  writes;
  /** @internal path → updateTimeRaw (null: it didn't exist) */
  seen;
  /** @internal */
  constructor(call) {
    this.call = call;
    this.writes = [];
    this.seen = /* @__PURE__ */ new Map();
  }
  async get(ref) {
    if (this.writes.length > 0) throw new Error("In a transaction, read before you write.");
    const snap = await ref.get();
    this.seen.set(ref.path, snap.updateTimeRaw);
    return snap;
  }
  guard(path, given) {
    if (given) return given;
    if (!this.seen.has(path)) return void 0;
    const at = this.seen.get(path);
    return at === null ? {
      exists: false
    } : {
      updateTime: at
    };
  }
  set(ref, data, options) {
    this.writes.push(setWrite(ref.path, data, {
      ...options,
      precondition: this.guard(ref.path, options?.precondition)
    }));
    return this;
  }
  update(ref, data, options) {
    this.writes.push(updateWrite(ref.path, data, this.guard(ref.path, options?.precondition)));
    return this;
  }
  delete(ref, options) {
    this.writes.push(deleteWrite(ref.path, {
      precondition: this.guard(ref.path, options?.precondition)
    }));
    return this;
  }
  /** @internal */
  async commit() {
    if (this.writes.length > 0) await commit(this.call, this.writes);
  }
};
var Documents = class {
  call;
  /** @internal */
  constructor(call) {
    this.call = call;
  }
  /** A collection by path ("patients", "patients/p1/visits"). */
  collection(path) {
    if (segments(path).length % 2 !== 1) throw new Error(`'${path}' isn't a collection path (an odd number of parts).`);
    return new CollectionReference(this.call, segments(path).join("/"));
  }
  /** A document by path ("patients/p1"). */
  doc(path) {
    if (segments(path).length % 2 !== 0 || segments(path).length === 0) throw new Error(`'${path}' isn't a document path (an even number of parts).`);
    return new DocumentReference(this.call, segments(path).join("/"));
  }
  /** Every collection with this id, under any parent ("sessionHistory"). */
  collectionGroup(id) {
    return new Query(this.call, {
      collection: id,
      group: true,
      filters: [],
      orderBy: []
    });
  }
  /** Several documents in one call, in order. */
  async getAll(refs) {
    const r = await this.call("db.getAll", {
      paths: refs.map((ref) => ref.path)
    });
    return r.documents.map((d, i) => d ? DocumentSnapshot.of(this.call, d) : new DocumentSnapshot(refs[i], false, null, null));
  }
  batch() {
    return new WriteBatch(this.call);
  }
  /** Deletes a document and everything under it, in batches (any size); how many documents went. */
  async recursiveDelete(ref) {
    return await this.call("db.deleteTree", {
      path: ref.path
    });
  }
  /** Runs `fn` with a transaction and commits it; runs it again when something it read changed meanwhile. */
  async runTransaction(fn, options) {
    const attempts = options?.maxAttempts ?? 5;
    for (let i = 1; ; i++) {
      const tx = new Transaction(this.call);
      const result = await fn(tx);
      try {
        await tx.commit();
        return result;
      } catch (e) {
        const code = e?.code;
        if (i >= attempts || !(code === "failed-precondition" || code === "already-exists" || code === "not-found" || code === "aborted")) throw e;
      }
    }
  }
};
function fileData(r) {
  return {
    id: r.id,
    path: r.path,
    directory: r.directory,
    mimeType: r.mimeType,
    sizeBytes: r.sizeBytes,
    uploadStatus: typeof r.uploadStatus === "string" ? r.uploadStatus.toLowerCase() : r.uploadStatus === 1 ? "complete" : "pending",
    contentDisposition: r.contentDisposition ?? null,
    cacheControl: r.cacheControl ?? null,
    contentHash: r.contentHash ?? null,
    metadata: r.metadata ?? {},
    version: r.version,
    createdAt: new Date(r.createdAt),
    updatedAt: new Date(r.updatedAt)
  };
}
var ChildReference = class _ChildReference {
  call;
  path;
  /** @internal */
  constructor(call, path) {
    this.call = call;
    this.path = path;
  }
  /** The last part of the path. */
  get name() {
    return this.path.slice(this.path.lastIndexOf("/") + 1);
  }
  get parent() {
    const i = this.path.lastIndexOf("/");
    return this.path === "" ? null : new _ChildReference(this.call, i < 0 ? "" : this.path.slice(0, i));
  }
  child(path) {
    return new _ChildReference(this.call, segments(`${this.path}/${path}`).join("/"));
  }
  /** The file's record; `exists` is false when there's none. */
  async getSnapshot() {
    const r = await this.call("files.get", {
      path: this.path
    });
    return new FileSnapshot(this, r ? fileData(r) : null);
  }
  /** The bytes (up to 32 MB), or null when there's no file. */
  async getData() {
    const r = await this.call("files.read", {
      path: this.path
    });
    return r === null ? null : base64.decode(r.base64);
  }
  /** The contents as UTF-8 text, or null. */
  async getText() {
    const bytes = await this.getData();
    return bytes === null ? null : new TextDecoder().decode(bytes);
  }
  /** Writes the file (new or replacing one), up to 32 MB; its triggers and hooks run as for an upload. */
  async putData(data, options) {
    const bytes = typeof data === "string" ? new TextEncoder().encode(data) : data instanceof Uint8Array ? data : new Uint8Array(data);
    const r = await this.call("files.write", {
      path: this.path,
      base64: base64.encode(bytes),
      mimeType: options?.mimeType ?? (typeof data === "string" ? "text/plain; charset=utf-8" : null),
      metadata: options?.metadata ?? null,
      contentDisposition: options?.contentDisposition ?? null,
      cacheControl: options?.cacheControl ?? null
    });
    return new FileSnapshot(this, fileData(r));
  }
  /**
   * Writes the file from a URL (a GET with these headers), up to 5 GB: the bytes go from the URL to storage on the
   * server, never through the function. The type is the one given, else the response's.
   */
  async putFromUrl(url, options) {
    const r = await this.call("files.copyFromUrl", {
      path: this.path,
      url,
      headers: options?.headers ?? null,
      mimeType: options?.mimeType ?? null,
      metadata: options?.metadata ?? null,
      contentDisposition: options?.contentDisposition ?? null,
      cacheControl: options?.cacheControl ?? null
    });
    return new FileSnapshot(this, fileData(r));
  }
  /** One page of the folder: files and sub-folders in name order. */
  async list(options) {
    const r = await this.call("files.page", {
      folder: this.path,
      maxResults: options?.maxResults ?? null,
      pageToken: options?.pageToken ?? null,
      mimeType: options?.mimeType ?? null
    });
    return new DirectorySnapshot(this, (r.items ?? []).map((f) => new FileSnapshot(new _ChildReference(this.call, f.path), fileData(f))), (r.prefixes ?? []).map((p) => new _ChildReference(this.call, p)), r.nextPageToken ?? null);
  }
  /** Every file and sub-folder of the folder (all pages). */
  async listChildren(options) {
    const files2 = [], folders = [];
    let token;
    do {
      const page = await this.list({
        maxResults: 1e3,
        pageToken: token,
        mimeType: options?.mimeType
      });
      files2.push(...page.files);
      folders.push(...page.folders);
      token = page.nextPageToken ?? void 0;
    } while (token);
    return new DirectorySnapshot(this, files2, folders, null);
  }
  /** Merges metadata (null removes a key); optionally changes the download headers ("" clears one), only at ifVersion. */
  async updateMetadata(metadata, options) {
    const r = await this.call("files.updateMetadata", {
      path: this.path,
      metadata,
      contentDisposition: options?.contentDisposition ?? null,
      cacheControl: options?.cacheControl ?? null,
      ifVersion: options?.ifVersion ?? null
    });
    return new FileSnapshot(this, fileData(r));
  }
  /** Deletes the file, or everything under the folder. False when nothing was there. */
  async delete() {
    let one = false, refused = null;
    try {
      one = await this.call("files.delete", {
        path: this.path
      }) === true;
    } catch (e) {
      if (e?.code !== "permission-denied") throw e;
      refused = e;
    }
    const under = await this.call("files.deleteUnder", {
      folder: this.path
    });
    if (refused && !(under > 0)) throw refused;
    return one || under > 0;
  }
  /** A link anyone holding it can download the file with, until expiresAt. */
  async downloadUrl() {
    const r = await this.call("files.downloadUrl", {
      path: this.path
    });
    return {
      url: r.url,
      expiresAt: new Date(r.expiresAt)
    };
  }
  isEqual(other) {
    return other instanceof _ChildReference && other.path === this.path;
  }
};
var FileSnapshot = class {
  reference;
  data;
  /** @internal */
  constructor(reference, data) {
    this.reference = reference;
    this.data = data;
  }
  get exists() {
    return this.data !== null;
  }
};
var DirectorySnapshot = class {
  reference;
  files;
  folders;
  nextPageToken;
  /** @internal */
  constructor(reference, files2, folders, nextPageToken) {
    this.reference = reference;
    this.files = files2;
    this.folders = folders;
    this.nextPageToken = nextPageToken;
  }
};
var Files = class {
  call;
  /** @internal */
  constructor(call) {
    this.call = call;
  }
  /** A file or folder by path; "" is the top. */
  child(path) {
    return new ChildReference(this.call, segments(path).join("/"));
  }
};
function userData(u) {
  return {
    uid: u.uid,
    email: u.email ?? null,
    emailVerified: u.emailVerified === true,
    displayName: u.displayName ?? null,
    photoUrl: u.photoUrl ?? null,
    isAnonymous: u.isAnonymous === true,
    disabled: u.disabled === true,
    mfaEnabled: u.mfaEnabled === true,
    providers: u.providers ?? [],
    customClaims: u.customClaims ?? {},
    createdAt: u.createdAt ? new Date(u.createdAt) : null,
    lastSignInAt: u.lastSignInAt ? new Date(u.lastSignInAt) : null
  };
}
var UserReference = class _UserReference {
  call;
  uid;
  /** @internal */
  constructor(call, uid) {
    this.call = call;
    this.uid = uid;
  }
  /** Their account; `exists` is false when there's no such user. */
  async get() {
    const r = await this.call("users.get", {
      uid: this.uid
    });
    return new UserSnapshot(this, r ? userData(r) : null);
  }
  /** Their custom claims ({} when none). Throws not-found when there's no such user. */
  async getCustomClaims() {
    const r = await this.call("users.get", {
      uid: this.uid
    });
    if (!r) throw new WincheError("not-found", `There's no user ${this.uid}.`);
    return r.customClaims ?? {};
  }
  /** Replaces their custom claims (null clears them); they're in the user's next token. */
  async setCustomClaims(claims) {
    await this.call("users.setClaims", {
      uid: this.uid,
      claims
    });
  }
  isEqual(other) {
    return other instanceof _UserReference && other.uid === this.uid;
  }
};
var UserSnapshot = class {
  reference;
  data;
  /** @internal */
  constructor(reference, data) {
    this.reference = reference;
    this.data = data;
  }
  get uid() {
    return this.reference.uid;
  }
  get exists() {
    return this.data !== null;
  }
  /** Their custom claims ({} when none, or no user). */
  get customClaims() {
    return this.data?.customClaims ?? {};
  }
};
var Auth = class {
  call;
  /** @internal */
  constructor(call) {
    this.call = call;
  }
  user(uid) {
    return new UserReference(this.call, uid);
  }
};
var AssetFileReference = class {
  call;
  release;
  path;
  /** @internal */
  constructor(call, release, path) {
    this.call = call;
    this.release = release;
    this.path = path;
  }
  /** Its bytes (up to 32 MB: larger ones through downloadUrl). */
  async bytes() {
    const r = await this.call("assets.read", {
      release: this.release,
      path: this.path
    });
    if (!r) throw new WincheError("not-found", `There's no ${this.path} in release ${this.release}.`);
    return base64.decode(r.base64);
  }
  /** Its contents as UTF-8 text. */
  async text() {
    return new TextDecoder().decode(await this.bytes());
  }
  /** Its contents decoded from JSON. */
  async json() {
    return JSON.parse(await this.text());
  }
  /** A short-lived link to its bytes (to hand to someone, or to fetch a large one). */
  async downloadUrl() {
    const r = await this.call("assets.downloadUrl", {
      release: this.release,
      path: this.path
    });
    return {
      url: r.url,
      expiresAt: new Date(r.expiresAt)
    };
  }
};
var AssetRelease = class _AssetRelease {
  call;
  number;
  note;
  by;
  at;
  files;
  /** @internal */
  constructor(call, number, note, by, at, files2) {
    this.call = call;
    this.number = number;
    this.note = note;
    this.by = by;
    this.at = at;
    this.files = files2;
  }
  /** @internal */
  static of(call, r) {
    return new _AssetRelease(call, r.number, r.note ?? null, r.by ?? null, new Date(r.at), r.files ?? []);
  }
  /** Whether it has a file at path. */
  has(path) {
    return this.files.some((f) => f.path === path);
  }
  /** The paths under a folder ("translations"), or all of them. */
  paths(folder) {
    const prefix = folder ? trimSlashes(folder) + "/" : "";
    return this.files.map((f) => f.path).filter((p) => p.startsWith(prefix));
  }
  /** A file of this release (reading one it hasn't throws not-found). */
  file(path) {
    return new AssetFileReference(this.call, this.number, trimSlashes(path));
  }
};
var AssetChannelReference = class {
  call;
  name;
  /** @internal */
  constructor(call, name) {
    this.call = call;
    this.name = name;
  }
  /** The release it points at now, or null when there's no such channel. */
  async get() {
    const r = await this.call("assets.channel", {
      name: this.name
    });
    return r ? AssetRelease.of(this.call, r) : null;
  }
};
var Assets = class {
  call;
  /** @internal */
  constructor(call) {
    this.call = call;
  }
  /** A channel by name ("production"). */
  channel(name) {
    return new AssetChannelReference(this.call, name);
  }
  /** A release by number, or null. */
  async release(number) {
    const r = await this.call("assets.release", {
      number
    });
    return r ? AssetRelease.of(this.call, r) : null;
  }
  /** The channels and the release each points at. */
  async channels() {
    return (await this.call("assets.channels", {})).map((c) => ({
      name: c.name,
      release: c.release,
      updatedAt: new Date(c.updatedAt),
      by: c.by ?? null
    }));
  }
  /** The releases, newest first, without their files. */
  async releases(limit = 50) {
    return (await this.call("assets.releases", {
      limit
    })).map((r) => ({
      ...r,
      at: new Date(r.at)
    }));
  }
};
var Functions = class {
  send;
  /** @internal */
  constructor(send) {
    this.send = send;
  }
  /** Runs the callable `name` with `data` and returns what it returns (its HttpsError as a WincheError with its code). */
  async call(name, data) {
    return await this.send("functions.call", {
      name,
      data: data === void 0 ? null : data
    });
  }
};
var Admin = class _Admin {
  transport;
  documents;
  files;
  auth;
  assets;
  functions;
  /** @internal */
  constructor(transport) {
    this.transport = transport;
    this.documents = new Documents(transport);
    this.files = new Files(transport);
    this.auth = new Auth(transport);
    this.assets = new Assets(transport);
    this.functions = new Functions(transport);
  }
  /**
   * The same API acting as one of the app's users (`ctx.caller` in a callable): every documents and files call is
   * checked against the project's access rules for them, and refused with permission-denied as an app would be.
   * Auth, assets and functions calls are refused (rules don't cover them).
   */
  as(user) {
    if (!user || typeof user.uid !== "string" || !user.uid) throw new WincheError("invalid-argument", "as(user): give a user with a uid (e.g. ctx.caller).");
    const who = {
      uid: user.uid,
      token: user.token ?? {}
    };
    return new _Admin((op, args) => this.transport(op, {
      ...args,
      as: who
    }));
  }
};
function initializeAdmin(options) {
  const server = (options?.server ?? "").replace(/\/+$/, "");
  if (!/^https?:\/\//.test(server)) throw new WincheError("invalid-argument", "initializeAdmin: give the server's address, e.g. https://winche.example.com.");
  if (!options.project) throw new WincheError("invalid-argument", "initializeAdmin: give the project's id.");
  if ([
    options.key,
    options.identityToken,
    options.token
  ].filter(Boolean).length !== 1) throw new WincheError("invalid-argument", "initializeAdmin: give one of key (a service account's), identityToken (a trusted identity's) or token.");
  const base = `${server}/console/v1/projects/${encodeURIComponent(options.project)}`;
  const send = options.fetch ?? fetch;
  const post = async (url, body, bearer) => {
    try {
      return await send(url, {
        method: "POST",
        body: JSON.stringify(body),
        headers: {
          "content-type": "application/json",
          "x-winche-protocol": String(PROTOCOL),
          ...bearer ? {
            authorization: `Bearer ${bearer}`
          } : {}
        }
      });
    } catch (e) {
      throw new WincheError("unavailable", `Couldn't reach ${server}: ${e?.message ?? e}`);
    }
  };
  const failure = async (response) => {
    const error = (await response.json().catch(() => null))?.error ?? {};
    return new WincheError(error.code ?? (response.status === 401 ? "unauthenticated" : response.status === 403 ? "permission-denied" : "internal"), error.message ?? `The server answered ${response.status}.`);
  };
  let current = null;
  let exchanging = null;
  const exchange = async () => {
    const body = options.key ? {
      key: options.key
    } : {
      assertion: await options.identityToken()
    };
    const response = await post(`${base}/service-accounts/token`, body);
    if (!response.ok) throw await failure(response);
    const answer = await response.json();
    current = {
      token: answer.accessToken,
      renewAt: Date.parse(answer.expiresAt) - 6e4
    };
    return current.token;
  };
  const accessToken = async (fresh) => {
    if (options.token) return typeof options.token === "function" ? await options.token() : options.token;
    if (!fresh && current && Date.now() < current.renewAt) return current.token;
    exchanging ??= exchange().finally(() => {
      exchanging = null;
    });
    return await exchanging;
  };
  return new Admin(async (op, args) => {
    let response = await post(`${base}/admin`, {
      op,
      args
    }, await accessToken(false));
    if (response.status === 401 && !options.token) response = await post(`${base}/admin`, {
      op,
      args
    }, await accessToken(true));
    if (!response.ok) throw await failure(response);
    const body = await response.json().catch(() => null);
    if (body?.error) throw new WincheError(body.error.code ?? "internal", body.error.message ?? "The server refused it.");
    return body?.result ?? null;
  });
}
var ambient = (op, args) => {
  const g = globalThis;
  const run = g[Symbol.for("winche.run")]?.() ?? g[Symbol.for("winche.test")]?.();
  if (run?.protocol !== void 0 && run.protocol !== PROTOCOL) return Promise.reject(mismatch(run.protocol));
  if (run?.call) return run.call(op, args);
  const page = g[Symbol.for("winche.page")];
  if (typeof page === "function") return page(op, args, PROTOCOL);
  return Promise.reject(new WincheError("failed-precondition", run ? "This test gave no server: use withServer(...) from @winche/functions/testing to reach one." : "@winche/admin isn't signed in here. Functions and console pages are signed in already (call it while a function runs, not at a module's top level); anywhere else use initializeAdmin({ server, project, key })."));
};
var mismatch = (server) => new WincheError("failed-precondition", `This @winche/admin speaks protocol ${PROTOCOL}; the server speaks ${server}: use the @winche/admin version made for it.`);
var signedIn = new Admin(ambient);
var documents = signedIn.documents;
var files = signedIn.files;
var auth = signedIn.auth;
var assets = signedIn.assets;
var functions = signedIn.functions;
var as = (user) => signedIn.as(user);
export {
  Admin,
  Aggregate,
  AssetChannelReference,
  AssetFileReference,
  AssetRelease,
  Assets,
  Auth,
  ChildReference,
  CollectionReference,
  DirectorySnapshot,
  DocumentReference,
  DocumentSnapshot,
  Documents,
  FieldValue,
  FileSnapshot,
  Files,
  Filter,
  Functions,
  GeoPoint,
  Link,
  PROTOCOL,
  Query,
  QuerySnapshot,
  Transaction,
  UserReference,
  UserSnapshot,
  WincheError,
  WriteBatch,
  as,
  assets,
  auth,
  decodeFields,
  decodeValue,
  documents,
  encodeFields,
  encodeValue,
  fileData,
  files,
  functions,
  initializeAdmin,
  userData
};
