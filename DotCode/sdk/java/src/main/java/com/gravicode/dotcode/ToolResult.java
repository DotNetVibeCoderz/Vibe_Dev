package com.gravicode.dotcode;

import java.util.List;

/**
 * Full control over a tool result (a handler may also return a String or any record/Map/List, sent as JSON).
 *
 * @param binaryResultsForLlm images returned to the model (base64 data + mime type)
 */
public record ToolResult(String textResultForLlm, boolean failure, List<Binary> binaryResultsForLlm) {
    public record Binary(String data, String mimeType) {}

    public static ToolResult success(String text) { return new ToolResult(text, false, List.of()); }
    public static ToolResult failure(String text) { return new ToolResult(text, true, List.of()); }
}
