package com.gravicode.marbots;

import java.util.Map;

/** A host's latest load report. */
public record HostMetrics(double cpuPercent, long freeMemoryMb, int runningCalls, long freeDiskMb) {
    static HostMetrics from(Object v) {
        if (!(v instanceof Map<?, ?>)) return null;
        Map<String, Object> d = W.obj(v);
        return new HostMetrics(W.dbl(d, "cpuPercent"), W.num(d, "freeMemoryMb"), (int) W.num(d, "runningCalls"), W.num(d, "freeDiskMb"));
    }
}
