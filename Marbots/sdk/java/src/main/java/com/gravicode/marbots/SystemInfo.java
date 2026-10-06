package com.gravicode.marbots;

import java.util.Map;

/** Server information. */
public record SystemInfo(String product, String version, String credits, String creditsEn, boolean modelConfigured) {
    static SystemInfo from(Map<String, Object> d) {
        return new SystemInfo(W.str(d, "product"), W.str(d, "version"), W.str(d, "credits"), W.str(d, "creditsEn"), W.bool(d, "modelConfigured"));
    }
}
