package com.gravicode.marbots;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/** Package-private helpers to read wire maps safely. */
final class W {
    private W() {}

    static String str(Map<String, Object> m, String k) { return str(m, k, ""); }

    static String str(Map<String, Object> m, String k, String def) {
        Object v = m.get(k);
        return v instanceof String s ? s : def;
    }

    static String opt(Map<String, Object> m, String k) {
        Object v = m.get(k);
        return v instanceof String s ? s : null;
    }

    static long num(Map<String, Object> m, String k) {
        Object v = m.get(k);
        return v instanceof Number n ? n.longValue() : 0L;
    }

    static double dbl(Map<String, Object> m, String k) {
        Object v = m.get(k);
        return v instanceof Number n ? n.doubleValue() : 0.0;
    }

    static boolean bool(Map<String, Object> m, String k) { return Boolean.TRUE.equals(m.get(k)); }

    static List<String> strs(Map<String, Object> m, String k) {
        List<String> out = new ArrayList<>();
        if (m.get(k) instanceof List<?> l) for (Object o : l) if (o instanceof String s) out.add(s);
        return List.copyOf(out);
    }

    @SuppressWarnings("unchecked")
    static List<Map<String, Object>> objs(Object v) {
        List<Map<String, Object>> out = new ArrayList<>();
        if (v instanceof List<?> l) for (Object o : l) if (o instanceof Map<?, ?> mm) out.add((Map<String, Object>) mm);
        return out;
    }

    @SuppressWarnings("unchecked")
    static Map<String, Object> obj(Object v) {
        return v instanceof Map<?, ?> m ? (Map<String, Object>) m : Map.of();
    }
}
