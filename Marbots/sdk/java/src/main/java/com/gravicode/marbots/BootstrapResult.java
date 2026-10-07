package com.gravicode.marbots;

import java.util.List;
import java.util.Map;

/** What an SSH bootstrap did, step by step. */
public record BootstrapResult(boolean success, String hostId, List<String> log, String error) {
    static BootstrapResult from(Map<String, Object> d) {
        return new BootstrapResult(W.bool(d, "success"), W.opt(d, "hostId"), W.strs(d, "log"), W.opt(d, "error"));
    }
}
