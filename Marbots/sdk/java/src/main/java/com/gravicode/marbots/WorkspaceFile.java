package com.gravicode.marbots;

import java.util.Map;

/** A file in a thread's project workspace. */
public record WorkspaceFile(String path, long size, String modified) {
    static WorkspaceFile from(Map<String, Object> d) { return new WorkspaceFile(W.str(d, "path"), W.num(d, "size"), W.str(d, "modified")); }
}
