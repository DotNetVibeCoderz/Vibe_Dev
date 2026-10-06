package com.gravicode.marbots;

import java.util.Map;

/** A machine that runs bots. */
public record HostInfo(String id, String name, String kind, String os, String status, int processorCount) {
    static HostInfo from(Map<String, Object> d) {
        return new HostInfo(W.str(d, "id"), W.str(d, "name"), W.str(d, "kind"), W.str(d, "os"), W.str(d, "status"), (int) W.num(d, "processorCount"));
    }
}
