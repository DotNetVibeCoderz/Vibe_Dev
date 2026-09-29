package com.gravicode.dotcode;

import java.util.Map;

/** Sent with a message: a {@link #file} the agent should read or an inline {@link #image}. */
public sealed interface Attachment {
    record File(String path) implements Attachment {}

    /** Base64 image data; mediaType is image/png, image/jpeg, image/gif or image/webp. */
    record Image(String data, String mediaType) implements Attachment {}

    static File file(String path) { return new File(path); }
    static Image image(String base64, String mediaType) { return new Image(base64, mediaType); }

    static Map<String, Object> toWire(Attachment a) {
        if (a instanceof File f) return Wire.map("type", "file", "path", f.path());
        Image i = (Image) a;
        return Wire.map("type", "image", "data", i.data(), "mediaType", i.mediaType() == null ? "image/png" : i.mediaType());
    }
}
