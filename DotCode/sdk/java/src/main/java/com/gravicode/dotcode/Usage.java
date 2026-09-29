package com.gravicode.dotcode;

import java.util.Map;

/** Token consumption. */
public record Usage(long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, long reasoningTokens) {
    static Usage fromWire(Map<String, Object> m) {
        return new Usage(Wire.lng(m, "inputTokens"), Wire.lng(m, "outputTokens"), Wire.lng(m, "cacheReadTokens"),
                Wire.lng(m, "cacheWriteTokens"), Wire.lng(m, "reasoningTokens"));
    }
}
