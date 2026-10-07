package com.gravicode.marbots;

import java.util.Map;

/**
 * A file in a thread's project workspace.
 *
 * @param host set when the file lives on a remote agent host (download with {@code ?host=<id>})
 */
public record WorkspaceFile(String path, long size, String modified, String host, String hostName) {
    static WorkspaceFile from(Map<String, Object> d) {
        return new WorkspaceFile(W.str(d, "path"), W.num(d, "size"), W.str(d, "modified"), W.opt(d, "host"), W.opt(d, "hostName"));
    }
}
