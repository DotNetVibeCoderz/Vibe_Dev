/**
 * Typed JSON Schema builder for tool parameters (no dependencies).
 *
 * Each builder produces a JSON Schema *and* carries the matching TypeScript type, so a tool handler's arguments
 * are inferred from the schema and typos are compile errors:
 *
 * ```ts
 * const params = s.object({
 *   city: s.string().describe("City name"),
 *   unit: s.enum(["celsius", "fahrenheit"]).optional(),
 * });
 * type Params = Infer<typeof params>;   // { city: string; unit?: "celsius" | "fahrenheit" }
 * ```
 *
 * Zod 4 schemas work too: anything with `toJSONSchema()` (and optionally `parse()`) is accepted wherever a
 * {@link SchemaLike} is expected.
 */

/** The subset of JSON Schema produced by the builder (and accepted from Zod). */
export interface JsonSchema {
  type?: "object" | "string" | "number" | "integer" | "boolean" | "array" | "null";
  description?: string;
  properties?: Record<string, JsonSchema>;
  required?: string[];
  additionalProperties?: boolean | JsonSchema;
  items?: JsonSchema;
  enum?: readonly (string | number | boolean)[];
  anyOf?: JsonSchema[];
  default?: unknown;
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  minItems?: number;
  maxItems?: number;
}

/** Anything that can describe tool parameters: a builder schema or a Zod schema (`toJSONSchema()` + `_output`). */
export interface SchemaLike<T = unknown> {
  readonly _output: T;
  toJSONSchema(): JsonSchema | Record<string, unknown>;
  parse?(value: unknown): T;
}

/** The TypeScript type described by a schema. */
export type Infer<S> = S extends SchemaLike<infer T> ? T : never;

/** Raised when tool arguments do not match the declared schema. */
export class SchemaValidationError extends Error {
  constructor(readonly path: string, message: string) {
    super(`${path || "arguments"}: ${message}`);
    this.name = "SchemaValidationError";
  }
}

/** Base class of all builder schemas. */
export abstract class Schema<T> implements SchemaLike<T> {
  declare readonly _output: T;
  protected meta: { description?: string; default?: unknown } = {};

  /** Adds a description the model sees. */
  describe(description: string): this {
    const copy = this.clone();
    copy.meta = { ...this.meta, description };
    return copy;
  }

  /** Marks an object property as optional. */
  optional(): OptionalSchema<T> {
    return new OptionalSchema(this);
  }

  /** Default value advertised to the model (the property becomes optional). */
  default(value: T): OptionalSchema<T> {
    const copy = this.clone();
    copy.meta = { ...this.meta, default: value };
    return new OptionalSchema(copy);
  }

  toJSONSchema(): JsonSchema {
    const schema = this.build();
    if (this.meta.description !== undefined) schema.description = this.meta.description;
    if (this.meta.default !== undefined) schema.default = this.meta.default;
    return schema;
  }

  /** Validates a value against the schema (called on tool arguments before the handler runs). */
  parse(value: unknown): T {
    return this.check(value, "");
  }

  /** @internal */
  abstract check(value: unknown, path: string): T;
  protected abstract build(): JsonSchema;
  protected clone(): this {
    return Object.assign(Object.create(Object.getPrototypeOf(this)), this);
  }
}

function fail(path: string, message: string): never {
  throw new SchemaValidationError(path, message);
}

export class StringSchema<T extends string = string> extends Schema<T> {
  private limits: Pick<JsonSchema, "minLength" | "maxLength" | "pattern"> = {};
  min(length: number): this { const c = this.clone(); c.limits = { ...this.limits, minLength: length }; return c; }
  max(length: number): this { const c = this.clone(); c.limits = { ...this.limits, maxLength: length }; return c; }
  regex(pattern: RegExp | string): this {
    const c = this.clone();
    c.limits = { ...this.limits, pattern: typeof pattern === "string" ? pattern : pattern.source };
    return c;
  }
  protected build(): JsonSchema { return { type: "string", ...this.limits }; }
  check(value: unknown, path: string): T {
    if (typeof value !== "string") fail(path, "expected a string");
    if (this.limits.minLength !== undefined && value.length < this.limits.minLength) fail(path, `must have at least ${this.limits.minLength} characters`);
    if (this.limits.maxLength !== undefined && value.length > this.limits.maxLength) fail(path, `must have at most ${this.limits.maxLength} characters`);
    if (this.limits.pattern !== undefined && !new RegExp(this.limits.pattern).test(value)) fail(path, `must match ${this.limits.pattern}`);
    return value as T;
  }
}

export class NumberSchema extends Schema<number> {
  private limits: Pick<JsonSchema, "minimum" | "maximum"> = {};
  constructor(private readonly integer: boolean) { super(); }
  min(value: number): this { const c = this.clone(); c.limits = { ...this.limits, minimum: value }; return c; }
  max(value: number): this { const c = this.clone(); c.limits = { ...this.limits, maximum: value }; return c; }
  protected build(): JsonSchema { return { type: this.integer ? "integer" : "number", ...this.limits }; }
  check(value: unknown, path: string): number {
    if (typeof value !== "number" || Number.isNaN(value)) fail(path, "expected a number");
    if (this.integer && !Number.isInteger(value)) fail(path, "expected an integer");
    if (this.limits.minimum !== undefined && value < this.limits.minimum) fail(path, `must be >= ${this.limits.minimum}`);
    if (this.limits.maximum !== undefined && value > this.limits.maximum) fail(path, `must be <= ${this.limits.maximum}`);
    return value;
  }
}

export class BooleanSchema extends Schema<boolean> {
  protected build(): JsonSchema { return { type: "boolean" }; }
  check(value: unknown, path: string): boolean {
    if (typeof value !== "boolean") fail(path, "expected a boolean");
    return value;
  }
}

export class EnumSchema<const V extends readonly [string, ...string[]]> extends Schema<V[number]> {
  constructor(readonly values: V) { super(); }
  protected build(): JsonSchema { return { type: "string", enum: this.values }; }
  check(value: unknown, path: string): V[number] {
    if (typeof value !== "string" || !this.values.includes(value)) fail(path, `expected one of ${this.values.join(", ")}`);
    return value as V[number];
  }
}

export class ArraySchema<I> extends Schema<I[]> {
  private limits: Pick<JsonSchema, "minItems" | "maxItems"> = {};
  constructor(readonly item: Schema<I>) { super(); }
  min(count: number): this { const c = this.clone(); c.limits = { ...this.limits, minItems: count }; return c; }
  max(count: number): this { const c = this.clone(); c.limits = { ...this.limits, maxItems: count }; return c; }
  protected build(): JsonSchema { return { type: "array", items: this.item.toJSONSchema(), ...this.limits }; }
  check(value: unknown, path: string): I[] {
    if (!Array.isArray(value)) fail(path, "expected an array");
    if (this.limits.minItems !== undefined && value.length < this.limits.minItems) fail(path, `must have at least ${this.limits.minItems} items`);
    if (this.limits.maxItems !== undefined && value.length > this.limits.maxItems) fail(path, `must have at most ${this.limits.maxItems} items`);
    return value.map((v, i) => this.item.check(v, `${path}[${i}]`));
  }
}

export class OptionalSchema<T> extends Schema<T | undefined> {
  readonly isOptional = true;
  constructor(readonly inner: Schema<T>) { super(); }
  override toJSONSchema(): JsonSchema {
    const schema = this.inner.toJSONSchema();
    if (this.meta.description !== undefined) schema.description = this.meta.description;
    return schema;
  }
  protected build(): JsonSchema { return this.inner.toJSONSchema(); }
  check(value: unknown, path: string): T | undefined {
    return value === undefined || value === null ? undefined : this.inner.check(value, path);
  }
}

/** Object shape: property name → schema. */
export type Shape = Record<string, Schema<any>>;

type OptionalKeys<S extends Shape> = { [K in keyof S]: S[K] extends OptionalSchema<any> ? K : never }[keyof S];
type RequiredKeys<S extends Shape> = Exclude<keyof S, OptionalKeys<S>>;
type Simplify<T> = { [K in keyof T]: T[K] } & {};

/** The object type described by a shape (optional properties become `?:`). */
export type ObjectOf<S extends Shape> = Simplify<
  { [K in RequiredKeys<S>]: Infer<S[K]> } & { [K in OptionalKeys<S>]?: Infer<S[K]> }
>;

export class ObjectSchema<S extends Shape> extends Schema<ObjectOf<S>> {
  constructor(readonly shape: S) { super(); }
  protected build(): JsonSchema {
    const properties: Record<string, JsonSchema> = {};
    const required: string[] = [];
    for (const [key, schema] of Object.entries(this.shape)) {
      properties[key] = schema.toJSONSchema();
      if (!(schema instanceof OptionalSchema)) required.push(key);
    }
    return { type: "object", properties, ...(required.length > 0 ? { required } : {}), additionalProperties: false };
  }
  check(value: unknown, path: string): ObjectOf<S> {
    if (typeof value !== "object" || value === null || Array.isArray(value)) fail(path, "expected an object");
    const input = value as Record<string, unknown>;
    const out: Record<string, unknown> = {};
    for (const [key, schema] of Object.entries(this.shape)) {
      const child = path ? `${path}.${key}` : key;
      if (input[key] === undefined && !(schema instanceof OptionalSchema)) fail(child, "is required");
      const parsed = schema.check(input[key], child);
      if (parsed !== undefined) out[key] = parsed;
    }
    return out as ObjectOf<S>;
  }
}

/** Schema builders. */
export const s = {
  string: () => new StringSchema(),
  number: () => new NumberSchema(false),
  integer: () => new NumberSchema(true),
  boolean: () => new BooleanSchema(),
  enum: <const V extends readonly [string, ...string[]]>(values: V) => new EnumSchema(values),
  array: <I>(item: Schema<I>) => new ArraySchema(item),
  object: <S extends Shape>(shape: S) => new ObjectSchema(shape),
};

/** @internal Resolves a schema (builder, Zod or omitted) to plain JSON Schema. */
export function toJsonSchema(schema: SchemaLike | undefined): Record<string, unknown> {
  return schema ? (schema.toJSONSchema() as Record<string, unknown>) : { type: "object", properties: {} };
}
