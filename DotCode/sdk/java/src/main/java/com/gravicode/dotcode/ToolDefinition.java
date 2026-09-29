package com.gravicode.dotcode;

import java.lang.reflect.Constructor;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.RecordComponent;
import java.lang.reflect.Type;
import java.util.ArrayList;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.function.BiFunction;
import java.util.function.Function;
import java.util.function.Supplier;

/**
 * A custom tool implemented by your application. Parameters are declared with Java types — a record or
 * {@link Param}s — never as a hand-written JSON schema, so a misspelled parameter is a compile error:
 *
 * <pre>{@code
 * record WeatherParams(@ToolParam("City name") String city,
 *                      @ToolParam(value = "Temperature unit", required = false) Unit unit) {}
 *
 * ToolDefinition weather = ToolDefinition.from("get_weather", "Weather for a city", WeatherParams.class,
 *         p -> p.city() + ": sunny");
 *
 * ToolDefinition search = ToolDefinition.from("search_items", "Searches items by keyword",
 *         Param.of(String.class, "keyword", "Search keyword"), keyword -> "Searching for: " + keyword);
 * }</pre>
 *
 * Handlers return a String, a {@link ToolResult}, a {@link CompletableFuture} of either, or any record/Map/List
 * (sent as JSON). Arguments are validated and converted before the handler runs; mismatches go back to the model.
 */
public final class ToolDefinition {
    @FunctionalInterface
    interface Invoker {
        Object invoke(Map<String, Object> arguments, ToolInvocation invocation) throws Exception;
    }

    private final String name;
    private final String description;
    private final Map<String, Object> parameters;
    private final Invoker invoker;
    private boolean readOnly;

    private ToolDefinition(String name, String description, Map<String, Object> parameters, Invoker invoker) {
        this.name = name;
        this.description = description;
        this.parameters = parameters;
        this.invoker = invoker;
    }

    public String getName() { return name; }
    public String getDescription() { return description; }
    /** The generated JSON Schema of the arguments. */
    public Map<String, Object> getParameters() { return parameters; }
    public boolean isReadOnly() { return readOnly; }

    /** Read-only tools may run in plan mode. Custom tools never prompt for permission. */
    public ToolDefinition readOnly(boolean value) {
        readOnly = value;
        return this;
    }

    // ------------------------------------------------------------------ factories

    /** A tool without parameters. */
    public static ToolDefinition from(String name, String description, Supplier<?> handler) {
        return new ToolDefinition(name, description, Schemas.object(Map.of(), List.of()), (args, inv) -> handler.get());
    }

    /** A tool whose parameters are the components of a record. */
    public static <R extends Record> ToolDefinition from(String name, String description, Class<R> params, Function<R, ?> handler) {
        return new ToolDefinition(name, description, Schemas.schema(params), (args, inv) -> handler.apply(Schemas.bind(params, args, "")));
    }

    /** A record-parameter tool that also receives the {@link ToolInvocation}. */
    public static <R extends Record> ToolDefinition fromWithToolInvocation(String name, String description, Class<R> params,
                                                                          BiFunction<R, ToolInvocation, ?> handler) {
        return new ToolDefinition(name, description, Schemas.schema(params), (args, inv) -> handler.apply(Schemas.bind(params, args, ""), inv));
    }

    /** A record-parameter tool with an asynchronous handler. */
    public static <R extends Record> ToolDefinition fromAsync(String name, String description, Class<R> params,
                                                              Function<R, ? extends CompletableFuture<?>> handler) {
        return new ToolDefinition(name, description, Schemas.schema(params), (args, inv) -> handler.apply(Schemas.bind(params, args, "")));
    }

    /** A tool with one parameter. */
    public static <A> ToolDefinition from(String name, String description, Param<A> a, Function<A, ?> handler) {
        return new ToolDefinition(name, description, Schemas.params(List.of(a)), (args, inv) -> handler.apply(Schemas.arg(a, args)));
    }

    /** A one-parameter tool that also receives the {@link ToolInvocation}. */
    public static <A> ToolDefinition fromWithToolInvocation(String name, String description, Param<A> a,
                                                            BiFunction<A, ToolInvocation, ?> handler) {
        return new ToolDefinition(name, description, Schemas.params(List.of(a)), (args, inv) -> handler.apply(Schemas.arg(a, args), inv));
    }

    /** A one-parameter tool with an asynchronous handler. */
    public static <A> ToolDefinition fromAsync(String name, String description, Param<A> a,
                                               Function<A, ? extends CompletableFuture<?>> handler) {
        return new ToolDefinition(name, description, Schemas.params(List.of(a)), (args, inv) -> handler.apply(Schemas.arg(a, args)));
    }

    /** A tool with two parameters. */
    public static <A, B> ToolDefinition from(String name, String description, Param<A> a, Param<B> b, BiFunction<A, B, ?> handler) {
        return new ToolDefinition(name, description, Schemas.params(List.of(a, b)),
                (args, inv) -> handler.apply(Schemas.arg(a, args), Schemas.arg(b, args)));
    }

    // ------------------------------------------------------------------ runtime

    Map<String, Object> toWire() {
        return Wire.map("name", name, "description", description, "inputSchema", parameters, "readOnly", readOnly);
    }

    /** Runs the handler; errors (including invalid arguments) are reported to the model. */
    Map<String, Object> invoke(Map<String, Object> arguments, ToolInvocation invocation) {
        try {
            Object result = invoker.invoke(arguments == null ? Map.of() : arguments, invocation);
            if (result instanceof CompletableFuture<?> f) result = f.get();
            return toWireResult(result);
        } catch (java.util.concurrent.ExecutionException e) {
            return Wire.map("content", "Error: " + e.getCause().getMessage(), "isError", true);
        } catch (Exception e) {
            return Wire.map("content", "Error: " + e.getMessage(), "isError", true);
        }
    }

    private static Map<String, Object> toWireResult(Object result) {
        if (result == null) return Wire.map("content", "");
        if (result instanceof String s) return Wire.map("content", s);
        if (result instanceof ToolResult r) {
            Object content = r.textResultForLlm();
            if (r.binaryResultsForLlm() != null && !r.binaryResultsForLlm().isEmpty()) {
                List<Object> parts = new ArrayList<>();
                parts.add(Wire.map("type", "text", "text", r.textResultForLlm()));
                for (ToolResult.Binary b : r.binaryResultsForLlm()) parts.add(Wire.map("type", "image", "data", b.data(), "mediaType", b.mimeType()));
                content = parts;
            }
            return Wire.map("content", content, "isError", r.failure());
        }
        return Wire.map("content", Json.write(result));
    }

    /** Raised when the model's arguments do not match the declared parameters. */
    public static final class ArgumentException extends IllegalArgumentException {
        private static final long serialVersionUID = 1L;

        ArgumentException(String message) { super(message); }
    }

    /** JSON Schema generation and argument binding for records, {@link Param}s and their field types. */
    static final class Schemas {
        private Schemas() {}

        static Map<String, Object> object(Map<String, Object> properties, List<String> required) {
            return Wire.map("type", "object", "properties", properties, "required", required.isEmpty() ? null : required,
                    "additionalProperties", false);
        }

        static Map<String, Object> params(List<Param<?>> params) {
            Map<String, Object> props = new LinkedHashMap<>();
            List<String> required = new ArrayList<>();
            for (Param<?> p : params) {
                Map<String, Object> s = new LinkedHashMap<>(of(p.type()));
                if (p.description() != null && !p.description().isEmpty()) s.put("description", p.description());
                props.put(p.name(), s);
                if (p.required()) required.add(p.name());
            }
            return object(props, required);
        }

        static <A> A arg(Param<A> p, Map<String, Object> args) {
            Object v = args.get(p.name());
            if (v == null && p.required()) throw new ArgumentException(p.name() + ": is required");
            return v == null ? null : bind(p.type(), v, p.name());
        }

        static Map<String, Object> schema(Class<? extends Record> type) {
            return of(type);
        }

        static Map<String, Object> of(Type t) {
            Class<?> c = raw(t);
            if (c == String.class || c == Character.class || c == char.class) return Wire.map("type", "string");
            if (c == Boolean.class || c == boolean.class) return Wire.map("type", "boolean");
            if (c == Integer.class || c == int.class || c == Long.class || c == long.class || c == Short.class || c == short.class
                    || c == Byte.class || c == byte.class) return Wire.map("type", "integer");
            if (c == Double.class || c == double.class || c == Float.class || c == float.class) return Wire.map("type", "number");
            if (c.isEnum()) {
                List<String> values = new ArrayList<>();
                for (Object e : c.getEnumConstants()) values.add(((Enum<?>) e).name());
                return Wire.map("type", "string", "enum", values);
            }
            if (Collection.class.isAssignableFrom(c) || c.isArray()) {
                Type item = c.isArray() ? c.getComponentType() : typeArg(t, 0);
                return Wire.map("type", "array", "items", item == null ? Wire.map() : of(item));
            }
            if (Map.class.isAssignableFrom(c)) {
                Type value = typeArg(t, 1);
                return Wire.map("type", "object", "additionalProperties", value == null ? Wire.map() : of(value));
            }
            if (c.isRecord()) {
                Map<String, Object> props = new LinkedHashMap<>();
                List<String> required = new ArrayList<>();
                for (RecordComponent rc : c.getRecordComponents()) {
                    Map<String, Object> s = new LinkedHashMap<>(of(rc.getGenericType()));
                    ToolParam meta = rc.getAnnotation(ToolParam.class);
                    if (meta != null && !meta.value().isEmpty()) s.put("description", meta.value());
                    props.put(rc.getName(), s);
                    if (meta == null || meta.required() || rc.getType().isPrimitive()) required.add(rc.getName());
                }
                return object(props, required);
            }
            if (c == Object.class) return Wire.map();
            throw new IllegalArgumentException("Unsupported tool parameter type: " + t.getTypeName());
        }

        @SuppressWarnings({"unchecked", "rawtypes"})
        static <T> T bind(Type t, Object v, String path) {
            Class<?> c = raw(t);
            String where = path.isEmpty() ? "arguments" : path;
            if (v == null) return null;
            if (c == Object.class) return (T) v;
            if (c == String.class) {
                if (!(v instanceof String)) throw new ArgumentException(where + ": expected a string");
                return (T) v;
            }
            if (c == Boolean.class || c == boolean.class) {
                if (!(v instanceof Boolean)) throw new ArgumentException(where + ": expected a boolean");
                return (T) v;
            }
            if (c == Integer.class || c == int.class || c == Long.class || c == long.class || c == Short.class || c == short.class
                    || c == Byte.class || c == byte.class) {
                if (!(v instanceof Number n) || n.doubleValue() != Math.rint(n.doubleValue()))
                    throw new ArgumentException(where + ": expected an integer");
                long l = n.longValue();
                if (c == Integer.class || c == int.class) return (T) Integer.valueOf((int) l);
                if (c == Short.class || c == short.class) return (T) Short.valueOf((short) l);
                if (c == Byte.class || c == byte.class) return (T) Byte.valueOf((byte) l);
                return (T) Long.valueOf(l);
            }
            if (c == Double.class || c == double.class || c == Float.class || c == float.class) {
                if (!(v instanceof Number n)) throw new ArgumentException(where + ": expected a number");
                return (T) (c == Float.class || c == float.class ? (Object) n.floatValue() : (Object) n.doubleValue());
            }
            if (c.isEnum()) {
                for (Object e : c.getEnumConstants())
                    if (((Enum<?>) e).name().equals(v) || e.toString().equals(v)) return (T) e;
                throw new ArgumentException(where + ": expected one of " + of(c).get("enum"));
            }
            if (Collection.class.isAssignableFrom(c)) {
                if (!(v instanceof List<?> list)) throw new ArgumentException(where + ": expected an array");
                Type item = typeArg(t, 0);
                Collection out = java.util.Set.class.isAssignableFrom(c) ? new LinkedHashSet<>() : new ArrayList<>();
                for (int i = 0; i < list.size(); i++) out.add(item == null ? list.get(i) : bind(item, list.get(i), path + "[" + i + "]"));
                return (T) out;
            }
            if (Map.class.isAssignableFrom(c)) {
                if (!(v instanceof Map<?, ?> map)) throw new ArgumentException(where + ": expected an object");
                Type value = typeArg(t, 1);
                Map<String, Object> out = new LinkedHashMap<>();
                map.forEach((k, x) -> out.put(String.valueOf(k), value == null ? x : bind(value, x, join(path, String.valueOf(k)))));
                return (T) out;
            }
            if (c.isRecord()) {
                if (!(v instanceof Map<?, ?> map)) throw new ArgumentException(where + ": expected an object");
                RecordComponent[] comps = c.getRecordComponents();
                Object[] values = new Object[comps.length];
                Class<?>[] types = new Class<?>[comps.length];
                for (int i = 0; i < comps.length; i++) {
                    RecordComponent rc = comps[i];
                    types[i] = rc.getType();
                    String child = join(path, rc.getName());
                    Object raw = map.get(rc.getName());
                    ToolParam meta = rc.getAnnotation(ToolParam.class);
                    boolean required = meta == null || meta.required() || rc.getType().isPrimitive();
                    if (raw == null && required) throw new ArgumentException(child + ": is required");
                    values[i] = raw == null ? defaultValue(rc.getType()) : bind(rc.getGenericType(), raw, child);
                }
                try {
                    Constructor<?> ctor = c.getDeclaredConstructor(types);
                    ctor.setAccessible(true);
                    return (T) ctor.newInstance(values);
                } catch (ReflectiveOperationException e) {
                    throw new IllegalStateException("Cannot create " + c.getName(), e);
                }
            }
            throw new IllegalArgumentException("Unsupported tool parameter type: " + t.getTypeName());
        }

        private static Object defaultValue(Class<?> c) {
            if (!c.isPrimitive()) return null;
            if (c == boolean.class) return false;
            if (c == double.class) return 0d;
            if (c == float.class) return 0f;
            if (c == long.class) return 0L;
            if (c == short.class) return (short) 0;
            if (c == byte.class) return (byte) 0;
            if (c == char.class) return '\0';
            return 0;
        }

        private static String join(String path, String key) { return path.isEmpty() ? key : path + "." + key; }

        private static Class<?> raw(Type t) {
            if (t instanceof Class<?> c) return c;
            if (t instanceof ParameterizedType p) return (Class<?>) p.getRawType();
            return Object.class;
        }

        private static Type typeArg(Type t, int index) {
            return t instanceof ParameterizedType p && p.getActualTypeArguments().length > index ? p.getActualTypeArguments()[index] : null;
        }
    }
}
