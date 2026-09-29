package com.gravicode.dotcode;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Internal helpers for protocol maps. */
final class Wire {
    private Wire() {}

    /** Builds a map from key/value pairs, skipping null values. */
    static Map<String, Object> map(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i < kv.length; i += 2) if (kv[i + 1] != null) m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    static String str(Map<String, Object> m, String k) {
        Object v = m.get(k);
        return v == null ? null : v.toString();
    }

    static double num(Map<String, Object> m, String k) {
        return m.get(k) instanceof Number n ? n.doubleValue() : 0;
    }

    static long lng(Map<String, Object> m, String k) {
        return (long) num(m, k);
    }

    static boolean bool(Map<String, Object> m, String k) {
        return Boolean.TRUE.equals(m.get(k));
    }

    @SuppressWarnings("unchecked")
    static Map<String, Object> obj(Map<String, Object> m, String k) {
        return m.get(k) instanceof Map<?, ?> o ? (Map<String, Object>) o : Map.of();
    }

    @SuppressWarnings("unchecked")
    static List<Object> list(Map<String, Object> m, String k) {
        return m.get(k) instanceof List<?> l ? (List<Object>) l : List.of();
    }
}
