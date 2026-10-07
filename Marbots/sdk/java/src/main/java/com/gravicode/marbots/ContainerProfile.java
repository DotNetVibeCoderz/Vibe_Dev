package com.gravicode.marbots;

import java.util.LinkedHashMap;
import java.util.Map;

/** Run the bot's shell commands in a throwaway Docker container with these quotas. */
public record ContainerProfile(String image, double cpus, int memoryMb, boolean network) {
    /** An image with 1 CPU, 1024 MB and network access. */
    public static ContainerProfile of(String image) { return new ContainerProfile(image, 1, 1024, true); }

    Map<String, Object> toWire() {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("image", image);
        m.put("cpus", cpus);
        m.put("memoryMb", memoryMb);
        m.put("network", network);
        return m;
    }

    static ContainerProfile from(Object v) {
        if (!(v instanceof Map<?, ?>)) return null;
        Map<String, Object> d = W.obj(v);
        return new ContainerProfile(W.str(d, "image"), W.dbl(d, "cpus"), (int) W.num(d, "memoryMb"), Boolean.TRUE.equals(d.get("network")));
    }
}
