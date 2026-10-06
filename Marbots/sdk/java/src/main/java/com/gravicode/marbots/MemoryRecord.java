package com.gravicode.marbots;

import java.util.Map;

/** A long-term memory. */
public record MemoryRecord(String id, String owner, MemoryKind kind, String content, String source, double confidence) {
    static MemoryRecord from(Map<String, Object> d) {
        return new MemoryRecord(W.str(d, "id"), W.str(d, "owner"), MemoryKind.from(W.str(d, "kind", "Semantic")), W.str(d, "content"),
            W.str(d, "source"), W.dbl(d, "confidence"));
    }
}
