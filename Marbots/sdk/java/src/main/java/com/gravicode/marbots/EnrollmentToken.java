package com.gravicode.marbots;

import java.util.Map;

/** One-time token for {@code marbots-host enroll}. */
public record EnrollmentToken(String token, String expiresAt, String enrollCommand) {
    static EnrollmentToken from(Map<String, Object> d) {
        return new EnrollmentToken(W.str(d, "token"), W.str(d, "expiresAt"), W.str(d, "enrollCommand"));
    }
}
