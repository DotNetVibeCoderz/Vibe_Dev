package com.gravicode.dotcode;

import java.util.Map;

/** The outcome of one turn. */
public record SendResult(String sessionId, StopReason stopReason, String result, boolean isError, String error,
                         long durationMs, int numModelCalls, double costUsd, double totalCostUsd, Usage usage) {
    static SendResult fromWire(Map<String, Object> m) {
        return new SendResult(Wire.str(m, "sessionId"), StopReason.fromValue(Wire.str(m, "stopReason")), Wire.str(m, "result"),
                Wire.bool(m, "isError"), Wire.str(m, "error"), Wire.lng(m, "durationMs"), (int) Wire.num(m, "numModelCalls"),
                Wire.num(m, "costUsd"), Wire.num(m, "totalCostUsd"), Usage.fromWire(Wire.obj(m, "usage")));
    }
}
