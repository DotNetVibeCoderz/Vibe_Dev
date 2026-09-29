package com.gravicode.dotcode;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/** Describes a tool parameter (a record component) for the model. */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.RECORD_COMPONENT, ElementType.FIELD, ElementType.PARAMETER})
public @interface ToolParam {
    /** Description the model sees. */
    String value() default "";

    /** Optional parameters may be omitted by the model (they arrive as null). */
    boolean required() default true;
}
