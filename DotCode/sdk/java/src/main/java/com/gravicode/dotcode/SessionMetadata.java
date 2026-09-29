package com.gravicode.dotcode;

/** A saved session. */
public record SessionMetadata(String id, String title, String firstPrompt, String modified, int messageCount) {}
