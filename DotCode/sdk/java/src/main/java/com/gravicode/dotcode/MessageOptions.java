package com.gravicode.dotcode;

import java.util.ArrayList;
import java.util.List;

/** One user message. */
public final class MessageOptions {
    private String prompt;
    private final List<Attachment> attachments = new ArrayList<>();

    public MessageOptions() {}

    public MessageOptions(String prompt) { this.prompt = prompt; }

    public String getPrompt() { return prompt; }
    public MessageOptions setPrompt(String v) { prompt = v; return this; }
    public List<Attachment> getAttachments() { return attachments; }
    public MessageOptions addAttachment(Attachment a) { attachments.add(a); return this; }
}
