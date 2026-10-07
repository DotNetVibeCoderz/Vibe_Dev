package com.gravicode.marbots;

import java.util.List;
import java.util.Map;

/**
 * A computer that runs bots' tools: the server itself or an enrolled agent host.
 *
 * @param capabilities shell, files, desktop, docker, dotnet, node, python, "pkg:winget" …
 */
public record HostInfo(String id, String name, String kind, String os, String status, int processorCount,
                       String architecture, String agentVersion, List<String> capabilities, HostMetrics metrics,
                       String installedVia) {
    static HostInfo from(Map<String, Object> d) {
        return new HostInfo(W.str(d, "id"), W.str(d, "name"), W.str(d, "kind"), W.str(d, "os"), W.str(d, "status"),
            (int) W.num(d, "processorCount"), W.str(d, "architecture"), W.str(d, "agentVersion"), W.strs(d, "capabilities"),
            HostMetrics.from(d.get("metrics")), W.opt(d, "installedVia"));
    }
}
