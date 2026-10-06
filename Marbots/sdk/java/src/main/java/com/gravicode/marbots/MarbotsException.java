package com.gravicode.marbots;

/** An API error: HTTP status plus the server's Problem Details message. */
public class MarbotsException extends RuntimeException {
    private static final long serialVersionUID = 1L;
    private final int status;

    public MarbotsException(int status, String message) {
        super("HTTP " + status + ": " + message);
        this.status = status;
    }

    /** The HTTP status code (0 for transport errors). */
    public int status() { return status; }
}
